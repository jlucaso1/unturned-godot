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

    // Blends a tile's splat layers per pixel: the control textures carry the layer weights (sampled by
    // UV2, the tile-normalized position), each layer texture is tiled by world XZ, and the result is
    // their weighted average. World XZ comes straight from VERTEX since each tile mesh sits at the origin.
    //
    // The shader is generated per *painted layer count* rather than written once for eight, because the
    // terrain is the one thing that fills the whole screen and every sample it takes is paid at every
    // pixel. Two things fall out of the splatmap that the fixed eight-way blend was paying for anyway:
    //
    //   Per tile, most layers are never painted. A tile names eight and PEI's paint 3.4 on average, so
    //   the material binds only the ones its ActiveLayerMask reports — and a tile that paints four or
    //   fewer needs ONE control texture instead of two, which is a sample and half the control VRAM.
    //
    //   Per pixel, most layers are zero. 87% of PEI's splat texels give all their weight to a single
    //   layer and 99% to at most two, so a layer whose weight is zero here is skipped outright.
    //
    // Neither changes a pixel: skipping `sample * 0.0` removes a term that contributes +0.0, and `total`
    // still sums every weight, so the normalized average is bit-identical to the eight-way form. That
    // was checked rather than argued — a terrain-only PEI capture (`water/foliage/objects` switched off,
    // so nothing animated is in frame) renders bit-for-bit the same before and after.
    //
    // The sample inside the branch keeps its IMPLICIT LOD. `uv` is computed in uniform control flow, so
    // every lane of the quad — helper lanes included — holds the value the derivative is taken from, and
    // the sampler picks the same mip and the same anisotropic taps it always did. The spec-tidy
    // alternative, hoisting dFdx/dFdy and sampling with textureGrad, was tried and rejected on evidence:
    // it came back visibly blurrier (deltas to 110/255 against the unmodified render), because explicit
    // gradients drop out of the anisotropic path on at least one driver.
    //
    // `specular_disabled` rides along for free: SPECULAR is 0, which makes f0 zero and with it the whole
    // Schlick-GGX lobe, so the render mode drops ALU that was multiplying out to nothing. SPECULAR still
    // has to be WRITTEN — the render mode only guards direct light, while the sky's indirect specular
    // reads f0 all the same, and leaving SPECULAR at its 0.5 default lit the ground measurably brighter.
    // Both halves are what the flat fallback material below has always done.
    private static readonly Dictionary<int, Shader> SplatShaders = new();

    private static Shader SplatShaderFor(int painted)
    {
        if (SplatShaders.TryGetValue(painted, out Shader? cached))
            return cached;
        var shader = new Shader { Code = SplatShaderCode(painted) };
        SplatShaders[painted] = shader;
        return shader;
    }

    // `painted` is the number of layers the tile paints (1..8); slot i samples `layer{i}`, weighted by
    // channel i%4 of `control{i/4}`.
    internal static string SplatShaderCode(int painted)
    {
        int controls = (painted + 3) / 4;
        var code = new System.Text.StringBuilder();
        code.Append("shader_type spatial;\n");
        code.Append("render_mode cull_back, specular_disabled;\n");
        for (int slot = 0; slot < painted; slot++)
            code.Append($"uniform sampler2D layer{slot} : source_color, repeat_enable, "
                + "filter_linear_mipmap_anisotropic;\n");
        for (int control = 0; control < controls; control++)
            code.Append($"uniform sampler2D control{control} : repeat_disable, filter_linear;\n");
        // The A/B control for the per-pixel skip, so `terrain.splat.unpainted.enabled 1` can price it in
        // a running frame. A bool uniform is the same value for every pixel of the draw, so the branch it
        // adds is uniform and costs nothing beyond the compare the skip already does.
        code.Append("uniform bool sample_unpainted = false;\n");
        code.Append("uniform float tiling = 0.15; // texture repeats every 1/tiling world metres\n");
        code.Append("varying vec2 world_xz;\n");
        code.Append("void vertex() { world_xz = VERTEX.xz; }\n");
        code.Append("void fragment() {\n");
        for (int control = 0; control < controls; control++)
            code.Append($"    vec4 c{control} = texture(control{control}, UV2);\n");
        code.Append("    vec2 uv = world_xz * tiling; // uniform control flow: the sampler's derivative\n");
        code.Append("    vec3 albedo = vec3(0.0);\n");
        for (int slot = 0; slot < painted; slot++)
        {
            string weight = $"c{slot / 4}.{"rgba"[slot % 4]}";
            code.Append($"    if (sample_unpainted || {weight} > 0.0)\n");
            code.Append($"        albedo += texture(layer{slot}, uv).rgb * {weight};\n");
        }
        code.Append("    float total = ");
        for (int slot = 0; slot < painted; slot++)
            code.Append(slot == 0 ? "" : " + ").Append($"c{slot / 4}.{"rgba"[slot % 4]}");
        code.Append(";\n");
        code.Append("    ALBEDO = total > 0.0 ? albedo / total : albedo;\n");
        code.Append("    ROUGHNESS = 1.0;\n");
        code.Append("    SPECULAR = 0.0; // f0 = 0: the sky's indirect specular reads this even with the\n");
        code.Append("                    // direct lobe disabled, and its 0.5 default brightens the ground\n");
        code.Append("}\n");
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
    // (FinishTile): its LOD-baked geometry (an ImporterMesh, a data-only Resource), the full-resolution
    // heightmap for collision, and which splat layers the tile paints — a scan of its 512 KB weight array,
    // done here so the main thread's FinishTile only assembles the material it decides.
    public readonly struct TileMesh
    {
        public readonly ImporterMesh Importer;
        public readonly ushort[]? Heights16;
        public readonly float[]? Heights32;
        public readonly int X;
        public readonly int Y;
        public readonly SplatmapTile? Splat;
        public readonly int[]? Painted;
        public TileMesh(ImporterMesh importer, ushort[]? heights16, float[]? heights32,
            int x, int y, SplatmapTile? splat, int[]? painted = null)
        {
            Importer = importer;
            Heights16 = heights16;
            Heights32 = heights32;
            X = x;
            Y = y;
            Splat = splat;
            Painted = painted;
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
        // Only the textured path builds a splat material, and only it needs to know what the tile paints.
        int[]? painted = textured && splat != null ? PaintedLayers(splat) : null;
        return new TileMesh(importer, tile.RawSamples, flat, tile.CoordX, tile.CoordY, splat, painted);
    }

    // Phase 2 — main thread only: realise the LOD mesh (GetMesh creates the ArrayMesh RenderingServer
    // resource) and attach the per-tile splat material (its control textures are GPU resources). The
    // full-res heightmap rides along in metadata for AddHeightfieldCollision.
    public sealed class ControlTextureCache
    {
        private readonly Dictionary<string, ImageTexture> _textures = new();

        public ImageTexture GetOrCreate(byte[] bytes)
        {
            if (!EnvFlag.IsOn(System.Environment.GetEnvironmentVariable("UG_DEDUP_GPU"), whenUnset: true))
            {
                const int rawRes = Landscape.SPLATMAP_RESOLUTION;
                return ImageTexture.CreateFromImage(
                    Image.CreateFromData(rawRes, rawRes, false, Image.Format.Rgba8, bytes));
            }
            string key = ExactContentKey.Bytes(bytes);
            if (!_textures.TryGetValue(key, out ImageTexture? texture))
            {
                const int res = Landscape.SPLATMAP_RESOLUTION;
                texture = ImageTexture.CreateFromImage(
                    Image.CreateFromData(res, res, false, Image.Format.Rgba8, bytes));
                _textures[key] = texture;
            }
            return texture;
        }
    }

    public static MeshInstance3D FinishTile(in TileMesh tm, ImageTexture[]? layerTextures,
        ControlTextureCache? controls = null)
    {
        ArrayMesh mesh = tm.Importer.GetMesh();
        mesh.SurfaceSetMaterial(0, layerTextures != null && tm.Splat != null
            ? BuildSplatMaterial(layerTextures, tm.Splat, tm.Painted, controls)
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

    // A per-tile ShaderMaterial: the layer textures this tile actually paints, plus the control textures
    // holding their weights (four weights to an RGBA8 image, sampled by UV2).
    private static ShaderMaterial BuildSplatMaterial(ImageTexture[] layers, SplatmapTile splat,
        int[]? prepared, ControlTextureCache? controls)
    {
        // Normally handed down from the worker thread; a caller that built the tile without one (the
        // synchronous fallback, and the tests) pays for the scan here instead of going without.
        int[] painted = prepared ?? PaintedLayers(splat);
        var material = new ShaderMaterial { Shader = SplatShaderFor(painted.Length) };
        for (int slot = 0; slot < painted.Length; slot++)
            material.SetShaderParameter($"layer{slot}", layers[painted[slot]]);

        var channels = new int[4];
        for (int control = 0; control * 4 < painted.Length; control++)
        {
            for (int channel = 0; channel < 4; channel++)
            {
                int slot = (control * 4) + channel;
                channels[channel] = slot < painted.Length ? painted[slot] : -1;
            }
            material.SetShaderParameter($"control{control}", ControlTexture(splat, channels, controls));
        }
        return material;
    }

    // The layers this tile paints, in layer order. A tile that paints none still gets one slot: its
    // weights are zero everywhere, so the shader's `total > 0.0` guard renders it black exactly as the
    // eight-way blend did, and a zero-sampler shader would not be well-formed.
    internal static int[] PaintedLayers(SplatmapTile splat)
    {
        int mask = splat.ActiveLayerMask();
        if (mask == 0)
            return new[] { 0 };
        var painted = new List<int>(SplatmapTile.LAYERS);
        for (int layer = 0; layer < SplatmapTile.LAYERS; layer++)
            if ((mask & (1 << layer)) != 0)
                painted.Add(layer);
        return painted.ToArray();
    }

    // Packs up to 4 splat layers into an RGBA8 image, one layer per channel in `channels` order; a
    // channel of -1 is a slot this tile does not fill and is written zero, so it contributes nothing to
    // the blend or to `total`. Image pixel (x, y) — addressed (y * res + x) — carries the weights the
    // splatmap stores at [x, y], so UV2 (which runs x->u, y->v) samples the same texel TerrainColor
    // blends per vertex.
    private static ImageTexture ControlTexture(SplatmapTile splat, IReadOnlyList<int> channels,
        ControlTextureCache? controls)
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        byte[] weights = splat.Weights;
        var bytes = new byte[res * res * 4];
        for (int channel = 0; channel < 4; channel++)
        {
            int layer = channels[channel];
            if (layer < 0)
                continue;
            for (int x = 0; x < res; x++)
            {
                int src = SplatmapTile.WeightIndex(x, 0, layer);
                int dst = (x * 4) + channel;
                for (int y = 0; y < res; y++, src += SplatmapTile.LAYERS, dst += res * 4)
                    bytes[dst] = weights[src];
            }
        }
        return controls?.GetOrCreate(bytes)
            ?? ImageTexture.CreateFromImage(Image.CreateFromData(res, res, false, Image.Format.Rgba8, bytes));
    }
}
