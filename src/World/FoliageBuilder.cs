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

    // Grass/flowers/pebbles are only meaningful up close (Unturned itself fades ground detail out at a
    // short distance). Beyond this, each 128 m chunk stops rendering, so the many chunks that carpet the
    // whole island no longer cost draw calls / primitives from elevated or distant views.
    private const float DrawDistance = 160f;
    private const float FadeMargin = 32f; // dither-fade over the last stretch so chunks don't pop

    // Takes the already-parsed foliage (the caller loads Foliage.blob once for its asset GUIDs) rather
    // than re-reading the 43 MB / 667k-instance blob from disk a second time.
    public static Node3D Build(LevelFoliage? foliage, IReadOnlyDictionary<Guid, ArrayMesh> meshLibrary)
    {
        var root = new Node3D { Name = "Foliage" };
        if (foliage == null)
            return root;

        // Group the FoliageInstances by chunk as references, not by copying their 667k transforms into new
        // Lists — the transforms stay in the parsed foliage pool and are read straight into each chunk's GPU
        // buffer below, saving a full ~32 MB transient duplication.
        var groups = new Dictionary<(int cx, int cy, Guid asset), List<FoliageInstances>>();
        foreach (FoliageTile tile in foliage.Tiles)
        {
            int cx = (int)Math.Floor(tile.X / (double)ChunkTiles);
            int cy = (int)Math.Floor(tile.Y / (double)ChunkTiles);
            foreach (FoliageInstances inst in tile.Instances)
            {
                if (!meshLibrary.ContainsKey(inst.Asset))
                    continue;
                (int, int, Guid) key = (cx, cy, inst.Asset);
                if (!groups.TryGetValue(key, out List<FoliageInstances>? list))
                    groups[key] = list = new List<FoliageInstances>();
                list.Add(inst);
            }
        }

        int total = 0;
        foreach (((int cx, int cy, Guid asset), List<FoliageInstances> instances) in groups)
        {
            int count = 0;
            foreach (FoliageInstances inst in instances)
                count += inst.Transforms.Count;

            var multimesh = new MultiMesh
            {
                Mesh = meshLibrary[asset],
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = count,
            };
            SetTransforms(multimesh, instances, count);

            root.AddChild(new MultiMeshInstance3D
            {
                Multimesh = multimesh,
                Name = $"F{cx}_{cy}_{asset:N}",
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, // foliage doesn't self-shadow
                VisibilityRangeEnd = DrawDistance,
                VisibilityRangeEndMargin = FadeMargin,
                VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Self,
            });
            total += count;
        }

        GD.Print($"[unturned-godot] Foliage: {total} instances in {groups.Count} chunks " +
            $"({foliage.AssetGuids.Count} asset types)");
        return root;
    }

    // Fills the MultiMesh's instance buffer directly from the chunk's grouped instances (12 floats per
    // Transform3D: three basis rows each followed by the origin component), reading the transforms straight
    // from the parsed foliage pool — no intermediate per-chunk Transform3D copy, no SetInstanceTransform.
    private static void SetTransforms(MultiMesh multimesh, List<FoliageInstances> instances, int count)
    {
        var buffer = new float[count * 12];
        int o = 0;
        foreach (FoliageInstances inst in instances)
        {
            IReadOnlyList<Transform3D> transforms = inst.Transforms;
            for (int i = 0; i < transforms.Count; i++)
            {
                Transform3D t = transforms[i];
                buffer[o + 0] = t.Basis.X.X; buffer[o + 1] = t.Basis.Y.X; buffer[o + 2] = t.Basis.Z.X; buffer[o + 3] = t.Origin.X;
                buffer[o + 4] = t.Basis.X.Y; buffer[o + 5] = t.Basis.Y.Y; buffer[o + 6] = t.Basis.Z.Y; buffer[o + 7] = t.Origin.Y;
                buffer[o + 8] = t.Basis.X.Z; buffer[o + 9] = t.Basis.Y.Z; buffer[o + 10] = t.Basis.Z.Z; buffer[o + 11] = t.Origin.Z;
                o += 12;
            }
        }
        multimesh.Buffer = buffer;
    }
}
