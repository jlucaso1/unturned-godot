using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests.Assets;

// Which directory an install keeps its Unity data in depends on which BUILD it is. The retail client
// writes Unturned_Data; the dedicated server — the one scripts/fetch-game-data.sh downloads, because it
// is the build Valve's anonymous account may have — writes Unturned_Headless_Data. Both carry the same
// prefab graph, so accepting only the client's name is what left a fetched check-out drawing a
// placeholder capsule instead of the real character.
public class UnturnedInstallDataFileTests
{
    [Theory]
    [InlineData("Unturned_Data")]
    [InlineData("Unturned_Headless_Data")]
    public void FindsResourcesAssetsUnderEitherLayout(string directory)
    {
        using var dir = new TempDir();
        string written = dir.Write(Path.Combine(directory, "resources.assets"), new byte[] { 1, 2, 3 });

        Assert.Equal(written, UnturnedInstall.FindDataFile(dir.Path, "resources.assets"));
    }

    // The client's layout wins when a directory somehow carries both, which is the order the names are
    // listed in: a real install is one build or the other, so this only decides an ambiguous tree.
    [Fact]
    public void PrefersTheClientLayoutWhenBothArePresent()
    {
        using var dir = new TempDir();
        string client = dir.Write(Path.Combine("Unturned_Data", "resources.assets"), new byte[] { 1 });
        dir.Write(Path.Combine("Unturned_Headless_Data", "resources.assets"), new byte[] { 2 });

        Assert.Equal(client, UnturnedInstall.FindDataFile(dir.Path, "resources.assets"));
    }

    // Null rather than a path that is not there, so the caller falls back to the placeholder instead of
    // handing a missing file to a reader.
    [Fact]
    public void ReturnsNullWhenNoLayoutHasIt()
    {
        using var dir = new TempDir();
        Assert.Null(UnturnedInstall.FindDataFile(dir.Path, "resources.assets"));

        // A directory that exists but is empty is the shape a half-finished download leaves.
        Directory.CreateDirectory(Path.Combine(dir.Path, "Unturned_Headless_Data"));
        Assert.Null(UnturnedInstall.FindDataFile(dir.Path, "resources.assets"));
    }
}
