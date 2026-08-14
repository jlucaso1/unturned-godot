using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

// The one sequence a map's placements resolve in, exercised over a fabricated install. Every assertion
// here stood as a comment in one or more of the four hand-maintained copies this replaced; the fourth had
// drifted away from three of them, which is what the drift tests at the bottom pin down.
public class LevelContentPlanTests
{
    private const string CoreBundle = "Asset_Bundle_Name core.masterbundle\n";

    // A fabricated install: a core content source with the asset trees LevelContentPlan scans, plus a map
    // folder under it. `Sources` and `Level` are what the two arguments to Resolve are built from.
    private sealed class Install : IDisposable
    {
        private readonly TempDir _dir = new();

        public Install()
        {
            _dir.Write(System.IO.Path.Combine("Bundles", "MasterBundle.dat"), CoreBundle);
            _dir.Write(System.IO.Path.Combine("Bundles", "core_linux.masterbundle"), "x");
            // Version 12 with no objects, so a test that only cares about trees still parses a real file.
            Objects();
        }

        public string Path => _dir.Path;
        public string MapPath => System.IO.Path.Combine(_dir.Path, "Maps", "Test");

        public IReadOnlyList<ContentSource> Sources =>
            ContentSource.Discover(_dir.Path, UnturnedInstall.Platform.Linux);

        public LevelInfo Level => new(MapPath);

        // An asset .dat under the tree its EAssetType belongs to. Which tree decides which legacy-id
        // namespace claims the id, which is the whole reason a tree and an object may share a number.
        public Install Asset(string tree, string name, Guid guid, ushort id, string type)
        {
            _dir.Write(System.IO.Path.Combine("Bundles", tree, name, "Asset.dat"),
                $"GUID {guid:N}\nType {type}\nID {id}\n");
            return this;
        }

        public Install Object(string name, Guid guid, ushort id, string type = "Large") =>
            Asset("Objects", name, guid, id, type);

        public Install Resource(string name, Guid guid, ushort id) =>
            Asset("Trees", name, guid, id, "Resource");

        public Install Npc(string name, Guid guid, ushort id) =>
            Asset(System.IO.Path.Combine("NPCs", "Characters"), name, guid, id, "NPC");

        public Install Vehicle(string name, Guid guid, ushort id) =>
            Asset("Vehicles", name, guid, id, "Vehicle");

        public Install Foliage(string name, Guid guid)
        {
            _dir.Write(System.IO.Path.Combine("Bundles", "Assets", "Foliage", name + ".asset"),
                $"Metadata\n{{\n    GUID {guid:N}\n"
                + "    Type SDG.Framework.Foliage.FoliageInstancedMeshInfoAsset, SDG.Glazier.Runtime\n}\n"
                + "Asset\n{\n    Mesh\n    {\n        Path Terrain/Foliage/" + name + ".fbx\n    }\n}\n");
            return this;
        }

        // Objects.dat at SAVEDATA_VERSION 12, every placement in region (0,0).
        public Install Objects(params (Vector3 Pos, ushort Id, Guid Guid)[] objects)
        {
            var w = new RiverBytes().Byte(12).UInt32(1).UInt16((ushort)objects.Length);
            foreach ((Vector3 pos, ushort id, Guid guid) in objects)
            {
                w.Vector3(pos).Vector3(Vector3.Zero).Vector3(Vector3.One)
                    .UInt16(id).Guid(guid).Byte(0).UInt32(42).Guid(Guid.Empty).Int32(-1).Bool(true);
            }
            for (int i = 1; i < LevelObjects.WORLD_SIZE * LevelObjects.WORLD_SIZE; i++)
                w.UInt16(0);
            _dir.Write(System.IO.Path.Combine("Maps", "Test", "Level", "Objects.dat"), w.ToArray());
            return this;
        }

