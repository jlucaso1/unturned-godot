using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

// The renderer's spatial policy, asserted as properties rather than through a golden render graph. Three
// of these were previously claims made by a comment: that the coarsening walk terminates, that the cell
// count falls monotonically as the cells widen, and that anchoring per group is what makes the partition
// independent of where the map sits relative to the world origin.
public class ObjectBatchPartitionTests
{
    private static PartitionSettings Settings(float baseMetres = 1024f, long minCellTriangles = 500,
        float maxCellMetres = 65_536f, bool requireSpread = true) =>
        new(baseMetres, minCellTriangles, maxCellMetres, requireSpread);

    private static List<Transform3D> At(params Vector3[] origins)
    {
        var transforms = new List<Transform3D>(origins.Length);
        foreach (Vector3 origin in origins)
            transforms.Add(new Transform3D(Basis.Identity, origin));
        return transforms;
    }

    private static List<Transform3D> Grid(int side, float spacing)
    {
        var transforms = new List<Transform3D>(side * side);
        for (int x = 0; x < side; x++)
            for (int z = 0; z < side; z++)
                transforms.Add(new Transform3D(Basis.Identity, new Vector3(x * spacing, 0f, z * spacing)));
        return transforms;
    }

    private static List<Transform3D> Translated(List<Transform3D> transforms, Vector3 by)
    {
        var moved = new List<Transform3D>(transforms.Count);
        foreach (Transform3D t in transforms)
            moved.Add(new Transform3D(t.Basis, t.Origin + by));
        return moved;
    }

    // Which cell each placement of `group` landed in, as an index into the group rather than as the cell's
    // own coordinates — translating the group is exactly what changes those, and what must not change is
    // which placements ended up together.
    private static int[] Grouping(List<Transform3D> group, float cellSize)
    {
        Dictionary<(int X, int Z), List<Transform3D>> cells =
            ObjectBatchPartition.Cells(group, cellSize);
        var labelOf = new Dictionary<Vector3, int>();
        int next = 0;
        foreach (List<Transform3D> inCell in cells.Values)
        {
            foreach (Transform3D t in inCell)
                labelOf[t.Origin] = next;
            next++;
        }

        // Renumbered in the group's own order, so two runs that enumerated their cells differently still
        // compare equal whenever the partition is the same.
        var canonical = new Dictionary<int, int>();
        var labels = new int[group.Count];
        for (int i = 0; i < group.Count; i++)
        {
            int label = labelOf[group[i].Origin];
            if (!canonical.TryGetValue(label, out int dense))
                canonical[label] = dense = canonical.Count;
            labels[i] = dense;
        }
        return labels;
    }

    // --- The anchor -----------------------------------------------------------------------------------

    [Fact]
    public void AnchorIsTheGroupsOwnLowestCorner()
    {
        Vector3 anchor = ObjectBatchPartition.AnchorOf(At(
            new Vector3(10f, 5f, -3f), new Vector3(-4f, 20f, 8f), new Vector3(7f, 1f, 30f)));

        Assert.Equal(new Vector3(-4f, 1f, -3f), anchor);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(-5000f, 3000f)]
    [InlineData(1f, -1f)]
    [InlineData(512f, 512f)]
    public void PartitioningIsInvariantUnderTranslation(float dx, float dz)
    {
        // The whole justification for anchoring at the group's own corner. Anchored at the world origin, a
        // group straddling it is cut along it however large the cells are — so it can never fall below
        // four batches, and on a map centred there that is most of them.
        List<Transform3D> group = Grid(6, 400f);
        List<Transform3D> moved = Translated(group, new Vector3(dx, 0f, dz));

        Assert.Equal(Grouping(group, 1024f), Grouping(moved, 1024f));
        Assert.Equal(ObjectBatchPartition.CellCount(group, 1024f),
            ObjectBatchPartition.CellCount(moved, 1024f));
    }

