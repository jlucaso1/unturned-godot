using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class LevelTreesTests
{
    private readonly record struct Tree(Vector3 Pos, Vector3 Euler, Vector3 Scale, Guid Guid, bool Generated);

    private readonly record struct RegionTree(ushort Id, Guid Guid, Vector3 Pos, bool Generated);

    private static byte[] Build(byte version, params Tree[] trees)
    {
        var w = new RiverBytes().Byte(version);
        if (version >= 7)
        {
            w.Int32(trees.Length);
            foreach (Tree t in trees)
            {
                w.Guid(t.Guid).Vector3(t.Pos);
                if (version >= 8)
                {
                    w.Vector3(t.Euler);
                    w.Vector3(t.Scale);
                }
                w.Bool(t.Generated);
            }
        }
        return w.ToArray();
    }

    // The pre-7 layout: every cell of the 64x64 region grid writes its count, then its trees. `placed`
    // puts each tree in its own cell, starting at the grid's origin.
    private static byte[] BuildRegions(byte version, params RegionTree[] placed)
    {
        var w = new RiverBytes().Byte(version);
        for (int cell = 0; cell < LevelObjects.WORLD_SIZE * LevelObjects.WORLD_SIZE; cell++)
        {
            if (cell >= placed.Length)
            {
                w.UInt16(0);
                continue;
            }

            RegionTree t = placed[cell];
            w.UInt16(1).UInt16(t.Id);
            if (version >= 6)
                w.Guid(t.Guid);
            w.Vector3(t.Pos).Bool(t.Generated);
        }
        return w.ToArray();
    }

    private static List<PlacedTree> LoadRegions(byte version, params RegionTree[] placed)
    {
        using var dir = new TempDir();
        return LevelTrees.Load(dir.Write("Trees.dat", BuildRegions(version, placed)));
    }

    private static List<PlacedTree> Load(byte version, params Tree[] trees)
    {
        using var dir = new TempDir();
        string path = dir.Write("Trees.dat", Build(version, trees));
        return LevelTrees.Load(path);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty() => Assert.Empty(LevelTrees.Load("/no/such/Trees.dat"));

    [Fact]
    public void Load_Version8_ReadsTransforms()
    {
        var guid = Guid.NewGuid();
        var tree = new Tree(new Vector3(-1464, 81, -1528), new Vector3(3, 242, 0), new Vector3(1.2f, 1.2f, 1.2f),
            guid, Generated: true);

        List<PlacedTree> result = Load(8, tree);

        Assert.Single(result);
        Assert.Equal(new Vector3(-1464, 81, -1528), result[0].Position);
        Assert.Equal(new Vector3(3, 242, 0), result[0].EulerDegrees);
        Assert.Equal(new Vector3(1.2f, 1.2f, 1.2f), result[0].Scale);
        Assert.Equal(guid, result[0].Guid);
    }

    [Fact]
    public void Load_Version7_DefaultsRotationAndScale()
    {
        var guid = Guid.NewGuid();
        var tree = new Tree(new Vector3(1, 2, 3), Vector3.Zero, Vector3.One, guid, Generated: false);

        List<PlacedTree> result = Load(7, tree);

        Assert.Single(result);
        Assert.Equal(Vector3.Zero, result[0].EulerDegrees);
        Assert.Equal(Vector3.One, result[0].Scale);
    }

    [Fact]
    public void Load_SkipsEmptyGuid()
    {
        var real = new Tree(new Vector3(1, 1, 1), Vector3.Zero, Vector3.One, Guid.NewGuid(), Generated: false);
        var empty = new Tree(new Vector3(2, 2, 2), Vector3.Zero, Vector3.One, Guid.Empty, Generated: false);

        Assert.Single(Load(8, real, empty));
    }

    [Fact]
    public void Load_Version6_ReadsRegionGrid()
    {
        // Alpha Valley's encoding: trees bucketed into the 64x64 region grid, carrying both the legacy id
        // and the GUID, and no rotation or scale (those arrived in 8).
        var guid = Guid.NewGuid();
        var far = Guid.NewGuid();

        List<PlacedTree> result = LoadRegions(6,
            new RegionTree(2, guid, new Vector3(-934, 48, -1024), Generated: false),
            new RegionTree(7, far, new Vector3(12, 34, 56), Generated: true));

        Assert.Equal(2, result.Count);
        Assert.Equal(new Vector3(-934, 48, -1024), result[0].Position);
        Assert.Equal(guid, result[0].Guid);
        Assert.Equal(2, result[0].Id);
        Assert.Equal(Vector3.Zero, result[0].EulerDegrees);
        Assert.Equal(Vector3.One, result[0].Scale);
        Assert.Equal(far, result[1].Guid);
        Assert.Equal(7, result[1].Id);
    }

    [Fact]
    public void Load_Version5_ReadsRegionGrid_WithLegacyIdOnly()
    {
        // Monolith's encoding: the same grid, but the GUID does not exist yet, so the legacy id is the
        // only identity a tree has (LegacyPlacements turns it into a GUID later).
        List<PlacedTree> result = LoadRegions(5,
            new RegionTree(11, Guid.Empty, new Vector3(1, 2, 3), Generated: false));

        PlacedTree tree = Assert.Single(result);
        Assert.Equal(11, tree.Id);
        Assert.Equal(Guid.Empty, tree.Guid);
        Assert.Equal(new Vector3(1, 2, 3), tree.Position);
    }

    [Fact]
    public void Load_RegionGrid_SkipsEntriesWithNoIdentity()
    {
        // Neither identifier set means Unturned discarded the entry; the grid still has to be walked past
        // it, so the tree that follows in a later cell must still come back.
        List<PlacedTree> result = LoadRegions(6,
            new RegionTree(0, Guid.Empty, new Vector3(1, 1, 1), Generated: false),
            new RegionTree(3, Guid.NewGuid(), new Vector3(2, 2, 2), Generated: false));

        Assert.Equal(new Vector3(2, 2, 2), Assert.Single(result).Position);
    }

    [Fact]
    public void Load_Version0_ReturnsEmpty() => Assert.Empty(LoadRegions(0));
}
