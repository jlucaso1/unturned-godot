using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class TypeTreeCacheTests
{
    private static Dictionary<int, List<TypeTreeNode>> Sample() => new()
    {
        [1] = new List<TypeTreeNode>
        {
            new() { Level = 0, IsArray = false, Type = "GameObject", Name = "Base", ByteSize = -1, MetaFlag = 0x4000 },
            new() { Level = 1, IsArray = true, Type = "vector", Name = "m_Component", ByteSize = 12, MetaFlag = 1 },
        },
        [137] = new List<TypeTreeNode>
        {
            new() { Level = 0, IsArray = false, Type = "SkinnedMeshRenderer", Name = "Base", ByteSize = 40, MetaFlag = 0 },
        },
    };

    [Fact]
    public void RoundTrip_PreservesEveryNode()
    {
        Dictionary<int, List<TypeTreeNode>> trees = Sample();
        using var stream = new MemoryStream();
        TypeTreeCache.Write(stream, trees, stamp: 42);
        stream.Position = 0;

        Dictionary<int, List<TypeTreeNode>>? read = TypeTreeCache.Read(stream, expectedStamp: 42);

        Assert.NotNull(read);
        Assert.Equal(trees.Count, read!.Count);
        foreach ((int classId, List<TypeTreeNode> nodes) in trees)
        {
            List<TypeTreeNode> got = read[classId];
            Assert.Equal(nodes.Count, got.Count);
            for (int i = 0; i < nodes.Count; i++)
            {
                Assert.Equal(nodes[i].Level, got[i].Level);
                Assert.Equal(nodes[i].IsArray, got[i].IsArray);
                Assert.Equal(nodes[i].Type, got[i].Type);
                Assert.Equal(nodes[i].Name, got[i].Name);
                Assert.Equal(nodes[i].ByteSize, got[i].ByteSize);
                Assert.Equal(nodes[i].MetaFlag, got[i].MetaFlag);
            }
        }
    }

    [Fact]
    public void Read_StampMismatch_ReturnsNull()
    {
        using var stream = new MemoryStream();
        TypeTreeCache.Write(stream, Sample(), stamp: 100);
        stream.Position = 0;
        Assert.Null(TypeTreeCache.Read(stream, expectedStamp: 999)); // different masterbundle
    }

    [Fact]
    public void Read_BadMagic_ReturnsNull()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Null(TypeTreeCache.Read(stream, expectedStamp: 0));
    }
}
