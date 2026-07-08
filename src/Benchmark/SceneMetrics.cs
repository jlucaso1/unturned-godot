using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Benchmark;

// Raw structural counts collected by walking a built scene — no rendering required, so it runs under
// --headless and is fully deterministic. Geometry is counted once per unique Mesh resource (upload/VRAM
// cost), while MultiMesh instance totals are tracked separately (draw scale). This is where the
// terrain-indexing win (#1) and the giant-MultiMesh-AABB problem (#2) become visible without a GPU.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class SceneMetricsResult
{
    public int Nodes;
    public int MeshInstances;
    public int MultiMeshInstances;
    public long MultiMeshTotalInstances;
    public long UploadedVertices;
    public long UploadedIndices;
    public long UploadedTriangles;
    public int UniqueMeshes;
    public int UniqueMaterials;
    public double MaxMultiMeshSpread;
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class SceneMetrics
{
    public static SceneMetricsResult Collect(IEnumerable<Node> roots)
    {
        var meshes = new HashSet<ulong>();
        var materials = new HashSet<ulong>();
        var r = new SceneMetricsResult();
        foreach (Node root in roots)
            Walk(root, r, meshes, materials);
        r.UniqueMeshes = meshes.Count;
        r.UniqueMaterials = materials.Count;
        return r;
    }

    private static void Walk(Node node, SceneMetricsResult r, HashSet<ulong> meshes, HashSet<ulong> materials)
    {
        r.Nodes++;
        switch (node)
        {
            case MultiMeshInstance3D { Multimesh: { } mm } mmi:
                r.MultiMeshInstances++;
                r.MultiMeshTotalInstances += mm.InstanceCount;
                AccountMesh(mm.Mesh, r, meshes);
                AccountMultiMeshMaterials(mmi, mm, materials);
                double spread = InstanceOriginSpread(mm);
                if (spread > r.MaxMultiMeshSpread)
                    r.MaxMultiMeshSpread = spread;
                break;
            case MeshInstance3D { Mesh: { } mesh } mi:
                r.MeshInstances++;
                AccountMesh(mesh, r, meshes);
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                    AccountMaterial(mi.GetActiveMaterial(s), materials);
                break;
        }

        foreach (Node child in node.GetChildren())
            Walk(child, r, meshes, materials);
    }

    // Geometry is charged once per unique mesh resource: 16 distinct terrain tiles all count, but the one
    // mesh shared by a MultiMesh's thousands of instances counts a single time (it is uploaded once).
    private static void AccountMesh(Mesh? mesh, SceneMetricsResult r, HashSet<ulong> meshes)
    {
        if (mesh is null || !meshes.Add(mesh.GetInstanceId()))
            return;

        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            long verts;
            long indices;
            if (mesh is ArrayMesh am)
            {
                verts = am.SurfaceGetArrayLen(s);
                indices = am.SurfaceGetArrayIndexLen(s);
            }
            else
            {
                Godot.Collections.Array arrays = mesh.SurfaceGetArrays(s);
                verts = arrays[(int)Mesh.ArrayType.Vertex].As<Vector3[]>()?.Length ?? 0;
                indices = arrays[(int)Mesh.ArrayType.Index].As<int[]>()?.Length ?? 0;
            }

            r.UploadedVertices += verts;
            r.UploadedIndices += indices;
            r.UploadedTriangles += (indices > 0 ? indices : verts) / 3;
        }
    }

    private static void AccountMultiMeshMaterials(MultiMeshInstance3D mmi, MultiMesh mm, HashSet<ulong> materials)
    {
        if (mmi.MaterialOverride is not null)
        {
            AccountMaterial(mmi.MaterialOverride, materials);
            return;
        }
        if (mm.Mesh is { } mesh)
            for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                AccountMaterial(mesh.SurfaceGetMaterial(s), materials);
    }

    private static void AccountMaterial(Material? material, HashSet<ulong> materials)
    {
        if (material is not null)
            materials.Add(material.GetInstanceId());
    }

    // Largest world-space extent spanned by a MultiMesh's instance origins. A MultiMesh is culled as a
    // single unit, so a large spread (relative to the map) means it can never be frustum-culled — the
    // signal behind fix #2. Computed from transforms directly (deterministic, no rendering server).
    private static double InstanceOriginSpread(MultiMesh mm)
    {
        int count = mm.InstanceCount;
        if (count == 0)
            return 0;

        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < count; i++)
        {
            Vector3 p = mm.GetInstanceTransform(i).Origin;
            min = new Vector3(Mathf.Min(min.X, p.X), Mathf.Min(min.Y, p.Y), Mathf.Min(min.Z, p.Z));
            max = new Vector3(Mathf.Max(max.X, p.X), Mathf.Max(max.Y, p.Y), Mathf.Max(max.Z, p.Z));
        }

        Vector3 size = max - min;
        return Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
    }
}
