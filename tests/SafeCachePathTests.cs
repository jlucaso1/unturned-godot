using System.IO;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class SafeCachePathTests
{
    [Theory]
    [InlineData("step_grass", "step_grass.ogg")]
    [InlineData("../../outside", "outside.ogg")]
    [InlineData("/tmp/rooted", "rooted.ogg")]
    [InlineData("C:\\temp\\windows", "windows.ogg")]
    [InlineData("bad:name?", "bad_name_.ogg")]
    [InlineData("..", "clip_2a.ogg")]
    [InlineData("", "clip_2a.ogg")]
    [InlineData("CON", "clip_2a.ogg")]
    [InlineData("lpt1.raw", "clip_2a.ogg")]
    public void FileNameReducesUntrustedNamesToPortableLeaves(string name, string expected) =>
        Assert.Equal(expected, SafeCachePath.FileName(name, "clip_2a", ".ogg"));

    [Fact]
    public void FileNameFallsBackWhenTheBundleNameIsUnreasonablyLong()
    {
        Assert.Equal("clip_2a.ogg", SafeCachePath.FileName(new string('x', 121), "clip_2a", ".ogg"));
    }

    [Fact]
    public void ResolveChildAcceptsOnlyADirectCacheChild()
    {
        using var dir = new TempDir();
        Assert.True(SafeCachePath.TryResolveChild(dir.Path, "clip.ogg", out string resolved));
        Assert.Equal(Path.Combine(dir.Path, "clip.ogg"), resolved);
        Assert.False(SafeCachePath.TryResolveChild(dir.Path, "../outside.ogg", out _));
        Assert.False(SafeCachePath.TryResolveChild(dir.Path, "/tmp/outside.ogg", out _));
        Assert.False(SafeCachePath.TryResolveChild(dir.Path, "nested/clip.ogg", out _));
    }
}
