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

        // Build each chunk's packed instance buffer on worker threads — a pure Array.Copy of the parsed
        // floats into the MultiMesh buffer layout. Creating the MultiMesh, assigning its Buffer and AddChild
        // are RenderingServer ops, so they stay on this (main) thread. Arrays (not a List) hold the parallel
        // results so different indices are written without synchronization.
        var keys = new (int cx, int cy, Guid asset)[groups.Count];
        var lists = new List<FoliageInstances>[groups.Count];
        int gi = 0;
        foreach (KeyValuePair<(int cx, int cy, Guid asset), List<FoliageInstances>> kv in groups)
        {
            keys[gi] = kv.Key;
            lists[gi] = kv.Value;
            gi++;
        }
        var buffers = new float[groups.Count][];
        System.Threading.Tasks.Parallel.For(0, groups.Count, i => buffers[i] = PackBuffer(lists[i]));

        int total = 0;
        for (int i = 0; i < keys.Length; i++)
        {
            (int cx, int cy, Guid asset) = keys[i];
            float[] buffer = buffers[i];
            int count = buffer.Length / 12;
            var multimesh = new MultiMesh
            {
                Mesh = meshLibrary[asset],
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = count,
                Buffer = buffer,
            };

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

    // Concatenates a chunk's grouped instances into one MultiMesh instance buffer with a bulk Array.Copy per
    // run — FoliageInstances already hold their transforms in the exact 12-float buffer layout, so no
    // per-instance Transform3D read is needed. Pure CPU (no Godot resource touched): safe on a worker thread.
    private static float[] PackBuffer(List<FoliageInstances> instances)
    {
        int count = 0;
        foreach (FoliageInstances inst in instances)
            count += inst.Count;

        var buffer = new float[count * 12];
        int o = 0;
        foreach (FoliageInstances inst in instances)
        {
            Array.Copy(inst.Packed, 0, buffer, o, inst.Packed.Length);
            o += inst.Packed.Length;
        }
        return buffer;
    }
}
