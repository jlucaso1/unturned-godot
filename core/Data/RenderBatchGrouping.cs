using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;

namespace UnturnedGodot.Data;

// Which mesh a batch draws, as a pair of levels. Level indices are opaque handles the caller assigns to
// the mesh resources it holds — one index per distinct resource — so identical indices mean the identical
// resource and nothing else. `NoLevel` is the absence of an authored lower level.
public readonly record struct RenderMeshKey(int Level0, int Level1)
{
    public const int NoLevel = -1;

    public static RenderMeshKey Single(int level0) => new(level0, NoLevel);
}

// One batch as the spatial partitioner emitted it: the mesh levels it draws, the placements in it, and
// the width the partition allows a batch of that group to cover — its cell size, or a non-positive value
// when the group was not partitioned at all and no width is being promised.
//
// `Mergeable` is false for a batch whose submission is load-bearing beyond the geometry in it, so it is
// submitted exactly as the partitioner emitted it. Two things disqualify a batch, both because the merge
// would change the frame rather than only how it is packed:
//
//   * an authored lower level. The switch distance a batch uses is derived from the placements inside it
//     — the largest scale among them, plus the batch's own radius — so a merged batch switches at a
//     distance neither of its halves would have used, and the half that used to switch earlier keeps
//     drawing LOD0 out to the merged range. That is different geometry, not different packing.
//   * an alpha-blended surface. Transparent geometry is depth-sorted per render object and never between
//     the instances inside one MultiMesh, so folding two transparent batches into one drops the sort
//     between them and overlapping glass can blend in a different order.
//
// `Category` is what the placements in the batch ARE — Unturned's own classification of the asset, so
// SMALL clutter, MEDIUM furniture, LARGE buildings and the RESOURCE family (trees, rocks, bushes) stay
// separable after batching. Nothing about rendering depends on it; the dev console does, because "what do
// the trees cost this frame" can only be answered by a submission that can be switched off on its own.
public readonly record struct RenderBatch(RenderMeshKey Key, float MaxExtentMetres,
    IReadOnlyList<Transform3D> Transforms, bool Mergeable = true,
    EObjectType Category = EObjectType.Unknown);

// One submitted batch: the placements of every input batch that was merged into it.
public sealed class MergedRenderGroup
{
    public RenderMeshKey Key { get; }
    public EObjectType Category { get; }
    public List<Transform3D> Transforms { get; }

    internal MergedRenderGroup(RenderMeshKey key, EObjectType category, List<Transform3D> transforms)
    {
        Key = key;
        Category = category;
        Transforms = transforms;
    }
}

// Decides which of the partitioner's batches are submitted as one.
//
// ObjectsBuilder keys placements on the asset GUID, because that is what collision, ladder volumes and
// asset policy are keyed on. The renderer does not care about the GUID: it cares about the mesh resource,
// and ModelLibrary already hands one ArrayMesh to every asset whose cached geometry is byte-identical
// (see UG_DEDUP_GPU). Two of those built two MultiMeshes over that one mesh, so any view holding both
// paid two draw calls — in the colour and shadow passes alike — for one batch of the same geometry with
// the same materials.
//
// Merging them changes nothing that is drawn: the mesh, its surfaces and their materials are the same
// resource, and every placement keeps its own world transform and is submitted exactly once. What it can
// change is what the frustum is able to reject, because two batches are two culling decisions and one
// batch is one. So the merge is deliberately not "same mesh anywhere on the map":
//
//   * the two batches' placement footprints must overlap, so neither is dragged into view by geometry
//     it does not sit among, and
//   * the merged footprint must still fit the width the partitioner promised for a batch of that group,
//     so a merge can never undo the cell size the partition chose.
//
// Merging by mesh alone was measured on PEI and rejected: it took 709 draw calls to 683 at the aerial
// poses, but it also took the ground pose from 623,238 primitives to 626,052 by joining batches that sit
// in different parts of the map. The two constraints above are what keep this a reduction in submissions
// rather than a trade against culling.
//
// The key is the pair of levels rather than the base mesh alone: two assets can share a base mesh and
// ship different authored lower levels, and then draw different geometry past their switch distance.
public static class RenderBatchGrouping
{
    private sealed class Bucket
    {
        public RenderMeshKey Key;
        public EObjectType Category;
        public float Limit;
        public float MinX, MaxX, MinZ, MaxZ;
        // The widest footprint any single batch in this bucket arrived with, per axis. The growth check
        // is against this and never against the running union: comparing against the union would let each
        // accepted merge raise the bar for the next one, so a chain of slightly shifted batches could
        // ratchet 2% at a time all the way out to the cell-size cap. The allowance is meant to be 2% of
        // an original batch, not 2% compounded per merge.
        public float WidestX, WidestZ;
        public List<Transform3D> Transforms = new();
    }

