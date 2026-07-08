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
    // We deliberately do NOT spatially partition these MultiMeshes into a grid (issue #2). It was swept
    // empirically with the Tier-2 benchmark and is a net loss on PEI: object types are already tightly
    // clustered, so each per-GUID MultiMesh has a compact AABB that frustum-culls well on its own — a
    // near-ground view already drops to ~16 draw calls. Grid-partitioning only fragmented draw calls
    // (full view 392 -> 700+, zoomed 216 -> 232) with a benefit only at extreme close-ups where the cost
    // was already negligible. The widest type spans just ~1.8 km, so nothing "never culls" badly enough
    // to justify it. Revisit for larger maps whose object types genuinely span the world.
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
        for (int i = 0; i < transforms.Count; i++)
            multimesh.SetInstanceTransform(i, transforms[i]);

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
