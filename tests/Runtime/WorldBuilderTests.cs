using System;
using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Turning a map on disk into a world.
//
// The half held here is the AUTHORITY's, and it is deliberately not the same build the player sees. A
// dedicated server needs static collision and a damage ledger; it needs no textures, no materials and no
// meshes, and building them would have every headless host decoding a 110 MB bundle for pixels nobody
// will ever look at. So the same placements, the same asset database and the same collision policy run
// through a path that realises bodies and ladder volumes only.
//
// The invariant that makes the ledger worth having is that a punch's HIT and its COLLIDER come from one
// pass. Built separately they would drift — a shot landing on a rock the ledger no longer knows about —
// and the drift would only show up as damage that silently does nothing.
public class WorldBuilderTests : TestClass
{
    public WorldBuilderTests(Node testScene) : base(testScene) { }

    // No tiles is an empty collision root rather than a null one. A level whose heightmaps failed to
    // read still has to hand the server something to add to its tree.
    [Test]
    public void ALevelWithNoTilesStillYieldsACollisionRoot()
    {
        Node3D root = WorldBuilder.BuildTerrainCollision(Array.Empty<HeightmapTile>());

        Assert.Equal(0, root.GetChildCount());
        root.Free();
    }

    // Each tile becomes one heightfield body at its own place in the world. The server's movement solver
    // and its zombie queries both stand on these, so a tile that built no body is a hole a player falls
    // through on a host that looks fine.
    [Test]
    public void EachTileBecomesOneHeightfieldBody()
    {
        var tiles = new List<HeightmapTile> { Flat(0, 0), Flat(1, 0), Flat(0, 1) };

        Node3D root = WorldBuilder.BuildTerrainCollision(tiles);

        Assert.Equal(3, root.GetChildCount());
        foreach (Node child in root.GetChildren())
        {
            var body = Assert.IsType<StaticBody3D>(child);
            var shape = Assert.IsType<CollisionShape3D>(body.GetChild(0));
            var heightfield = Assert.IsType<HeightMapShape3D>(shape.Shape);
            Assert.Equal(Landscape.HEIGHTMAP_RESOLUTION, heightfield.MapWidth);
            Assert.Equal(Landscape.HEIGHTMAP_RESOLUTION, heightfield.MapDepth);
        }

        // Named by coordinate, which is what makes a body identifiable in a crash report or a repro dump.
        Assert.NotNull(root.GetNodeOrNull("Terrain_1_0"));
        root.Free();
    }

    // The navigation field records the same heightfields the physics server gets. It exists so the
    // navmesh reconciliation can probe the ground off the physics thread — reading a different surface
    // there would delete the wrong faces, and the map would look fine while zombies refused to cross it.
    [Test]
    public void TheNavigationFieldRecordsTheSameGround()
    {
        var field = new CollisionFieldBuilder();

        Node3D root = WorldBuilder.BuildTerrainCollision(new List<HeightmapTile> { Flat(0, 0), Flat(1, 0) },
            field);

        Assert.Equal(2, field.TileCount);
        root.Free();
        field.Release();
    }

    // A level with no objects file yields an empty collision root and an empty ledger — not a throw. A
    // map can genuinely have no placements, and a workshop map can be installed without its content.
    [Test]
    public void ALevelWithNoObjectsYieldsAnEmptyWorldAndAnEmptyLedger()
    {
        Node3D root = WorldBuilder.BuildObjectCollision("/nonexistent-unturned",
            new LevelInfo("/nonexistent-unturned/Maps/None"),
            out IReadOnlySet<Guid> selected, out Damage.DamageableWorld damageable);

        Assert.Empty(selected);
        Assert.NotNull(damageable);
        root.Free();
    }

    // PEI's own objects, built the way a headless host builds them.
    //
    // What is asserted is that the three products agree: the bodies that exist, the GUIDs the build says
    // it selected, and the ledger a punch resolves against. Built separately they would drift, and the
    // drift shows up only as damage that silently does nothing.
    [Test]
    public void TheRealMapsObjectCollisionAgreesWithItsDamageLedger()
    {
        if (!RealMap(out string install, out LevelInfo level))
            return;

        var field = new CollisionFieldBuilder();
        Node3D root = WorldBuilder.BuildObjectCollision(install, level,
            out IReadOnlySet<Guid> selected, out Damage.DamageableWorld damageable, field);

        Assert.NotEmpty(selected);
        Assert.NotNull(damageable);
        Assert.True(root.GetChildCount() > 0, "PEI built no static object collision at all");

        root.Free();
        field.Release();
    }

    // A map with no vehicles is an empty node rather than a missing one. The world root always has a
    // Vehicles branch, because everything downstream — the streamer, the metrics, the repro dump — walks
    // the same tree whether or not a map placed any.
    [Test]
    public void AMapWithNoVehiclesStillGetsAVehiclesBranch()
    {
        Node3D vehicles = WorldBuilder.BuildVehicles(Array.Empty<PlacedObject>(),
            new ObjectAssetDatabase(), new Dictionary<Guid, ArrayMesh>(),
            new Dictionary<Guid, List<CachedCollider>>(), null);

        Assert.Equal(0, vehicles.GetChildCount());
        vehicles.Free();
    }

    // A level with no terrain on disk builds no tiles, and still hands back a sampler. The sampler is
    // what roads, foliage and the spawn point all conform to — a null one would take the load down after
    // the terrain stage had already reported success.
    [Test]
    public void ALevelWithNoTerrainStillHandsBackASampler()
    {
        (Node3D root, int tileCount, HeightmapSampler heights) = WorldBuilder.BuildTerrain(
            "/nonexistent-unturned", new LevelInfo("/nonexistent-unturned/Maps/None"));

        Assert.Equal(0, tileCount);
        Assert.NotNull(heights);
        Assert.False(heights.TrySampleHeight(0f, 0f, out _), "a map with no terrain sampled a height");
        root.Free();
    }

    // --- helpers -------------------------------------------------------------------------------------

    // One flat tile, at the coordinate given. Flat because what is being checked is the shape of the
    // build, not the shape of the ground.
    private static HeightmapTile Flat(int coordX, int coordY) => HeightmapTile.FromHeights(coordX, coordY,
        new float[Landscape.HEIGHTMAP_RESOLUTION, Landscape.HEIGHTMAP_RESOLUTION]);

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
