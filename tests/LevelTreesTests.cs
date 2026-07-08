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
    public void Load_ObsoleteVersion_ReturnsEmpty()
    {
        // Versions below 7 use the region-grid encoding, which current maps never write.
        Assert.Empty(Load(6));
    }
}
