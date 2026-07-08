using System.IO;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class TextureCacheTests
{
    [Fact]
    public void RoundTrip()
    {
        var texture = new CachedTexture(10, 256, 128, 4, new byte[] { 1, 2, 3, 4, 5 });
        using var stream = new MemoryStream();
        TextureCache.Write(stream, texture);
        stream.Position = 0;
        CachedTexture read = TextureCache.Read(stream);

        Assert.Equal(10, read.Format);
        Assert.Equal(256, read.Width);
        Assert.Equal(128, read.Height);
        Assert.Equal(4, read.MipCount);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, read.Pixels);
    }

    [Fact]
    public void Read_BadMagic_Throws()
    {
        using var stream = new MemoryStream(new byte[] { 0, 0, 0, 0, 1, 2, 3, 4 });
        Assert.Throws<InvalidDataException>(() => TextureCache.Read(stream));
    }
}