    [Fact]
    public void AGroupStraddlingTheWorldOriginIsNotCutAlongIt()
    {
        // Four placements a metre apart around (0,0). A grid anchored at the world origin puts each in its
        // own quadrant; anchored at the group's corner they are one cell however wide the cells are.
        List<Transform3D> group = At(
            new Vector3(-1f, 0f, -1f), new Vector3(1f, 0f, -1f),
            new Vector3(-1f, 0f, 1f), new Vector3(1f, 0f, 1f));

        Assert.Equal(1, ObjectBatchPartition.CellCount(group, 1024f));
    }

    [Fact]
    public void CellsPartitionTheGroupWithNothingLostOrDuplicated()
    {
        List<Transform3D> group = Grid(8, 300f);

        Dictionary<(int X, int Z), List<Transform3D>> cells =
            ObjectBatchPartition.Cells(group, 512f);

        int total = 0;
        foreach (List<Transform3D> inCell in cells.Values)
            total += inCell.Count;
        Assert.Equal(group.Count, total);
        Assert.Equal(cells.Count, ObjectBatchPartition.CellCount(group, 512f));
        Assert.True(cells.Count > 1); // an 8x300 m grid does not fit in one 512 m cell
    }

    // --- The coarsening walk --------------------------------------------------------------------------

    [Fact]
    public void CellCountIsMonotonicallyNonIncreasingInCellSize()
    {
        // What makes the doubling walk sound: each step can only merge cells, never split them, so the
        // loop's exit condition is approached at every iteration.
        List<Transform3D> group = Grid(10, 137f); // a spacing that lines up with no power of two

        int previous = int.MaxValue;
        for (float metres = 16f; metres <= 65_536f; metres *= 2f)
        {
            int cells = ObjectBatchPartition.CellCount(group, metres);
            Assert.True(cells <= previous,
                $"{metres} m produced {cells} cells, more than the {previous} of the size below it");
            previous = cells;
        }
        Assert.Equal(1, previous); // in the limit, the whole group in one cell
    }

    [Fact]
    public void CoarseningTerminatesAtTheCeilingEvenWhenNoCellEverCarriesEnough()
    {
        // The degenerate case the ceiling exists for: one triangle per instance and a floor nothing can
        // reach. Without MaxCellMetres this walk would double forever.
        List<Transform3D> group = Grid(4, 1e9f);

        float metres = ObjectBatchPartition.CellSizeFor(group, trianglesPerInstance: 1,
            Settings(baseMetres: 1024f, minCellTriangles: long.MaxValue, maxCellMetres: 65_536f));

        Assert.True(metres >= 65_536f);
        Assert.True(float.IsFinite(metres));
    }

    [Fact]
    public void CoarseningStopsAsSoonAsAnAverageCellEarnsItsDrawCall()
    {
        // 64 copies of a 1000-triangle mesh spread over 2800 m. At 1024 m that is 9 cells, so an average
        // cell carries ~7 100 triangles — well past a 500 floor, and the walk returns the base size
        // without coarsening. The group really does span several cells, so this exercises the floor and
        // not the "already one cell" exit.
        List<Transform3D> group = Grid(8, 400f);
        Assert.True(ObjectBatchPartition.CellCount(group, 1024f) > 1);

        Assert.Equal(1024f, ObjectBatchPartition.CellSizeFor(group, 1000, Settings()));
    }

    [Fact]
    public void ALightGroupIsCoarsenedUntilItsCellsAreWorthIt()
    {
        // The same layout with one triangle per copy: no cell can reach the floor until the group is one
        // cell, which is what a group too light to be worth splitting should be.
        List<Transform3D> group = Grid(8, 400f);

        float metres = ObjectBatchPartition.CellSizeFor(group, 1, Settings());

        Assert.True(metres > 1024f);
        Assert.Equal(1, ObjectBatchPartition.CellCount(group, metres));
    }

    [Fact]
    public void ZeroMinCellTrianglesRestoresOneFixedSizeForEveryGroup()
    {
        // The documented A/B control: with no floor, no group is coarsened, however light it is.
        List<Transform3D> group = Grid(8, 400f);

        Assert.Equal(1024f,
            ObjectBatchPartition.CellSizeFor(group, 1, Settings(minCellTriangles: 0)));
        // And likewise for a mesh whose triangle count could not be measured.
        Assert.Equal(1024f, ObjectBatchPartition.CellSizeFor(group, 0, Settings()));
    }

