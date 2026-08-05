using System.Collections.Generic;
using System.Linq;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class RenderBatchGroupingTests
{
    private const float Cell = 100f;

    private static Transform3D At(float x, float z = 0f) =>
        new(Basis.Identity, new Vector3(x, 0f, z));

    private static RenderBatch Batch(RenderMeshKey key, params Transform3D[] transforms) =>
        new(key, Cell, transforms);

    private static RenderBatch Unpartitioned(RenderMeshKey key, params Transform3D[] transforms) =>
        new(key, 0f, transforms);

    [Fact]
    public void Merge_NoBatches_SubmitsNothing() =>
        Assert.Empty(RenderBatchGrouping.Merge(new List<RenderBatch>()));

    [Fact]
    public void Merge_SingleBatch_KeepsItsKeyAndTransforms()
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[]
        {
            Batch(RenderMeshKey.Single(0), At(1f), At(2f)),
        });

        MergedRenderGroup only = Assert.Single(merged);
        Assert.Equal(RenderMeshKey.Single(0), only.Key);
        Assert.Equal(new[] { At(1f), At(2f) }, only.Transforms);
    }

    // The motivating case: two placed assets whose cached geometry was byte-identical are handed one
    // ArrayMesh by ModelLibrary, and used to build one MultiMesh each over that same mesh.
    [Fact]
    public void Merge_OverlappingBatchesOfOneMesh_BecomeOneSubmission()
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[]
        {
            Batch(RenderMeshKey.Single(7), At(0f, 0f), At(30f, 30f)),
            Batch(RenderMeshKey.Single(7), At(10f, 10f), At(20f, 5f)),
        });

        Assert.Equal(new[] { At(0f, 0f), At(30f, 30f), At(10f, 10f), At(20f, 5f) },
            Assert.Single(merged).Transforms);
    }

    // The constraint that keeps this a reduction in submissions rather than a trade against culling:
    // two batches that sit in different parts of the map stay two culling decisions.
    [Fact]
    public void Merge_BatchesWhoseFootprintsDoNotOverlap_StayApart()
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[]
        {
            Batch(RenderMeshKey.Single(0), At(0f, 0f), At(10f, 10f)),
            Batch(RenderMeshKey.Single(0), At(40f, 0f), At(50f, 10f)),
        });

        Assert.Equal(2, merged.Count);
    }

    [Theory]
    [InlineData(0f, 40f)]   // X overlaps, Z does not
    [InlineData(40f, 0f)]   // Z overlaps, X does not
    public void Merge_RequiresOverlapOnBothAxes(float offsetX, float offsetZ)
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[]
        {
            Batch(RenderMeshKey.Single(0), At(0f, 0f), At(10f, 10f)),
            Batch(RenderMeshKey.Single(0), At(offsetX, offsetZ), At(offsetX + 10f, offsetZ + 10f)),
        });

        Assert.Equal(2, merged.Count);
    }

    // Overlap alone is not enough. A merge may not produce a batch wider than the cell size the
    // partitioner chose for that group, or it would quietly undo the partition.
    [Fact]
    public void Merge_OverlappingBatchesWiderThanTheCellSize_StayApart()
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[]
        {
            Batch(RenderMeshKey.Single(0), At(0f), At(90f)),
            Batch(RenderMeshKey.Single(0), At(80f), At(160f)),   // union spans 160 m > 100 m
        });

        Assert.Equal(2, merged.Count);
    }

    // The narrower of the two promises is the one that has to hold.
    [Fact]
    public void Merge_TakesTheSmallerOfTheTwoCellSizesAsTheLimit()
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[]
        {
            new RenderBatch(RenderMeshKey.Single(0), 400f, new[] { At(0f), At(150f) }),
            new RenderBatch(RenderMeshKey.Single(0), 100f, new[] { At(140f), At(160f) }),
        });

        Assert.Equal(2, merged.Count);
    }

    // A group the partitioner left whole was promised no width, so nothing is claimed for it either:
    // overlap is then the only constraint.
    [Fact]
    public void Merge_UnpartitionedBatches_AreBoundedOnlyByOverlap()
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[]
        {
            Unpartitioned(RenderMeshKey.Single(0), At(0f), At(5_000f)),
            Unpartitioned(RenderMeshKey.Single(0), At(2_000f)),
        });

        Assert.Equal(new[] { At(0f), At(5_000f), At(2_000f) }, Assert.Single(merged).Transforms);
    }

    // A batch the caller marked ineligible — an authored lower level, whose switch distance is derived
    // from the placements in the batch, or an alpha-blended surface, whose draw order is per render
    // object — is submitted exactly as the partitioner emitted it.
    [Fact]
    public void Merge_IneligibleBatch_IsNeitherFoldedInNorFoldedInto()
    {
        var eligible = new RenderBatch(RenderMeshKey.Single(0), Cell, new[] { At(0f), At(20f) });
        var ineligible = new RenderBatch(RenderMeshKey.Single(0), Cell, new[] { At(5f) }, Mergeable: false);
        var alsoEligible = new RenderBatch(RenderMeshKey.Single(0), Cell, new[] { At(10f) });

        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(
            new[] { eligible, ineligible, alsoEligible });

        Assert.Equal(2, merged.Count);
        // The ineligible batch did not join the first bucket, and the third batch was not diverted into
        // the ineligible one either — it went to the bucket it would have joined anyway.
        Assert.Equal(new[] { At(0f), At(20f), At(10f) }, merged[0].Transforms);
        Assert.Equal(new[] { At(5f) }, merged[1].Transforms);
    }

    [Fact]
    public void Merge_TwoIneligibleBatchesOfOneMesh_StayApart()
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[]
        {
            new RenderBatch(RenderMeshKey.Single(0), Cell, new[] { At(0f) }, Mergeable: false),
            new RenderBatch(RenderMeshKey.Single(0), Cell, new[] { At(0f) }, Mergeable: false),
        });

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_DifferentBaseLevels_StayApart()
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[]
        {
            Batch(RenderMeshKey.Single(0), At(1f)),
            Batch(RenderMeshKey.Single(1), At(1f)),
        });

        Assert.Equal(new[] { RenderMeshKey.Single(0), RenderMeshKey.Single(1) },
            merged.Select(g => g.Key));
    }

    // A shared base mesh is not enough. Two assets with different authored lower levels draw different
    // geometry past their switch distance, so one batch could not represent both.
    [Fact]
    public void Merge_SharedBaseButDifferentLowerLevels_StayApart()
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[]
        {
            Batch(new RenderMeshKey(0, 1), At(1f)),
            Batch(new RenderMeshKey(0, 2), At(1f)),
            Batch(RenderMeshKey.Single(0), At(1f)),   // and no lower level at all is a third answer
        });

        Assert.Equal(3, merged.Count);
        Assert.All(merged, g => Assert.Equal(new[] { At(1f) }, g.Transforms));
    }

    [Fact]
    public void Merge_EmptyBatch_IsNotSubmitted()
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[]
        {
            Batch(RenderMeshKey.Single(0)),
            Batch(RenderMeshKey.Single(1), At(1f)),
        });

        Assert.Equal(RenderMeshKey.Single(1), Assert.Single(merged).Key);
    }

    // A merge may not widen the batch: one culling decision has to cover the same ground the two it
    // replaced did, or the draw call it saves is paid for in geometry the frustum can no longer reject.
    [Fact]
    public void Merge_OverlappingBatchesThatWouldWidenTheBatch_StayApart()
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[]
        {
            Batch(RenderMeshKey.Single(0), At(0f), At(50f)),
            Batch(RenderMeshKey.Single(0), At(40f), At(90f)),   // union spans 90 m, the wider input 50 m
        });

        Assert.Equal(2, merged.Count);
    }

    // ... and `growth` is how much of that widening is tolerated, as a fraction of the wider input.
    [Theory]
    [InlineData(0f, 2)]
    [InlineData(0.05f, 2)]
    [InlineData(0.2f, 1)]
    public void Merge_GrowthAllowanceDecidesAMarginalPair(float growth, int expected)
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(
            new[]
            {
                Batch(RenderMeshKey.Single(0), At(0f), At(50f)),
                Batch(RenderMeshKey.Single(0), At(5f), At(55f)),   // union 55 m against a wider input of 50
            },
            growth);

        Assert.Equal(expected, merged.Count);
    }

    // The allowance is 2% of an original batch, not 2% compounded per merge. Measuring each candidate
    // against the bucket's running union instead would let a chain of slightly shifted batches ratchet
    // the batch open one step at a time, all the way out to the cell size.
    [Fact]
    public void Merge_ChainOfShiftedBatches_DoesNotRatchetPastTheAllowance()
    {
        // Ten batches 100 m wide, each starting 2 m after the last. Every one overlaps the batch before
        // it and every single step is inside the 5% allowance, so a union-relative check accepts all of
        // them and walks one submission out to 118 m — nearly a fifth wider than any batch it holds.
        var batches = new List<RenderBatch>();
        for (int i = 0; i < 10; i++)
            batches.Add(new RenderBatch(RenderMeshKey.Single(0), 1_000f,
                new[] { At(i * 2f), At(i * 2f + 100f) }));

        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(batches, growth: 0.05f);

        Assert.True(merged.Count > 1, "the chain was expected to merge at least once before being cut");

        foreach (MergedRenderGroup group in merged)
        {
            float min = group.Transforms.Min(t => t.Origin.X);
            float max = group.Transforms.Max(t => t.Origin.X);
            Assert.True(max - min <= 105f, $"a submission spans {max - min:0.#} m, over the 5% allowance");
        }
        Assert.Equal(batches.Sum(b => b.Transforms.Count), merged.Sum(g => g.Transforms.Count));
    }

    // A batch joins the FIRST bucket that accepts it, so a batch that could extend either of two lands in
    // the earlier one rather than merging the pair through itself.
    [Fact]
    public void Merge_BatchAcceptedByTwoBuckets_JoinsTheFirst()
    {
        var first = new RenderBatch(RenderMeshKey.Single(0), Cell, new[] { At(0f), At(50f) });
        var second = new RenderBatch(RenderMeshKey.Single(0), Cell, new[] { At(40f), At(90f) });
        var inside = new RenderBatch(RenderMeshKey.Single(0), Cell, new[] { At(45f), At(48f) });

        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[] { first, second, inside });

        Assert.Equal(2, merged.Count);
        Assert.Equal(new[] { At(0f), At(50f), At(45f), At(48f) }, merged[0].Transforms);
        Assert.Equal(new[] { At(40f), At(90f) }, merged[1].Transforms);
    }

    // The conservation property the whole change rests on: batching may only decide which submission
    // carries a placement, never whether it is carried, how often, or with what transform.
    [Fact]
    public void Merge_KeepsEveryPlacementExactlyOnceAndUnchanged()
    {
        var batches = new List<RenderBatch>();
        var expected = new List<Transform3D>();
        for (int batch = 0; batch < 24; batch++)
        {
            var transforms = new List<Transform3D>();
            // Footprints 40 m across, stepping 20 m at a time within a key, so some pairs overlap, some
            // sit clear of each other, and the 100 m cell size stops a chain of overlaps running away.
            float x0 = batch / 2 % 6 * 20f;
            for (int i = 0; i <= batch % 3; i++)
            {
                var placement = new Transform3D(
                    Basis.Identity.Rotated(Vector3.Up, batch * 0.1f).Scaled(new Vector3(1f + i, 1f, 1f)),
                    new Vector3(x0 + i * 20f, i * 2f, batch % 4 * 5f));
                transforms.Add(placement);
                expected.Add(placement);
            }
            // Two base meshes, one of which also appears with an authored lower level.
            batches.Add(new RenderBatch(
                batch % 2 == 0 ? RenderMeshKey.Single(0) : new RenderMeshKey(1, 2),
                Cell, transforms));
        }

        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(batches);

        Assert.True(merged.Count < batches.Count);
        List<Transform3D> survived = merged.SelectMany(g => g.Transforms).ToList();
        Assert.Equal(expected.Count, survived.Count);
        Assert.Equal(
            expected.OrderBy(t => t.Origin.X).ThenBy(t => t.Origin.Y).ThenBy(t => t.Origin.Z).ToList(),
            survived.OrderBy(t => t.Origin.X).ThenBy(t => t.Origin.Y).ThenBy(t => t.Origin.Z).ToList());
        // Nothing is submitted under a key its own batch did not name.
        foreach (MergedRenderGroup group in merged)
            Assert.Subset(
                batches.Where(b => b.Key == group.Key).SelectMany(b => b.Transforms).ToHashSet(),
                group.Transforms.ToHashSet());
    }

    // The structural gate compares these counts between runs, so the same input has to produce the same
    // submissions in the same order or the gate flaps for reasons that are not a change.
    [Fact]
    public void Merge_IsDeterministic()
    {
        var batches = new List<RenderBatch>
        {
            Batch(new RenderMeshKey(2, 5), At(1f)),
            Batch(RenderMeshKey.Single(0), At(2f)),
            Batch(new RenderMeshKey(2, 5), At(3f)),
            Batch(RenderMeshKey.Single(0), At(400f)),
            Batch(new RenderMeshKey(1, 5), At(5f)),
        };

        List<MergedRenderGroup> first = RenderBatchGrouping.Merge(batches);
        List<MergedRenderGroup> second = RenderBatchGrouping.Merge(batches);

        // First-seen order, so the submission order follows the placement data rather than a hash. None
        // of these five merge: a batch holding one placement has a point footprint, and two points only
        // overlap where they coincide.
        Assert.Equal(batches.Select(b => b.Key), first.Select(g => g.Key));
        Assert.Equal(first.Select(g => g.Key), second.Select(g => g.Key));
        for (int i = 0; i < first.Count; i++)
            Assert.Equal(first[i].Transforms, second[i].Transforms);
    }

    // What the placements ARE travels with the batch, so the dev console can stop drawing one kind of
    // object — the trees, say — without touching the buildings batched beside them.
    [Fact]
    public void Merge_CarriesTheCategoryOntoTheSubmittedGroup()
    {
        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[]
        {
            new RenderBatch(RenderMeshKey.Single(0), Cell, new[] { At(1f) }, true, EObjectType.Resource),
        });

        Assert.Equal(EObjectType.Resource, Assert.Single(merged).Category);
    }

    // Two categories over one deduplicated mesh stay two submissions. It is a rare shape — a mesh belongs
    // to the asset that ships it — but a merge there would hand two kinds of object one visibility flag,
    // and switching the small clutter off would take the furniture with it.
    [Fact]
    public void Merge_KeepsCategoriesApartEvenOverTheSameMesh()
    {
        var clutter = new RenderBatch(RenderMeshKey.Single(0), Cell, new[] { At(0f), At(10f) }, true,
            EObjectType.Small);
        var furniture = new RenderBatch(RenderMeshKey.Single(0), Cell, new[] { At(0f), At(10f) }, true,
            EObjectType.Medium);

        List<MergedRenderGroup> merged = RenderBatchGrouping.Merge(new[] { clutter, furniture });

        Assert.Equal(2, merged.Count);
        Assert.Equal(new[] { EObjectType.Small, EObjectType.Medium }, merged.Select(g => g.Category));

        // Same category, same mesh, same footprint: that pair is exactly what the merge is for.
        Assert.Single(RenderBatchGrouping.Merge(new[] { clutter, clutter }));
    }
}