        // Trees.dat at the flat post-7 layout (GUID) or the pre-7 region grid (legacy id + GUID).
        public Install Trees(params (Vector3 Pos, Guid Guid)[] trees)
        {
            var w = new RiverBytes().Byte(8).Int32(trees.Length);
            foreach ((Vector3 pos, Guid guid) in trees)
                w.Guid(guid).Vector3(pos).Vector3(Vector3.Zero).Vector3(Vector3.One).Bool(false);
            _dir.Write(System.IO.Path.Combine("Maps", "Test", "Terrain", "Trees.dat"), w.ToArray());
            return this;
        }

        public Install LegacyTrees(params (Vector3 Pos, ushort Id)[] trees)
        {
            var w = new RiverBytes().Byte(6);
            for (int cell = 0; cell < LevelObjects.WORLD_SIZE * LevelObjects.WORLD_SIZE; cell++)
            {
                if (cell >= trees.Length)
                {
                    w.UInt16(0);
                    continue;
                }
                w.UInt16(1).UInt16(trees[cell].Id).Guid(Guid.Empty)
                    .Vector3(trees[cell].Pos).Bool(false);
            }
            _dir.Write(System.IO.Path.Combine("Maps", "Test", "Terrain", "Trees.dat"), w.ToArray());
            return this;
        }

        // Spawns/Vehicles.dat with one table naming one legacy vehicle id, and `points` spawnpoints far
        // enough apart that none is rejected for crowding.
        public Install VehicleSpawns(ushort legacyId, int points)
        {
            var w = new RiverBytes().Byte(4)
                .Byte(1).Byte(0).Byte(0).Byte(0).Str("Cars").UInt16(0)
                .Byte(1).Str("Tier").Single(1f).Byte(1).UInt16(legacyId)
                .UInt16((ushort)points);
            for (int i = 0; i < points; i++)
                w.Byte(0).Vector3(new Vector3(i * 100f, 0f, 0f)).Byte(45);
            _dir.Write(System.IO.Path.Combine("Maps", "Test", "Spawns", "Vehicles.dat"), w.ToArray());
            return this;
        }

        public void Dispose() => _dir.Dispose();
    }

    private static LevelContent Resolve(Install install, IReadOnlyList<Guid>? foliageGuids = null)
    {
        IReadOnlyList<ContentSource> sources = install.Sources;
        return LevelContentPlan.Resolve(sources, install.Level,
            ContentExtraction.ScanAssets(sources), foliageGuids);
    }

    private static Vector3 At(float x) => new(x, 0f, 0f);

    [Fact]
    public void ResolvesAModernMapsObjectsAndTreesIntoOneList()
    {
        var house = Guid.NewGuid();
        var pine = Guid.NewGuid();
        using var install = new Install();
        install.Object("House", house, 12).Resource("Pine", pine, 5)
            .Objects((At(1), 0, house)).Trees((At(2), pine));

        LevelContent content = Resolve(install);

        Assert.Equal(new[] { house, pine }, content.Objects.ConvertAll(o => o.Guid));
        Assert.Equal(new HashSet<Guid> { house, pine }, content.NeededGuids);
        Assert.Equal(0, content.LegacyResolved); // both were placed by GUID
    }

    [Fact]
    public void TreesResolveThroughTheResourceNamespaceNotTheObjectOne()
    {
        // 69 of the game's own resource ids also name an object, and every id Monolith's trees are placed
        // with is one of them. Resolved through the object table, a forest renders as houses.
        var house = Guid.NewGuid();
        var pine = Guid.NewGuid();
        using var install = new Install();
        install.Object("House", house, 5).Resource("Pine", pine, 5).LegacyTrees((At(2), 5));

        LevelContent content = Resolve(install);

        Assert.Equal(new[] { pine }, content.Objects.ConvertAll(o => o.Guid));
        Assert.Equal(1, content.LegacyResolved);
    }

    [Fact]
    public void LegacyObjectIdsBorrowTheirAssetsGuid()
    {
        var house = Guid.NewGuid();
        using var install = new Install();
        install.Object("House", house, 12).Objects((At(1), 12, Guid.Empty));

        LevelContent content = Resolve(install);

        Assert.Equal(house, Assert.Single(content.Objects).Guid);
        Assert.Equal(new HashSet<Guid> { house }, content.NeededGuids);
        Assert.Equal(1, content.LegacyResolved);
    }

