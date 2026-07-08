using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Unity;

// The single Unity->Godot mesh translation. Unity is left-handed (+Z forward); Godot is right-handed, so
// world space is the reflection F = diag(1, 1, -1) (negate Z — exactly what Landscape.UnityToGodot does to
// positions). Under a reflection:
//   - positions map by F      -> negate Z,
//   - normals map by (F^-1)^T = F (F is symmetric orthogonal) -> negate Z, same as positions,
//   - a reflection flips triangle orientation, so the winding must be reversed to stay front-facing.
// Translating the authored normals (rather than re-deriving smooth ones) preserves the mesh's hard edges
// and can never disagree with the geometry. Every skinned/static mesh built from Unity data should go
// through here so this class of normal/winding bug cannot recur per-feature.
public static class UnityMeshConverter
{
    public readonly struct GodotMesh
    {
        public readonly Vector3[] Vertices;
        public readonly Vector3[] Normals; // empty when the source has none
        public readonly Vector2[] Uvs;
        public readonly int[] Indices;      // all submeshes flattened, winding reversed

        public GodotMesh(Vector3[] vertices, Vector3[] normals, Vector2[] uvs, int[] indices)
        {
            Vertices = vertices;
            Normals = normals;
            Uvs = uvs;
            Indices = indices;
        }
    }

    public static GodotMesh ToGodot(UnityMesh mesh)
    {
        int n = mesh.Vertices.Length;

        var verts = new Vector3[n];
        for (int i = 0; i < n; i++)
            verts[i] = ReflectZ(mesh.Vertices[i]);

        // Translate the authored normals by the same reflection; re-deriving them would smooth hard edges
        // and risk sign mismatches. Empty stays empty (caller may derive its own).
        var normals = new Vector3[mesh.Normals.Length == n ? n : 0];
        for (int i = 0; i < normals.Length; i++)
            normals[i] = ReflectZ(mesh.Normals[i]);

        var uvs = new Vector2[n];
        for (int i = 0; i < n; i++)
            uvs[i] = i < mesh.Uvs.Length ? new Vector2(mesh.Uvs[i].X, 1f - mesh.Uvs[i].Y) : Vector2.Zero; // Godot V origin is top-left

        var indices = new List<int>();
        foreach (int[] submesh in mesh.Submeshes)
            for (int i = 0; i + 2 < submesh.Length; i += 3)
            {
                indices.Add(submesh[i]);
                indices.Add(submesh[i + 2]); // reversed: the reflection flips orientation
                indices.Add(submesh[i + 1]);
            }

        return new GodotMesh(verts, normals, uvs, indices.ToArray());
    }

    // The normal transform under F: for a reflection, normals use (F^-1)^T = F, so negate Z — identical to
    // the position map. This is the geometric face normal of the reversed-winding triangle, so the normal
    // and the winding always agree.
    public static Vector3 ReflectZ(Vector3 v) => new(v.X, v.Y, -v.Z);
}
