using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

public static partial class TerrainBuilder
{
    // One material shared by every tile (#4): 16 identical StandardMaterial3D instances become one, so
    // the renderer batches state instead of switching per tile. Back-face culling (#3) halves fill rate
    // — the ground is only ever seen from above, so drawing its underside was wasted work. Used only as
    // the fallback when the map's real terrain textures can't be loaded.
    private static readonly StandardMaterial3D SharedMaterial = new()
    {
        VertexColorUseAsAlbedo = true,
        CullMode = BaseMaterial3D.CullModeEnum.Back,
        Roughness = 1.0f,
        // Fully-rough dielectric: the GGX specular lobe contributes nothing visible, so skip its
        // per-fragment ALU over the terrain's screen-dominating fill.
        SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
    };

    // Blends the splat layers a tile actually paints, per pixel: a control texture carries the layer
    // weights (sampled by UV2, the tile-normalized position), each layer texture is tiled by world XZ,
    // and the result is their weighted average. World XZ comes straight from VERTEX since each tile mesh
    // sits at the origin.
    //
    // The shader is generated per layer count rather than written once for eight, because the terrain is
    // the surface that covers most of the screen and every layer costs it an anisotropic fetch of every
    // pixel of that surface. Eight is the format's maximum, not a map's habit: PEI's sixteen tiles paint
    // 3.4 layers on average and three of them paint exactly one. A layer whose weight is zero over the
    // whole tile adds zero to the weighted sum AND zero to the divisor, so leaving it out is not an
    // approximation — see SplatmapTile.Coverage, which is what decides.
    //
    // Only the layer COUNT varies the code (the weights are repacked into the leading channels of the
    // control texture, so a tile painting layers 0, 2 and 5 runs the same three-layer shader as one
    // painting 1, 3 and 4), so every tile of every map shares a handful of programs — nine layer counts
    // times the one branch that turns on whether the tile leaves any texel unpainted. PEI needs five.
    private static readonly Dictionary<(int Layers, bool Covered), Shader> SplatShaders = new();

    // Main thread only (a Shader is a RenderingServer resource); FinishTile is the only caller.
    private static Shader SplatShaderFor(int layers, bool covered)
    {
        if (!SplatShaders.TryGetValue((layers, covered), out Shader? shader))
        {
            shader = new Shader { Code = SplatShaderCode(layers, covered) };
            SplatShaders[(layers, covered)] = shader;
        }
        return shader;
    }

    // `covered` means every texel of the tile has some weight on it, which is what lets the blend divide
    // by its total unconditionally — and, at one layer, skip the control texture entirely, since
    // `layer * w / w` is the layer whatever w is.
    internal static string SplatShaderCode(int layers, bool covered)
    {
        int controls = layers == 0 || (layers == 1 && covered) ? 0 : (layers + 3) / 4;
        var code = new System.Text.StringBuilder();
        code.AppendLine("shader_type spatial;");
        code.AppendLine("render_mode cull_back;");
        for (int i = 0; i < layers; i++)
        {
            code.AppendLine($"uniform sampler2D layer{i} : source_color, repeat_enable, "
                + "filter_linear_mipmap_anisotropic;");
        }
        for (int i = 0; i < controls; i++)
            code.AppendLine($"uniform sampler2D control{i} : repeat_disable; // weights of the layers packed into it");

        if (layers == 0)
        {
            // A splatmap that paints nothing: the eight-layer blend produced a zero numerator over a zero
            // total, which it resolved to black. Same pixel, no fetches.
            code.AppendLine("void fragment() { ALBEDO = vec3(0.0); ROUGHNESS = 1.0; SPECULAR = 0.0; }");
            return code.ToString();
        }

        code.AppendLine("uniform float tiling = 0.15; // texture repeats every 1/tiling world metres");
        // Only XZ of the world position is ever used, so only XZ is interpolated. The scaling stays in the
        // fragment: interpolating the SCALED coordinate instead rounds differently, and the terrain is a
        // surface where a fraction of a texel of drift is visible as a changed pixel.
        code.AppendLine("varying vec2 world_xz;");
        code.AppendLine("void vertex() { world_xz = VERTEX.xz; }");
        code.AppendLine("void fragment() {");
        code.AppendLine("    vec2 layer_uv = world_xz * tiling;");
        for (int i = 0; i < controls; i++)
            code.AppendLine($"    vec4 c{i} = texture(control{i}, UV2);");

        if (controls == 0)
        {
            code.AppendLine("    ALBEDO = texture(layer0, layer_uv).rgb;");
        }
        else
        {
            var albedo = new System.Text.StringBuilder();
            var total = new System.Text.StringBuilder();
            for (int i = 0; i < layers; i++)
            {
                string weight = $"c{i / 4}.{"rgba"[i % 4]}";
                albedo.Append(i == 0 ? "" : " + ").Append($"texture(layer{i}, layer_uv).rgb * {weight}");
                total.Append(i == 0 ? "" : " + ").Append(weight);
            }
            code.AppendLine($"    vec3 albedo = {albedo};");
            code.AppendLine($"    float total = {total};");
            code.AppendLine(covered
                ? "    ALBEDO = albedo / total;"
                : "    ALBEDO = total > 0.0 ? albedo / total : albedo;");
        }
        code.AppendLine("    ROUGHNESS = 1.0;");
        code.AppendLine("    SPECULAR = 0.0;");
        code.AppendLine("}");
        return code.ToString();
    }

