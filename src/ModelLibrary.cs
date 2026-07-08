using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Loads cached meshes into Godot ArrayMeshes keyed by object GUID. Vertices are converted from
// Unity to Godot space (negate Z, matching ObjectPlacement), which flips winding, so triangles
// are reversed to keep faces outward.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class ModelLibrary
{
    public static int CachedMeshCount(string cacheDir) =>
        Directory.Exists(cacheDir) ? Directory.GetFiles(cacheDir, "*.mesh").Length : 0;

    public static Dictionary<Guid, ArrayMesh> Load(string cacheDir)
    {
        var library = new Dictionary<Guid, ArrayMesh>();
        if (!Directory.Exists(cacheDir))
            return library;

        foreach (string path in Directory.GetFiles(cacheDir, "*.mesh"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid guid))
                continue;

            using var stream = File.OpenRead(path);
            (Vector3[] verts, Vector3[] normals, int[] indices) = MeshCache.Read(stream);
            ArrayMesh? mesh = Build(verts, normals, indices);
            if (mesh != null)
                library[guid] = mesh;
        }
        return library;
    }

    private static ArrayMesh? Build(Vector3[] verts, Vector3[] normals, int[] indices)
    {
        if (verts.Length == 0 || indices.Length < 3)
            return null;

        var gverts = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++)
            gverts[i] = Landscape.UnityToGodot(verts[i]);

        var gnormals = new Vector3[normals.Length];
        for (int i = 0; i < normals.Length; i++)
            gnormals[i] = Landscape.UnityToGodot(normals[i]);

        // Reverse winding to compensate for the mirrored (Z-negated) space.
        var gindices = new int[indices.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            gindices[i] = indices[i];
            gindices[i + 1] = indices[i + 2];
            gindices[i + 2] = indices[i + 1];
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = gverts;
        if (gnormals.Length == gverts.Length)
            arrays[(int)Mesh.ArrayType.Normal] = gnormals;
        arrays[(int)Mesh.ArrayType.Index] = gindices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
