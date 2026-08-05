using System;
using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Maps that are wrong.
//
// The game's own maps are well-formed, and they are not the ones that break this. A workshop map is
// authored by someone with an editor and shipped by someone with a zip file, and every part of it is
// optional in practice: a Level directory with no Objects.dat, a Terrain with half its heightmaps, an
// Environment whose Bounds.dat was truncated by a bad upload. None of that is hypothetical — it is the
// ordinary condition of user content, and a map that failed to load is a map nobody can report a problem
// with.
//
// What these establish is that the readers draw a line, and where they draw it:
//
//   a file that is ABSENT is handled — the level loads without whatever it described;
//   a file that is TRUNCATED is NOT — the reader runs off the end and the load dies.
//
// The second half is recorded as a finding rather than asserted around, and it is the more likely of the
// two in practice: a map with no Objects.dat was authored that way, while a map with half an Objects.dat
// is what a bad upload, a full disk or an interrupted download produces. See the tests that pin it.
//
// The damage is done to a COPY, which is the whole install as far as the catalog is concerned. Pointing
// at the real one instead would quietly resolve the undamaged map and prove nothing. Nothing here writes
// to the installed game.
public class DamagedMapTests : TestClass
{
    public DamagedMapTests(Node testScene) : base(testScene) { }

    // A map with no objects file builds anyway. A workshop map that shipped its Level directory empty is
    // exactly this, and the terrain is still a place to stand.
    //
    // The placements do not go to zero, and that is right rather than surprising: the map's TREES come
    // from Terrain/Trees.dat, which is a different file describing a different thing. A build that lost
    // the forest along with the buildings would be reading one failure as two.
    [Test]
    [Timeout(300_000)]
    public void AMapWithNoObjectsFileStillBuilds()
    {
        if (!Copy(out DamagedMap map))
            return;

        using (map)
        {
            int before = Placements(map);
            map.Remove("Level/Objects.dat");

            WorldBuildResult world = WorldBuilder.Build(map.Install, map.Name);
            try
            {
                Assert.True(world.TileCount > 0, "the terrain went with the objects");
                Assert.True(world.PlacedObjectCount < before,
                    "removing every placed object changed nothing, so the copy was not the map that built");
            }
            finally
            {
                Release(world);
            }
        }
    }

    // A TRUNCATED objects file takes the load down, and this pins that rather than the behaviour one
    // would want. Reported as a finding, not asserted around.
    //
    // It is the likelier damage of the two: a map with no Objects.dat was authored that way, while a map
    // with half an Objects.dat is what a bad upload, a full disk or an interrupted download produces. The
    // reader runs off the end of the stream and the exception leaves through WorldBuilder.Build, so the
    // level does not load at all — where a file cut mid-record still has every placement before the cut,
    // and the ones after it are the only thing that could not be honoured.
    //
    // Whether to stop at the last whole record instead is a production decision and is left to one; the
    // test writes the behaviour down so it cannot change silently in either direction.
    [Test]
    [Timeout(300_000)]
    public void ATruncatedObjectsFileTakesTheLoadDown()
    {
        if (!Copy(out DamagedMap map))
            return;

        using (map)
        {
            map.Truncate("Level/Objects.dat", 64);

            Assert.ThrowsAny<Exception>(() => WorldBuilder.Build(map.Install, map.Name));
        }
    }

    // A map missing a heightmap tile builds the tiles it has. The terrain is per-tile on disk, so one
    // unreadable file is one hole rather than a level that will not open.
    [Test]
    [Timeout(300_000)]
    public void AMissingHeightmapTileLeavesTheOthersStanding()
    {
        if (!Copy(out DamagedMap map))
            return;

        using (map)
        {
            foreach (string tile in map.Files("Landscape/Heightmaps", "*.heightmap"))
            {
                File.Delete(tile);
                break;
            }

            WorldBuildResult world = WorldBuilder.Build(map.Install, map.Name);
            try
            {
                Assert.NotNull(world.Terrain);
                Assert.NotNull(world.Heights);
            }
            finally
            {
                Release(world);
            }
        }
    }

    // A map with NO navigation data parks nothing and hands back no router, which is the answer every
    // map without a navmesh gets: zombies steer directly instead of pathing.
    [Test]
    [Timeout(300_000)]
    public void AMapWithNoNavigationDataYieldsNoRouter()
    {
        if (!Copy(out DamagedMap map))
            return;

        using (map)
        {
            foreach (string nav in map.Files("Environment", "Navigation_*.dat"))
                File.Delete(nav);

            ZombieNavigation.DiscardPreloaded();
            ZombieNavigation.Preload(map.Path);

            Assert.Null(ZombieNavigation.TakePreloaded());
        }
    }

    // TRUNCATED navigation data throws out of the parse, same finding as the objects file above and on
    // the same reasoning. It matters more here than it looks: the parse happens during INTERACTIVE
    // loading, early, before the collision world exists — so the throw lands inside the load rather than
    // in front of a player who could be told which file is bad.
    [Test]
    [Timeout(300_000)]
    public void TruncatedNavigationDataThrowsOutOfTheParse()
    {
        if (!Copy(out DamagedMap map))
            return;

        using (map)
        {
            string[] navs = map.Files("Environment", "Navigation_*.dat");
            if (navs.Length == 0)
                return; // this map ships none, which is a map's right

            foreach (string nav in navs)
                Truncate(nav, 16);

            ZombieNavigation.DiscardPreloaded();
            Assert.ThrowsAny<Exception>(() => ZombieNavigation.Preload(map.Path));
            ZombieNavigation.DiscardPreloaded();
        }
    }

