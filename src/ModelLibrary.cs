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

        foreach (string path in Directory.GetFiles(cacheDir, "*.mesh"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid guid))
                continue;

            using var stream = File.OpenRead(path);
            var (verts, normals, uvs, submeshes) = MeshCache.Read(stream);
            ArrayMesh? mesh = Build(verts, normals, uvs, submeshes, registry);
            if (mesh != null)
                library[guid] = mesh;
        }
        return library;
    }

    private static ArrayMesh? Build(Vector3[] verts, Vector3[] normals, Vector2[] uvs,
        List<CachedSubmesh> submeshes, TextureRegistry registry)
    {
        if (verts.Length == 0 || submeshes.Count == 0)
            return null;

        var gverts = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++)
            gverts[i] = Landscape.UnityToGodot(verts[i]);

        var gnormals = new Vector3[normals.Length];
        for (int i = 0; i < normals.Length; i++)
            gnormals[i] = Landscape.UnityToGodot(normals[i]);

        var guvs = new Vector2[uvs.Length];
        for (int i = 0; i < uvs.Length; i++)
            guvs[i] = new Vector2(uvs[i].X, 1f - uvs[i].Y); // Godot's texture origin is top-left

        var mesh = new ArrayMesh();
        int surfaces = 0;
        foreach (CachedSubmesh sm in submeshes)
        {
            if (sm.Indices.Length < 3)
                continue;

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = gverts;
            if (gnormals.Length == gverts.Length)
                arrays[(int)Mesh.ArrayType.Normal] = gnormals;
            if (guvs.Length == gverts.Length)
                arrays[(int)Mesh.ArrayType.TexUV] = guvs;
            arrays[(int)Mesh.ArrayType.Index] = ReverseWinding(sm.Indices);

            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(surfaces, MaterialFor(sm, registry));
            surfaces++;
        }
        return surfaces > 0 ? mesh : null;
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
    private static StandardMaterial3D MaterialFor(CachedSubmesh sm, TextureRegistry registry)
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = sm.Color,
            Roughness = 1f,
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
        return material;
    }

    internal static ImageTexture? BuildTexture(CachedTexture cached)
    {
        Image.Format format = cached.Format switch
        {
            3 => Image.Format.Rgb8,
            4 => Image.Format.Rgba8,
            10 => Image.Format.Dxt1,
            12 => Image.Format.Dxt5,
            25 => Image.Format.BptcRgba,
            _ => (Image.Format)(-1), // unsupported (e.g. crunched) -> no texture
        };
        if ((int)format < 0)
            return null;

        Image image = Image.CreateFromData(cached.Width, cached.Height, cached.MipCount > 1, format, cached.Pixels);
        return ImageTexture.CreateFromImage(image);
    }
}
