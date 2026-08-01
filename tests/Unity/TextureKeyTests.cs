using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class TextureKeyTests
{
    [Fact]
    public void KeyCarriesBundleAndId()
    {
        Assert.Equal("core_1a2b", TextureKey.For("core", 0x1a2b));
        // Negative ids (the game's are routinely negative) are written as two's complement, so the key
        // stays hex-only and filename-safe.
        Assert.Equal("california2_fffffffffffffffe", TextureKey.For("california2", -2));
    }

    // The whole point of the namespacing: the same path id in two bundles must not name the same file.
    [Fact]
    public void SameIdInDifferentBundlesGivesDifferentKeys() =>
        Assert.NotEqual(TextureKey.For("core", 5), TextureKey.For("california2", 5));

    [Fact]
    public void RoundTrips()
    {
        Assert.True(TextureKey.TryParse(TextureKey.For("core", 0x7fffffff), out string bundle, out long id));
        Assert.Equal("core", bundle);
        Assert.Equal(0x7fffffff, id);
    }

    // Path ids are signed and the game's are routinely negative, so the hex round-trip has to survive
    // the full 64-bit range rather than just small positive values.
    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void RoundTripsAcrossTheIdRange(long pathId)
    {
        Assert.True(TextureKey.TryParse(TextureKey.For("mod", pathId), out string bundle, out long parsed));
        Assert.Equal("mod", bundle);
        Assert.Equal(pathId, parsed);
    }

    // Splitting from the right, so a tag that somehow carries an underscore still yields the id intact.
    [Fact]
    public void SplitsOnTheLastUnderscore()
    {
        Assert.True(TextureKey.TryParse("a_workshop_mod_ff", out string bundle, out long id));
        Assert.Equal("a_workshop_mod", bundle);
        Assert.Equal(0xff, id);
    }

    // Keys written by the pre-namespacing cache format are bare hex. They must not parse, or a texture
    // pass would resolve another bundle's id against its own SerializedFile.
    [Theory]
    [InlineData("1a2b")]
    [InlineData("")]
    [InlineData("core_")]
    [InlineData("core_zz")]
    public void RejectsAnythingItDidNotWrite(string key) =>
        Assert.False(TextureKey.TryParse(key, out _, out _));

    [Fact]
    public void TryParseForOnlyAcceptsItsOwnBundle()
    {
        string key = TextureKey.For("core", 9);
        Assert.True(TextureKey.TryParseFor(key, "core", out long id));
        Assert.Equal(9, id);
        Assert.False(TextureKey.TryParseFor(key, "california2", out _));
    }

    // The tag comes from the bundle NAME, not its file name: the file carries a platform suffix, which
    // would give the same content a different key (and so a second full extraction) per platform.
    [Theory]
    [InlineData("core.masterbundle", "core")]
    [InlineData("California2.MasterBundle", "california2")]
    public void TagIsFilenameSafeAndPlatformIndependent(string bundleName, string expected) =>
        Assert.Equal(expected, TextureKey.TagFor(bundleName));

    [Fact]
    public void NormalizedNamesKeepDistinctCacheNamespaces()
    {
        string spaced = TextureKey.TagFor("some mod");
        string dashed = TextureKey.TagFor("some-mod");
        string slashed = TextureKey.TagFor("a/b");
        string underscored = TextureKey.TagFor("a_b");

        Assert.NotEqual(spaced, dashed);
        Assert.NotEqual(slashed, underscored);
        Assert.DoesNotContain('_', spaced);
        Assert.DoesNotContain('_', slashed);
    }

    // A tag never contains the separator, so the key format stays unambiguous.
    [Fact]
    public void TagNeverContainsTheSeparator() =>
        Assert.DoesNotContain('_', TextureKey.TagFor("mod_with_underscores.masterbundle"));
}