    // A map whose environment is gone entirely still builds. Bounds, flags and nodes are all optional to
    // a level that only has to be walked around in.
    [Test]
    [Timeout(300_000)]
    public void AMapWithNoEnvironmentStillBuilds()
    {
        if (!Copy(out DamagedMap map))
            return;

        using (map)
        {
            map.RemoveDirectory("Environment");

            WorldBuildResult world = WorldBuilder.Build(map.Install, map.Name);
            try
            {
                Assert.True(world.TileCount > 0);
            }
            finally
            {
                Release(world);
            }
        }
    }

    // And the authority builds the same damaged map. A dedicated host reads the same files through a
    // different path — collision and a damage ledger, no meshes — so a map that only the client survives
    // would take the server down while every player's own game looked fine.
    [Test]
    [Timeout(300_000)]
    public void TheAuthorityBuildsTheSameDamagedMap()
    {
        if (!Copy(out DamagedMap map))
            return;

        using (map)
        {
            map.Remove("Level/Objects.dat");

            var level = new LevelInfo(map.Path);
            Node3D root = WorldBuilder.BuildObjectCollision(map.Install, level,
                out IReadOnlySet<Guid> selected, out Damage.DamageableWorld damageable);

            Assert.NotNull(damageable);
            Assert.NotNull(selected);
            root.Free();
        }
    }

    // How many placements the map has before anything is damaged, so a test can tell "removed" from
    // "never resolved the copy in the first place".
    private static int Placements(DamagedMap map)
    {
        WorldBuildResult world = WorldBuilder.Build(map.Install, map.Name);
        try
        {
            return world.PlacedObjectCount;
        }
        finally
        {
            Release(world);
        }
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static void Release(WorldBuildResult world)
    {
        world.Terrain.Free();
        world.Objects.Free();
        world.Foliage.Free();
        world.Vehicles.Free();
    }

    private static void Truncate(string path, int keep)
    {
        byte[] whole = File.ReadAllBytes(path);
        File.WriteAllBytes(path, whole.Length <= keep ? whole : whole[..keep]);
    }

    // A copy of a real map, in a directory shaped like an install so the catalog can resolve it. Small
    // map on purpose: what is being tested is how the readers behave when a file is wrong, and a bigger
    // map would only make the copy slower.
    private static bool Copy(out DamagedMap map)
    {
        map = null!;
        string? install = Assets.UnturnedInstall.Find();
        string source = install == null ? "" : Path.Combine(install, "Maps", "PEI");
        if (source.Length == 0 || !Directory.Exists(source))
        {
            if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
            {
                throw new IOException(
                    "UG_REQUIRE_REAL_DATA=1 but the PEI map is not present; this run exists to prove "
                    + "these tests execute");
            }

            Log.Print("[runtime-tests] skipping: no map to damage a copy of "
                + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
            return false;
        }

        map = new DamagedMap(source, install!);
        return true;
    }

    private sealed class DamagedMap : IDisposable
    {
        private readonly string _root;

        public string Install { get; }
        public string Name { get; }
        public string Path { get; }

        public DamagedMap(string source, string realInstall)
        {
            _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "unturned-godot-damaged-" + Guid.NewGuid().ToString("N"));
            Name = System.IO.Path.GetFileName(source);
            Path = System.IO.Path.Combine(_root, "Maps", Name);
            CopyTree(source, Path);

            // The copy IS the install as far as the catalog is concerned — that is the whole point, and
            // pointing at the real one instead would quietly resolve the undamaged map and prove nothing.
            // Only the Maps tree is copied; the bundles and asset trees are linked, because damaging a map
            // says nothing about damaging the game and copying a 110 MB bundle per test would make this
            // file the slowest thing in the suite by an order of magnitude.
            LinkContent(realInstall, _root);
            Install = _root;
        }

        public void Remove(string relative)
        {
            string target = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (File.Exists(target))
                File.Delete(target);
        }

        public void RemoveDirectory(string relative)
        {
            string target = System.IO.Path.Combine(Path, relative);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }

        public void Truncate(string relative, int keep)
        {
            string target = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (File.Exists(target))
                DamagedMapTests.Truncate(target, keep);
        }

        public string[] Files(string relativeDirectory, string pattern)
        {
            string target = System.IO.Path.Combine(Path,
                relativeDirectory.Replace('/', System.IO.Path.DirectorySeparatorChar));
            return Directory.Exists(target) ? Directory.GetFiles(target, pattern) : Array.Empty<string>();
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
                File.Copy(file, System.IO.Path.Combine(to, System.IO.Path.GetFileName(file)), overwrite: true);
            foreach (string directory in Directory.GetDirectories(from))
            {
                CopyTree(directory,
                    System.IO.Path.Combine(to, System.IO.Path.GetFileName(directory)));
            }
        }

        private static void LinkContent(string install, string root)
        {
            foreach (string name in new[] { "Bundles", "Sandbox", "Extras" })
            {
                string from = System.IO.Path.Combine(install, name);
                if (Directory.Exists(from))
                    Directory.CreateSymbolicLink(System.IO.Path.Combine(root, name), from);
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
