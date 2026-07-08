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
    public static int CachedMeshCount(string cacheDir) =>
        Directory.Exists(cacheDir) ? Directory.GetFiles(cacheDir, "*.mesh").Length : 0;

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
        var materials = new Dictionary<(string, Color, UnityMaterial.Blend), StandardMaterial3D>();
        foreach (string path in Directory.GetFiles(cacheDir, "*.mesh"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid guid))
                continue;

            using var stream = File.OpenRead(path);
            var (verts, normals, uvs, submeshes) = MeshCache.Read(stream);
            ArrayMesh? mesh = Build(verts, normals, uvs, submeshes, registry, materials);
            if (mesh != null)
                library[guid] = mesh;
        }
        return library;
    }

    private static ArrayMesh? Build(Vector3[] verts, Vector3[] normals, Vector2[] uvs,
        List<CachedSubmesh> submeshes, TextureRegistry registry,
        Dictionary<(string, Color, UnityMaterial.Blend), StandardMaterial3D> materials)
    {
        if (verts.Length == 0 || submeshes.Count == 0)
            return null;

        var gverts = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++)
            gverts[i] = Landscape.UnityToGodot(verts[i]);

        var guvs = new Vector2[uvs.Length];
        for (int i = 0; i < uvs.Length; i++)
            guvs[i] = new Vector2(uvs[i].X, 1f - uvs[i].Y); // Godot's texture origin is top-left

        // Reverse winding first (the Unity->Godot Z flip mirrors the geometry), then derive normals from
        // the flipped triangles. Reflecting Unity's authored normals instead points them inward for faces
        // aligned with the Z axis — which is why flat, laid-down objects like roads rendered unlit.
        var reversed = new int[submeshes.Count][];
        for (int s = 0; s < submeshes.Count; s++)
            reversed[s] = ReverseWinding(submeshes[s].Indices);
        Vector3[] gnormals = SmoothNormals(gverts, reversed);

        var mesh = new ArrayMesh();
        int surfaces = 0;
        for (int s = 0; s < submeshes.Count; s++)
        {
            if (submeshes[s].Indices.Length < 3)
                continue;

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = gverts;
            arrays[(int)Mesh.ArrayType.Normal] = gnormals;
            if (guvs.Length == gverts.Length)
                arrays[(int)Mesh.ArrayType.TexUV] = guvs;
            arrays[(int)Mesh.ArrayType.Index] = reversed[s];

            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(surfaces, MaterialFor(submeshes[s], registry, materials));
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
        Dictionary<(string, Color, UnityMaterial.Blend), StandardMaterial3D> cache)
    {
        var key = (sm.TextureKey, sm.Color, sm.Blend);
        if (cache.TryGetValue(key, out StandardMaterial3D? shared))
            return shared; // already built and registered under this texture key

        var material = new StandardMaterial3D
        {
            AlbedoColor = sm.Color,
            Roughness = 1f,
            // Fully-rough dielectric: the GGX specular term is not visible, so skip its per-fragment ALU.
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
            // Many object meshes (rocks, foliage) are single-sided shells; render both sides so they
            // don't show culling holes up close.
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
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