    [Fact]
    public void AStaleModernGuidFallsBackToTheLegacyId()
    {
        // ObjectAssetDatabase.Resolve's documented fallback, which only runs because the needed set comes
        // from ResolvePlacementGuids. Built from the raw placement GUIDs — as the editor preview did — the
        // placement keeps pointing at a GUID no bundle has and renders as a box forever.
        var house = Guid.NewGuid();
        var stale = Guid.NewGuid();
        using var install = new Install();
        install.Object("House", house, 12).Objects((At(1), 12, stale));

        LevelContent content = Resolve(install);

        Assert.Equal(house, Assert.Single(content.Objects).Guid);
        Assert.Equal(new HashSet<Guid> { house }, content.NeededGuids);
        Assert.DoesNotContain(stale, content.NeededGuids);
    }

    [Fact]
    public void NpcsLeaveTheObjectListAndTheNeededSet()
    {
        // The bug the editor preview shipped: an NPC resolves to the player rig rather than to an
        // extracted mesh, so a GUID left in here is one the extraction plan chases on every load and
        // ObjectsBuilder then draws as a placeholder box.
        var scout = Guid.NewGuid();
        var house = Guid.NewGuid();
        using var install = new Install();
        install.Npc("Scout", scout, 753).Object("House", house, 12)
            .Objects((At(1), 0, house), (At(2), 0, scout));

        LevelContent content = Resolve(install);

        Assert.Equal(new[] { 2f }, content.Npcs.ConvertAll(o => o.Position.X));
        Assert.Equal(new[] { 1f }, content.Objects.ConvertAll(o => o.Position.X));
        Assert.Equal(new HashSet<Guid> { house }, content.NeededGuids);
    }

    [Fact]
    public void VehiclesAreRolledAndJoinTheNeededSet()
    {
        var hatchback = Guid.NewGuid();
        using var install = new Install();
        install.Vehicle("Hatchback", hatchback, 40).VehicleSpawns(40, points: 3);

        LevelContent content = Resolve(install);

        Assert.Equal(3, content.Vehicles.Count);
        Assert.All(content.Vehicles, v => Assert.Equal(hatchback, v.Guid));
        Assert.Contains(hatchback, content.NeededGuids);
        // They are their own list: a vehicle gets its own scene root, not a slot among the placements.
        Assert.Empty(content.Objects);
    }

    [Fact]
    public void ResolvedFoliageJoinsTheNeededSetAndUnresolvedFoliageDoesNot()
    {
        // An unresolved foliage GUID has nothing to extract, so counting it as needed reports the cache
        // cold on every boot.
        var grass = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        using var install = new Install();
        install.Foliage("Grass_00", grass);

        LevelContent content = Resolve(install, new[] { grass, unknown });

        Assert.Equal(new[] { grass }, new List<Guid>(content.FoliageAssets.Keys));
        Assert.Equal(new HashSet<Guid> { grass }, content.NeededGuids);
    }

    [Fact]
    public void NoFoliageGuidsMeansNoFoliageScanAtAll()
    {
        // What the dedicated server passes: it needs bodies, and grass has none. Distinct from an empty
        // list, which is a map whose blob names no types.
        var grass = Guid.NewGuid();
        using var install = new Install();
        install.Foliage("Grass_00", grass);

        Assert.Empty(Resolve(install, foliageGuids: null).FoliageAssets);
        Assert.Empty(Resolve(install, Array.Empty<Guid>()).FoliageAssets);
    }

    [Fact]
    public void UnresolvedPlacementsKeepTheirGuidSoTheMissIsReported()
    {
        // Diagnostics, not a bug: a modern GUID nothing declares stays in the needed set, so a completed
        // extraction attempt can record it as a miss instead of retrying it every load.
        var unknown = Guid.NewGuid();
        using var install = new Install();
        install.Objects((At(1), 0, unknown));

        LevelContent content = Resolve(install);

        Assert.Equal(new HashSet<Guid> { unknown }, content.NeededGuids);
    }

