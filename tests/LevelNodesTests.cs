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
}
