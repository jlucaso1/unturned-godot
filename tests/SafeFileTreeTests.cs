using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class SafeFileTreeTests
{
    [Fact]
    public void EnumerateFiles_SkipsOnlyTheInaccessibleSubtree()
    {
        var files = new Dictionary<string, string[]>
        {
            ["assets"] = new[] { "assets/root.asset" },
            ["assets/good"] = new[] { "assets/good/palette.asset" },
        };
        var directories = new Dictionary<string, string[]>
        {
            ["assets"] = new[] { "assets/locked", "assets/good" },
            ["assets/good"] = Array.Empty<string>(),
        };

        IReadOnlyList<string> found = SafeFileTree.EnumerateFiles("assets", "*.asset",
            (directory, _) => directory == "assets/locked"
                ? throw new UnauthorizedAccessException()
                : files.GetValueOrDefault(directory, Array.Empty<string>()),
            directory => directory == "assets/locked"
                ? throw new UnauthorizedAccessException()
                : directories.GetValueOrDefault(directory, Array.Empty<string>()));

        Assert.Equal(new[] { "assets/root.asset", "assets/good/palette.asset" }, found);
    }

    [Fact]
    public void EnumerateFiles_MissingOrUnreadableRoot_ReturnsEmpty()
    {
        Assert.Empty(SafeFileTree.EnumerateFiles("assets", "*.asset",
            (_, _) => throw new IOException(), _ => throw new IOException()));
    }
}