    [Fact]
    public void TheEmptyGuidIsNeverNeeded()
    {
        // An id-only placement nothing declares has no GUID any bundle or cache could be asked for.
        using var install = new Install();
        install.Objects((At(1), 900, Guid.Empty));

        LevelContent content = Resolve(install);

        Assert.Empty(content.NeededGuids);
        Assert.Single(content.Objects); // still placed: it gets a fallback box, like any unresolved one
    }

    [Fact]
    public void AMapWithNoFilesAtAllResolvesToNothing()
    {
        using var dir = new TempDir();
        dir.Write(Path.Combine("Bundles", "MasterBundle.dat"), CoreBundle);
        IReadOnlyList<ContentSource> sources =
            ContentSource.Discover(dir.Path, UnturnedInstall.Platform.Linux);

        LevelContent content = LevelContentPlan.Resolve(sources,
            new LevelInfo(Path.Combine(dir.Path, "Maps", "Nope")),
            ContentExtraction.ScanAssets(sources), foliageGuids: null);

        Assert.Empty(content.Objects);
        Assert.Empty(content.Vehicles);
        Assert.Empty(content.Npcs);
        Assert.Empty(content.NeededGuids);
        Assert.Equal(0, content.LegacyResolved);
    }

    [Fact]
    public void TreesDatIsTheLayoutEveryCallerDerived()
    {
        var level = new LevelInfo(Path.Combine("some", "map"));
        Assert.Equal(Path.Combine("some", "map", "Terrain", "Trees.dat"),
            LevelContentPlan.TreesDat(level));
    }

    // --- Order ----------------------------------------------------------------------------------------
    // The three assertions that only hold because the steps run in one fixed sequence.

    [Fact]
    public void AnNpcIsReachableOnlyByGuid()
    {
        // The NPC legacy-id namespace is deliberately not reachable from an object placement, so an
        // id-only placement whose number happens to be an NPC's resolves to nothing and takes a fallback
        // box rather than a character. That costs nothing real: NPCs postdate GUIDs, so no map that
        // places by id alone can place one — and the alternative, folding the two namespaces together,
        // is what made every one of Russia's forty shadow an unrelated object.
        var scout = Guid.NewGuid();
        using var install = new Install();
        install.Npc("Scout", scout, 753).Objects((At(1), 753, Guid.Empty), (At(2), 0, scout));

        LevelContent content = Resolve(install);

        Assert.Equal(new[] { 2f }, content.Npcs.ConvertAll(o => o.Position.X));
        Assert.Equal(new[] { 1f }, content.Objects.ConvertAll(o => o.Position.X));
        Assert.Empty(content.NeededGuids);
    }

    [Fact]
    public void TreesAreAppendedBeforeTheNeededSetIsBuilt()
    {
        // AppendTrees runs after the asset scan and before ResolvePlacementGuids, so a tree contributes to
        // the needed set exactly as an object does. Appended afterwards, every tree's mesh goes missing.
        var pine = Guid.NewGuid();
        using var install = new Install();
        install.Resource("Pine", pine, 5).LegacyTrees((At(1), 5), (At(2), 5));

        LevelContent content = Resolve(install);

        Assert.Equal(2, content.Objects.Count);
        Assert.Equal(new HashSet<Guid> { pine }, content.NeededGuids);
        Assert.Equal(2, content.LegacyResolved);
    }

    [Fact]
    public void AnNpcIsNotNeededEvenWhenSomethingElseSharesItsLegacyId()
    {
        // EAssetType.NPC ids run straight through the object range (Russia's are 752..832), so this is
        // also what stops the partition taking an unrelated object for a character.
        var scout = Guid.NewGuid();
        var house = Guid.NewGuid();
        using var install = new Install();
        install.Npc("Scout", scout, 753).Object("House", house, 753)
            .Objects((At(1), 753, Guid.Empty), (At(2), 0, scout));

        LevelContent content = Resolve(install);

        Assert.Equal(new[] { house }, content.Objects.ConvertAll(o => o.Guid));
        Assert.Equal(new[] { scout }, content.Npcs.ConvertAll(o => o.Guid));
        Assert.Equal(new HashSet<Guid> { house }, content.NeededGuids);
    }
}
