using System;
using System.IO;
using UnturnedGodot.Tests.Helpers;
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
    public void SourceOwner_RequiresTheSameBundlePathAndStamp()
    {
        using var dir = new TempDir();
        string texture = dir.Write("cache/item.tex", Array.Empty<byte>());
        using (FileStream stream = File.Create(texture))
            TextureCache.Write(stream, new CachedTexture(3, 1, 1, 1, new byte[] { 1 }));
        string firstBundle = dir.Write("first/masterbundle", new byte[] { 1 });
        string secondBundle = dir.Write("second/masterbundle", new byte[] { 1 });

        TextureCache.RecordSource(texture, firstBundle, 123);

        Assert.True(TextureCache.IsCurrentForSource(texture, firstBundle, 123));
        Assert.False(TextureCache.IsCurrentForSource(texture, firstBundle, 124));
        Assert.False(TextureCache.IsCurrentForSource(texture, secondBundle, 123));
        File.WriteAllBytes(TextureCache.SourcePath(texture), new byte[] { 1, 2, 3 });
        Assert.False(TextureCache.IsCurrentForSource(texture, firstBundle, 123));
    }

    [Fact]
    public void Read_BadMagic_Throws()
    {
        using var stream = new MemoryStream(new byte[] { 0, 0, 0, 0, 1, 2, 3, 4 });
        Assert.Throws<InvalidDataException>(() => TextureCache.Read(stream));
    }

    [Fact]
    public void From_PlainTexture_KeepsItsPixelsAndFilterMode()
    {
        var tex = new UnityTexture { Width = 4, Height = 2, Format = 12, MipCount = 1, FilterMode = 0 };
        CachedTexture cached = CachedTexture.From(tex, new byte[] { 7, 8, 9 });

        Assert.Equal(12, cached.Format);
        Assert.Equal(4, cached.Width);
        Assert.Equal(2, cached.Height);
        Assert.Equal(1, cached.MipCount);
        Assert.Equal(0, cached.FilterMode);
        Assert.Equal(new byte[] { 7, 8, 9 }, cached.Pixels);
    }

    [Fact]
    public void From_CrunchedTexture_IsDecodedToPlainDxt()
    {
        // The 8x8 DXT1 image the Crunch tests build, arriving as Unity's DXT1Crunched (28).
        byte[] crn = CrunchedDxt1();
        var tex = new UnityTexture { Width = 8, Height = 8, Format = 28, MipCount = 1 };
        CachedTexture cached = CachedTexture.From(tex, crn);

        Assert.Equal(10, cached.Format); // DXT1
        Assert.Equal(8, cached.Width);
        Assert.Equal(8, cached.Height);
        Assert.Equal(1, cached.MipCount);
        Assert.Equal(32, cached.Pixels.Length);
    }

    [Fact]
    public void Decoded_CrunchedDxt5Entry_BecomesDxt5()
    {
        // The other half of the mapping: a cache entry written before the decoder existed still holds the
        // container, and reading it back has to unwrap it too.
        var entry = new CachedTexture(29, 4, 4, 1, CrunchedDxt5());
        CachedTexture decoded = CachedTexture.Decoded(entry);

        Assert.Equal(12, decoded.Format); // DXT5
        Assert.Equal(16, decoded.Pixels.Length);
    }

    [Fact]
    public void From_CrunchedTextureItCannotDecode_IsLeftAlone()
    {
        // A container claiming to be crunched but holding nothing decodable: the renderer treats the
        // unknown format as "no texture", which beats failing the whole load.
        var tex = new UnityTexture { Width = 8, Height = 8, Format = 29, MipCount = 1 };
        CachedTexture cached = CachedTexture.From(tex, new byte[] { 1, 2, 3, 4 });

        Assert.Equal(29, cached.Format);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, cached.Pixels);
    }

    // A 4x4 DXT5 image: one block, and one chunk whose other three block slots are clipped away.
    private static byte[] CrunchedDxt5()
    {
        var tables = new CrnBuilder.BitWriter();
        tables.Model(2, 0, 1); // chunk encodings
        tables.Model(2, 0, 1); // colour endpoint delta
        tables.Model(2, 0, 1); // colour selector delta
        tables.Model(2, 0, 1); // alpha endpoint delta
        tables.Model(2, 0, 1); // alpha selector delta

        var colorEndpoints = new CrnBuilder.BitWriter();
        colorEndpoints.Model(2, 0, 1);
        colorEndpoints.Model(2, 0, 1);
        for (int i = 0; i < 6; i++)
            colorEndpoints.Symbol(true);

        var alphaEndpoints = new CrnBuilder.BitWriter();
        alphaEndpoints.Model(2, 0, 1);
        alphaEndpoints.Symbol(true);
        alphaEndpoints.Symbol(true);

        var level = new CrnBuilder.BitWriter();
        level.Symbol(false);        // chunk encoding 0
        level.Symbol(false);        // alpha endpoint delta
        level.Symbol(false);        // colour endpoint delta
        for (int block = 0; block < 4; block++)
        {
            level.Symbol(false);    // alpha selector delta
            level.Symbol(false);    // colour selector delta
        }

        var crn = new CrnBuilder
        {
            Width = 4,
            Height = 4,
            Format = CrnBuilder.FormatDxt5,
            Tables = tables.ToArray(),
            ColorEndpoints = (colorEndpoints.ToArray(), 1),
            ColorSelectors = (Selectors(maxValue: 3), 1),
            AlphaEndpoints = (alphaEndpoints.ToArray(), 1),
            AlphaSelectors = (Selectors(maxValue: 7), 1),
        };
        crn.Levels.Add(level.ToArray());
        return crn.Build();
    }

    // One selector palette entry, eight symbols of the delta pair that changes nothing.
    private static byte[] Selectors(int maxValue)
    {
        int unique = (maxValue * 2) + 1;
        int noChange = (maxValue * unique) + maxValue;

        var w = new CrnBuilder.BitWriter();
        w.Model(noChange + 1, 0, noChange);
        for (int j = 0; j < 8; j++)
            w.Symbol(true);
        return w.ToArray();
    }

    private static byte[] CrunchedDxt1()
    {
        var tables = new CrnBuilder.BitWriter();
        tables.Model(2, 0, 1); // chunk encodings
        tables.Model(2, 0, 1); // colour endpoint delta
        tables.Model(2, 0, 1); // colour selector delta

        var endpoints = new CrnBuilder.BitWriter();
        endpoints.Model(2, 0, 1);
        endpoints.Model(2, 0, 1);
        for (int i = 0; i < 6; i++)
            endpoints.Symbol(true);

        var level = new CrnBuilder.BitWriter();
        level.Symbol(false);
        level.Symbol(false);
        for (int block = 0; block < 4; block++)
            level.Symbol(false);

        var crn = new CrnBuilder
        {
            Width = 8,
            Height = 8,
            Format = CrnBuilder.FormatDxt1,
            Tables = tables.ToArray(),
            ColorEndpoints = (endpoints.ToArray(), 1),
            ColorSelectors = (Selectors(maxValue: 3), 1),
        };
        crn.Levels.Add(level.ToArray());
        return crn.Build();
    }
}
