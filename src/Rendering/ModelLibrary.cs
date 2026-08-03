using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Loads cached models into Godot ArrayMeshes keyed by object GUID: one surface per submesh, with the
// submesh's texture applied. Vertices convert Unity->Godot (negate Z, matching ObjectPlacement), which
// flips winding, so triangles are reversed; UV V is flipped for Godot's texture origin.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class ModelLibrary
{
    private static readonly bool DeduplicateGpu = EnvFlag.IsOn(System.Environment.GetEnvironmentVariable("UG_DEDUP_GPU"), whenUnset: true);
    // How long the realise phase works before handing the frame back. Fixed rather than derived from the
    // frame rate: a slower machine has longer frames, so this is a smaller share of one, not a larger.
    private const double RealiseSecondsPerFrame = 0.008;

    // Meshes prepared before they are realised and let go. Preparing all of them first meant every
    // ImporterMesh and every ArrayMesh built from it were alive at the same time — half a gigabyte of
    // native memory on a large map, for no gain: the prepared form is dead the moment it is realised.
    private const int PrepareChunk = 256;

    // Staged variant for interactive loads: the same two phases as Load, but the realise phase yields to
    // the render loop on a time budget so the loading screen keeps animating.
    public static async System.Threading.Tasks.Task<Dictionary<Guid, ArrayMesh>> LoadStagedAsync(
        string cacheDir, TextureRegistry registry, Node yieldOn, IReadOnlySet<Guid>? only = null,
        string suffix = ".mesh")
    {
        var library = new Dictionary<Guid, ArrayMesh>();
        if (!Directory.Exists(cacheDir))
            return library;

        var sources = new List<(Guid Item, string Path)>(CachedMeshPaths(cacheDir, only, suffix));
        IReadOnlyList<ExactFileGroups.Group<Guid>> groups = ExactFileGroups.Build(sources, DeduplicateGpu);

        // Yield on a time budget rather than every N meshes: a fixed count either stalls the frame on
        // heavy meshes or, far worse here, spends most of the phase waiting for frames it did not need —
        // a thousand-mesh map was paying fifty frame waits to realise work worth a handful.
        var materials = new Dictionary<(string, Color, UnityMaterial.Blend, float, float, EShaderCull), Material>();
        long budget = (long)(System.Diagnostics.Stopwatch.Frequency * RealiseSecondsPerFrame);
        long until = System.Diagnostics.Stopwatch.GetTimestamp() + budget;

        for (int start = 0; start < groups.Count; start += PrepareChunk)
        {
            int count = Math.Min(PrepareChunk, groups.Count - start);
            PreparedMesh[] prepared = await System.Threading.Tasks.Task.Run(
                () => PrepareRange(groups, start, count));

            for (int i = 0; i < prepared.Length; i++)
            {
                PreparedMesh mesh = prepared[i];
                if (mesh.Importer != null)
                {
                    ArrayMesh realised = Realise(mesh, registry, materials);
                    foreach (Guid guid in groups[start + i].Items)
                        library[guid] = realised;
                }

                if (System.Diagnostics.Stopwatch.GetTimestamp() >= until)
                {
                    await yieldOn.ToSignal(yieldOn.GetTree(), SceneTree.SignalName.ProcessFrame);
                    until = System.Diagnostics.Stopwatch.GetTimestamp() + budget;
                }
            }
        }
        Log.Print($"[stream] materials realised: {materials.Count} resources, "
            + $"{registry.MaterialAliasCount} exact texture-key aliases");
        return library;
    }

    // Phase 1 — worker threads: read each cached mesh, convert it and generate its LOD chain. All of that
    // is pure CPU on an ImporterMesh, a data-only Resource, which is what lets the whole library be built
    // in parallel; the terrain tiles are split the same way. A map places thousands of distinct models, so
    // this was the longest single-threaded stretch of a load.
    private static PreparedMesh[] PrepareRange(IReadOnlyList<ExactFileGroups.Group<Guid>> groups,
        int start, int count)
    {
        var prepared = new PreparedMesh[count];
        System.Threading.Tasks.Parallel.For(0, count, i =>
        {
            ExactFileGroups.Group<Guid> group = groups[start + i];
            prepared[i] = Prepare(group.Items[0], group.Path, group.ContentKey);
        });
        return prepared;
    }

    // A mesh converted and LOD-ed but not yet realised: Importer is null when the cache entry held nothing
    // renderable (no vertices, or every submesh degenerate).
    private readonly record struct PreparedMesh(Guid Guid, string ContentKey, ImporterMesh? Importer,
        List<CachedSubmesh> Surfaces);

    private static PreparedMesh Prepare(Guid guid, string path, string contentKey)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return new PreparedMesh(guid, "", null, new List<CachedSubmesh>());
        }

        if (!MeshCache.IsCurrent(data)) // stale format; the extraction pass rewrites it
            return new PreparedMesh(guid, "", null, new List<CachedSubmesh>());

        var (verts, normals, uvs, submeshes) = MeshCache.Read(data);
        return Build(guid, contentKey, verts, normals, uvs, submeshes);
    }

    // Phase 2 — main thread: GetMesh creates the RenderingServer resource, and each surface takes the
    // deduplicated material for its texture key (materials are GPU resources too).
    private static ArrayMesh Realise(in PreparedMesh prepared, TextureRegistry registry,
        Dictionary<(string, Color, UnityMaterial.Blend, float, float, EShaderCull), Material> materials)
    {
        ArrayMesh mesh = prepared.Importer!.GetMesh();
        int surfaces = mesh.GetSurfaceCount();
        for (int i = 0; i < surfaces && i < prepared.Surfaces.Count; i++)
            mesh.SurfaceSetMaterial(i, MaterialFor(prepared.Surfaces[i], registry, materials));
        return mesh;
    }

    // The cached meshes to realise, as (guid, file bytes). With `only` given — the GUIDs the map actually
    // places — the cache is addressed by name instead of scanned: a shared cache holding every map ever
    // opened then costs nothing extra, where scanning it built (and kept) hundreds of ArrayMeshes, their
    // materials and their texture registrations for objects this map never places.
    private static IEnumerable<(Guid Guid, string Path)> CachedMeshPaths(string cacheDir,
        IReadOnlySet<Guid>? only, string suffix = ".mesh")
    {
        if (only != null)
        {
            foreach (Guid guid in only)
            {
                if (guid == Guid.Empty)
                    continue;
                string path = Path.Combine(cacheDir, guid.ToString("N") + suffix);
                if (File.Exists(path))
                    yield return (guid, path);
            }
            yield break;
        }

        foreach (string path in Directory.GetFiles(cacheDir, "*" + suffix))
        {
            string name = Path.GetFileName(path);
            if (Guid.TryParseExact(name[..^suffix.Length], "N", out Guid guid))
                yield return (guid, path);
        }
    }

    // Builds the meshes and their materials, registering each textured submesh's material under its
    // texture key so the caller can apply textures later (registry.ApplyAllAvailable for a warm cache, or
    // progressively as ExtractTextures streams them in on a cold load).
    public static Dictionary<Guid, ArrayMesh> Load(string cacheDir, TextureRegistry registry,
        IReadOnlySet<Guid>? only = null, string suffix = ".mesh")
    {
        var library = new Dictionary<Guid, ArrayMesh>();
        if (!Directory.Exists(cacheDir))
            return library;

        // Deduplicate materials across every mesh: submeshes sharing a texture key, color and blend share
        // one StandardMaterial3D (fewer material objects + GPU parameter buffers, fewer render-state
        // changes). Scoped to this load so nothing leaks between calls.
        var materials = new Dictionary<(string, Color, UnityMaterial.Blend, float, float, EShaderCull), Material>();
        var sources = new List<(Guid Item, string Path)>(CachedMeshPaths(cacheDir, only, suffix));
        IReadOnlyList<ExactFileGroups.Group<Guid>> groups = ExactFileGroups.Build(sources, DeduplicateGpu);
        for (int start = 0; start < groups.Count; start += PrepareChunk)
        {
            int count = Math.Min(PrepareChunk, groups.Count - start);
            PreparedMesh[] prepared = PrepareRange(groups, start, count);
            for (int i = 0; i < prepared.Length; i++)
            {
                PreparedMesh mesh = prepared[i];
                if (mesh.Importer != null)
                {
                    ArrayMesh realised = Realise(mesh, registry, materials);
                    foreach (Guid guid in groups[start + i].Items)
                        library[guid] = realised;
                }
            }
        }

        return library;
    }

    private static PreparedMesh Build(Guid guid, string contentKey, Vector3[] verts, Vector3[] normals, Vector2[] uvs,
        List<CachedSubmesh> submeshes)
    {
        var empty = new List<CachedSubmesh>();
        if (verts.Length == 0 || submeshes.Count == 0)
            return new PreparedMesh(guid, contentKey, null, empty);

        var gverts = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++)
            gverts[i] = Landscape.UnityToGodot(verts[i]);

        var guvs = new Vector2[uvs.Length];
        for (int i = 0; i < uvs.Length; i++)
            guvs[i] = new Vector2(uvs[i].X, 1f - uvs[i].Y); // Godot's texture origin is top-left

        // Winding is KEPT: Unity fronts are clockwise, and reflecting Z while keeping the index order
        // leaves them clockwise for Godot's clockwise front faces. (Reversing here put every face's front
        // on the inside; with cull disabled the two-sided shading then flipped the correct authored
        // normals, which is why the city streets read permanently in shade.)
        var reversed = new int[submeshes.Count][];
        for (int s = 0; s < submeshes.Count; s++)
            reversed[s] = submeshes[s].Indices;

        // Prefer the mesh's authored normals — Unturned's own hard and soft edges (a stop sign's face stays
        // dead flat; deriving smooth normals there bends the face's shading into its bevel). Authored normals
        // map by the plain reflection (x,y,-z): both the scene and the sun are mirrored by the same Z flip,
        // so this preserves every dot(N, L) — Unity's lighting exactly. That also holds for foliage-style
        // normals that are deliberately decoupled from the geometry (grass cards author up-bent normals to
        // light like the terrain; the cofactor map (-x,-y,z) would point them at the ground and shade half
        // the blades dark). Meshes that ship without normals keep the derived smooth ones.
        Vector3[] gnormals;
        if (normals.Length == verts.Length)
        {
            gnormals = new Vector3[normals.Length];
            for (int i = 0; i < normals.Length; i++)
                gnormals[i] = new Vector3(normals[i].X, normals[i].Y, -normals[i].Z);
        }
        else
        {
            gnormals = SmoothNormals(gverts, reversed);
        }

        // Merge submeshes that resolve to the same complete material (TextureKey/Color/Blend/Metallic/
        // Smoothness/Cull) into one
        // surface with concatenated indices: a mesh's several same-material submeshes then cost one
        // instanced draw call per MultiMesh instead of several. Output is pixel-identical — the vertex/
        // normal/uv pool and the material are the same, only the surface count changes.
        bool hasUv = guvs.Length == gverts.Length;
        var groups = new List<(CachedSubmesh rep, List<int> indices)>();
        var groupByKey = new Dictionary<(string, Color, UnityMaterial.Blend, float, float, EShaderCull), int>();
        for (int s = 0; s < submeshes.Count; s++)
        {
            int[] idx = reversed[s];
            if (idx.Length < 3)
                continue;
            CachedSubmesh sm = submeshes[s];
            var key = (sm.TextureKey, sm.Color, sm.Blend, sm.Metallic, sm.Smoothness, sm.Cull);
            if (!groupByKey.TryGetValue(key, out int gi))
            {
                gi = groups.Count;
                groupByKey[key] = gi;
                groups.Add((sm, new List<int>()));
            }
            groups[gi].indices.AddRange(idx);
        }

        // Built through an ImporterMesh so meshoptimizer can generate the LOD chain, exactly as the
        // terrain tiles do. Objects are the bulk of the frame's geometry — 17.2M primitives per frame on
        // California2 — and without LODs every distant tree and house is submitted at full density.
        var importer = new ImporterMesh();
        var surfaces = new List<CachedSubmesh>(groups.Count);
        foreach ((CachedSubmesh rep, List<int> indices) in groups)
        {
            using var arrays = new Godot.Collections.Array(); // freed each iteration (data copied into the mesh)
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = gverts;
            arrays[(int)Mesh.ArrayType.Normal] = gnormals;
            if (hasUv)
                arrays[(int)Mesh.ArrayType.TexUV] = guvs;
            arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

            // No material yet: they are GPU resources, so Realise attaches them on the main thread.
            importer.AddSurface(Mesh.PrimitiveType.Triangles, arrays);
            surfaces.Add(rep);
        }
        if (surfaces.Count == 0)
            return new PreparedMesh(guid, contentKey, null, empty);

        // Same angle thresholds the terrain uses: meshoptimizer collapses what it can without folding
        // hard edges, and Godot picks the level from screen coverage at draw time.
        importer.GenerateLods(25f, 60f, new Godot.Collections.Array());
        return new PreparedMesh(guid, contentKey, importer, surfaces);
    }

    // Area-weighted smooth normals over all submeshes (CCW front faces, so the cross product points out).
    private static Vector3[] SmoothNormals(Vector3[] verts, int[][] submeshIndices)
    {
        var normals = new Vector3[verts.Length];
        foreach (int[] indices in submeshIndices)
        {
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = indices[i], b = indices[i + 1], c = indices[i + 2];
                Vector3 face = (verts[c] - verts[a]).Cross(verts[b] - verts[a]);
                normals[a] += face;
                normals[b] += face;
                normals[c] += face;
            }
        }
        for (int i = 0; i < normals.Length; i++)
            normals[i] = normals[i].LengthSquared() > 0f ? normals[i].Normalized() : Vector3.Up;
        return normals;
    }


    // Flat material tinted with the palette color; glass/blended submeshes get alpha transparency. A
    // textured submesh's material starts untextured and is registered under its texture key — the texture
    // is applied later (immediately for a warm cache, progressively while a cold load streams).
    // Cutout surfaces (grass cards, tree leaves, garlands) render with Unturned's own foliage-family
    // shaders: alpha-clipped, specular off, and — crucially — lit with the authored normal on BOTH sides.
    // Godot's built-in materials negate the normal on backfaces of two-sided geometry, which turns the
    // deliberately up-bent foliage normals downward on every card's reverse side (dark inner blades). This
    // spatial shader undoes that flip, matching Unity's surface-shader behavior. Two variants only differ
    // in the sampler filter, mirroring the texture's own Unity filter mode.
    private static Shader CutoutShader(string filterHint, string cullMode) => new()
    {
        Code = $$"""
        shader_type spatial;
        render_mode {{cullMode}}, specular_disabled;
        uniform vec4 tint : source_color = vec4(1.0);
        uniform sampler2D albedo_texture : source_color, {{filterHint}};
        uniform bool has_texture = false;
        void fragment() {
            vec4 c = (has_texture ? texture(albedo_texture, UV) : vec4(1.0)) * tint;
            ALBEDO = c.rgb;
            ALPHA = c.a;
            ALPHA_SCISSOR_THRESHOLD = 0.5;
            ROUGHNESS = 1.0;
            if (!FRONT_FACING) {
                NORMAL = -NORMAL;
            }
        }
        """,
    };

    // One shader per (filter, cull) combination, lazily built: the filter mirrors the texture's Unity
    // filter mode and the cull is the material's shader-authored culling (EShaderCull).
    private static readonly Dictionary<(bool Nearest, EShaderCull Cull), Shader> CutoutShaders = new();

    internal static Shader CutoutVariant(bool nearest, EShaderCull cull)
    {
        var key = (nearest, cull);
        if (CutoutShaders.TryGetValue(key, out Shader? shader))
            return shader;
        string filter = nearest ? "filter_nearest_mipmap" : "filter_linear_mipmap_anisotropic";
        string mode = cull switch
        {
            EShaderCull.TwoSided => "cull_disabled",
            EShaderCull.Front => "cull_front",
            _ => "cull_back",
        };
        shader = CutoutShader(filter, mode);
        CutoutShaders[key] = shader;
        return shader;
    }

    // The nearest-filter twin of a cutout material's current shader, keeping its culling (used when the
    // streamed texture turns out to be point-filtered).
    internal static Shader CutoutAsNearest(Shader current)
    {
        foreach (((bool nearest, EShaderCull cull), Shader shader) in CutoutShaders)
            if (!nearest && shader == current)
                return CutoutVariant(nearest: true, cull);
        return current; // already nearest (or unknown): keep as is
    }

    private static Material MaterialFor(CachedSubmesh sm, TextureRegistry registry,
        Dictionary<(string, Color, UnityMaterial.Blend, float, float, EShaderCull), Material> cache)
    {
        string textureIdentity = registry.MaterialIdentity(sm.TextureKey);
        var key = (textureIdentity, sm.Color, sm.Blend, sm.Metallic, sm.Smoothness, sm.Cull);
        if (cache.TryGetValue(key, out Material? shared))
        {
            registry.Register(sm.TextureKey, shared);
            return shared; // already built and registered under this texture key
        }

        if (sm.Blend == UnityMaterial.Blend.Cutout)
        {
            var foliage = new ShaderMaterial { Shader = CutoutVariant(nearest: false, sm.Cull) };
            foliage.SetShaderParameter("tint", sm.Color);
            registry.Register(sm.TextureKey, foliage);
            cache[key] = foliage;
            return foliage;
        }

        bool matte = sm.Metallic <= 0f && sm.Smoothness <= 0f; // the overwhelmingly common case in the data
        var material = new StandardMaterial3D
        {
            AlbedoColor = sm.Color,
            // The Unity Standard values straight from the material data: most objects are fully matte
            // (_Metallic/_Glossiness 0); metal/gloss props (signs, vehicles, roofs) keep their response.
            Metallic = sm.Metallic,
            Roughness = 1f - sm.Smoothness,
            // Fully-rough matte dielectric: the GGX specular term is not visible, so skip its per-fragment
            // ALU; anything with real gloss or metal keeps the specular path.
            SpecularMode = matte
                ? BaseMaterial3D.SpecularModeEnum.Disabled
                : BaseMaterial3D.SpecularModeEnum.SchlickGgx,
            // The shader-authored culling, straight from the bundle data: the Standard family (nearly
            // every prop) back-face culls exactly like the game; only foliage/card/flag shaders author
            // Cull Off, and decal projectors author Cull Front.
            CullMode = sm.Cull switch
            {
                EShaderCull.TwoSided => BaseMaterial3D.CullModeEnum.Disabled,
                EShaderCull.Front => BaseMaterial3D.CullModeEnum.Front,
                _ => BaseMaterial3D.CullModeEnum.Back,
            },
            // Walls/roofs/roads seen at grazing angles blur into their mips without anisotropy.
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        registry.Register(sm.TextureKey, material);
        if (sm.Blend == UnityMaterial.Blend.Alpha)
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; // blend (glass)
        cache[key] = material;
        return material;
    }

    internal static ImageTexture? BuildTexture(CachedTexture entry)
    {
        // Cache entries written before the Crunch decoder existed still hold the container; unwrapping
        // here means they show up on the next load instead of waiting for the cache to be rebuilt.
        CachedTexture cached = CachedTexture.Decoded(entry);
        byte[] pixels = cached.Pixels;
        Image.Format format;
        switch (cached.Format)
        {
            case 3: format = Image.Format.Rgb8; break;
            case 4: format = Image.Format.Rgba8; break;
            case 5: // ARGB32: Godot has no ARGB, so swizzle to RGBA8 (used by the terrain layer textures)
                pixels = ArgbToRgba(cached.Pixels);
                format = Image.Format.Rgba8;
                break;
            case 10: format = Image.Format.Dxt1; break;
            case 12: format = Image.Format.Dxt5; break;
            case 25: format = Image.Format.BptcRgba; break;
            default: return null; // unsupported (e.g. crunched) -> no texture
        }

        Image image = Image.CreateFromData(cached.Width, cached.Height, cached.MipCount > 1, format, pixels);
        return ImageTexture.CreateFromImage(image);
    }

    // Reorders A,R,G,B bytes to R,G,B,A across every pixel (all mip levels), for Unity's ARGB32 format.
    private static byte[] ArgbToRgba(byte[] argb)
    {
        var rgba = new byte[argb.Length];
        for (int i = 0; i + 3 < argb.Length; i += 4)
        {
            rgba[i + 0] = argb[i + 1]; // R
            rgba[i + 1] = argb[i + 2]; // G
            rgba[i + 2] = argb[i + 3]; // B
            rgba[i + 3] = argb[i + 0]; // A
        }
        return rgba;
    }
}
