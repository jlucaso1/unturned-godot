using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;

namespace UnturnedGodot;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class ObjectsBuilder
{
    // Instances real meshes (grouped per GUID into one MultiMesh each) where available; placed objects
    // without an extracted mesh fall back to colored placeholder boxes.
    //
    // We deliberately do NOT spatially partition these MultiMeshes into a grid (issue #2). Vegetation is
    // the tempting case — the 1694 trees of a type spread ~3 km, so their single MultiMesh AABB never
    // frustum-culls and re-draws ~679k primitives (~20% of the frame, ~0.18 ms) in every view. But
    // splitting the wide types into a grid was swept empirically (Tier-2) and is a large net LOSS: the
    // oblique/overhead views that dominate frame cost see most cells, so it only multiplies draw calls
    // (507 -> 1605 at 512 m cells / 994 at 2048 m) and ~doubles frame time there, while the only view it
    // helps (near-ground "tight") was already the cheapest (~0.5 ms). Median frame time regressed +52-82%.
    // Real vegetation wins need per-instance LOD/impostors, which MultiMesh doesn't do natively.
    public static Node3D Build(IReadOnlyList<PlacedObject> objects, ObjectAssetDatabase db,
        IReadOnlyDictionary<Guid, ArrayMesh> meshLibrary, out int withMesh)
    {
        var root = new Node3D { Name = "Objects" };

        var byMesh = new Dictionary<Guid, List<Transform3D>>();
        var fallback = new List<(Transform3D transform, Color color)>();

        foreach (PlacedObject obj in objects)
        {
            Transform3D transform = ObjectPlacement.ComputeTransform(obj);
            if (obj.Guid != Guid.Empty && meshLibrary.ContainsKey(obj.Guid))
            {
                if (!byMesh.TryGetValue(obj.Guid, out List<Transform3D>? list))
                    byMesh[obj.Guid] = list = new List<Transform3D>();
                list.Add(transform);
            }
            else
            {
                fallback.Add((transform, ObjectColor.ForAsset(db.Resolve(obj.Guid, obj.Id))));
            }
        }

        withMesh = 0;
        foreach ((Guid guid, List<Transform3D> transforms) in byMesh)
        {
            // No MaterialOverride: the mesh's per-submesh surface materials carry the textures.
            root.AddChild(BuildMultiMesh(meshLibrary[guid], transforms, $"Mesh_{guid:N}"));
            withMesh += transforms.Count;
        }

        if (fallback.Count > 0)
            root.AddChild(BuildFallbackBoxes(fallback));

        return root;
    }

    private static MultiMeshInstance3D BuildMultiMesh(Mesh mesh, List<Transform3D> transforms, string name)
    {
        var multimesh = new MultiMesh
        {
            Mesh = mesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            InstanceCount = transforms.Count,
        };
        // One native buffer upload (12 floats per Transform3D: three basis rows each followed by the
        // origin component) instead of a marshaled SetInstanceTransform call per instance.
        var buffer = new float[transforms.Count * 12];
        for (int i = 0; i < transforms.Count; i++)
        {
            Transform3D t = transforms[i];
            int o = i * 12;
            buffer[o + 0] = t.Basis.X.X; buffer[o + 1] = t.Basis.Y.X; buffer[o + 2] = t.Basis.Z.X; buffer[o + 3] = t.Origin.X;
            buffer[o + 4] = t.Basis.X.Y; buffer[o + 5] = t.Basis.Y.Y; buffer[o + 6] = t.Basis.Z.Y; buffer[o + 7] = t.Origin.Y;
            buffer[o + 8] = t.Basis.X.Z; buffer[o + 9] = t.Basis.Y.Z; buffer[o + 10] = t.Basis.Z.Z; buffer[o + 11] = t.Origin.Z;
        }
        multimesh.Buffer = buffer;

        return new MultiMeshInstance3D { Multimesh = multimesh, Name = name };
    }

    private static MultiMeshInstance3D BuildFallbackBoxes(List<(Transform3D transform, Color color)> items)
    {
        var multimesh = new MultiMesh
        {
            Mesh = new BoxMesh { Size = new Vector3(2, 2, 2) },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = items.Count,
        };
        for (int i = 0; i < items.Count; i++)
        {
            multimesh.SetInstanceTransform(i, items[i].transform);
            multimesh.SetInstanceColor(i, items[i].color);
        }

        return new MultiMeshInstance3D
        {
            Multimesh = multimesh,
            Name = "ObjectPlaceholders",
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true },
        };
    }
}