    // Batches are visited in the order given and each joins the first bucket that accepts it, so the
    // result depends only on the input — the structural gate compares these counts between runs.
    // Batches with no placements produce nothing: an empty MultiMesh is a render object that can never
    // draw anything.
    //
    // `growth` is how much wider than the wider of the two footprints their union may be, as a fraction.
    // Zero means a merge may not widen the batch at all on either axis, which is the only setting under
    // which the merged batch is culled exactly as often as the one it replaced.
    public static List<MergedRenderGroup> Merge(IReadOnlyList<RenderBatch> batches, float growth = 0f)
    {
        var buckets = new List<Bucket>(batches.Count);
        // Keyed on the category as well as the mesh, so a merge can never hand two kinds of object one
        // submission. In practice the two are the same question — a mesh belongs to the asset that ships
        // it — and this only separates the case where deduplication gave a SMALL and a MEDIUM asset one
        // byte-identical mesh resource. There the split is the correct answer rather than a cost:
        // `objects.small.enabled 0` has to be able to take the small one away without the other.
        var byKey = new Dictionary<(RenderMeshKey Key, EObjectType Category), List<Bucket>>();
        foreach (RenderBatch batch in batches)
        {
            if (batch.Transforms.Count == 0)
                continue;
            Footprint area = Footprint.Of(batch.Transforms);
            float limit = batch.MaxExtentMetres > 0f ? batch.MaxExtentMetres : float.PositiveInfinity;
            if (!batch.Mergeable)
            {
                // Submitted on its own, and offered to nobody: an ineligible batch must not collect
                // others into itself either.
                buckets.Add(new Bucket
                {
                    Key = batch.Key,
                    Category = batch.Category,
                    Limit = limit,
                    MinX = area.MinX,
                    MaxX = area.MaxX,
                    MinZ = area.MinZ,
                    MaxZ = area.MaxZ,
                    WidestX = area.MaxX - area.MinX,
                    WidestZ = area.MaxZ - area.MinZ,
                    Transforms = new List<Transform3D>(batch.Transforms),
                });
                continue;
            }
            if (!byKey.TryGetValue((batch.Key, batch.Category), out List<Bucket>? candidates))
                byKey.Add((batch.Key, batch.Category), candidates = new List<Bucket>());

            Bucket? target = null;
            foreach (Bucket candidate in candidates)
            {
                float joint = MathF.Min(candidate.Limit, limit);
                if (area.MinX > candidate.MaxX || area.MaxX < candidate.MinX
                    || area.MinZ > candidate.MaxZ || area.MaxZ < candidate.MinZ)
                    continue;  // footprints do not overlap
                float unionX = MathF.Max(candidate.MaxX, area.MaxX) - MathF.Min(candidate.MinX, area.MinX);
                float unionZ = MathF.Max(candidate.MaxZ, area.MaxZ) - MathF.Min(candidate.MinZ, area.MinZ);
                if (unionX > joint || unionZ > joint)
                    continue;  // wider than the partition promised a batch of this group would be
                float wideX = MathF.Max(candidate.WidestX, area.MaxX - area.MinX);
                float wideZ = MathF.Max(candidate.WidestZ, area.MaxZ - area.MinZ);
                if (unionX > wideX * (1f + growth) || unionZ > wideZ * (1f + growth))
                    continue;  // the merge would widen the batch, and a wider batch is culled less often
                target = candidate;
                break;
            }

            if (target == null)
            {
                target = new Bucket
                {
                    Key = batch.Key,
                    Category = batch.Category,
                    Limit = limit,
                    MinX = area.MinX,
                    MaxX = area.MaxX,
                    MinZ = area.MinZ,
                    MaxZ = area.MaxZ,
                    WidestX = area.MaxX - area.MinX,
                    WidestZ = area.MaxZ - area.MinZ,
                };
                buckets.Add(target);
                candidates.Add(target);
            }
            else
            {
                target.Limit = MathF.Min(target.Limit, limit);
                target.WidestX = MathF.Max(target.WidestX, area.MaxX - area.MinX);
                target.WidestZ = MathF.Max(target.WidestZ, area.MaxZ - area.MinZ);
                target.MinX = MathF.Min(target.MinX, area.MinX);
                target.MaxX = MathF.Max(target.MaxX, area.MaxX);
                target.MinZ = MathF.Min(target.MinZ, area.MinZ);
                target.MaxZ = MathF.Max(target.MaxZ, area.MaxZ);
            }
            target.Transforms.AddRange(batch.Transforms);
        }

        var merged = new List<MergedRenderGroup>(buckets.Count);
        foreach (Bucket bucket in buckets)
            merged.Add(new MergedRenderGroup(bucket.Key, bucket.Category, bucket.Transforms));
        return merged;
    }

    // The ground footprint of a batch's placements. Height is deliberately not part of it: the partition
    // is a 2D grid, and two batches stacked on the same ground are as co-located as batching cares about.
    private readonly record struct Footprint(float MinX, float MaxX, float MinZ, float MaxZ)
    {
        public static Footprint Of(IReadOnlyList<Transform3D> transforms)
        {
            Vector3 first = transforms[0].Origin;
            float minX = first.X, maxX = first.X, minZ = first.Z, maxZ = first.Z;
            for (int i = 1; i < transforms.Count; i++)
            {
                Vector3 origin = transforms[i].Origin;
                minX = MathF.Min(minX, origin.X);
                maxX = MathF.Max(maxX, origin.X);
                minZ = MathF.Min(minZ, origin.Z);
                maxZ = MathF.Max(maxZ, origin.Z);
            }
            return new Footprint(minX, maxX, minZ, maxZ);
        }
    }
}
