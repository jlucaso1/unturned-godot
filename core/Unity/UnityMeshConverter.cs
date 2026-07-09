using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Unity;

// The single Unity->Godot mesh translation. Unity is left-handed (+Z forward); Godot is right-handed, so
// world space is the reflection F = diag(1, 1, -1) (negate Z — exactly what Landscape.UnityToGodot does to
// positions). Under a reflection:
//   - positions map by F -> negate Z;
//   - normals map by the cofactor det(F)*(F^-1)^T = -F -> negate X and Y (KEEP Z). This -F, not F, is the
//     subtlety: a plain reflection points the translated normal inward (the det(F) = -1 sign is what makes
//     it outward again). Godot lights by this explicit normal.
//   - Godot's front face is counter-clockwise, and the reflection already turns Unity's clockwise winding
//     counter-clockwise, so the winding is kept as-is; the -F normal then agrees with it, so cull_back
//     shows the outward faces lit correctly.
// Translating the authored normals (rather than re-deriving smooth ones) preserves the mesh's hard edges.
// Every skinned/static mesh built from Unity data should go through here so this class of normal/winding
// bug cannot recur per-feature.
public static class UnityMeshConverter
{
    public readonly struct GodotMesh
    {
        public readonly Vector3[] Vertices;
        public readonly Vector3[] Normals; // empty when the source has none
        public readonly Vector2[] Uvs;
        public readonly int[] Indices;      // all submeshes flattened

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
            verts[i] = ReflectPosition(mesh.Vertices[i]);

        // Translate the authored normals by F itself (negate Z, like a position); re-deriving them would
        // smooth hard edges. F — not the cofactor -F — is the only choice that composes with converted
        // transforms: rotations convert as R' = F*R*F, so a normal lands at R'*(F*n) = F*(R*n), the mirror
        // of where Unity puts it. Empty stays empty (caller may derive its own).
        var normals = new Vector3[mesh.Normals.Length == n ? n : 0];
        for (int i = 0; i < normals.Length; i++)
            normals[i] = ReflectNormal(mesh.Normals[i]);

        var uvs = new Vector2[n];
        for (int i = 0; i < n; i++)
            uvs[i] = i < mesh.Uvs.Length ? new Vector2(mesh.Uvs[i].X, 1f - mesh.Uvs[i].Y) : Vector2.Zero; // Godot V origin is top-left

        // Winding is KEPT: Unity fronts are clockwise and so are Godot's, and the Z reflection keeps the
        // clockwise order intact for the mirrored geometry (a Godot front face's outward normal is
        // Cross(e2, e1) of its wound triangle, matching the F-translated authored normals above).
        var indices = new List<int>();
        foreach (int[] submesh in mesh.Submeshes)
            for (int i = 0; i + 2 < submesh.Length; i += 3)
            {
                indices.Add(submesh[i]);
                indices.Add(submesh[i + 1]);
                indices.Add(submesh[i + 2]);
            }

        return new GodotMesh(verts, normals, uvs, indices.ToArray());
    }

    // Position under F: negate Z.
    public static Vector3 ReflectPosition(Vector3 v) => new(v.X, v.Y, -v.Z);

    // Normal under F: negate Z, exactly like a position (F is orthogonal, so its inverse-transpose is F).
    // With the kept winding above, this is each Godot front face's outward normal, Cross(e2, e1).
    public static Vector3 ReflectNormal(Vector3 v) => new(v.X, v.Y, -v.Z);
}