    // --- Spread ---------------------------------------------------------------------------------------

    [Fact]
    public void ExceedsCellSpanAgreesWithTheCellCountItGates()
    {
        List<Transform3D> tight = At(new Vector3(0f, 0f, 0f), new Vector3(100f, 0f, 100f));
        List<Transform3D> wide = At(new Vector3(0f, 0f, 0f), new Vector3(3000f, 0f, 0f));

        Assert.False(ObjectBatchPartition.ExceedsCellSpan(tight, 1024f));
        Assert.Equal(1, ObjectBatchPartition.CellCount(tight, 1024f));
        Assert.True(ObjectBatchPartition.ExceedsCellSpan(wide, 1024f));
        Assert.True(ObjectBatchPartition.CellCount(wide, 1024f) > 1);
    }

    [Fact]
    public void ExceedsCellSpanSeesTheZAxisToo()
    {
        Assert.True(ObjectBatchPartition.ExceedsCellSpan(
            At(Vector3.Zero, new Vector3(0f, 0f, 3000f)), 1024f));
    }

    [Fact]
    public void ASingleCopyNeverSpansACell()
    {
        Assert.False(ObjectBatchPartition.ExceedsCellSpan(At(new Vector3(9999f, 0f, 9999f)), 1024f));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public void RequireSpreadGatesGroupsThatFitInOneCell(bool require, bool spans, bool satisfied)
    {
        Assert.Equal(satisfied, Settings(requireSpread: require).SpreadSatisfied(spans));
    }

    // --- Bounds and the switch distance ---------------------------------------------------------------

    [Fact]
    public void BoundsAreTheCentreAndHalfDiagonalOfThePlacements()
    {
        BatchBounds bounds = ObjectBatchPartition.BoundsOf(At(
            new Vector3(-10f, 0f, -10f), new Vector3(10f, 0f, 10f)));

        Assert.Equal(Vector3.Zero, bounds.Centre);
        // Half the diagonal of a 20 x 0 x 20 box.
        Assert.Equal(MathF.Sqrt(800f) * 0.5f, bounds.Radius, 4);
    }

    [Fact]
    public void ASingleCopyHasNoRadius()
    {
        BatchBounds bounds = ObjectBatchPartition.BoundsOf(At(new Vector3(3f, 4f, 5f)));

        Assert.Equal(new Vector3(3f, 4f, 5f), bounds.Centre);
        Assert.Equal(0f, bounds.Radius);
    }

    [Fact]
    public void TheLargestPlacementInTheBatchSetsTheSwitchDistance()
    {
        // A 2x-scaled copy must keep its detail twice as far out, or it visibly swaps down while the
        // unscaled copies beside it do not.
        var transforms = new List<Transform3D>
        {
            new(Basis.Identity, Vector3.Zero),
            new(Basis.Identity.Scaled(new Vector3(2f, 2f, 2f)), new Vector3(50f, 0f, 0f)),
        };

        Assert.Equal(120f, ObjectBatchPartition.SwitchDistance(10f, transforms, 6f), 3);
    }

    [Fact]
    public void ANegativelyScaledCopyCountsByItsMagnitude()
    {
        // A prefab mirrored on an axis covers the same screen area as one that is not.
        var transforms = new List<Transform3D>
        {
            new(Basis.Identity.Scaled(new Vector3(-3f, 1f, 1f)), Vector3.Zero),
        };

        Assert.Equal(180f, ObjectBatchPartition.SwitchDistance(10f, transforms, 6f), 3);
    }

    [Fact]
    public void ATinyMeshStillSwitchesNoNearerThanTheFloor()
    {
        // Below the floor the near batch's range would be inside the player's own reach, so the second
        // batch would be drawn for nothing.
        var transforms = new List<Transform3D> { new(Basis.Identity, Vector3.Zero) };

        Assert.Equal(ObjectBatchPartition.MinSwitchDistance,
            ObjectBatchPartition.SwitchDistance(0.01f, transforms, 6f));
    }
}
