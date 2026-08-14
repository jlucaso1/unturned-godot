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
    //
    // Every radius in the file is a 0..1 SLIDER — the sizes below are half-way ones — and each type
    // turns it into metres through its own end points: Lerp(MIN_SIZE, MAX_SIZE, t) * 0.5.
    [Fact]
    public void Load_KeepsEveryVolumesData()
    {
        LevelNodeSet nodes = LoadSet(new RiverBytes()
            .Byte(9)
            .Byte(8)
            .Vector3(new Vector3(10, 20, 30)).Byte(0).Str("Town")
            .Vector3(new Vector3(1, 2, 3)).Byte(1).Single(0.5f).Bool(true).Bool(true).Bool(false)
            .Vector3(new Vector3(4, 5, 6)).Byte(2).Single(0.5f).UInt16(3).UInt32(99)
            .Vector3(new Vector3(7, 8, 9)).Byte(3).Single(0.5f)
            .Vector3(new Vector3(11, 12, 13)).Byte(4).Single(0.5f).Byte(1)
            .Vector3(new Vector3(14, 15, 16)).Byte(5).UInt16(7)
            .Vector3(new Vector3(17, 18, 19)).Byte(6).Byte(2).Single(0.5f).Vector3(Vector3.One)
                .UInt16(4).Bool(false).Bool(true)
            .Vector3(new Vector3(1, 2, 3)).Byte(0).Str("Bridge")
            .ToArray());

        Assert.Equal(2, nodes.Locations.Count);

        SafezoneNode safezone = Assert.Single(nodes.Safezones);
        Assert.Equal(new Vector3(1, 2, -3), safezone.Position); // Unity -> Godot negates Z
        Assert.Equal(0.5f, safezone.NormalizedRadius);
        Assert.Equal(264f, safezone.Radius); // (32 + 1024) / 2 / 2
        Assert.True(safezone.IsHeight);
        Assert.True(safezone.NoWeapons);
        Assert.False(safezone.NoBuildables);

        PurchaseNode purchase = Assert.Single(nodes.Purchases);
        Assert.Equal(0.5f, purchase.NormalizedRadius);
        Assert.Equal(4.5f, purchase.Radius); // (2 + 16) / 2 / 2
        Assert.Equal(3, purchase.Id);
        Assert.Equal(99u, purchase.Cost);

        Assert.Equal(2080f, Assert.Single(nodes.Arenas).Radius); // (128 + 8192) / 2 / 2

        DeadzoneNode deadzone = Assert.Single(nodes.Deadzones);
        Assert.Equal(264f, deadzone.Radius);
        Assert.Equal(EDeadzoneType.Radiation, deadzone.Type);

        Assert.Equal(7, Assert.Single(nodes.Airdrops).SpawnTableId);

        EffectNode effect = Assert.Single(nodes.Effects);
        Assert.Equal(2, effect.Shape);
        Assert.Equal(0.5f, effect.NormalizedRadius);
        Assert.Equal(66f, effect.Radius); // (8 + 256) / 2 / 2
        Assert.Equal(Vector3.One, effect.Bounds);
        Assert.Equal(4, effect.EffectId);
        Assert.False(effect.NoWater);
        Assert.True(effect.NoLighting);
    }

    // "Max diameter was doubled from 4096 to 8192 in v6", so an older file's slider is halved before it
    // meets the new end points and lands on the volume it was authored as.
    [Theory]
    [InlineData(6, 2080f)] // Lerp(128, 8192, 0.5) / 2
    [InlineData(5, 1072f)] // ...and the same file at v5 reads the slider as 0.25: Lerp(128, 8192, 0.25) / 2
    public void Load_ArenaSliderIsHalvedBeforeVersionSix(byte version, float expected)
    {
        LevelNodeSet nodes = LoadSet(new RiverBytes()
            .Byte(version).Byte(1)
            .Vector3(Vector3.Zero).Byte(3).Single(0.5f)
            .ToArray());

        Assert.Equal(expected, Assert.Single(nodes.Arenas).Radius);
    }

    // The version-gated fields take their absent defaults on an old document rather than reading
    // whatever byte happens to follow.
    [Fact]
    public void Load_OldVersion_TakesTheAbsentDefaults()
    {
        LevelNodeSet nodes = LoadSet(new RiverBytes()
            .Byte(1)
            .Byte(3)
            .Vector3(Vector3.Zero).Byte(1).Single(0.5f)
            .Vector3(Vector3.Zero).Byte(4).Single(0.5f)
            .Vector3(Vector3.Zero).Byte(6).Single(0.5f).UInt16(4).Bool(false)
            .ToArray());

        SafezoneNode safezone = Assert.Single(nodes.Safezones);
        Assert.False(safezone.IsHeight);
        // TRUE for a file too old to carry them — "bool noWeapons = true;" in LevelNodes.load, only
        // overwritten past version 4. A safezone predating the flags is the strictest kind, and reading
        // them as false quietly re-armed the fist inside every one of them.
        Assert.True(safezone.NoWeapons);
        Assert.True(safezone.NoBuildables);

        Assert.Equal(EDeadzoneType.Default, Assert.Single(nodes.Deadzones).Type);

        EffectNode effect = Assert.Single(nodes.Effects);
        Assert.Equal(0, effect.Shape);
        Assert.Equal(Vector3.Zero, effect.Bounds);
        Assert.False(effect.NoLighting);
    }

    // An ordinary safezone is a SPHERE of the radius its slider resolves to — that is what
    // AutoConvertLegacyVolumes builds from one (SetSphereRadius + ELevelVolumeShape.Sphere), and the
    // slider's floor of zero is already a 16-metre ball rather than a point.
    [Fact]
    public void Safezone_ContainsIsASphereOfTheResolvedRadius()
    {
        var zone = new SafezoneNode(Vector3.Zero, 0f, isHeight: false, false, false);

        Assert.Equal(16f, zone.Radius); // Lerp(32, 1024, 0) / 2
        Assert.True(zone.Contains(new Vector3(5, 0, 0)));
        Assert.True(zone.Contains(new Vector3(0, 5, 0)));
        Assert.False(zone.Contains(new Vector3(0, 50, 0)));  // a sphere, so height leaves it
        Assert.False(zone.Contains(new Vector3(16, 0, 0)));  // strictly inside, as "< radius" is
    }

    // isHeight is not a vertical bound but the paintball arena's "infinite plane above the point",
    // which the game approximates with a box 10000 x 2000 x 10000 centred a kilometre above the node.
    // Read as a bounded sphere it was the opposite volume, and read as an infinite cylinder it still
    // covered the ground the node stands on — which the box does not.
    [Fact]
    public void Safezone_IsHeightIsTheGiantBoxAboveThePoint()
    {
        var zone = new SafezoneNode(new Vector3(0, 100, 0), 0f, isHeight: true, false, false);

        Assert.True(zone.Contains(new Vector3(0, 100, 0)));    // the node itself: the box's floor
        Assert.True(zone.Contains(new Vector3(0, 2000, 0)));   // most of the way up it
        Assert.False(zone.Contains(new Vector3(0, 99, 0)));    // a metre below: outside
        Assert.False(zone.Contains(new Vector3(0, 2101, 0)));  // and past the two-kilometre ceiling
        Assert.True(zone.Contains(new Vector3(4999, 500, -4999)));
        Assert.False(zone.Contains(new Vector3(5001, 500, 0)));
        // The radius plays no part in it, which is why the shipped one ships with a slider of zero.
        Assert.True(new SafezoneNode(Vector3.Zero, 1f, isHeight: true, false, false)
            .Contains(new Vector3(0, 1000, 0)));
    }

    // Unity's Mathf.Lerp clamps its weight and Godot's does not, so a slider outside 0..1 — a truncated
    // or hand-edited file — has to land on the end point rather than extrapolating past it.
    [Theory]
    [InlineData(0f, 16f)]
    [InlineData(1f, 512f)]
    [InlineData(10f, 512f)]   // clamped, not a four-kilometre safezone
    [InlineData(-5f, 16f)]
    public void Safezone_RadiusIsClampedToTheSlidersEnds(float slider, float expected) =>
        Assert.Equal(expected, new SafezoneNode(Vector3.Zero, slider, false, false, false).Radius);

    [Fact]
    public void Deadzone_ContainsIsAlwaysASphere()
    {
        var zone = new DeadzoneNode(new Vector3(100, 0, 0), 0f, EDeadzoneType.Radiation);

        Assert.Equal(16f, zone.Radius);
        Assert.True(zone.Contains(new Vector3(105, 0, 0)));
        Assert.False(zone.Contains(new Vector3(100, 50, 0)));
        Assert.False(zone.Contains(Vector3.Zero));
    }

    // SafezoneManager.checkPointValid, which is the gate the zombie respawn and the punch both want.
    [Fact]
    public void SafezoneLookups_FindTheContainingZone()
    {
        // Sliders of zero, which is a 16-metre sphere each — far enough apart that the point between
        // them is in neither. isHeight is false so they ARE spheres; the flag would make them the
        // paintball box, which swallows the whole map.
        LevelNodeSet nodes = LoadSet(new RiverBytes()
            .Byte(9).Byte(2)
            .Vector3(new Vector3(0, 0, 0)).Byte(1).Single(0f).Bool(false).Bool(true).Bool(false)
            .Vector3(new Vector3(100, 0, 0)).Byte(1).Single(0f).Bool(false).Bool(false).Bool(true)
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
            .Vector3(new Vector3(0, 0, 0)).Byte(4).Single(0f).Byte(2)
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
            .Vector3(Vector3.Zero).Byte(1).Single(0.25f).Bool(true).Bool(true).Bool(true)
            .Vector3(Vector3.Zero).Byte(99)
            .Vector3(Vector3.Zero).Byte(1).Single(0.75f).Bool(true).Bool(true).Bool(true)
            .ToArray());

        Assert.Equal(0.25f, Assert.Single(nodes.Safezones).NormalizedRadius);
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

    // A shipped safezone, at the size it really is. Russia's is the biggest the game ships and its
    // slider is 0.573 — read as metres, as this port did, it was a volume 57 centimetres across that
    // nothing could ever stand inside, and the noWeapons flag behind it could never fire.
    // The same fix, on the one map the content fetch downloads — so CI covers the normalized radius
    // rather than leaving it to a machine that happens to have the whole game.
    //
    // PEI ships no safezone at all (Russia is the only official map that does), but its deadzone reads
    // its radius through the same slider, and its slider is ZERO. That is the sharpest case of the bug
    // there is: read as metres it is a volume of radius 0 that nothing can ever be inside, and read as
    // the slider it means the MINIMUM size — Lerp(32, 1024, 0) * 0.5 = 16 m. Nothing about "0" says
    // which, which is exactly why the port got it wrong.
    [RealDataFact(Map = "PEI")]
    public void RealPeiDeadzone_TakesTheMinimumSizeRatherThanNoSizeAtAll()
    {
        LevelNodeSet nodes = LevelNodes.Load(
            Path.Combine(GameData.Map("PEI")!, "Environment", "Nodes.dat"));

        DeadzoneNode zone = Assert.Single(nodes.Deadzones);
        Assert.Equal(0f, zone.NormalizedRadius);
        Assert.Equal(DeadzoneNode.MinSize * 0.5f, zone.Radius, 3);
        Assert.Equal(16f, zone.Radius, 3);
    }

    [RealDataFact(Map = "Russia")]
    public void RealRussiaSafezone_IsHundredsOfMetresAcross()
    {
        LevelNodeSet nodes = LevelNodes.Load(
            Path.Combine(GameData.Map("Russia")!, "Environment", "Nodes.dat"));

        SafezoneNode zone = Assert.Single(nodes.Safezones);
        Assert.True(zone.NoWeapons, "Russia's safezone forbids weapons");
        Assert.False(zone.IsHeight); // an ordinary sphere, not the paintball box
        Assert.InRange(zone.NormalizedRadius, 0f, 1f);
        Assert.InRange(zone.Radius, 100f, SafezoneNode.MaxSize * 0.5f);

        // And the volume actually contains the ground it is centred on, which is the whole point.
        Assert.True(nodes.IsPointInSafezone(zone.Position));
        Assert.NotNull(nodes.SafezoneAt(zone.Position + new Vector3(50f, 0f, 0f)));
        Assert.Null(nodes.SafezoneAt(zone.Position + new Vector3(1000f, 0f, 0f)));
    }
}
