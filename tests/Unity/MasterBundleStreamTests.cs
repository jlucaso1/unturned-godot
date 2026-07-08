using System.Text;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class MasterBundleStreamTests
{
    private static byte[] LzmaBundle(byte[] sf, byte[] resS, bool atEnd = false, bool padding = false,
        int version = 8) =>
        new UnityFsBuilder { LzmaBlocks = true, BlockInfoAtEnd = atEnd, PaddingAtStart = padding, FormatVersion = version }
            .Add("CAB-x", sf)
            .Add("CAB-x.resS", resS)
            .Build();

    [Fact]
    public void StreamsSerializedFileThenResS()
    {
        byte[] sf = Encoding.ASCII.GetBytes(new string('m', 200) + "MESH");
        byte[] resS = Encoding.ASCII.GetBytes(new string('t', 300) + "TEX");

        using MasterBundleStream? stream = MasterBundleStream.Open(LzmaBundle(sf, resS));
        Assert.NotNull(stream);
        Assert.Equal(2, stream!.Nodes.Count);
        Assert.Equal("CAB-x", stream.Nodes[0].Path);
        Assert.Equal(0, stream.Nodes[0].Offset);
        Assert.Equal(sf.Length, stream.Nodes[0].Size);
        Assert.Equal("CAB-x.resS", stream.Nodes[1].Path);
        Assert.Equal(sf.Length, stream.Nodes[1].Offset);

        Assert.Equal(sf, stream.Read((int)stream.Nodes[0].Size));
        Assert.Equal(sf.Length, stream.Cursor);
        Assert.Equal(resS, stream.Read((int)stream.Nodes[1].Size));
        Assert.Equal(sf.Length + resS.Length, stream.Cursor);
        Assert.Equal(sf.Length + resS.Length, stream.TotalSize);
    }

    [Fact]
    public void ReadPastEnd_ReturnsShortBuffer()
    {
        byte[] sf = Encoding.ASCII.GetBytes(new string('a', 64));
        using MasterBundleStream? stream = MasterBundleStream.Open(LzmaBundle(sf, new byte[] { 1, 2, 3 }));
        Assert.NotNull(stream);
        stream!.Read((int)stream.TotalSize); // drain the whole blob exactly
        byte[] extra = stream.Read(1000);     // nothing left: short (empty) buffer, not 1000 bytes
        Assert.Empty(extra);
    }

    [Fact]
    public void BlockInfoAtEnd_IsSupported()
    {
        byte[] sf = Encoding.ASCII.GetBytes(new string('e', 80));
        using MasterBundleStream? stream = MasterBundleStream.Open(LzmaBundle(sf, new byte[] { 9 }, atEnd: true));
        Assert.NotNull(stream);
        Assert.Equal(sf, stream!.Read(sf.Length));
    }

    [Fact]
    public void PaddingAtStart_IsSupported()
    {
        byte[] sf = Encoding.ASCII.GetBytes(new string('p', 80));
        using MasterBundleStream? stream = MasterBundleStream.Open(LzmaBundle(sf, new byte[] { 7 }, padding: true));
        Assert.NotNull(stream);
        Assert.Equal(sf, stream!.Read(sf.Length));
    }

    [Fact]
    public void OldFormatVersion_SkipsAlignment()
    {
        byte[] sf = Encoding.ASCII.GetBytes(new string('v', 80));
        using MasterBundleStream? stream = MasterBundleStream.Open(LzmaBundle(sf, new byte[] { 5 }, version: 6));
        Assert.NotNull(stream);
        Assert.Equal(sf, stream!.Read(sf.Length));
    }

    [Fact]
    public void BadSignature_ReturnsNull()
    {
        var bytes = new byte[64];
        Encoding.ASCII.GetBytes("NotUnity\0").CopyTo(bytes, 0);
        Assert.Null(MasterBundleStream.Open(bytes));
    }

    [Fact]
    public void MultipleBlocks_ReturnNull()
    {
        byte[] bundle = new UnityFsBuilder { BlockCount = 2 }
            .Add("CAB-x", new byte[16])
            .Build();
        Assert.Null(MasterBundleStream.Open(bundle));
    }

    [Fact]
    public void UncompressedBlock_ReturnsNull()
    {
        // A single, but uncompressed, block is not the streamed (LZMA) shape.
        byte[] bundle = new UnityFsBuilder()
            .Add("CAB-x", Encoding.ASCII.GetBytes("plain"))
            .Build();
        Assert.Null(MasterBundleStream.Open(bundle));
    }

    [Fact]
    public void MalformedBlocksInfoCompression_ReturnsNull()
    {
        // Unsupported blocks-info compression throws inside decode and is caught → null (fallback).
        byte[] bundle = new UnityFsBuilder { BlockInfoCompression = 5 }
            .Add("CAB-x", new byte[4])
            .Build();
        Assert.Null(MasterBundleStream.Open(bundle));
    }
}
