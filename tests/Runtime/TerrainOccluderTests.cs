using System;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The coarse occluder mesh per terrain tile, and the terrain layer textures.
//
// The occluder exists because a frustum cannot reject anything hidden BEHIND terrain: on a hilly map most
// of what is submitted every frame is on the far side of a ridge. Godot rasterises these on the CPU each
// frame and drops whatever they cover.
//
// The grid arithmetic is pure and unit tested in core/ (ConservativeOccluder). What is only testable here
// is the part that turns it into world space and into an engine resource — and the property that matters
// most is CONSERVATISM: an occluder poking above the real ground hides geometry that is genuinely
// visible, which looks like objects popping out of existence. That is the worst kind of rendering bug,
// because it reads as corruption rather than as a missing feature.
public class TerrainOccluderTests : TestClass
{
    public TerrainOccluderTests(Node testScene) : base(testScene) { }

    // Coarse by design: a tile's heightmap is 257x257, and rasterising that every frame would cost more
    // than it saves. The occluder samples it on a 17x17 grid — ~512 triangles instead of ~131k.
    [Test]
    public void TheOccluderIsCoarseEnoughToRasteriseEveryFrame()
    {
        TerrainOccluder.Prepared prepared = TerrainOccluder.Prepare(Tile(FlatAt(0.5f)));

        Assert.Equal(17 * 17, prepared.Vertices.Length);
        // 16x16 quads, two triangles each, three indices per triangle.
        Assert.Equal(16 * 16 * 2 * 3, prepared.Indices.Length);
        Assert.Equal("Occluder_0_0", prepared.Name);
    }

    // The property the whole thing rests on. Each coarse vertex takes the MINIMUM height of the cells it
    // spans, never the average, so the occluder always sits at or below the surface it stands for. Given
    // a heightmap with one deep pit, an averaging occluder would float above it.
    [Test]
    public void TheOccluderNeverRisesAboveTheGroundItStandsFor()
    {
        // Mostly high ground with a single low cell inside the first coarse quad.
        float[] heights = FlatAt(0.9f);
        heights[(3 * Landscape.HEIGHTMAP_RESOLUTION) + 3] = 0.1f;

        TerrainOccluder.Prepared prepared = TerrainOccluder.Prepare(Tile(heights));

        // The corner vertex covering that cell must have been pulled down to it, not averaged up.
        float lowest = float.MaxValue;
        foreach (Vector3 vertex in prepared.Vertices)
            lowest = Math.Min(lowest, vertex.Y);
        float pit = Landscape.UnityToGodot(Landscape.GetWorldPosition(0, 0, 3, 3, 0.1f)).Y;
        float plateau = Landscape.UnityToGodot(Landscape.GetWorldPosition(0, 0, 0, 0, 0.9f)).Y;

        Assert.True(lowest <= pit + 0.001f,
            $"the occluder sat at {lowest}, above the pit at {pit} — it would hide visible geometry");
        Assert.True(pit < plateau, "the fixture is wrong: the pit is not below the plateau");
    }

    // Tiles are placed by their own coordinates, so two tiles do not build the same occluder in the same
    // place — which would leave half the map unoccluded and the other half double-covered.
    [Test]
    public void EachTileOccludesItsOwnGround()
    {
        TerrainOccluder.Prepared origin = TerrainOccluder.Prepare(Tile(FlatAt(0.5f), x: 0, y: 0));
        TerrainOccluder.Prepared neighbour = TerrainOccluder.Prepare(Tile(FlatAt(0.5f), x: 1, y: 0));

        Assert.Equal("Occluder_1_0", neighbour.Name);
        Assert.NotEqual(origin.Vertices[0], neighbour.Vertices[0]);
    }

    // 16-bit and 32-bit heightmaps both read back as the same surface: the 16-bit form is normalised
    // against ushort.MaxValue, and getting that wrong would put the whole tile at the wrong altitude.
    [Test]
    public void SixteenAndThirtyTwoBitHeightmapsAgree()
    {
        var wide = new ushort[Landscape.HEIGHTMAP_RESOLUTION * Landscape.HEIGHTMAP_RESOLUTION];
        for (int i = 0; i < wide.Length; i++)
            wide[i] = ushort.MaxValue / 2;

        TerrainOccluder.Prepared from16 = TerrainOccluder.Prepare(
            new TerrainBuilder.TileMesh(new ImporterMesh(), wide, null, 0, 0, null));
        TerrainOccluder.Prepared from32 = TerrainOccluder.Prepare(Tile(FlatAt(ushort.MaxValue / 2 / 65535f)));

        Assert.True(Mathf.Abs(from16.Vertices[0].Y - from32.Vertices[0].Y) < 0.01f,
            $"the two heightmap widths disagree: {from16.Vertices[0].Y} vs {from32.Vertices[0].Y}");
    }

    // The engine resource is built separately from the data, because the data half runs on worker threads
    // beside the terrain mesh generation and RenderingServer resources must not.
    [Test]
    public void TheEngineResourceIsBuiltFromThePreparedData()
    {
        TerrainOccluder.Prepared prepared = TerrainOccluder.Prepare(Tile(FlatAt(0.5f)));

        OccluderInstance3D instance = TerrainOccluder.Finish(prepared);
        TestScene.AddChild(instance);

        Assert.Equal("Occluder_0_0", instance.Name);
        Assert.NotNull(instance.Occluder);

        instance.QueueFree();
    }

    // --- terrain layer textures ----------------------------------------------------------------------

    // A map with no Materials.unity3d builds with flat layer colours rather than failing: workshop maps
    // ship all sorts, and a missing bundle is not a broken map.
    [Test]
    public void AMapWithNoTerrainBundleLoadsNoTextures()
    {
        Assert.Empty(TerrainTextures.Load(Path.Combine(Path.GetTempPath(), "unturned-godot-no-such-terrain")));
    }

    // A bundle this reader cannot decode is the same story: the map still builds, and the reason is
    // printed rather than thrown. Two container formats ship in the wild and workshop maps carry others,
    // so "cannot read this one" has to be survivable.
    [Test]
    public void AnUndecodableTerrainBundleIsSurvivable()
    {
        string dir = Path.Combine(Path.GetTempPath(), "unturned-godot-terrain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Materials.unity3d"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            Assert.Empty(TerrainTextures.Load(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static float[] FlatAt(float height)
    {
        var heights = new float[Landscape.HEIGHTMAP_RESOLUTION * Landscape.HEIGHTMAP_RESOLUTION];
        for (int i = 0; i < heights.Length; i++)
            heights[i] = height;
        return heights;
    }

    private static TerrainBuilder.TileMesh Tile(float[] heights, int x = 0, int y = 0) =>
        new(new ImporterMesh(), null, heights, x, y, null);
}
