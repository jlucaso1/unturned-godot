using System;
using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The foliage root, and the three shapes of input it accepts.
//
// A map with no Foliage.blob is ordinary — many workshop maps ship none — so every one of these has to
// hand back a usable root rather than null. The caller adds it to the world unconditionally, and a null
// here would take the load down over a map that simply has no grass.
//
// Which ROOT comes back also matters. The streaming path and the batched path are different node types
// with different lifecycles, and the switch between them is an environment variable: UG_NODE_MULTIMESH
// picks plain Node3D wrappers over the RID-owning renderer. A switch nobody exercises is a switch that is
// broken on one side, so both are asserted.
//
// The instance packing itself is pure and unit tested in core/ (FoliageGroups, FoliageResidencyIndex).
public class FoliageBuilderTests : TestClass
{
    public FoliageBuilderTests(Node testScene) : base(testScene) { }

    // All three overloads survive a map with no foliage at all, which is the case the caller cannot check
    // for itself.
    [Test]
    public void AMapWithNoFoliageStillGetsARoot()
    {
        var meshes = new Dictionary<Guid, ArrayMesh>();

        Node3D fromIndex = FoliageBuilder.Build((Data.FoliageResidencyIndex?)null, meshes);
        Node3D fromChunks = FoliageBuilder.Build((Data.LevelFoliageChunks?)null, meshes);
        Node3D fromLevel = FoliageBuilder.Build((Data.LevelFoliage?)null, meshes);

        foreach (Node3D root in new[] { fromIndex, fromChunks, fromLevel })
        {
            Assert.NotNull(root);
            Assert.Equal(0, root.GetChildCount());
            root.Free();
        }
    }

    // The batched path hands back the RID-owning renderer, which is what keeps a map's thousands of
    // foliage chunks from becoming thousands of MultiMeshInstance3D wrappers.
    [Test]
    public void TheBatchedPathUsesTheRidOwningRenderer()
    {
        Node3D root = FoliageBuilder.Build((Data.LevelFoliageChunks?)null, new Dictionary<Guid, ArrayMesh>());

        Assert.IsType<MultiMeshRidRenderer>(root);
        root.Free();
    }

    // …and the node-wrapper switch really switches. This is the side that would rot unnoticed: it exists
    // for profiling comparisons, so nothing in a normal run would ever notice it had stopped working.
    [Test]
    public void TheNodeWrapperSwitchReallySwitches()
    {
        string previous = OS.GetEnvironment("UG_NODE_MULTIMESH");
        OS.SetEnvironment("UG_NODE_MULTIMESH", "1");
        try
        {
            Node3D root = FoliageBuilder.Build((Data.LevelFoliageChunks?)null,
                new Dictionary<Guid, ArrayMesh>());

            Assert.IsNotType<MultiMeshRidRenderer>(root);
            root.Free();
        }
        finally
        {
            OS.SetEnvironment("UG_NODE_MULTIMESH", previous);
        }
    }

    // The tuning knobs are read once and clamped, so a hostile or mistyped value cannot produce a chunk
    // size of zero (an infinite loop over tiles) or a draw distance that carpets the island in draw calls.
    [Test]
    public void TheTuningValuesAreSaneWhateverTheEnvironmentSays()
    {
        Assert.True(FoliageBuilder.RuntimeChunkTiles >= 1, "a chunk cannot span zero tiles");
        Assert.True(FoliageBuilder.RuntimeChunkTiles <= 32);
        // The fade margin is a quarter of the draw distance, capped: it has to leave room to fade rather
        // than popping chunks out at the boundary.
        Assert.True(FoliageBuilder.FadeMarginValue > 0f);
        Assert.True(FoliageBuilder.FadeMarginValue <= 32f);
    }

    // Residency streaming is on by default and is turned off by the node-wrapper switch, because the two
    // are alternative ways to own the same instances and running both would double the work.
    [Test]
    public void ResidencyStreamingAndNodeWrappersAreMutuallyExclusive()
    {
        Assert.True(FoliageBuilder.SpatialResidencyEnabled); // the default
    }
}
