using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

// The arithmetic behind cutting a hole out of a heightfield. Whether the SHAPES that come out of it add
// up to the surface the map describes is a physics question and is answered against a real physics server
// by TerrainBuilderTests.ExactlyTheCellsTheMapCutsAwayAreOpenToTheSky; this is the half that can be
// checked without one.
public class TerrainHoleCollisionTests
{
    private const int Res = Landscape.HEIGHTMAP_RESOLUTION;

    private static LandscapeHoles Cut(params (int X, int Y)[] cells)
    {
        var bytes = new byte[LandscapeHoles.FILE_BYTES];
        bytes[0] = 1;
        for (int i = 1; i < bytes.Length; i++)
            bytes[i] = 0xFF;
        foreach ((int x, int y) in cells)
            bytes[1 + (x * (Landscape.HOLES_RESOLUTION / 8)) + (y >> 3)] &= (byte)~(1 << (y & 7));
        return LandscapeHoles.Parse(bytes, 0, 0);
    }

    private static float[] FlatMapData(float height = 0.5f)
    {
        var heights = new float[Res, Res];
        for (int x = 0; x < Res; x++)
            for (int y = 0; y < Res; y++)
                heights[x, y] = height;
        return TerrainHeightfield.MapData(heights);
    }

    // Godot's heightfield can only refuse collision at a sample, so a cut cell marks all FOUR of its
    // corners — which is what makes the cell disappear whichever way the engine splits it, and what makes
    // the cut spill into the cells around it.
    [Fact]
    public void ACutCellMarksItsFourCorners()
    {
        float[] data = FlatMapData();

        Assert.True(TerrainHoleCollision.MarkNoCollisionSamples(data, Cut((10, 20))));

        // Sample (hx, hy) lives at ((Res - 1 - hx) * Res) + hy.
        Assert.True(float.IsNaN(data[((Res - 1 - 10) * Res) + 20]));
        Assert.True(float.IsNaN(data[((Res - 1 - 10) * Res) + 21]));
        Assert.True(float.IsNaN(data[((Res - 1 - 11) * Res) + 20]));
        Assert.True(float.IsNaN(data[((Res - 1 - 11) * Res) + 21]));
        // And nothing beyond them.
        Assert.False(float.IsNaN(data[((Res - 1 - 9) * Res) + 20]));
        Assert.False(float.IsNaN(data[((Res - 1 - 12) * Res) + 20]));
        Assert.False(float.IsNaN(data[((Res - 1 - 10) * Res) + 19]));
        Assert.False(float.IsNaN(data[((Res - 1 - 10) * Res) + 22]));
    }

    [Fact]
    public void ATileThatCutsNothingMarksNothing()
    {
        float[] data = FlatMapData();

        Assert.False(TerrainHoleCollision.MarkNoCollisionSamples(data, Cut()));

        foreach (float height in data)
            Assert.False(float.IsNaN(height));
    }

