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
        var materials = new Dictionary<(string, Color, UnityMaterial.Blend, float, float), StandardMaterial3D>();
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
        Dictionary<(string, Color, UnityMaterial.Blend, float, float), StandardMaterial3D> materials)
    {
        if (verts.Length == 0 || submeshes.Count == 0)
            return null;

        var gverts = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++)
            gverts[i] = Landscape.UnityToGodot(verts[i]);

        var guvs = new Vector2[uvs.Length];
        for (int i = 0; i < uvs.Length; i++)
            guvs[i] = new Vector2(uvs[i].X, 1f - uvs[i].Y); // Godot's texture origin is top-left

        var reversed = new int[submeshes.Count][];
        for (int s = 0; s < submeshes.Count; s++)
            reversed[s] = ReverseWinding(submeshes[s].Indices);

        // Prefer the mesh's authored normals — Unturned's own hard and soft edges (a stop sign's face stays
        // dead flat; deriving smooth normals there bends the face's shading into its bevel). Under this
        // pipeline's convention (Z-negated vertices + reversed winding) the normal maps by the reflection's
        // cofactor, (x,y,z) -> (-x,-y,z): verified against the derived geometric normals over the real
        // masterbundle meshes (99.5% agreement; the plain (x,y,-z) reflection agrees with 0.4%). Meshes that
        // ship without normals keep the derived smooth ones.
        Vector3[] gnormals;
        if (normals.Length == verts.Length)
        {
            gnormals = new Vector3[normals.Length];
            for (int i = 0; i < normals.Length; i++)
                gnormals[i] = new Vector3(-normals[i].X, -normals[i].Y, normals[i].Z);
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

    private static int[] ReverseWinding(int[] indices)
    {
        var r = new int[indices.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            r[i] = indices[i];
            r[i + 1] = indices[i + 2];
            r[i + 2] = indices[i + 1];
        }
        return r;
    }

    // Flat material tinted with the palette color; glass/blended submeshes get alpha transparency. A
    // textured submesh's material starts untextured and is registered under its texture key — the texture
    // is applied later (immediately for a warm cache, progressively while a cold load streams).
    private static StandardMaterial3D MaterialFor(CachedSubmesh sm, TextureRegistry registry,
        Dictionary<(string, Color, UnityMaterial.Blend, float, float), StandardMaterial3D> cache)
    {
        var key = (sm.TextureKey, sm.Color, sm.Blend, sm.Metallic, sm.Smoothness);
        if (cache.TryGetValue(key, out StandardMaterial3D? shared))
            return shared; // already built and registered under this texture key

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
        material.Transparency = sm.Blend switch
        {
            UnityMaterial.Blend.Cutout => BaseMaterial3D.TransparencyEnum.AlphaScissor, // alpha clip (garlands, foliage)
            UnityMaterial.Blend.Alpha => BaseMaterial3D.TransparencyEnum.Alpha,          // blend (glass)
            _ => BaseMaterial3D.TransparencyEnum.Disabled,
        };
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