    // Maps the map's terrain textures (by name) onto the 8 splat layers in layer order, returning null if
    // any layer's texture is missing so the caller falls back to the averaged-color material.
    public static ImageTexture[]? MapLayerTextures(System.Collections.Generic.IReadOnlyDictionary<string, ImageTexture> textures)
    {
        var layers = new ImageTexture[SplatmapTile.LAYERS];
        for (int i = 0; i < layers.Length; i++)
        {
            if (!textures.TryGetValue(TerrainPalette.LayerTextureNames[i], out ImageTexture? tex))
                return null;
            layers[i] = tex;
        }
        return layers;
    }

    // Everything a tile carries between the worker-thread build (BuildTileMesh) and the main-thread finish
    // (FinishTile): its LOD-baked geometry (an ImporterMesh, a data-only Resource) and the full-resolution
    // heightmap for collision.
    public readonly struct TileMesh
    {
        public readonly ImporterMesh Importer;
        public readonly ushort[]? Heights16;
        public readonly float[]? Heights32;
        public readonly int X;
        public readonly int Y;
        public readonly SplatmapTile? Splat;
        // Which layers the splatmap paints, scanned on the worker thread beside the tessellation rather
        // than on the main thread while the loading screen is waiting on it.
        public readonly SplatmapTile.SplatCoverage Coverage;
        public TileMesh(ImporterMesh importer, ushort[]? heights16, float[]? heights32,
            int x, int y, SplatmapTile? splat, SplatmapTile.SplatCoverage coverage = default)
        {
            Importer = importer;
            Heights16 = heights16;
            Heights32 = heights32;
            X = x;
            Y = y;
            Splat = splat;
            Coverage = coverage;
        }
        public float HeightAt(int index) => Heights16 != null
            ? Heights16[index] / (float)ushort.MaxValue : Heights32![index];
    }

