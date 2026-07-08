using System;
using System.Text;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class UnityBundleTests
{
    [Fact]
    public void ReadsUncompressedFiles()
    {
        byte[] bundle = new UnityFsBuilder()
            .Add("CAB-a", Encoding.ASCII.GetBytes("hello"))
            .Add("CAB-a.resS", Encoding.ASCII.GetBytes("world!"))
            .Build();

        UnityBundle b = UnityBundle.Read(bundle);
        Assert.Equal(2, b.Files.Count);
        Assert.Equal("hello", Encoding.ASCII.GetString(b.Files["CAB-a"]));
        Assert.Equal("world!", Encoding.ASCII.GetString(b.Files["CAB-a.resS"]));
    }

    [Fact]
    public void ReadsLzmaCompressedBlock()
    {
        byte[] payload = Encoding.ASCII.GetBytes(new string('z', 400) + "end");
        byte[] bundle = new UnityFsBuilder { LzmaBlocks = true }
            .Add("CAB-a", payload)
            .Build();
        Assert.Equal(payload, UnityBundle.Read(bundle).Files["CAB-a"]);
    }

    [Fact]
    public void ReadsLz4CompressedBlock()
    {
        byte[] payload = Encoding.ASCII.GetBytes(new string('q', 30) + "tail");
        byte[] bundle = new UnityFsBuilder { Lz4Blocks = true }.Add("CAB-a", payload).Build();
        Assert.Equal(payload, UnityBundle.Read(bundle).Files["CAB-a"]);
    }

    [Fact]
    public void PaddingAtStartFlag()
    {
        byte[] bundle = new UnityFsBuilder { PaddingAtStart = true }
            .Add("CAB-a", Encoding.ASCII.GetBytes("padded"))
            .Build();
        Assert.Equal("padded", Encoding.ASCII.GetString(UnityBundle.Read(bundle).Files["CAB-a"]));
    }

    [Fact]
    public void BlockInfoAtEnd()
    {
        byte[] bundle = new UnityFsBuilder { BlockInfoAtEnd = true }
            .Add("CAB-a", Encoding.ASCII.GetBytes("payload"))
            .Build();

        UnityBundle b = UnityBundle.Read(bundle);
        Assert.Equal("payload", Encoding.ASCII.GetString(b.Files["CAB-a"]));
    }

    [Fact]
    public void CapExcludesNodesBeyondPrefix()
    {
        // First file fits in the cap; the second lies beyond it and is dropped.
        byte[] bundle = new UnityFsBuilder()
            .Add("first", new byte[8])
            .Add("second", new byte[8])
            .Build();

        UnityBundle b = UnityBundle.Read(bundle, maxDecompressedBytes: 8);
        Assert.True(b.Files.ContainsKey("first"));
        Assert.False(b.Files.ContainsKey("second"));
    }

    [Fact]
    public void CapStopsBlockLoopAcrossMultipleBlocks()
    {
        // 16 bytes over 2 blocks (8 each); a cap of 8 fills after block 0 and breaks the loop.
        byte[] bundle = new UnityFsBuilder { BlockCount = 2 }
            .Add("first", new byte[8])
            .Add("second", new byte[8])
            .Build();

        UnityBundle b = UnityBundle.Read(bundle, maxDecompressedBytes: 8);
        Assert.True(b.Files.ContainsKey("first"));
        Assert.False(b.Files.ContainsKey("second"));
    }

    [Fact]
    public void OldFormatVersion_SkipsAlignment()
    {
        byte[] bundle = new UnityFsBuilder { FormatVersion = 6 }
            .Add("CAB-a", Encoding.ASCII.GetBytes("v6"))
            .Build();
        Assert.Equal("v6", Encoding.ASCII.GetString(UnityBundle.Read(bundle).Files["CAB-a"]));
    }

    [Fact]
    public void BadSignature_Throws()
    {
        var bytes = new byte[64];
        Encoding.ASCII.GetBytes("NotUnity\0").CopyTo(bytes, 0);
        Assert.Throws<NotSupportedException>(() => UnityBundle.Read(bytes));
    }

    [Fact]
    public void UnsupportedBlocksInfoCompression_Throws()
    {
        byte[] bundle = new UnityFsBuilder { BlockInfoCompression = 5 }
            .Add("CAB-a", new byte[4])
            .Build();
        Assert.Throws<NotSupportedException>(() => UnityBundle.Read(bundle));
    }
}
