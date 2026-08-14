using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class LegacyPlacementsTests
{
    private static ObjectAsset Asset(Guid guid, ushort id, string type = "Large", string extra = "") =>
        ObjectAsset.TryParse(DatParser.Parse($"GUID {guid:N}\nID {id}\nType {type}\n{extra}"), null,
            out ObjectAsset? a)
            ? a
            : throw new InvalidOperationException("asset fixture did not parse");

    private static ObjectAssetDatabase DatabaseWith(params ObjectAsset[] assets)
    {
        var db = new ObjectAssetDatabase();
        foreach (ObjectAsset asset in assets)
            db.Add(asset);
        return db;
    }

    private static PlacedObject Placement(ushort id, Guid guid) =>
        new(new Vector3(1, 2, 3), new Vector3(0, 90, 0), Vector3.One, id, guid);

    private static PlacedTree Tree(ushort id, Guid guid) =>
        new(new Vector3(4, 5, 6), Vector3.Zero, Vector3.One, guid, id);

    [Fact]
    public void ResolveGuids_FillsInTheGuidOfALegacyIdPlacement()
    {
        var guid = Guid.NewGuid();
        var placements = new List<PlacedObject> { Placement(id: 42, guid: Guid.Empty) };

        Assert.Equal(1, LegacyPlacements.ResolveGuids(placements, DatabaseWith(Asset(guid, 42))));

        Assert.Equal(guid, placements[0].Guid);
        Assert.Equal(42, placements[0].Id);          // the id it was placed with is kept
        Assert.Equal(new Vector3(1, 2, 3), placements[0].Position);
        Assert.Equal(new Vector3(0, 90, 0), placements[0].EulerDegrees);
    }

    [Fact]
    public void ResolveGuids_LeavesModernPlacementsAlone()
    {
        var placed = Guid.NewGuid();
        var other = Guid.NewGuid();
        // The id collides with a different asset: a placement that already has a GUID must not follow it.
        var placements = new List<PlacedObject> { Placement(id: 42, guid: placed) };

        Assert.Equal(0, LegacyPlacements.ResolveGuids(placements, DatabaseWith(Asset(other, 42))));
        Assert.Equal(placed, placements[0].Guid);
    }

    [Fact]
    public void ResolveGuids_UnknownIdStaysUnresolved()
    {
        var placements = new List<PlacedObject>
        {
            Placement(id: 7, guid: Guid.Empty),   // no asset carries this id
            Placement(id: 0, guid: Guid.Empty),   // no identity at all
        };

        Assert.Equal(0, LegacyPlacements.ResolveGuids(placements, DatabaseWith(Asset(Guid.NewGuid(), 42))));
        Assert.Equal(Guid.Empty, placements[0].Guid);
        Assert.Equal(Guid.Empty, placements[1].Guid);
    }

    [Fact]
    public void AppendTrees_ResolvesThroughTheResourceNamespace()
    {
        // The id collision that matters: every id Monolith's trees are placed with also names an object.
        // Whichever the merged database indexed first, a tree must land on the resource.
        var tree = Guid.NewGuid();
        var house = Guid.NewGuid();
        var placements = new List<PlacedObject>();

        Assert.Equal(1, LegacyPlacements.AppendTrees(new[] { Tree(id: 3, guid: Guid.Empty) }, placements,
            DatabaseWith(Asset(house, 3), Asset(tree, 3, "Resource"))));

        Assert.Equal(tree, Assert.Single(placements).Guid);
        // Sunk by the default Vertical_Offset: the file's Y is where the trunk meets the ground.
        Assert.Equal(new Vector3(4, 5f - 0.75f, 6), placements[0].Position);
        Assert.Equal(3, placements[0].Id);
    }

    [Fact]
    public void AppendTrees_KeepsAGuidItAlreadyHas()
    {
        var placed = Guid.NewGuid();
        var placements = new List<PlacedObject>();

        // Not counted as resolved, and the resource sharing its id must not displace it.
        Assert.Equal(0, LegacyPlacements.AppendTrees(new[] { Tree(id: 3, guid: placed) }, placements,
            DatabaseWith(Asset(Guid.NewGuid(), 3, "Resource"))));

        Assert.Equal(placed, Assert.Single(placements).Guid);
    }

    [Fact]
    public void AppendTrees_UnknownIdStillTakesItsPlace()
    {
        // The tree is kept without a GUID rather than dropped: it renders as a placeholder box, which is
        // how an unresolvable placement is meant to show up.
        var placements = new List<PlacedObject>();

        Assert.Equal(0, LegacyPlacements.AppendTrees(new[] { Tree(id: 9, guid: Guid.Empty) }, placements,
            DatabaseWith(Asset(Guid.NewGuid(), 3, "Resource"))));

        Assert.Equal(Guid.Empty, Assert.Single(placements).Guid);
    }

    [Fact]
    public void ResolveGuids_DoesNotFollowResourceIds()
    {
        // The mirror of the tree case: an object placement must not land on the resource sharing its id.
        var placements = new List<PlacedObject> { Placement(id: 3, guid: Guid.Empty) };

        Assert.Equal(0, LegacyPlacements.ResolveGuids(placements,
            DatabaseWith(Asset(Guid.NewGuid(), 3, "Resource"))));
        Assert.Equal(Guid.Empty, placements[0].Guid);
    }

    [Fact]
    public void ResolveGuids_DoesNotFollowVehicleIds()
    {
        // Vehicles are indexed separately (a vehicle and an object may share a legacy id), and a tree or
        // object placement must never resolve into that table.
        var placements = new List<PlacedObject> { Placement(id: 5, guid: Guid.Empty) };

        Assert.Equal(0, LegacyPlacements.ResolveGuids(placements,
            DatabaseWith(Asset(Guid.NewGuid(), 5, "SDG.Unturned.VehicleAsset, Assembly-CSharp"))));
        Assert.Equal(Guid.Empty, placements[0].Guid);
    }

    // --- Where a tree actually stands (ResourceSpawnpoint.cs:484) ---

    private static PlacedTree TreeAt(Guid guid, Vector3 position, Vector3 scale, bool legacy = false) =>
        new(position, Vector3.Zero, scale, guid, id: 0, needsLegacyRotationAndScale: legacy);

    [Fact]
    public void AppendTrees_SinksTheModelByTheAssetsVerticalOffset()
    {
        // The two mushrooms are the reason this reads the field instead of using the -0.75 default: they
        // are LIFTED by 0.1, and a hardcoded constant would bury them 0.85 m under the ground.
        var mushroom = Guid.NewGuid();
        var pine = Guid.NewGuid();
        var placements = new List<PlacedObject>();
        var db = DatabaseWith(
            Asset(mushroom, 10, "Resource", "Vertical_Offset 0.1\n"),
            Asset(pine, 11, "Resource"));

        LegacyPlacements.AppendTrees(
            new[]
            {
                TreeAt(mushroom, new Vector3(0, 100, 0), Vector3.One),
                TreeAt(pine, new Vector3(0, 100, 0), Vector3.One),
            },
            placements, db, HolidayPolicy.None);

        Assert.Equal(100.1f, placements[0].Position.Y, 4);
        Assert.Equal(99.25f, placements[1].Position.Y, 4);
    }

    [Fact]
    public void AppendTrees_ScalesTheVerticalOffsetByTheTreesOwnHeight()
    {
        // `Vector3.up * scale.y * verticalOffset`: a pine drawn at double size sinks twice as far, which
        // is what keeps the root ball buried by the same fraction of the trunk rather than by a fixed
        // 0.75 m that would leave a big one hovering.
        var pine = Guid.NewGuid();
        var placements = new List<PlacedObject>();

        LegacyPlacements.AppendTrees(new[] { TreeAt(pine, new Vector3(0, 100, 0), new Vector3(2, 2, 2)) },
            placements, DatabaseWith(Asset(pine, 11, "Resource")), HolidayPolicy.None);

        Assert.Equal(100f - 1.5f, Assert.Single(placements).Position.Y, 4);
    }

    [Fact]
    public void AppendTrees_LeavesATreeWithNoAssetWhereTheFilePutIt()
    {
        // No asset means no offset to apply; the placement still takes its place as a placeholder.
        var placements = new List<PlacedObject>();

        LegacyPlacements.AppendTrees(new[] { TreeAt(Guid.NewGuid(), new Vector3(0, 100, 0), Vector3.One) },
            placements, DatabaseWith(), HolidayPolicy.None);

        Assert.Equal(100f, Assert.Single(placements).Position.Y);
    }

    // --- The jitter a pre-v8 Trees.dat owes its trees (LevelGround.cs:782) ---

    [Fact]
    public void AppendTrees_JittersATreeFromAFileWithNoRotationOrScale()
    {
        var pine = Guid.NewGuid();
        var placements = new List<PlacedObject>();
        var db = DatabaseWith(Asset(pine, 11, "Resource", "RandomUniformScale_Min 1\nRandomUniformScale_Max 1.55\n"));

        LegacyPlacements.AppendTrees(new[] { TreeAt(pine, new Vector3(13, 100, 27), Vector3.One, legacy: true) },
            placements, db, HolidayPolicy.None);

        PlacedObject placed = Assert.Single(placements);
        // seed = sin((13+4096)*32 + (27+4096)*32); the port must land on the same value the game does.
        float seed = MathF.Sin(((13f + 4096f) * 32f) + ((27f + 4096f) * 32f));
        float weight = (seed + 1f) * 0.5f;
        Assert.Equal(1f + ((1.55f - 1f) * weight), placed.Scale.X, 5);
        Assert.Equal(placed.Scale.X, placed.Scale.Y);   // uniform, all three axes
        Assert.Equal(placed.Scale.X, placed.Scale.Z);
        Assert.Equal(-5f + (10f * weight), placed.EulerDegrees.X, 5);  // default ±5 deviation
        Assert.Equal(seed * 360f, placed.EulerDegrees.Y, 3);
        Assert.Equal(0f, placed.EulerDegrees.Z);
        // The offset rides the jittered scale, not the unit scale the file carried.
        Assert.Equal(100f - (0.75f * placed.Scale.Y), placed.Position.Y, 4);
    }

    [Fact]
    public void AppendTrees_JitterIsDeterministicPerPosition()
    {
        // The whole point of the sine: a server and a client that never exchange a tree's transform still
        // draw the same forest. Two trees at the same spot must agree, and two elsewhere must not.
        var pine = Guid.NewGuid();
        var db = DatabaseWith(Asset(pine, 11, "Resource", "Scale 0.5\n"));
        var placements = new List<PlacedObject>();

        LegacyPlacements.AppendTrees(
            new[]
            {
                TreeAt(pine, new Vector3(13, 0, 27), Vector3.One, legacy: true),
                TreeAt(pine, new Vector3(13, 0, 27), Vector3.One, legacy: true),
                TreeAt(pine, new Vector3(14, 0, 27), Vector3.One, legacy: true),
            },
            placements, db, HolidayPolicy.None);

        Assert.Equal(placements[0].Scale, placements[1].Scale);
        Assert.Equal(placements[0].EulerDegrees, placements[1].EulerDegrees);
        Assert.NotEqual(placements[0].EulerDegrees.Y, placements[2].EulerDegrees.Y);
        // Legacy "Scale 0.5" means the range 1.1 .. 1.1 + 2*0.5, so nothing lands below 1.1.
        Assert.InRange(placements[0].Scale.X, 1.1f, 2.1f);
    }

    [Fact]
    public void AppendTrees_LeavesAModernFilesOwnRotationAndScaleAlone()
    {
        // Version 8 baked the transform into the file; deriving one would overwrite what the author saved.
        var pine = Guid.NewGuid();
        var placements = new List<PlacedObject>();
        var authored = new PlacedTree(new Vector3(13, 100, 27), new Vector3(0, 42, 0), new Vector3(3, 3, 3),
            pine);

        LegacyPlacements.AppendTrees(new[] { authored }, placements,
            DatabaseWith(Asset(pine, 11, "Resource", "Scale 0.5\n")), HolidayPolicy.None);

        Assert.Equal(new Vector3(0, 42, 0), Assert.Single(placements).EulerDegrees);
        Assert.Equal(new Vector3(3, 3, 3), placements[0].Scale);
    }

    [Fact]
    public void AppendTrees_DoesNotJitterAnIdOnlyTree()
    {
        // Unturned looks the jitter asset up with `Assets.find(guid)` and nothing else (LevelGround.cs:869),
        // while the legacy id is not resolved until ResourceSpawnpoint runs several lines later. So a
        // Trees.dat older than version 6, which stores no GUID at all, really does keep identity and unit
        // scale in the real game — Monolith's 1,810 trees stand dead straight there too. The offset still
        // applies, because ResourceSpawnpoint DOES resolve the id before reaching for verticalOffset.
        var pine = Guid.NewGuid();
        var placements = new List<PlacedObject>();
        var idOnly = new PlacedTree(new Vector3(13, 100, 27), Vector3.Zero, Vector3.One, Guid.Empty,
            id: 11, needsLegacyRotationAndScale: true);

        LegacyPlacements.AppendTrees(new[] { idOnly }, placements,
            DatabaseWith(Asset(pine, 11, "Resource", "Scale 0.5\n")), HolidayPolicy.None);

        PlacedObject placed = Assert.Single(placements);
        Assert.Equal(Vector3.Zero, placed.EulerDegrees);
        Assert.Equal(Vector3.One, placed.Scale);
        Assert.Equal(99.25f, placed.Position.Y, 4);
    }

    // --- Holiday restriction (LevelObject.cs:428, ResourceSpawnpoint.cs:539) ---

    [Fact]
    public void ResolveGuids_HidesAnObjectRestrictedToAnotherHoliday()
    {
        var xmas = Guid.NewGuid();
        var ordinary = Guid.NewGuid();
        var db = DatabaseWith(Asset(xmas, 1, "Medium", "Holiday_Restriction CHRISTMAS\n"), Asset(ordinary, 2));
        var placements = new List<PlacedObject> { Placement(0, xmas), Placement(0, ordinary) };

        LegacyPlacements.ResolveGuids(placements, db, HolidayPolicy.None);

        Assert.Equal(ordinary, Assert.Single(placements).Guid);
    }

    [Fact]
    public void ResolveGuids_KeepsARestrictedObjectDuringItsOwnHoliday()
    {
        var xmas = Guid.NewGuid();
        var spooky = Guid.NewGuid();
        var db = DatabaseWith(
            Asset(xmas, 1, "Medium", "Holiday_Restriction CHRISTMAS\n"),
            Asset(spooky, 2, "Medium", "Holiday_Restriction HALLOWEEN\n"));
        var placements = new List<PlacedObject> { Placement(0, xmas), Placement(0, spooky) };

        // One holiday is active at a time, so the wrong one is hidden even in December.
        LegacyPlacements.ResolveGuids(placements, db, new HolidayPolicy(ENPCHoliday.Christmas, false));

        Assert.Equal(xmas, Assert.Single(placements).Guid);
    }

    [Fact]
    public void AppendTrees_HidesARestrictedTree()
    {
        // A resource carries the same key and ResourceSpawnpoint gates on it identically; five of the
        // game's 69 tree assets have one.
        var spooky = Guid.NewGuid();
        var placements = new List<PlacedObject>();

        LegacyPlacements.AppendTrees(new[] { TreeAt(spooky, new Vector3(0, 0, 0), Vector3.One) }, placements,
            DatabaseWith(Asset(spooky, 10, "Resource", "Holiday_Restriction HALLOWEEN\n")), HolidayPolicy.None);

        Assert.Empty(placements);
    }

    [Fact]
    public void ResolveGuids_LeavesAPlacementWithNoAssetAloneWhateverTheHoliday()
    {
        // Nothing to read a restriction off: the placement survives as the placeholder it already was.
        var placements = new List<PlacedObject> { Placement(0, Guid.NewGuid()) };

        LegacyPlacements.ResolveGuids(placements, DatabaseWith(), new HolidayPolicy(ENPCHoliday.Christmas, false));

        Assert.Single(placements);
    }

    // --- Holiday substitution (LevelObjects.cs:1270, LevelGround.cs:1546) ---

    private static ObjectAssetDatabase RedirectingDatabase(Guid from, Guid to, string type = "Medium") =>
        DatabaseWith(Asset(from, 1, type, $"Christmas_Redirect {to:N}\n"), Asset(to, 2, type));

    [Fact]
    public void ResolveGuids_SubstitutesTheHolidayVariantAndItsId()
    {
        var plain = Guid.NewGuid();
        var festive = Guid.NewGuid();
        var placements = new List<PlacedObject> { Placement(1, plain) };

        LegacyPlacements.ResolveGuids(placements, RedirectingDatabase(plain, festive),
            new HolidayPolicy(ENPCHoliday.Christmas, allowRedirects: true));

        // "id = redirect.id; GUID = redirect.GUID" — the target's legacy id replaces the original's.
        Assert.Equal(festive, Assert.Single(placements).Guid);
        Assert.Equal(2, placements[0].Id);
    }

    [Fact]
    public void ResolveGuids_LeavesTheOriginalWhenTheMapDidNotAllowRedirects()
    {
        // Allow_Holiday_Redirects is the map's own opt-in; without it December changes nothing.
        var plain = Guid.NewGuid();
        var festive = Guid.NewGuid();
        var placements = new List<PlacedObject> { Placement(1, plain) };

        LegacyPlacements.ResolveGuids(placements, RedirectingDatabase(plain, festive),
            new HolidayPolicy(ENPCHoliday.Christmas, allowRedirects: false));

        Assert.Equal(plain, Assert.Single(placements).Guid);
    }

    [Fact]
    public void ResolveGuids_LeavesAnAssetWithNoRedirectForThisHoliday()
    {
        // "Does not have a redirect for this event, so use the original." A Christmas-only redirect is
        // not followed at Halloween, and the object stays exactly as authored.
        var plain = Guid.NewGuid();
        var festive = Guid.NewGuid();
        var placements = new List<PlacedObject> { Placement(1, plain) };

        LegacyPlacements.ResolveGuids(placements, RedirectingDatabase(plain, festive),
            new HolidayPolicy(ENPCHoliday.Halloween, allowRedirects: true));

        Assert.Equal(plain, Assert.Single(placements).Guid);
    }

    [Fact]
    public void ResolveGuids_DropsAPlacementWhoseRedirectTargetIsMissing()
    {
        // Unturned logs "Missing holiday redirect" and leaves the asset null, which the loader reads as
        // "skip this placement" — so an install missing the variant loses the object rather than drawing
        // the wrong one.
        var plain = Guid.NewGuid();
        var placements = new List<PlacedObject> { Placement(1, plain) };
        var db = DatabaseWith(Asset(plain, 1, "Medium", $"Christmas_Redirect {Guid.NewGuid():N}\n"));

        LegacyPlacements.ResolveGuids(placements, db,
            new HolidayPolicy(ENPCHoliday.Christmas, allowRedirects: true));

        Assert.Empty(placements);
    }

    [Fact]
    public void AppendTrees_DropsATreeWhoseRedirectResolvesToNothing()
    {
        // The line that makes a forest thin out by season: 32 of the game's 69 tree assets carry a
        // redirect, and a tree whose substitute cannot be found leaves the list entirely.
        var pine = Guid.NewGuid();
        var placements = new List<PlacedObject>();
        var db = DatabaseWith(Asset(pine, 30, "Resource", $"Christmas_Redirect {Guid.NewGuid():N}\n"));

        LegacyPlacements.AppendTrees(new[] { TreeAt(pine, Vector3.Zero, Vector3.One) }, placements, db,
            new HolidayPolicy(ENPCHoliday.Christmas, allowRedirects: true));

        Assert.Empty(placements);
    }

    [Fact]
    public void AppendTrees_SubstitutesASnowyVariantAndTakesItsOffset()
    {
        var pine = Guid.NewGuid();
        var snowy = Guid.NewGuid();
        var placements = new List<PlacedObject>();
        var db = DatabaseWith(
            Asset(pine, 30, "Resource", $"Christmas_Redirect {snowy:N}\n"),
            Asset(snowy, 31, "Resource", "Vertical_Offset -2\n"));

        LegacyPlacements.AppendTrees(new[] { TreeAt(pine, new Vector3(0, 100, 0), Vector3.One) }, placements,
            db, new HolidayPolicy(ENPCHoliday.Christmas, allowRedirects: true));

        // The offset comes off the asset that is actually placed, not the one the file named.
        Assert.Equal(snowy, Assert.Single(placements).Guid);
        Assert.Equal(98f, placements[0].Position.Y, 4);
    }

    [Fact]
    public void AppendTrees_DropsATreeWhoseGuidNamesAnObjectRatherThanAResource()
    {
        // `Assets.find(originalId) as ResourceAsset` returns null for anything that is not a resource,
        // and null is the drop. The object/resource id namespaces already collide in this file; this is
        // the same hazard one level up, on the GUID.
        var notATree = Guid.NewGuid();
        var placements = new List<PlacedObject>();

        LegacyPlacements.AppendTrees(new[] { TreeAt(notATree, Vector3.Zero, Vector3.One) }, placements,
            DatabaseWith(Asset(notATree, 1, "Medium")),
            new HolidayPolicy(ENPCHoliday.Christmas, allowRedirects: true));

        Assert.Empty(placements);
    }

    [Fact]
    public void ResolveGuids_DropsAnIdOnlyPlacementWhileSubstitutionIsOn()
    {
        // The game's own sharp edge, kept rather than smoothed: the substitution runs on the GUID the
        // file carries, nothing resolves Guid.Empty, and so an id-only placement is dropped. Unreachable
        // in practice — id-only means an Objects.dat older than version 8, and those maps predate the
        // Config.json that would have to say Allow_Holiday_Redirects.
        var placements = new List<PlacedObject> { Placement(42, Guid.Empty) };

        LegacyPlacements.ResolveGuids(placements, DatabaseWith(Asset(Guid.NewGuid(), 42)),
            new HolidayPolicy(ENPCHoliday.Christmas, allowRedirects: true));

        Assert.Empty(placements);
    }

    [Fact]
    public void ResolveGuids_ResolvesEachGuidOnceHoweverOftenItIsPlaced()
    {
        // The redirector maps are memoized per load because a map places the same few hundred GUIDs
        // thousands of times; the answer has to be the same every time regardless.
        var plain = Guid.NewGuid();
        var festive = Guid.NewGuid();
        var placements = new List<PlacedObject>();
        for (int i = 0; i < 50; i++)
            placements.Add(Placement(1, plain));

        LegacyPlacements.ResolveGuids(placements, RedirectingDatabase(plain, festive),
            new HolidayPolicy(ENPCHoliday.Christmas, allowRedirects: true));

        Assert.Equal(50, placements.Count);
        Assert.All(placements, p => Assert.Equal(festive, p.Guid));
    }

    [Fact]
    public void AppendTrees_KeepsATreeWithNoIdentityAtAll()
    {
        // LevelTrees never emits one — it drops a tree with neither a GUID nor an id — but AppendTrees
        // takes any list, and a placement with nothing to look an asset up by must not throw: it lands
        // where the file put it, unoffset, as the placeholder it is.
        var placements = new List<PlacedObject>();
        var nameless = new PlacedTree(new Vector3(0, 100, 0), Vector3.Zero, Vector3.One, Guid.Empty, id: 0);

        Assert.Equal(0, LegacyPlacements.AppendTrees(new[] { nameless }, placements, DatabaseWith(),
            HolidayPolicy.None));

        Assert.Equal(100f, Assert.Single(placements).Position.Y);
        Assert.Equal(Guid.Empty, placements[0].Guid);
    }
}
