using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class TextureIdentityTests
{
    [Fact]
    public void ByteIdenticalTexturesShareAnIdentity_AndEveryFieldOfTheCacheEntryParticipates()
    {
        using var dir = new TempDir();
        string original = Write(dir, "core_1", new CachedTexture(10, 8, 8, 1, new byte[] { 1, 2, 3, 4 }));
        string copy = Write(dir, "mod_1", new CachedTexture(10, 8, 8, 1, new byte[] { 1, 2, 3, 4 }));

        Assert.Equal(original, copy);
        Assert.NotEqual(original, Write(dir, "d_format", new CachedTexture(12, 8, 8, 1, new byte[] { 1, 2, 3, 4 })));
        Assert.NotEqual(original, Write(dir, "d_size", new CachedTexture(10, 16, 8, 1, new byte[] { 1, 2, 3, 4 })));
        Assert.NotEqual(original, Write(dir, "d_mips", new CachedTexture(10, 8, 8, 2, new byte[] { 1, 2, 3, 4 })));
        Assert.NotEqual(original, Write(dir, "d_pixels", new CachedTexture(10, 8, 8, 1, new byte[] { 1, 2, 3, 5 })));
        // Filter mode changes how the same pixels are sampled, so it must not be merged away either.
        Assert.NotEqual(original, Write(dir, "d_filter",
            new CachedTexture(10, 8, 8, 1, new byte[] { 1, 2, 3, 4 }, filterMode: 0)));
    }

    [Fact]
    public void AnAbsentStaleOrUnnamedTextureResolvesToNothing()
    {
        using var dir = new TempDir();
        // The cold-load case: the mesh phase asks before the texture phase has written anything.
        Assert.Equal("", TextureIdentity.Resolve(dir.Path, "core_1"));
        Assert.Equal("", TextureIdentity.Resolve(dir.Path, ""));

        // A stale format is rewritten by the next texture pass; hashing it would pin an identity the
        // pixels behind it do not have.
        dir.Write("stale.tex", new byte[] { 9, 9, 9, 9, 0, 0, 0, 0 });
        Assert.Equal("", TextureIdentity.Resolve(dir.Path, "stale"));
    }

    [Fact]
    public void ResolveAll_AnswersForTheKeysItCan_AndLeavesTheRestToTheirCaller()
    {
        using var dir = new TempDir();
        string alias = Write(dir, "core_1", new CachedTexture(10, 4, 4, 1, new byte[] { 7, 7 }));
        Write(dir, "mod_2", new CachedTexture(10, 4, 4, 1, new byte[] { 7, 7 }));
        Write(dir, "mod_3", new CachedTexture(10, 4, 4, 1, new byte[] { 8, 8 }));

        Dictionary<string, string> resolved = TextureIdentity.ResolveAll(dir.Path,
            new[] { "core_1", "mod_2", "mod_3", "never_extracted" });

        // The key that never landed is simply absent: the caller keeps whatever identity it already had,
        // which is the raw key, so it cannot be merged with anything.
        Assert.Equal(new HashSet<string> { "core_1", "mod_2", "mod_3" }, new HashSet<string>(resolved.Keys));
        Assert.Equal(alias, resolved["core_1"]);
        Assert.Equal(alias, resolved["mod_2"]);
        Assert.NotEqual(alias, resolved["mod_3"]);
    }

    private static string Write(TempDir dir, string key, CachedTexture texture)
    {
        using (var stream = new MemoryStream())
        {
            TextureCache.Write(stream, texture);
            dir.Write(key + ".tex", stream.ToArray());
        }
        return TextureIdentity.Resolve(dir.Path, key);
    }
}
