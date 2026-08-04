using System.Collections.Generic;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class UnityTextureTests
{
    private static Dictionary<string, object> TextureDict(string streamPath, long offset, int size, byte[] inline) =>
        new()
        {
            ["m_Name"] = "T",
            ["m_Width"] = 64,
            ["m_Height"] = 32,
            ["m_TextureFormat"] = 10,
            ["m_MipCount"] = 2,
            ["image data"] = inline,
            ["m_StreamData"] = new Dictionary<string, object>
            {
                ["path"] = streamPath,
                ["offset"] = (ulong)offset,
                ["size"] = (uint)size,
            },
        };

    [Fact]
    public void ParsesFields()
    {
        UnityTexture t = UnityTexture.Read(TextureDict("archive:/x/CAB-y.resS", 100, 8, System.Array.Empty<byte>()));
        Assert.Equal(64, t.Width);
        Assert.Equal(32, t.Height);
        Assert.Equal(10, t.Format);
        Assert.Equal(2, t.MipCount);
        Assert.Equal(1, t.FilterMode); // no m_TextureSettings -> bilinear default
        Assert.Equal("CAB-y.resS", t.StreamFileName); // last path segment
    }

    [Fact]
    public void ParsesFilterMode_Point()
    {
        Dictionary<string, object> dict = TextureDict("archive:/x/CAB-y.resS", 0, 0, System.Array.Empty<byte>());
        dict["m_TextureSettings"] = new Dictionary<string, object> { ["m_FilterMode"] = 0 };
        Assert.Equal(0, UnityTexture.Read(dict).FilterMode);
    }

    [Fact]
    public void GetPixels_FromStream()
    {
        UnityTexture t = UnityTexture.Read(TextureDict("archive:/x/res.resS", 2, 3, System.Array.Empty<byte>()));
        byte[]? pixels = t.GetPixels(name => name == "res.resS" ? new byte[] { 9, 9, 1, 2, 3, 9 } : null);
        Assert.Equal(new byte[] { 1, 2, 3 }, pixels);
    }

    [Fact]
    public void GetPixels_Inline_WhenNoStreamPath()
    {
        UnityTexture t = UnityTexture.Read(TextureDict("", 0, 0, new byte[] { 7, 8 }));
        Assert.Equal(new byte[] { 7, 8 }, t.GetPixels(_ => null));
    }

    [Fact]
    public void GetPixels_MissingStreamFile_ReturnsNull()
    {
        UnityTexture t = UnityTexture.Read(TextureDict("archive:/x/res.resS", 0, 4, System.Array.Empty<byte>()));
        Assert.Null(t.GetPixels(_ => null));                     // file not found
        Assert.Null(t.GetPixels(_ => new byte[] { 1, 2 }));      // out of range
    }

    [Fact]
    public void Read_WithoutStreamData_UsesInline()
    {
        // Unity 5.x per-map textures (Roads.unity3d) have no m_StreamData; pixels are inline.
        var dict = new Dictionary<string, object>
        {
            ["m_Name"] = "Highway_0",
            ["m_Width"] = 256,
            ["m_Height"] = 128,
            ["m_TextureFormat"] = 3,
            ["m_MipCount"] = 1,
            ["image data"] = new byte[] { 5, 6, 7 },
        };
        UnityTexture t = UnityTexture.Read(dict);
        Assert.Equal(string.Empty, t.StreamPath);
        Assert.Equal(new byte[] { 5, 6, 7 }, t.GetPixels(_ => null));
    }

    [Fact]
    public void StreamFileName_NoSlash_ReturnsPath()
    {
        UnityTexture t = UnityTexture.Read(TextureDict("plain.resS", 0, 0, new byte[0]));
        Assert.Equal("plain.resS", t.StreamFileName);
    }

    [Fact]
    public void Read_WithoutNameOrInlineData_Defaults()
    {
        var dict = new Dictionary<string, object>
        {
            ["m_Width"] = 4,
            ["m_Height"] = 4,
            ["m_TextureFormat"] = 4,
            ["m_MipCount"] = 1,
            ["m_StreamData"] = new Dictionary<string, object>
            {
                ["path"] = "archive:/x/f.resS",
                ["offset"] = 0UL,
                ["size"] = 2u,
            },
        };
        UnityTexture t = UnityTexture.Read(dict);
        Assert.Equal(string.Empty, t.Name);
        Assert.Empty(t.InlineData);
    }

    // Unity 4.x textures (the pre-2018 per-map bundles) have no m_MipCount, only a m_MipMap bool.
    private static Dictionary<string, object> Unity4Texture(int width, int height, bool mipMap) =>
        new()
        {
            ["m_Name"] = "Trail",
            ["m_Width"] = width,
            ["m_Height"] = height,
            ["m_TextureFormat"] = 4,
            ["m_MipMap"] = mipMap,
            ["image data"] = new byte[] { 1, 2, 3, 4 },
        };

    [Theory]
    [InlineData(64, 64, 7)]   // the whole chain down to 1x1
    [InlineData(64, 32, 7)]   // non-square: the longer side sets the length
    [InlineData(2, 2, 2)]
    [InlineData(1, 1, 1)]
    public void Read_MipMapFlag_DerivesTheChainLength(int width, int height, int expected) =>
        Assert.Equal(expected, UnityTexture.Read(Unity4Texture(width, height, mipMap: true)).MipCount);

    [Fact]
    public void Read_MipMapFlagOff_IsASingleLevel() =>
        Assert.Equal(1, UnityTexture.Read(Unity4Texture(32, 32, mipMap: false)).MipCount);

    [Fact]
    public void Read_MipCountWins_WhenBothFieldsArePresent()
    {
        Dictionary<string, object> dict = Unity4Texture(64, 64, mipMap: true);
        dict["m_MipCount"] = 3;
        Assert.Equal(3, UnityTexture.Read(dict).MipCount);
    }
}
