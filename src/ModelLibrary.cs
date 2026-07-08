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

    public static Dictionary<Guid, ArrayMesh> Load(string cacheDir, string textureCacheDir)
    {
        var library = new Dictionary<Guid, ArrayMesh>();
        if (!Directory.Exists(cacheDir))
            return library;

        var textures = new Dictionary<string, ImageTexture?>();
        foreach (string path in Directory.GetFiles(cacheDir, "*.mesh"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid guid))
                continue;

            using var stream = File.OpenRead(path);
            var (verts, normals, uvs, submeshes) = MeshCache.Read(stream);
            ArrayMesh? mesh = Build(verts, normals, uvs, submeshes, textureCacheDir, textures);
            if (mesh != null)
                library[guid] = mesh;
        }
        return library;
    }

    private static ArrayMesh? Build(Vector3[] verts, Vector3[] normals, Vector2[] uvs,
        List<CachedSubmesh> submeshes, string textureCacheDir, Dictionary<string, ImageTexture?> textures)
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
            mesh.SurfaceSetMaterial(surfaces, MaterialFor(submeshes[s], textureCacheDir, textures));
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

    // Flat material tinted with the palette color; textured props also get their albedo texture, and
    // glass/blended submeshes get alpha transparency.
    private static StandardMaterial3D MaterialFor(CachedSubmesh sm, string textureCacheDir,
        Dictionary<string, ImageTexture?> textureCache)
    {
        var material = new StandardMaterial3D
        {
            AlbedoColor = sm.Color,
            Roughness = 1f,
            // Many object meshes (rocks, foliage) are single-sided shells; render both sides so they
            // don't show culling holes up close.
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        if (sm.TextureKey.Length > 0)
            material.AlbedoTexture = LoadTexture(sm.TextureKey, textureCacheDir, textureCache);
        material.Transparency = sm.Blend switch
        {
            UnityMaterial.Blend.Cutout => BaseMaterial3D.TransparencyEnum.AlphaScissor, // alpha clip (garlands, foliage)
            UnityMaterial.Blend.Alpha => BaseMaterial3D.TransparencyEnum.Alpha,          // blend (glass)
            _ => BaseMaterial3D.TransparencyEnum.Disabled,
        };
        return material;
    }

    private static ImageTexture? LoadTexture(string textureKey, string textureCacheDir,
        Dictionary<string, ImageTexture?> cache)
    {
        if (cache.TryGetValue(textureKey, out ImageTexture? tex))
            return tex;

        tex = null;
        string path = Path.Combine(textureCacheDir, textureKey + ".tex");
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            tex = BuildTexture(TextureCache.Read(stream));
        }
        cache[textureKey] = tex;
        return tex;
    }

    private static ImageTexture? BuildTexture(CachedTexture cached)
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
