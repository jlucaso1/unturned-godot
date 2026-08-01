using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class ExactContentKeyTests
{
    [Fact]
    public void StreamingFileKeyMatchesMemoryKeyAndChangesWithContent()
    {
        string path = Path.GetTempFileName();
        try
        {
            byte[] first = Enumerable.Range(0, 8193).Select(i => (byte)(i * 31)).ToArray();
            File.WriteAllBytes(path, first);
            Assert.Equal(ExactContentKey.Bytes(first), ExactContentKey.File(path));

            first[^1] ^= 1;
            File.WriteAllBytes(path, first);
            Assert.Equal(ExactContentKey.Bytes(first), ExactContentKey.File(path));
            Assert.NotEqual(ExactContentKey.Bytes(first.AsSpan(0, first.Length - 1)), ExactContentKey.File(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EqualContentSharesAKey_AndEveryImageDimensionParticipates()
    {
        byte[] pixels = { 1, 2, 3, 4, 5 };
        Assert.Equal(ExactContentKey.Bytes(pixels), ExactContentKey.Bytes((byte[])pixels.Clone()));
        string image = ExactContentKey.Image(10, 32, 16, 4, pixels);
        Assert.Equal(image, ExactContentKey.Image(10, 32, 16, 4, (byte[])pixels.Clone()));
        Assert.NotEqual(image, ExactContentKey.Image(12, 32, 16, 4, pixels));
        Assert.NotEqual(image, ExactContentKey.Image(10, 64, 16, 4, pixels));
        Assert.NotEqual(image, ExactContentKey.Image(10, 32, 8, 4, pixels));
        Assert.NotEqual(image, ExactContentKey.Image(10, 32, 16, 3, pixels));
        Assert.NotEqual(image, ExactContentKey.Image(10, 32, 16, 4, new byte[] { 1, 2, 3, 4, 6 }));
    }

    [Fact]
    public void FileGroups_DeduplicateExactBytes_PreserveOrder_AndKeepSameSizeDifferences()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ug-groups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string A(string name, params byte[] bytes)
            {
                string path = Path.Combine(dir, name);
                File.WriteAllBytes(path, bytes);
                return path;
            }
            var sources = new List<(string Item, string Path)>
            {
                ("a", A("a", 1, 2, 3)), ("unique", A("u", 9)),
                ("different", A("d", 3, 2, 1)), ("alias", A("alias", 1, 2, 3)),
            };

            IReadOnlyList<ExactFileGroups.Group<string>> groups = ExactFileGroups.Build(sources);

            Assert.Equal(3, groups.Count);
            Assert.Equal(new[] { "a", "alias" }, groups[0].Items);
            Assert.Equal(new[] { "unique" }, groups[1].Items);
            Assert.Equal(new[] { "different" }, groups[2].Items);
            Assert.Equal(4, ExactFileGroups.Build(sources, deduplicate: false).Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
