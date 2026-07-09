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
    // Counts only current-format caches: after a format bump the stale files don't count, so the cold-load
    // check re-extracts instead of loading nothing.
    public static int CachedMeshCount(string cacheDir) =>
        Directory.Exists(cacheDir)
            ? Array.FindAll(Directory.GetFiles(cacheDir, "*.mesh"), MeshCache.IsCurrent).Length
            : 0;

    // Staged variant for interactive loads: identical to Load, but yields to the render loop every
    // `batch` meshes so the loading screen keeps animating while the ~400 ArrayMeshes are realised.
    public static async System.Threading.Tasks.Task<Dictionary<Guid, ArrayMesh>> LoadStagedAsync(
        string cacheDir, TextureRegistry registry, Node yieldOn, int batch = 48)
    {
        var library = new Dictionary<Guid, ArrayMesh>();
        if (!Directory.Exists(cacheDir))
            return library;

        var materials = new Dictionary<(string, Color, UnityMaterial.Blend, float, float), Material>();
        int sinceYield = 0;
        foreach (string path in Directory.GetFiles(cacheDir, "*.mesh"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid guid))
                continue;
            if (!MeshCache.IsCurrent(path))
                continue;

            var (verts, normals, uvs, submeshes) = MeshCache.Read(File.ReadAllBytes(path));
            ArrayMesh? mesh = Build(verts, normals, uvs, submeshes, registry, materials);
            if (mesh != null)
                library[guid] = mesh;

            if (++sinceYield >= batch)
            {
                sinceYield = 0;
                await yieldOn.ToSignal(yieldOn.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }
        return library;
    }

    // Builds the meshes and their materials, registering each textured submesh's material under its
    // texture key so the caller can apply textures later (registry.ApplyAllAvailable for a warm cache, or
    // progressively as ExtractTextures streams them in on a cold load).
    public static Dictionary<Guid, ArrayMesh> Load(string cacheDir, TextureRegistry registry)
    {
        var library = new Dictionary<Guid, ArrayMesh>();
        if (!Directory.Exists(cacheDir))
            return library;

        // Deduplicate materials across every mesh: submeshes sharing a texture key, color and blend share
        // one StandardMaterial3D (fewer material objects + GPU parameter buffers, fewer render-state
        // changes). Scoped to this load so nothing leaks between calls.
        var materials = new Dictionary<(string, Color, UnityMaterial.Blend, float, float), Material>();
        foreach (string path in Directory.GetFiles(cacheDir, "*.mesh"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid guid))
                continue;
            if (!MeshCache.IsCurrent(path))
                continue; // stale format; the extraction pass rewrites it

            var (verts, normals, uvs, submeshes) = MeshCache.Read(File.ReadAllBytes(path));
            ArrayMesh? mesh = Build(verts, normals, uvs, submeshes, registry, materials);
            if (mesh != null)
                library[guid] = mesh;
        }
        return library;
    }

    private static ArrayMesh? Build(Vector3[] verts, Vector3[] normals, Vector2[] uvs,
        List<CachedSubmesh> submeshes, TextureRegistry registry,
        Dictionary<(string, Color, UnityMaterial.Blend, float, float), Material> materials)
    {
        if (verts.Length == 0 || submeshes.Count == 0)
            return null;

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

        // Merge submeshes that resolve to the same deduplicated material (TextureKey/Color/Blend) into one
        // surface with concatenated indices: a mesh's several same-material submeshes then cost one
        // instanced draw call per MultiMesh instead of several. Output is pixel-identical — the vertex/
        // normal/uv pool and the material are the same, only the surface count changes.
        bool hasUv = guvs.Length == gverts.Length;
        var groups = new List<(CachedSubmesh rep, List<int> indices)>();
        var groupByKey = new Dictionary<(string, Color, UnityMaterial.Blend), int>();
        for (int s = 0; s < submeshes.Count; s++)
        {
            int[] idx = reversed[s];
            if (idx.Length < 3)
                continue;
            CachedSubmesh sm = submeshes[s];
            var key = (sm.TextureKey, sm.Color, sm.Blend);
            if (!groupByKey.TryGetValue(key, out int gi))
            {
                gi = groups.Count;
                groupByKey[key] = gi;
                groups.Add((sm, new List<int>()));
            }
            groups[gi].indices.AddRange(idx);
        }

        var mesh = new ArrayMesh();
        int surfaces = 0;
        foreach ((CachedSubmesh rep, List<int> indices) in groups)
        {
            using var arrays = new Godot.Collections.Array(); // freed each iteration (data copied into the mesh)
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = gverts;
            arrays[(int)Mesh.ArrayType.Normal] = gnormals;
            if (hasUv)
                arrays[(int)Mesh.ArrayType.TexUV] = guvs;
            arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();

            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(surfaces, MaterialFor(rep, registry, materials));
            surfaces++;
        }
        return surfaces > 0 ? mesh : null;
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
    private static Shader CutoutShader(string filterHint) => new()
    {
        Code = $$"""
        shader_type spatial;
        render_mode cull_disabled, specular_disabled;
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

    internal static readonly Shader CutoutLinear = CutoutShader("filter_linear_mipmap_anisotropic");
    internal static readonly Shader CutoutNearest = CutoutShader("filter_nearest_mipmap");

    private static Material MaterialFor(CachedSubmesh sm, TextureRegistry registry,
        Dictionary<(string, Color, UnityMaterial.Blend, float, float), Material> cache)
    {
        var key = (sm.TextureKey, sm.Color, sm.Blend, sm.Metallic, sm.Smoothness);
        if (cache.TryGetValue(key, out Material? shared))
            return shared; // already built and registered under this texture key

        if (sm.Blend == UnityMaterial.Blend.Cutout)
        {
            var foliage = new ShaderMaterial { Shader = CutoutLinear };
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
            // Many object meshes (rocks, foliage) are single-sided shells; render both sides so they
            // don't show culling holes up close.
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            // Walls/roofs/roads seen at grazing angles blur into their mips without anisotropy.
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        registry.Register(sm.TextureKey, material);
        if (sm.Blend == UnityMaterial.Blend.Alpha)
            material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha; // blend (glass)
        cache[key] = material;
        return material;
    }

    internal static ImageTexture? BuildTexture(CachedTexture cached)
    {
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
