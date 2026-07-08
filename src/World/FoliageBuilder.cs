using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// Scatters the map's baked foliage (Foliage.blob): grass, flowers and pebbles instanced from real
// masterbundle meshes. Instances are grouped into chunks of foliage tiles so each MultiMesh keeps a
// compact AABB Godot frustum-culls on its own — only the foliage in view is submitted, which matters
// with PEI's ~667k instances.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class FoliageBuilder
{
    // 32 m foliage tiles grouped 4x4 into 128 m chunks: few enough MultiMeshes to build fast, small
    // enough AABBs that off-screen foliage culls.
    private const int ChunkTiles = 4;

    public static Node3D Build(string mapPath, IReadOnlyDictionary<Guid, ArrayMesh> meshLibrary)
    {
        var root = new Node3D { Name = "Foliage" };
        LevelFoliage? foliage = LevelFoliage.Load(Path.Combine(mapPath, "Foliage.blob"));
        if (foliage == null)
            return root;

        var groups = new Dictionary<(int cx, int cy, Guid asset), List<Transform3D>>();
        foreach (FoliageTile tile in foliage.Tiles)
        {
            int cx = (int)Math.Floor(tile.X / (double)ChunkTiles);
            int cy = (int)Math.Floor(tile.Y / (double)ChunkTiles);
            foreach (FoliageInstances inst in tile.Instances)
            {
                if (!meshLibrary.ContainsKey(inst.Asset))
                    continue;
                (int, int, Guid) key = (cx, cy, inst.Asset);
                if (!groups.TryGetValue(key, out List<Transform3D>? list))
                    groups[key] = list = new List<Transform3D>();
                list.AddRange(inst.Transforms);
            }
        }

        int total = 0;
        foreach (((int cx, int cy, Guid asset), List<Transform3D> transforms) in groups)
        {
            var multimesh = new MultiMesh
            {
                Mesh = meshLibrary[asset],
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = transforms.Count,
            };
            SetTransforms(multimesh, transforms);

            root.AddChild(new MultiMeshInstance3D
            {
                Multimesh = multimesh,
                Name = $"F{cx}_{cy}_{asset:N}",
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, // foliage doesn't self-shadow
            });
            total += transforms.Count;
        }

        GD.Print($"[unturned-godot] Foliage: {total} instances in {groups.Count} chunks " +
            $"({foliage.AssetGuids.Count} asset types)");
        return root;
    }

    // Fills the MultiMesh's instance buffer directly (12 floats per Transform3D: three basis rows each
    // followed by the origin component), far cheaper than a SetInstanceTransform call per instance.
    private static void SetTransforms(MultiMesh multimesh, List<Transform3D> transforms)
    {
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
    }
}
