using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class LevelNodesTests
{
    private static List<LocationNode> Load(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"nodes-{System.Guid.NewGuid():N}.dat");
        File.WriteAllBytes(path, bytes);
        try
        {
            return LevelNodes.LoadLocations(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadLocations_ReturnsOnlyLocations_AdvancingPastVolumes()
    {
        // Version 9: every optional field is present. Interleave the volume node types between two
        // LOCATION nodes so the second only parses correctly if each volume advanced the reader exactly.
        byte[] bytes = new RiverBytes()
            .Byte(9)          // version
            .Byte(8)          // node count
            .Vector3(new Vector3(10, 20, 30)).Byte(0).Str("Town")           // LOCATION
            .Vector3(Vector3.Zero).Byte(1).Single(50f).Bool(true).Bool(true).Bool(false) // SAFEZONE
            .Vector3(Vector3.Zero).Byte(2).Single(1f).UInt16(3).UInt32(99)  // PURCHASE
            .Vector3(Vector3.Zero).Byte(3).Single(40f)                      // ARENA
            .Vector3(Vector3.Zero).Byte(4).Single(60f).Byte(1)              // DEADZONE (+ type, v>6)
            .Vector3(Vector3.Zero).Byte(5).UInt16(7)                        // AIRDROP
            .Vector3(Vector3.Zero).Byte(6).Byte(2).Single(5f).Vector3(Vector3.One).UInt16(4).Bool(false).Bool(true) // EFFECT
            .Vector3(new Vector3(1, 2, 3)).Byte(0).Str("Bridge")           // LOCATION
            .ToArray();

        List<LocationNode> locations = Load(bytes);

        Assert.Equal(2, locations.Count);
        Assert.Equal("Town", locations[0].Name);
        Assert.Equal(new Vector3(10, 20, -30), locations[0].Position); // Unity -> Godot negates Z
        Assert.Equal("Bridge", locations[1].Name);
        Assert.Equal(new Vector3(1, 2, -3), locations[1].Position);
    }

    [Fact]
    public void LoadLocations_OldVersion_SkipsAbsentOptionalFields()
    {
        // Version 1: none of the version-gated fields are present. The trailing LOCATION only aligns if the
        // volumes were parsed with the v1 layout.
        byte[] bytes = new RiverBytes()
            .Byte(1)          // version
            .Byte(4)          // node count
            .Vector3(Vector3.Zero).Byte(1).Single(50f)                     // SAFEZONE (radius only)
            .Vector3(Vector3.Zero).Byte(4).Single(60f)                     // DEADZONE (no type)
            .Vector3(Vector3.Zero).Byte(6).Single(5f).UInt16(4).Bool(false) // EFFECT (no shape/bounds/noLighting)
            .Vector3(new Vector3(5, 6, 7)).Byte(0).Str("Old Town")         // LOCATION
            .ToArray();

        LocationNode loc = Assert.Single(Load(bytes));
        Assert.Equal("Old Town", loc.Name);
        Assert.Equal(new Vector3(5, 6, -7), loc.Position);
    }

    [Fact]
    public void LoadLocations_UnknownType_StopsAtThatNode()
    {
        byte[] bytes = new RiverBytes()
            .Byte(9).Byte(3)
            .Vector3(Vector3.Zero).Byte(0).Str("Kept")   // LOCATION
            .Vector3(Vector3.Zero).Byte(99)              // unknown type -> stop
            .Vector3(Vector3.Zero).Byte(0).Str("Lost")   // never reached
            .ToArray();

        LocationNode loc = Assert.Single(Load(bytes));
        Assert.Equal("Kept", loc.Name);
    }

    [Fact]
    public void LoadLocations_VersionZero_ReturnsEmpty() =>
        Assert.Empty(Load(new RiverBytes().Byte(0).ToArray()));

    [Fact]
    public void LoadLocations_MissingFile_ReturnsEmpty() =>
        Assert.Empty(LevelNodes.LoadLocations(Path.Combine(Path.GetTempPath(), "no-such-nodes.dat")));

    // ---- the volumes, which used to be parsed only to advance the cursor -------------------------

    private static LevelNodeSet LoadSet(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"nodes-{System.Guid.NewGuid():N}.dat");
        File.WriteAllBytes(path, bytes);
        try
        {
            return LevelNodes.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The same v9 document as the first test, now asserted on the fields it carries rather than only on
    // the cursor having landed in the right place afterwards.
    [Fact]
    public void Load_KeepsEveryVolumesData()
    {
        LevelNodeSet nodes = LoadSet(new RiverBytes()
            .Byte(9)
            .Byte(8)
            .Vector3(new Vector3(10, 20, 30)).Byte(0).Str("Town")
            .Vector3(new Vector3(1, 2, 3)).Byte(1).Single(50f).Bool(true).Bool(true).Bool(false)
            .Vector3(new Vector3(4, 5, 6)).Byte(2).Single(1f).UInt16(3).UInt32(99)
            .Vector3(new Vector3(7, 8, 9)).Byte(3).Single(40f)
            .Vector3(new Vector3(11, 12, 13)).Byte(4).Single(60f).Byte(1)
            .Vector3(new Vector3(14, 15, 16)).Byte(5).UInt16(7)
            .Vector3(new Vector3(17, 18, 19)).Byte(6).Byte(2).Single(5f).Vector3(Vector3.One)
                .UInt16(4).Bool(false).Bool(true)
            .Vector3(new Vector3(1, 2, 3)).Byte(0).Str("Bridge")
            .ToArray());

        Assert.Equal(2, nodes.Locations.Count);

        SafezoneNode safezone = Assert.Single(nodes.Safezones);
        Assert.Equal(new Vector3(1, 2, -3), safezone.Position); // Unity -> Godot negates Z
        Assert.Equal(50f, safezone.Radius);
        Assert.True(safezone.IsHeight);
        Assert.True(safezone.NoWeapons);
        Assert.False(safezone.NoBuildables);

        PurchaseNode purchase = Assert.Single(nodes.Purchases);
        Assert.Equal(1f, purchase.Radius);
        Assert.Equal(3, purchase.Id);
        Assert.Equal(99u, purchase.Cost);

        Assert.Equal(40f, Assert.Single(nodes.Arenas).Radius);

        DeadzoneNode deadzone = Assert.Single(nodes.Deadzones);
        Assert.Equal(60f, deadzone.Radius);
        Assert.Equal(EDeadzoneType.Radiation, deadzone.Type);

        Assert.Equal(7, Assert.Single(nodes.Airdrops).SpawnTableId);

        EffectNode effect = Assert.Single(nodes.Effects);
        Assert.Equal(2, effect.Shape);
        Assert.Equal(5f, effect.Radius);
        Assert.Equal(Vector3.One, effect.Bounds);
        Assert.Equal(4, effect.EffectId);
        Assert.False(effect.NoWater);
        Assert.True(effect.NoLighting);
    }

    // The version-gated fields take their absent defaults on an old document rather than reading
    // whatever byte happens to follow.
    [Fact]
    public void Load_OldVersion_TakesTheAbsentDefaults()
    {
        LevelNodeSet nodes = LoadSet(new RiverBytes()
            .Byte(1)
            .Byte(3)
            .Vector3(Vector3.Zero).Byte(1).Single(50f)
            .Vector3(Vector3.Zero).Byte(4).Single(60f)
            .Vector3(Vector3.Zero).Byte(6).Single(5f).UInt16(4).Bool(false)
            .ToArray());

        SafezoneNode safezone = Assert.Single(nodes.Safezones);
        Assert.False(safezone.IsHeight);
        Assert.False(safezone.NoWeapons);
        Assert.False(safezone.NoBuildables);

        Assert.Equal(EDeadzoneType.Default, Assert.Single(nodes.Deadzones).Type);

        EffectNode effect = Assert.Single(nodes.Effects);
        Assert.Equal(0, effect.Shape);
        Assert.Equal(Vector3.Zero, effect.Bounds);
        Assert.False(effect.NoLighting);
    }

    // Node.isPointInside: a sphere when isHeight, otherwise the XZ disc at any height — which is what a
    // town safezone wants, so a building's roof is inside it too.
    [Fact]
    public void Safezone_ContainsIsASphereOnlyWhenBounded()
    {
        var bounded = new SafezoneNode(Vector3.Zero, 10f, isHeight: true, false, false);
        var cylinder = new SafezoneNode(Vector3.Zero, 10f, isHeight: false, false, false);

        Assert.True(bounded.Contains(new Vector3(5, 0, 0)));
        Assert.False(bounded.Contains(new Vector3(0, 50, 0)));   // far above: outside the sphere
        Assert.True(cylinder.Contains(new Vector3(0, 50, 0)));   // still inside the column
        Assert.False(cylinder.Contains(new Vector3(50, 0, 0)));
        Assert.False(bounded.Contains(new Vector3(10, 0, 0)));   // strictly inside, as "< radius" is
    }

    [Fact]
    public void Deadzone_ContainsIsAlwaysASphere()
    {
        var zone = new DeadzoneNode(new Vector3(100, 0, 0), 10f, EDeadzoneType.Radiation);

        Assert.True(zone.Contains(new Vector3(105, 0, 0)));
        Assert.False(zone.Contains(new Vector3(100, 50, 0)));
        Assert.False(zone.Contains(Vector3.Zero));
    }

    // SafezoneManager.checkPointValid, which is the gate the zombie respawn and the punch both want.
    [Fact]
    public void SafezoneLookups_FindTheContainingZone()
    {
        LevelNodeSet nodes = LoadSet(new RiverBytes()
            .Byte(9).Byte(2)
            .Vector3(new Vector3(0, 0, 0)).Byte(1).Single(10f).Bool(true).Bool(true).Bool(false)
            .Vector3(new Vector3(100, 0, 0)).Byte(1).Single(10f).Bool(true).Bool(false).Bool(true)
            .ToArray());

        Assert.True(nodes.IsPointInSafezone(new Vector3(1, 0, 0)));
        Assert.False(nodes.IsPointInSafezone(new Vector3(50, 0, 0)));

        SafezoneNode? first = nodes.SafezoneAt(new Vector3(1, 0, 0));
        Assert.NotNull(first);
        Assert.True(first!.Value.NoWeapons);

        SafezoneNode? second = nodes.SafezoneAt(new Vector3(101, 0, 0));
        Assert.NotNull(second);
        Assert.False(second!.Value.NoWeapons);
        Assert.True(second.Value.NoBuildables);

        Assert.Null(nodes.SafezoneAt(new Vector3(50, 0, 0)));
    }

    [Fact]
    public void DeadzoneAt_FindsOrMisses()
    {
        LevelNodeSet nodes = LoadSet(new RiverBytes()
            .Byte(9).Byte(1)
            .Vector3(new Vector3(0, 0, 0)).Byte(4).Single(10f).Byte(2)
            .ToArray());

        Assert.Equal(EDeadzoneType.Bloodthirst, nodes.DeadzoneAt(new Vector3(1, 0, 0))!.Value.Type);
        Assert.Null(nodes.DeadzoneAt(new Vector3(500, 0, 0)));
    }

    [Fact]
    public void Load_MissingFile_IsAnEmptySet()
    {
        LevelNodeSet nodes = LevelNodes.Load(Path.Combine(Path.GetTempPath(), "no-such-nodes.dat"));
        Assert.Empty(nodes.Safezones);
        Assert.False(nodes.IsPointInSafezone(Vector3.Zero));
        Assert.Null(nodes.SafezoneAt(Vector3.Zero));
        Assert.Null(nodes.DeadzoneAt(Vector3.Zero));
    }

    [Fact]
    public void Load_VersionZero_IsAnEmptySet() =>
        Assert.Empty(LoadSet(new RiverBytes().Byte(0).ToArray()).Safezones);

    [Fact]
    public void Load_UnknownType_KeepsWhatItReadFirst()
    {
        LevelNodeSet nodes = LoadSet(new RiverBytes()
            .Byte(9).Byte(3)
            .Vector3(Vector3.Zero).Byte(1).Single(10f).Bool(true).Bool(true).Bool(true)
            .Vector3(Vector3.Zero).Byte(99)
            .Vector3(Vector3.Zero).Byte(1).Single(20f).Bool(true).Bool(true).Bool(true)
            .ToArray());

        Assert.Equal(10f, Assert.Single(nodes.Safezones).Radius);
    }

    // PEI's own nodes. The locations were already read; the point is that the rest is kept now.
    [RealDataFact(Map = "PEI")]
    public void RealPeiNodes_ParseWithoutOverreading()
    {
        LevelNodeSet nodes = LevelNodes.Load(
            Path.Combine(GameData.Map("PEI")!, "Environment", "Nodes.dat"));

        Assert.NotEmpty(nodes.Locations);
        // Whatever PEI ships, the parse has to have landed: an over-read would have produced a wild
        // radius rather than a plausible one, so every volume's radius is checked for sanity.
        Assert.All(nodes.Safezones, zone => Assert.InRange(zone.Radius, 0f, 4096f));
        Assert.All(nodes.Deadzones, zone => Assert.InRange(zone.Radius, 0f, 4096f));
        Assert.All(nodes.Effects, effect => Assert.InRange(effect.Radius, 0f, 4096f));
        Assert.All(nodes.Arenas, arena => Assert.InRange(arena.Radius, 0f, 4096f));
    }
}
