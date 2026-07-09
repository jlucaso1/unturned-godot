using System.IO;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class TextureCacheTests
{
    [Fact]
    public void RoundTrip()
    {
        var texture = new CachedTexture(10, 256, 128, 4, new byte[] { 1, 2, 3, 4, 5 }, filterMode: 0);
        using var stream = new MemoryStream();
        TextureCache.Write(stream, texture);
        stream.Position = 0;
        CachedTexture read = TextureCache.Read(stream);

        Assert.Equal(10, read.Format);
        Assert.Equal(256, read.Width);
        Assert.Equal(128, read.Height);
        Assert.Equal(4, read.MipCount);
        Assert.Equal(0, read.FilterMode); // Point, for the tiny palette textures
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, read.Pixels);
    }

    [Fact]
    public void FilterMode_DefaultsToBilinear()
    {
        Assert.Equal(1, new CachedTexture(3, 4, 2, 1, new byte[] { 1 }).FilterMode);
    }

    [Fact]
    public void IsCurrent_TrueForFresh_FalseForStaleOrMissing()
    {
        string dir = Directory.CreateTempSubdirectory("texcache").FullName;
        try
        {
            string fresh = Path.Combine(dir, "fresh.tex");
            using (FileStream f = File.Create(fresh))
                TextureCache.Write(f, new CachedTexture(3, 4, 2, 1, new byte[] { 1, 2, 3 }));
            Assert.True(TextureCache.IsCurrent(fresh));

            string stale = Path.Combine(dir, "stale.tex");
            File.WriteAllBytes(stale, new byte[] { 0x54, 0x47, 0x58, 0x32, 0 }); // "TGX2" (old format)
            Assert.False(TextureCache.IsCurrent(stale));

            string shortFile = Path.Combine(dir, "short.tex");
            File.WriteAllBytes(shortFile, new byte[] { 0x54 });
            Assert.False(TextureCache.IsCurrent(shortFile));

            Assert.False(TextureCache.IsCurrent(Path.Combine(dir, "missing.tex")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Read_BadMagic_Throws()
    {
        using var stream = new MemoryStream(new byte[] { 0, 0, 0, 0, 1, 2, 3, 4 });
        Assert.Throws<InvalidDataException>(() => TextureCache.Read(stream));
    }
}
