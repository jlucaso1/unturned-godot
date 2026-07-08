using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;

namespace UnturnedGodot;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class ObjectsBuilder
{
    // One MultiMesh for every placeholder; per-instance color encodes the resolved asset type.
    public static MultiMeshInstance3D Build(
        IReadOnlyList<PlacedObject> objects, ObjectAssetDatabase db, out int resolved)
    {
        var multimesh = new MultiMesh
        {
            Mesh = new BoxMesh { Size = new Vector3(2, 2, 2) },
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = objects.Count,
        };

        resolved = 0;
        for (int i = 0; i < objects.Count; i++)
        {
            PlacedObject obj = objects[i];
            ObjectAsset? asset = db.Resolve(obj.Guid, obj.Id);
            if (asset != null) resolved++;

            multimesh.SetInstanceTransform(i, ObjectPlacement.ComputeTransform(obj));
            multimesh.SetInstanceColor(i, ObjectColor.ForAsset(asset));
        }

        return new MultiMeshInstance3D
        {
            Multimesh = multimesh,
            Name = "ObjectPlaceholders",
            MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true },
        };
    }
}
