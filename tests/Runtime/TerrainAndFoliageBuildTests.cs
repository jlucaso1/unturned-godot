using System;
using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Building the ground the map stands on, and the grass growing out of it, from the real files.
//
// The terrain build is split across threads on purpose and the split is the thing worth holding: the
// geometry and its meshoptimizer LODs are pure CPU over data-only meshes, so they go wide on workers,
// while realising the meshes and attaching materials touches the RenderingServer and therefore may only
// happen on the main thread. Getting that boundary wrong does not fail — it corrupts, intermittently,
// on whichever machine happens to schedule it badly.
//
// The foliage is the other extreme: hundreds of thousands of instances, which is why it is read as
// CHUNKS and drawn as batches rather than as nodes. A build that produced a node per blade would be a
// map that never finishes loading.
//
// Both need the installed game; UG_REQUIRE_REAL_DATA=1 turns a skip into a failure so the job that
// fetches the content cannot pass by finding nothing.
public class TerrainAndFoliageBuildTests : TestClass
{
    public TerrainAndFoliageBuildTests(Node testScene) : base(testScene) { }

    // A map with no heightmaps builds no tiles and still hands back a sampler, because everything
    // downstream — roads, foliage, the spawn point — conforms to one.
    [Test]
    public void AMapWithNoHeightmapsBuildsNoTiles()
    {
        (Node3D root, int tileCount, HeightmapSampler heights) = WorldBuilder.BuildTerrain(
            "/nonexistent-unturned", new LevelInfo("/nonexistent-unturned/Maps/None"));

        Assert.Equal(0, tileCount);
        Assert.NotNull(heights);
        root.Free();
    }

    // PEI's own terrain, built the way a session builds it: every tile becomes drawable geometry, and the
    // sampler that comes back agrees with the ground it just built.
    [Test]
    [Timeout(300_000)]
    public void TheRealMapsTerrainBuilds()
    {
        if (!RealMap(out string install, out LevelInfo level))
            return;

        (Node3D root, int tileCount, HeightmapSampler heights) = WorldBuilder.BuildTerrain(install, level);

        try
        {
            Assert.True(tileCount > 0, "PEI built no terrain tiles");

            // Every tile is real geometry rather than an empty placeholder. A tile that built no surface
            // is a hole the player falls through, and it looks like nothing until they walk onto it.
            int drawn = 0;
            foreach (Node child in root.GetChildren())
                if (child is MeshInstance3D { Mesh: { } mesh })
                {
                    Assert.True(mesh.GetSurfaceCount() > 0, $"{child.Name} built a mesh with no surfaces");
                    drawn++;
                }

            Assert.True(drawn >= tileCount, $"{tileCount} tiles produced only {drawn} drawable meshes");

            // And the sampler answers over the map it was built from. A sampler that agreed with nothing
            // would drop every spawn, road and blade of grass to y=0.
            Assert.True(heights.TrySampleHeight(0f, 0f, out _),
                "the sampler built from PEI's own tiles could not sample its origin");
        }
        finally
        {
            root.Free();
        }
    }

    // Nothing to grow is an empty branch rather than a missing one, on both foliage paths. A map may
    // genuinely ship no Foliage.blob, and everything downstream still walks the same tree.
    [Test]
    public void AMapWithNoFoliageStillGetsAFoliageBranch()
    {
        Node3D fromChunks = FoliageBuilder.Build((LevelFoliageChunks?)null,
            new Dictionary<Guid, ArrayMesh>());
        Node3D fromWhole = FoliageBuilder.Build((LevelFoliage?)null, new Dictionary<Guid, ArrayMesh>());

        Assert.Equal(0, fromChunks.GetChildCount());
        Assert.Equal(0, fromWhole.GetChildCount());

        fromChunks.Free();
        fromWhole.Free();
    }

    // PEI's real Foliage.blob, read as chunks and built with no meshes to draw it with.
    //
    // No meshes is the honest fixture here rather than a shortcut: the mesh library comes from the object
    // extraction, and what this covers is the placement walk — hundreds of thousands of instances sorted
    // into chunks and batched. A build that drew each one as a node would be a map that never finishes
    // loading, and that failure is in the walk, not in the meshes.
    [Test]
    [Timeout(300_000)]
    public void TheRealMapsFoliageIsWalkedInChunks()
    {
        if (!RealMap(out _, out LevelInfo level))
            return;

        string blob = System.IO.Path.Combine(level.Path, "Foliage.blob");
        if (!System.IO.File.Exists(blob))
            return; // this map ships none, which is a map's right

        LevelFoliageChunks? chunks = LevelFoliageChunks.Load(blob, FoliageBuilder.RuntimeChunkTiles);
        Assert.NotNull(chunks);

        Node3D root = FoliageBuilder.Build(chunks, new Dictionary<Guid, ArrayMesh>());

        // With no mesh for any type, nothing is drawable — and that is the assertion: the walk completes
        // over the real data and produces no batch it cannot fill, rather than an empty node per instance.
        Assert.Equal(0, root.GetChildCount());
        root.Free();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static bool RealMap(out string install, out LevelInfo level)
    {
        install = "";
        level = null!;
        string? found = Assets.UnturnedInstall.Find();
        string? maps = found == null ? null : System.IO.Path.Combine(found, "Maps", "PEI");
        if (found == null || maps == null || !System.IO.Directory.Exists(maps))
        {
            if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
            {
                throw new System.IO.IOException(
                    "UG_REQUIRE_REAL_DATA=1 but the PEI map is not present; this run exists to prove these "
                    + "tests execute");
            }

            Log.Print("[runtime-tests] skipping: no PEI map "
                + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
            return false;
        }

        install = found;
        level = new LevelInfo(maps);
        return true;
    }
}
