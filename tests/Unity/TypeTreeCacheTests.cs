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

    // Magic and stamp only prove the file was written by this build against this masterbundle. Neither
    // says the BODY is intact, and both counts behind them size a collection directly: a corrupt entry
    // count reached `new Dictionary<>(count)` and a corrupt node count `new List<>(count)`, which throw
    // ArgumentOutOfRangeException on a negative and reserve gigabytes on a large positive — before any
    // read could notice the file is far too short to hold that many. That exception escaped
    // ModelExtractor.ReadClassTypeTrees, whose catch covered IOException alone, and reached
    // SkyboxAssets.Load on the environment step of every load.
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(1000)]
    public void Read_ImplausibleEntryCount_ReturnsNullRatherThanAllocating(int entryCount)
    {
        using var stream = new MemoryStream();
        WriteHeader(stream, stamp: 7);
        new BinaryWriter(stream).Write(entryCount);
        stream.Position = 0;

        Assert.Null(TypeTreeCache.Read(stream, expectedStamp: 7));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(500)]
    public void Read_ImplausibleNodeCount_ReturnsNullRatherThanAllocating(int nodeCount)
    {
        using var stream = new MemoryStream();
        WriteHeader(stream, stamp: 7);
        var w = new BinaryWriter(stream);
        w.Write(1); // one entry...
        w.Write(114); // ...whose class id is fine...
        w.Write(nodeCount); // ...and whose node count is not
        stream.Position = 0;

        Assert.Null(TypeTreeCache.Read(stream, expectedStamp: 7));
    }

    // A file cut off part-way — the shape a non-atomic write leaves behind when the process is killed
    // mid-write — is refused rather than half-read. Every prefix of a real cache is tried, so this covers
    // a cut in the middle of a count, of a string length and of a string body alike. EndOfStreamException
    // is an IOException, which is the one failure every caller of this already handled.
    [Fact]
    public void Read_TruncatedAtAnyPoint_NeverReturnsAPartialCache()
    {
        using var complete = new MemoryStream();
        TypeTreeCache.Write(complete, Sample(), stamp: 7);
        byte[] bytes = complete.ToArray();

        for (int length = 0; length < bytes.Length; length++)
        {
            using var truncated = new MemoryStream(bytes, 0, length, writable: false);
            try
            {
                Assert.Null(TypeTreeCache.Read(truncated, expectedStamp: 7));
            }
            catch (EndOfStreamException)
            {
            }
        }
    }

    private static void WriteHeader(Stream stream, long stamp)
    {
        var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(0x54544755u); // "UGTT"
        w.Write(stamp);
        w.Flush();
    }
}
