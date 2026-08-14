using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class LevelObjectsTests
{
    private readonly record struct Obj(Vector3 Pos, Vector3 Euler, Vector3 Scale, ushort Id, Guid Guid);

    // Serializes an Objects.dat for a given SAVEDATA_VERSION with every object in region (0,0),
    // matching LevelObjects.load's version-gated field layout exactly.
    private static byte[] Build(int version, params Obj[] objs)
    {
        var w = new RiverBytes().Byte((byte)version);

        if (version > 1 && version < 3)
            w.UInt64(76561198000000000);
        if (version > 8)
            w.UInt32(1);

        // Region (0,0) holds the objects; the remaining 4095 regions are empty.
        w.UInt16((ushort)objs.Length);
        foreach (Obj o in objs)
        {
            w.Vector3(o.Pos).Vector3(o.Euler);
            if (version > 3) w.Vector3(o.Scale);
            w.UInt16(o.Id);
            if (version >= 6 && version <= 9) w.Str("legacyName");
            if (version > 7) w.Guid(o.Guid);
            if (version > 6) w.Byte(0);
            if (version > 8) w.UInt32(42);
            if (version >= 11) { w.Guid(Guid.Empty); w.Int32(-1); }
            if (version >= 12) w.Bool(true);
        }
        for (int i = 1; i < LevelObjects.WORLD_SIZE * LevelObjects.WORLD_SIZE; i++)
            w.UInt16(0);

        return w.ToArray();
    }

    private static List<PlacedObject> Load(int version, params Obj[] objs)
    {
        using var dir = new TempDir();
        string path = dir.Write("Objects.dat", Build(version, objs));
        return LevelObjects.Load(path);
    }

    [Fact]
    public void MissingFile_ReturnsEmpty()
    {
        Assert.Empty(LevelObjects.Load("/no/such/Objects.dat"));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(11)]
    [InlineData(10)]
    [InlineData(9)]
    [InlineData(8)]
    [InlineData(6)]
    [InlineData(4)]
    public void ReadsModernObject_AcrossVersions(int version)
    {
        var guid = Guid.NewGuid();
        var obj = new Obj(new Vector3(5, 6, 7), new Vector3(0, 90, 0), new Vector3(2, 2, 2), 57, guid);
        List<PlacedObject> result = Load(version, obj);

        PlacedObject p = Assert.Single(result);
        Assert.Equal(new Vector3(5, 6, 7), p.Position);
        Assert.Equal(new Vector3(0, 90, 0), p.EulerDegrees);
        Assert.Equal(57, p.Id);
        // GUID only exists from version 8 onward; earlier versions keep it empty.
        Assert.Equal(version > 7 ? guid : Guid.Empty, p.Guid);
        // Scale only exists from version 4 onward.
        Assert.Equal(version > 3 ? new Vector3(2, 2, 2) : Vector3.One, p.Scale);
    }

    [Fact]
    public void LegacyVersions_WithSteamIdHeader()
    {
        var obj = new Obj(new Vector3(1, 1, 1), Vector3.Zero, Vector3.One, 10, Guid.Empty);
        Assert.Single(Load(2, obj)); // 1 < version < 3 → SteamID header present
    }

    [Fact]
    public void Version1_NoSteamIdHeader_KeepsByLegacyId()
    {
        var obj = new Obj(new Vector3(1, 1, 1), Vector3.Zero, Vector3.One, 99, Guid.Empty);
        PlacedObject p = Assert.Single(Load(1, obj));
        Assert.Equal(99, p.Id);
    }

    [Fact]
    public void DiscardsUnidentifiedObjects()
    {
        // id 0 and empty GUID → not kept.
        var ghost = new Obj(Vector3.Zero, Vector3.Zero, Vector3.One, 0, Guid.Empty);
        var real = new Obj(Vector3.One, Vector3.Zero, Vector3.One, 5, Guid.Empty);
        Assert.Single(Load(12, ghost, real));
    }

    [Fact]
    public void Load_RoundsATransformThatIsNearlySquare()
    {
        // LevelObjects.cs:652 and :656 round as they read, so two walls an editor left a hair out of
        // square end up exactly coplanar instead of z-fighting. None of PEI's 4,329 placements is off by
        // enough to move -- the modern editor already writes them rounded -- so this is for the workshop
        // maps and the older editors that do drift.
        List<PlacedObject> placed = Load(12, new Obj(new Vector3(1, 2, 3),
            new Vector3(0.001f, 89.99f, -0.02f), new Vector3(1.0000001f, -0.9999f, 0.5f), 7, Guid.NewGuid()));

        Assert.Equal(new Vector3(0, 90, 0), Assert.Single(placed).EulerDegrees);
        Assert.Equal(new Vector3(1, -1, 0.5f), placed[0].Scale);
        Assert.Equal(new Vector3(1, 2, 3), placed[0].Position); // position is never rounded
    }

    [Fact]
    public void Load_LeavesADeliberateTransformExactlyAsAuthored()
    {
        var euler = new Vector3(0, 37.5f, 0);
        var scale = new Vector3(0.5f, 2f, 0.998f);

        List<PlacedObject> placed = Load(12, new Obj(Vector3.Zero, euler, scale, 7, Guid.NewGuid()));

        Assert.Equal(euler, Assert.Single(placed).EulerDegrees);
        Assert.Equal(scale, placed[0].Scale);
    }
}