    // What has to be repaired is everything that shares a CORNER with a hole — the eight cells around it,
    // not the four that share an edge. A repair that only covered the edge neighbours would leave four
    // quarter-cell wedges of missing floor on the diagonals.
    [Fact]
    public void EveryCellSharingACornerWithAHoleIsRepaired()
    {
        List<(int X, int Y)> repair = TerrainHoleCollision.CellsToRepair(Cut((10, 20)));

        Assert.Equal(8, repair.Count);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0)
                    Assert.Contains((10 + dx, 20 + dy), repair);
        Assert.DoesNotContain((10, 20), repair); // the hole itself is never repaired
    }

    // Holes next to each other must not repair one another: the ring is around the whole cluster.
    [Fact]
    public void AClusterIsRepairedAroundItsOutsideOnly()
    {
        // PEI's own shape on tile (-1, -1): three cells by two.
        List<(int X, int Y)> repair = TerrainHoleCollision.CellsToRepair(
            Cut((62, 62), (62, 63), (63, 62), (63, 63), (64, 62), (64, 63)));

        // The 3x2 block dilates to 5x4; minus the six holes, fourteen cells.
        Assert.Equal(14, repair.Count);
        foreach ((int x, int y) in repair)
        {
            Assert.InRange(x, 61, 65);
            Assert.InRange(y, 61, 64);
            Assert.False(x is >= 62 and <= 64 && y is >= 62 and <= 63); // never a hole
        }
    }

    // The tile's edge is where the dilation runs off the grid, and the cells outside it simply do not
    // exist — clamping there rather than throwing is what keeps the caller free of the special case.
    [Fact]
    public void ACutAtTheTilesEdgeRepairsOnlyWhatIsOnTheTile()
    {
        List<(int X, int Y)> repair = TerrainHoleCollision.CellsToRepair(Cut((0, 0)));

        Assert.Equal(3, repair.Count); // (0,1), (1,0), (1,1) — the rest is off the tile
        foreach ((int x, int y) in repair)
        {
            Assert.InRange(x, 0, 1);
            Assert.InRange(y, 0, 1);
        }
    }

    // The repair triangles are the render mesh's own triangles for those cells: the same split, the same
    // winding, the same world positions. That is not a coincidence to be preserved by accident — it is
    // why the ground beside a hole is walked at exactly the height it is drawn at.
    [Fact]
    public void RepairFacesAreTheRenderMeshsOwnTrianglesForThoseCells()
    {
        LandscapeHoles holes = Cut((10, 20));
        float Height(int hx, int hy) => 0.5f + (0.0001f * ((hx * 3) + hy));

        Vector3[] faces = TerrainHoleCollision.RepairFaces(holes, 2, -3, Height);

        Assert.Equal(8 * 6, faces.Length); // eight cells, two triangles each

        // The first repaired cell is (9, 19) — CellsToRepair walks x then y.
        Vector3 Corner(int hx, int hy) => Landscape.UnityToGodot(
            Landscape.GetWorldPosition(2, -3, hx, hy, Height(hx, hy)));
        Assert.Equal(Corner(9, 19), faces[0]);
        Assert.Equal(Corner(10, 19), faces[1]);
        Assert.Equal(Corner(10, 20), faces[2]);
        Assert.Equal(Corner(9, 19), faces[3]);
        Assert.Equal(Corner(10, 20), faces[4]);
        Assert.Equal(Corner(9, 20), faces[5]);
    }

    [Fact]
    public void ATileThatCutsNothingNeedsNoRepair()
    {
        Assert.Empty(TerrainHoleCollision.CellsToRepair(Cut()));
        Assert.Empty(TerrainHoleCollision.RepairFaces(Cut(), 0, 0, (_, _) => 0.5f));
    }

    // Hole centres are what a decimated copy of the tile is checked against, so they have to land in the
    // middle of the cell the map cut, in Godot's mirrored Z.
    [Fact]
    public void HoleCentresLandInTheMiddleOfTheCutCell()
    {
        Vector2[] centres = TerrainHoleCollision.HoleCentres(Cut((10, 20)), 1, -2);

        Vector2 centre = Assert.Single(centres);
        // hy -> world X, hx -> world Z, 4 m cells, tile origin at (1024, -(-2048)).
        Assert.Equal(1024f + (20.5f * 4f), centre.X, 0.001f);
        Assert.Equal(-((-2 * 1024f) + (10.5f * 4f)), centre.Y, 0.001f);
    }

    // A triangle list that paves over a hole is one that must not be drawn, and one that leaves it alone
    // is fine however coarse it is. This is the test the LOD chain is truncated by.
    [Fact]
    public void ATriangleOverAHoleIsWhatClosesIt()
    {
        Vector2[] centres = TerrainHoleCollision.HoleCentres(Cut((10, 20)), 0, 0);
        var vertices = new[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(400f, 0f, 0f),
            new Vector3(0f, 0f, -400f),
            new Vector3(400f, 0f, -400f),
        };

        // One big triangle covering the whole corner of the tile swallows the hole at (82, -42).
        Assert.False(TerrainHoleCollision.KeepsHolesOpen(vertices, new[] { 0, 1, 2 }, centres));
        // The opposite one does not reach it.
        Assert.True(TerrainHoleCollision.KeepsHolesOpen(vertices, new[] { 1, 3, 2 }, centres));
        // And a tile with no holes is never closed by anything.
        Assert.True(TerrainHoleCollision.KeepsHolesOpen(vertices, new[] { 0, 1, 2 },
            TerrainHoleCollision.HoleCentres(Cut(), 0, 0)));
    }

    // Outside is outside on every side of the triangle, and the edge itself counts as covered — a hole
    // whose centre lands exactly on a seam between two decimated triangles is still a hole that has been
    // paved over, and answering "open" there would keep a level that closes it.
    [Theory]
    [InlineData(-1f, -1f, true)]   // outside past the (0,0)-(10,0) edge
    [InlineData(5f, 20f, true)]    // outside past the hypotenuse
    [InlineData(-1f, 5f, true)]    // outside past the (0,0)-(0,10) edge
    [InlineData(2f, 2f, false)]    // inside
    [InlineData(5f, 0f, false)]    // exactly on an edge
    [InlineData(0f, 0f, false)]    // exactly on a corner
    public void APointIsCoveredUnlessItIsOutsideEveryEdge(float x, float z, bool open)
    {
        var vertices = new[]
        {
            new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 0f), new Vector3(0f, 0f, 10f),
        };

        Assert.Equal(open, TerrainHoleCollision.KeepsHolesOpen(
            vertices, new[] { 0, 1, 2 }, new[] { new Vector2(x, z) }));
    }

    // Navigation reads its own copy of the collision world, and a no-collision sample has to mean the
    // same thing there: not "ground at no height", which is what a NaN falling through the comparisons
    // produces, but "ask the physics server". The server has both the marked heightfield and the repair
    // patch beside it; this field has only the first, so a cell touching a hole is not its call.
    [Fact]
    public void NavigationTreatsAHoleAsAQuestionForThePhysicsServer()
    {
        float[] data = FlatMapData(0.5f); // sea level, so the probe segment below straddles it
        TerrainHoleCollision.MarkNoCollisionSamples(data, Cut((10, 20)));
        var builder = new CollisionFieldBuilder();
        builder.AddHeightfield(TerrainHeightfield.CollisionTransform(0, 0), Res, Res, data);
        CollisionField field = builder.Build();

        // The middle of the cut cell, and a cell well clear of it.
        float cell = TerrainHeightfield.CellSize;
        SurfaceSample hole = field.Probe(20.5f * cell, -(10.5f * cell), 100f, -100f);
        SurfaceSample ground = field.Probe(60.5f * cell, -(60.5f * cell), 100f, -100f);

        Assert.True(hole.Uncertain);
        Assert.False(ground.Uncertain);
        Assert.True(ground.Hit);
        Assert.Equal(0f, ground.Y, 0.001f);
    }
}
