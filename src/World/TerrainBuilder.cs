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
        // per-fragment ALU over the terrain's screen-dominating fill. Specular is zeroed as well as
        // disabled, because the render mode guards direct light alone — see the splat shader below.
        SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
        MetallicSpecular = 0f,
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
    // The sample inside the branch keeps its IMPLICIT LOD — the opposite of what the tidy reading of the
    // spec suggests, and what three captures against the unmodified render actually say:
    //
    //     texture() inside the branch       0 of 5,988,600 channel samples differ
    //     textureGrad(), no branch          max delta 5/255 — coarse-against-fine derivative rounding
    //     textureGrad() inside the branch   max delta 110/255 — visibly blurrier ground
    //
    // The middle row is the one that settles it: the gradients themselves are right, so what fails is
    // hoisting them PAST a branch. A compiler is free to sink a dFdx whose only use is inside the
    // conditional back into it, and taken there it is taken under divergent flow. The implicit form has
    // no such value to sink — the derivative belongs to the sample instruction, which the quad's own
    // semantics cover — and `uv` is computed before the branch, so every lane, helper lanes included,
    // holds what it is taken from. The zero is the evidence that matters rather than the argument: that
    // frame is full of splat boundaries, which are exactly the divergent quads the doubt is about.
    //
    // `specular_disabled` rides along for free: SPECULAR is 0, which makes f0 zero and with it the whole
    // Schlick-GGX lobe, so the render mode drops ALU that was multiplying out to nothing. SPECULAR still
    // has to be WRITTEN — the render mode only guards direct light, while the sky's indirect specular
    // reads f0 all the same, and leaving SPECULAR at its 0.5 default lit the ground measurably brighter.
    // Both halves are what the flat fallback material above does — though only since the
    // `MetallicSpecular = 0f` beside its render mode. `SpecularModeEnum.Disabled` is the render mode
    // alone; Godot's own docs say it "does not affect specular reflections from the sky", so every
    // material that set it without zeroing MetallicSpecular kept the 4% f0 this paragraph is about.
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
        // No shader here uses a preprocessor directive, so the pass has nothing to do but read the text.
        code.Append("#pragma disable_preprocessor\n");
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
    // heightmap for collision, the cells the map cuts away, and which splat layers the tile paints — a
    // scan of its 512 KB weight array, done here so the main thread's FinishTile only assembles the
    // material it decides.
    public readonly struct TileMesh
    {
        public readonly ImporterMesh Importer;
        public readonly ushort[]? Heights16;
        public readonly float[]? Heights32;
        public readonly int X;
        public readonly int Y;
        public readonly SplatmapTile? Splat;
        public readonly int[]? Painted;
        public readonly LandscapeHoles? Holes;
        public TileMesh(ImporterMesh importer, ushort[]? heights16, float[]? heights32,
            int x, int y, SplatmapTile? splat, int[]? painted = null, LandscapeHoles? holes = null)
        {
            Importer = importer;
            Heights16 = heights16;
            Heights32 = heights32;
            X = x;
            Y = y;
            Splat = splat;
            Painted = painted;
            Holes = holes;
        }
        public float HeightAt(int index) => Heights16 != null
            ? Heights16[index] / (float)ushort.MaxValue : Heights32![index];
    }

    // Phase 1 — safe to run on a worker thread: read + tessellate the tile and generate its meshoptimizer
    // LOD chain. This is pure CPU/meshopt work on an ImporterMesh (a data-only Resource); no
    // RenderingServer object is created until FinishTile calls GetMesh() on the main thread.
    // `textured` selects the render path (true: splat shader via UV2, skip per-vertex colors; false:
    // averaged vertex colors for the flat fallback material).
    public static TileMesh BuildTileMesh(HeightmapTile tile, SplatmapTile? splat, bool textured)
    {
        // One quad per heightmap cell, at the heightmap's own resolution — the same 257x257 grid the
        // collision heightfield is built from and HeightmapSampler interpolates on. This used to subsample
        // every second index, and the three then disagreed: the drawn surface interpolated straight over
        // whatever sat on an odd index while the collider and the sampler kept it, so a ridge there put
        // the player above the ground being drawn and a gully sank them into it, and a road lofted onto
        // the sampler buried itself wherever the decimation had moved the drawn surface. Unturned has one
        // TerrainData per tile and Unity draws and collides against that one heightmap, so agreeing at
        // full resolution is also what the original does.
        //
        // Distance is meshoptimizer's job rather than the source grid's: GenerateLods below coarsens the
        // tile with distance from a full-resolution base, and in Godot 4.7 generate_lods() locks the
        // mesh's topological border, so each tile's shared edge survives every LOD and adjacent tiles
        // never crack. Measured on PEI, going full-resolution left every pose's DRAW CALLS unchanged and
        // the aerial poses within 4-15% of the subsampled primitive counts; what it costs is the near
        // view, where LOD 0 is what is in frame (see the pull request for the numbers).
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        const int vertexCount = res * res;

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
        const float invRes = 1f / (res - 1);
        for (int hx = 0; hx < res; hx++)
        {
            for (int hy = 0; hy < res; hy++)
            {
                int idx = (hx * res) + hy;
                float h01 = tile.HeightAt(hx, hy);
                Vector3 unity = Landscape.GetWorldPosition(tile.CoordX, tile.CoordY, hx, hy, h01);
                positions[idx] = Landscape.UnityToGodot(unity);
                if (colors != null)
                    colors[idx] = TerrainColor.ForVertex(splat, hx, hy, unity.Y);
                uv2[idx] = new Vector2(hx * invRes, hy * invRes); // tile-normalized, for the splat control lookup
            }
        }

        // Front faces point up (verified on screen): back-face culling (#3) then drops the underside.
        //
        // A cell the map cuts away emits nothing at all. That is the whole of drawing a hole, and it only
        // works because a cell IS a quad here: at the old subsampled resolution a 4 m hole was half of an
        // 8 m quad, and half a quad cannot be left out. The vertices around it stay in the array — two of
        // them go unreferenced in the middle of a solid block of holes — because dropping them would mean
        // renumbering every index behind them to save a few dozen bytes a map.
        LandscapeHoles? holes = tile.Holes is { HasAnyHoles: true } h ? h : null;
        int cells = res - 1;
        var indices = new int[cells * cells * 6];
        int t = 0;
        for (int hx = 0; hx < cells; hx++)
        {
            for (int hy = 0; hy < cells; hy++)
            {
                if (holes != null && holes.IsHole(hx, hy))
                    continue;
                int v00 = (hx * res) + hy;
                int v01 = (hx * res) + hy + 1;
                int v10 = ((hx + 1) * res) + hy;
                int v11 = ((hx + 1) * res) + hy + 1;
                indices[t++] = v00;
                indices[t++] = v10;
                indices[t++] = v11;
                indices[t++] = v00;
                indices[t++] = v11;
                indices[t++] = v01;
            }
        }
        if (t != indices.Length)
            System.Array.Resize(ref indices, t);

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
        if (holes != null)
            importer = KeepLodsThatKeepHolesOpen(importer, holes, tile.CoordX, tile.CoordY);

        // Carry the heightmap (row-major, normalized) so collision is built from the source grid rather
        // than read back off the mesh — the drawn surface is LOD'd and meshoptimizer reorders its
        // vertices, so LOD 0's geometry is not recoverable from what ends up on the GPU. The two now
        // describe the same 257x257 grid; this is how the collider gets at it.
        float[]? flat = null;
        if (tile.RawSamples == null)
        {
            flat = new float[vertexCount];
            for (int hx = 0; hx < res; hx++)
                for (int hy = 0; hy < res; hy++)
                    flat[(hx * res) + hy] = tile.HeightAt(hx, hy);
        }
        // Only the textured path builds a splat material, and only it needs to know what the tile paints.
        int[]? painted = textured && splat != null ? PaintedLayers(splat) : null;
        return new TileMesh(importer, tile.RawSamples, flat, tile.CoordX, tile.CoordY, splat, painted,
            holes);
    }

    // Rebuilds a tile's surface keeping only the generated levels that still leave its holes open.
    //
    // meshoptimizer welds an interior boundary shut once it is decimating hard enough — a hole is exactly
    // that kind of boundary, and the level where it goes depends on the tile, so it is found rather than
    // assumed. Everything up to that level is kept; from it on the tile draws the last honest level at
    // every distance instead of a coarser one that has paved over an entrance. Only tiles with holes pay
    // for this, and only in geometry at long range.
    private static ImporterMesh KeepLodsThatKeepHolesOpen(ImporterMesh generated,
        LandscapeHoles holes, int tileX, int tileY)
    {
        Vector2[] centres = TerrainHoleCollision.HoleCentres(holes, tileX, tileY);
        // The levels index the SURFACE's vertex array, so that is what they are read against rather than
        // the array this method's caller happens to still be holding.
        using var arrays = generated.GetSurfaceArrays(0);
        Vector3[] positions = arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>();

        int lodCount = generated.GetSurfaceLodCount(0);
        var kept = new Godot.Collections.Dictionary();
        for (int lod = 0; lod < lodCount; lod++)
        {
            int[] indices = generated.GetSurfaceLodIndices(0, lod);
            if (!TerrainHoleCollision.KeepsHolesOpen(positions, indices, centres))
                break;
            kept[generated.GetSurfaceLodSize(0, lod)] = indices;
        }
        if (kept.Count == lodCount)
            return generated; // every level survived: nothing to rebuild

        var trimmed = new ImporterMesh();
        trimmed.AddSurface(Mesh.PrimitiveType.Triangles, arrays,
            new Godot.Collections.Array<Godot.Collections.Array>(), kept);
        return trimmed;
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
            Holes = tm.Holes,
            TileX = tm.X,
            TileY = tm.Y,
        };
        return node;
    }

    private sealed partial class TerrainTileNode : MeshInstance3D
    {
        public ushort[]? CollisionHeights16;
        public float[]? CollisionHeights32;
        public LandscapeHoles? Holes;
        public int TileX;
        public int TileY;
    }

    // Gives a rendered terrain tile a cheap heightfield StaticBody (a 257x257 HeightMapShape3D) instead of
    // a ~131k-triangle concave trimesh, from the heightmap the tile carried in metadata (FinishTile) —
    // the same grid the mesh is drawn from, so the surface walked is the surface drawn. Verified sample by
    // sample against a built tile's own vertices by TerrainHeightfieldTests.
    //
    // A tile with holes gets a second shape beside the heightfield. See TerrainHoleCollision for why the
    // cut has to be made too wide and then repaired: Godot's heightfield can only refuse collision at a
    // SAMPLE, and a hole is a CELL.
    //
    // `navigationField`, when given, receives the same heightfield the physics server does, so navmesh
    // reconciliation can probe the ground without a physics tick. See CollisionField.
    public static void AddHeightfieldCollision(MeshInstance3D tile,
        CollisionFieldBuilder? navigationField = null)
    {
        if (tile is not TerrainTileNode terrainTile
            || (terrainTile.CollisionHeights16 == null && terrainTile.CollisionHeights32 == null))
            return;

        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        ushort[]? raw = terrainTile.CollisionHeights16;
        float[]? flat = terrainTile.CollisionHeights32;
        float[] mapData = raw != null
            ? TerrainHeightfield.MapData(raw)
            : MapDataFromFlat(flat!);
        Transform3D placement =
            TerrainHeightfield.CollisionTransform(terrainTile.TileX, terrainTile.TileY);

        var body = new StaticBody3D { Name = "TerrainCollision" };
        LandscapeHoles? holes = terrainTile.Holes;
        Vector3[]? repair = holes != null && TerrainHoleCollision.MarkNoCollisionSamples(mapData, holes)
            ? TerrainHoleCollision.RepairFaces(holes, terrainTile.TileX, terrainTile.TileY,
                (hx, hy) => raw != null
                    ? raw[(hx * res) + hy] / (float)ushort.MaxValue
                    : flat![(hx * res) + hy])
            : null;

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
        if (repair is { Length: > 0 })
        {
            // The repair faces are already in world space, like the tile mesh's own vertices, so this
            // shape takes no transform — unlike the heightfield above, which is a unit-cell grid that has
            // to be scaled and centred onto the tile.
            var patch = new ConcavePolygonShape3D();
            patch.SetFaces(repair);
            body.AddChild(new CollisionShape3D { Shape = patch, Name = "HoleRepair" });
        }
        tile.AddChild(body);
        // The navigation field is given the marked heightfield, not the raw one: a probe over a hole has
        // to find no ground, exactly as the physics server now does. It is NOT given the repair patch,
        // because CollisionField answers "I cannot tell" for any cell touching a no-collision sample and
        // sends it to the server — which has both shapes — rather than guessing at the seam between them.
        navigationField?.AddHeightfield(placement, res, res, mapData);

        // The heightfield now lives in the physics server's HeightMapShape3D; drop the ~264 KB/tile source
        // copy the tile carried in metadata (collision is built once and never rebuilt).
        terrainTile.CollisionHeights16 = null;
        terrainTile.CollisionHeights32 = null;
        terrainTile.Holes = null;
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