    // Phase 1 — safe to run on a worker thread: read + tessellate the tile at a subsampled resolution and
    // generate its meshoptimizer LOD chain. This is pure CPU/meshopt work on an ImporterMesh (a data-only
    // Resource); no RenderingServer object is created until FinishTile calls GetMesh() on the main thread.
    // `textured` selects the render path (true: splat shader via UV2, skip per-vertex colors; false:
    // averaged vertex colors for the flat fallback material).
    public static TileMesh BuildTileMesh(HeightmapTile tile, SplatmapTile? splat, bool textured)
    {
        // Build the visual tile at a subsampled resolution (step must divide 256 so tile edges land on 0
        // and 256 and adjacent tiles stay gap-free), then let Godot's meshoptimizer LODs coarsen it further
        // with distance. In Godot 4.7 generate_lods() locks the mesh's topological border, so each tile's
        // shared edge is preserved at full resolution across every LOD and adjacent tiles never crack. The
        // collision keeps the full-resolution heightmap (attached below) so player movement is unaffected.
        const int LodStep = 2;
        int res = ((Landscape.HEIGHTMAP_RESOLUTION - 1) / LodStep) + 1;
        int vertexCount = res * res;

        // Build the mesh arrays in C# and hand them to the engine in a single AddSurfaceFromArrays call,
        // instead of SurfaceTool's per-element AddVertex/SetColor/AddIndex + GenerateNormals. The profiler
        // showed that path was ~44% of world-build time (GenerateNormals alone 34%), almost all of it
        // marshaling ~400k managed→native calls per tile. Vertex (x, y) lands at index x * res + y.
        // The splat shader derives ALBEDO from the layer textures via UV2 and never samples COLOR, so on
        // the textured (normal) path skip the per-vertex TerrainPalette.Blend and the uploaded color
        // attribute; keep them only for the flat-color fallback material (VertexColorUseAsAlbedo).
        var positions = new Vector3[vertexCount];
        Color[]? colors = textured ? null : new Color[vertexCount];
        var uv2 = new Vector2[vertexCount];
        float invRes = 1f / (res - 1);
        for (int x = 0; x < res; x++)
        {
            for (int y = 0; y < res; y++)
            {
                int idx = x * res + y;
                int hx = x * LodStep, hy = y * LodStep; // full-res heightmap sample
                float h01 = tile.HeightAt(hx, hy);
                Vector3 unity = Landscape.GetWorldPosition(tile.CoordX, tile.CoordY, hx, hy, h01);
                positions[idx] = Landscape.UnityToGodot(unity);
                if (colors != null)
                    colors[idx] = TerrainColor.ForVertex(splat, hx, hy, unity.Y);
                uv2[idx] = new Vector2(x * invRes, y * invRes); // tile-normalized, for the splat control lookup
            }
        }

        // Front faces point up (verified on screen): back-face culling (#3) then drops the underside.
        var indices = new int[(res - 1) * (res - 1) * 6];
        int t = 0;
        for (int x = 0; x < res - 1; x++)
        {
            for (int y = 0; y < res - 1; y++)
            {
                int v00 = x * res + y;
                int v01 = x * res + (y + 1);
                int v10 = (x + 1) * res + y;
                int v11 = (x + 1) * res + (y + 1);
                indices[t++] = v00;
                indices[t++] = v10;
                indices[t++] = v11;
                indices[t++] = v00;
                indices[t++] = v11;
                indices[t++] = v01;
            }
        }

        // Smooth normals: accumulate each triangle's (area-weighted) face normal into its three vertices,
        // then normalize. Done with raw float accumulators rather than Godot.Vector3 operators — the
        // profiler flagged the per-triangle Vector3 add/sub/cross/normalize (~11% of terrain build) as
        // operator/ctor overhead. The arithmetic is identical (Vector3.Cross expanded inline), so the
        // resulting normals — and the shading — are unchanged.
        var nx = new float[vertexCount];
        var ny = new float[vertexCount];
        var nz = new float[vertexCount];
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = indices[i];
            int b = indices[i + 1];
            int c = indices[i + 2];
            Vector3 pa = positions[a];
            float e1x = positions[b].X - pa.X;
            float e1y = positions[b].Y - pa.Y;
            float e1z = positions[b].Z - pa.Z;
            float e2x = positions[c].X - pa.X;
            float e2y = positions[c].Y - pa.Y;
            float e2z = positions[c].Z - pa.Z;
            // Cross(e2, e1): the triangles wind so that Cross(e1, e2) faces down, which left the terrain
            // lit only by ambient (no sun term, so no shadows and no slope shading).
            float fnx = e1z * e2y - e1y * e2z;
            float fny = e1x * e2z - e1z * e2x;
            float fnz = e1y * e2x - e1x * e2y;
            nx[a] += fnx;
            ny[a] += fny;
            nz[a] += fnz;
            nx[b] += fnx;
            ny[b] += fny;
            nz[b] += fnz;
            nx[c] += fnx;
            ny[c] += fny;
            nz[c] += fnz;
        }
        var normals = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            float len = Mathf.Sqrt(nx[i] * nx[i] + ny[i] * ny[i] + nz[i] * nz[i]);
            if (len > 0f)
                normals[i] = new Vector3(nx[i] / len, ny[i] / len, nz[i] / len);
        }

        using var arrays = new Godot.Collections.Array(); // freed at scope exit (data copied into the mesh)
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = positions;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        if (colors != null)
            arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        // Generate meshoptimizer LODs (border-locked in 4.7 -> seam-free between tiles). No material yet:
        // BuildSplatMaterial creates control textures (a RenderingServer/GPU op), so it runs on the main
        // thread in FinishTile. ImporterMesh here is a plain data container — GenerateLods is CPU/meshopt.
        var importer = new ImporterMesh();
        importer.AddSurface(Mesh.PrimitiveType.Triangles, arrays);
        importer.GenerateLods(25f, 60f, new Godot.Collections.Array());

        // Carry the full-resolution heightmap (row-major, normalized) so the on-demand player collision
        // stays full-res and correct — the visual mesh is decimated/LOD'd and meshoptimizer reorders its
        // vertices, so collision can't be reconstructed from it.
        float[]? flat = null;
        if (tile.RawSamples == null)
        {
            int fullRes = Landscape.HEIGHTMAP_RESOLUTION;
            flat = new float[fullRes * fullRes];
            for (int hx = 0; hx < fullRes; hx++)
                for (int hy = 0; hy < fullRes; hy++)
                    flat[(hx * fullRes) + hy] = tile.HeightAt(hx, hy);
        }
        return new TileMesh(importer, tile.RawSamples, flat, tile.CoordX, tile.CoordY, splat,
            splat?.Coverage() ?? default);
    }

    // Phase 2 — main thread only: realise the LOD mesh (GetMesh creates the ArrayMesh RenderingServer
    // resource) and attach the per-tile splat material (its control textures are GPU resources). The
    // full-res heightmap rides along in metadata for AddHeightfieldCollision.
    public sealed class ControlTextureCache
    {
        private readonly Dictionary<string, ImageTexture> _textures = new();

        public ImageTexture GetOrCreate(byte[] bytes, Image.Format format)
        {
            if (!EnvFlag.IsOn(System.Environment.GetEnvironmentVariable("UG_DEDUP_GPU"), whenUnset: true))
                return Create(bytes, format);
            // The format rides in the key beside the content: two tiles whose weights happen to agree
            // byte for byte are still different textures if one packs them into one channel and the
            // other into four.
            string key = $"{(int)format}:{ExactContentKey.Bytes(bytes)}";
            if (!_textures.TryGetValue(key, out ImageTexture? texture))
                _textures[key] = texture = Create(bytes, format);
            return texture;
        }

        private static ImageTexture Create(byte[] bytes, Image.Format format)
        {
            const int res = Landscape.SPLATMAP_RESOLUTION;
            return ImageTexture.CreateFromImage(Image.CreateFromData(res, res, false, format, bytes));
        }
    }

    public static MeshInstance3D FinishTile(in TileMesh tm, ImageTexture[]? layerTextures,
        ControlTextureCache? controls = null)
    {
        ArrayMesh mesh = tm.Importer.GetMesh();
        mesh.SurfaceSetMaterial(0, layerTextures != null && tm.Splat != null
            ? BuildSplatMaterial(layerTextures, tm.Splat, tm.Coverage, controls)
            : SharedMaterial);

        var node = new TerrainTileNode
        {
            Mesh = mesh,
            Name = $"Tile_{tm.X}_{tm.Y}",
            CollisionHeights16 = tm.Heights16,
            CollisionHeights32 = tm.Heights32,
            TileX = tm.X,
            TileY = tm.Y,
        };
        return node;
    }

    private sealed partial class TerrainTileNode : MeshInstance3D
    {
        public ushort[]? CollisionHeights16;
        public float[]? CollisionHeights32;
        public int TileX;
        public int TileY;
    }

    // Gives a rendered terrain tile a cheap full-resolution heightfield StaticBody (a 257x257
    // HeightMapShape3D) instead of a ~131k-triangle concave trimesh, from the full-res heightmap the tile
    // carried in metadata (FinishTile) — independent of the tile's visual LOD. Verified to reproduce the
    // render surface exactly by TerrainHeightfieldTests.
    // `navigationField`, when given, receives the same heightfield the physics server does, so navmesh
    // reconciliation can probe the ground without a physics tick. See CollisionField.
    public static void AddHeightfieldCollision(MeshInstance3D tile,
        CollisionFieldBuilder? navigationField = null)
    {
        if (tile is not TerrainTileNode terrainTile
            || (terrainTile.CollisionHeights16 == null && terrainTile.CollisionHeights32 == null))
            return;

        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        float[] mapData = terrainTile.CollisionHeights16 != null
            ? TerrainHeightfield.MapData(terrainTile.CollisionHeights16)
            : MapDataFromFlat(terrainTile.CollisionHeights32!);
        Transform3D placement =
            TerrainHeightfield.CollisionTransform(terrainTile.TileX, terrainTile.TileY);

        var body = new StaticBody3D { Name = "TerrainCollision" };
        body.AddChild(new CollisionShape3D
        {
            Shape = new HeightMapShape3D
            {
                MapWidth = res,
                MapDepth = res,
                MapData = mapData,
            },
            Transform = placement,
        });
        tile.AddChild(body);
        navigationField?.AddHeightfield(placement, res, res, mapData);

        // The heightfield now lives in the physics server's HeightMapShape3D; drop the ~264 KB/tile source
        // copy the tile carried in metadata (collision is built once and never rebuilt).
        terrainTile.CollisionHeights16 = null;
        terrainTile.CollisionHeights32 = null;
    }

    private static float[] MapDataFromFlat(float[] heights)
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        var data = new float[heights.Length];
        for (int hx = 0; hx < res; hx++)
            for (int hy = 0; hy < res; hy++)
                data[(res - 1 - hx) * res + hy] = (-Landscape.TILE_HEIGHT / 2f)
                    + (heights[(hx * res) + hy] * Landscape.TILE_HEIGHT);
        return data;
    }

    // A per-tile ShaderMaterial: the layer textures this tile paints with, bound in the compacted order
    // its shader expects, plus the control texture(s) holding their weights.
    private static ShaderMaterial BuildSplatMaterial(ImageTexture[] layers, SplatmapTile splat,
        SplatmapTile.SplatCoverage coverage, ControlTextureCache? controls)
    {
        int[] used = coverage.UsedLayers();
        var material = new ShaderMaterial
        {
            Shader = SplatShaderFor(used.Length, coverage.Covered),
        };
        for (int i = 0; i < used.Length; i++)
            material.SetShaderParameter($"layer{i}", layers[used[i]]);

        // One control texture per four painted layers, and none at all for the single fully-painted
        // layer whose weights the blend would only divide back out.
        if (used.Length == 0 || (used.Length == 1 && coverage.Covered))
            return material;
        for (int first = 0, control = 0; first < used.Length; first += 4, control++)
        {
            int count = System.Math.Min(4, used.Length - first);
            material.SetShaderParameter($"control{control}",
                ControlTexture(splat, new System.ReadOnlySpan<int>(used, first, count), controls));
        }
        return material;
    }

    // Packs the given splat layers, in order, into the leading channels of an image — one channel each,
    // so a tile painting three layers uploads three channels rather than the eight the format allows.
    // Image pixel (x, y) — addressed (y * res + x) — carries the weights the splatmap stores at [x, y],
    // so UV2 (which runs x->u, y->v) samples the same texel TerrainColor blends per vertex.
    private static ImageTexture ControlTexture(SplatmapTile splat, System.ReadOnlySpan<int> pack,
        ControlTextureCache? controls)
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        // R8 and RG8 are real GPU formats and cut the bytes this texture reads per pixel; RGB8 is not
        // (the driver would widen it back to four channels), so three layers ride in an RGBA8 whose
        // alpha nothing samples.
        (Image.Format format, int channels) = pack.Length switch
        {
            1 => (Image.Format.R8, 1),
            2 => (Image.Format.Rg8, 2),
            _ => (Image.Format.Rgba8, 4),
        };
        byte[] weights = splat.Weights;
        var bytes = new byte[res * res * channels];
        for (int x = 0; x < res; x++)
        {
            for (int y = 0; y < res; y++)
            {
                int dst = (y * res + x) * channels;
                for (int i = 0; i < pack.Length; i++)
                    bytes[dst + i] = weights[SplatmapTile.WeightIndex(x, y, pack[i])];
            }
        }
        return controls?.GetOrCreate(bytes, format)
            ?? ImageTexture.CreateFromImage(Image.CreateFromData(res, res, false, format, bytes));
    }
}
