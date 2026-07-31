using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class UnturnedInstallTests
{
    private const string Home = "/home/tester";

    [Fact]
    public void DefaultSteamRoots_Windows_UsesBothProgramFiles()
    {
        IReadOnlyList<string> roots = UnturnedInstall.DefaultSteamRoots(
            UnturnedInstall.Platform.Windows, Home, @"C:\Program Files (x86)", @"C:\Program Files");

        Assert.Equal(2, roots.Count);
        Assert.Contains(Path.Combine(@"C:\Program Files (x86)", "Steam"), roots);
        Assert.Contains(Path.Combine(@"C:\Program Files", "Steam"), roots);
    }

    [Fact]
    public void DefaultSteamRoots_Windows_DropsUnresolvedFolders_AndDuplicates()
    {
        // Off Windows the ProgramFiles special folders resolve to "", and Path.Combine would then yield
        // a bare relative "Steam", which must never be probed. Identical folders collapse to one.
        IReadOnlyList<string> empty = UnturnedInstall.DefaultSteamRoots(
            UnturnedInstall.Platform.Windows, Home, "", "");
        Assert.Empty(empty);

        IReadOnlyList<string> same = UnturnedInstall.DefaultSteamRoots(
            UnturnedInstall.Platform.Windows, Home, @"C:\Program Files", @"C:\Program Files");
        Assert.Single(same);
    }

    [Fact]
    public void DefaultSteamRoots_Mac_UsesApplicationSupport()
    {
        IReadOnlyList<string> roots = UnturnedInstall.DefaultSteamRoots(
            UnturnedInstall.Platform.Mac, Home, "", "");

        Assert.Equal(new[] { Path.Combine(Home, "Library", "Application Support", "Steam") }, roots);
    }

    [Fact]
    public void DefaultSteamRoots_Linux_CoversTheUsualLayoutsIncludingFlatpak()
    {
        IReadOnlyList<string> roots = UnturnedInstall.DefaultSteamRoots(
            UnturnedInstall.Platform.Linux, Home, "", "");

        Assert.Equal(4, roots.Count);
        Assert.Equal(Path.Combine(Home, ".local", "share", "Steam"), roots[0]);
        Assert.Contains(Path.Combine(Home, ".steam", "steam"), roots);
        Assert.Contains(Path.Combine(Home, ".steam", "root"), roots);
        Assert.Contains(Path.Combine(Home, ".var", "app", "com.valvesoftware.Steam",
            ".local", "share", "Steam"), roots);
    }

    [Fact]
    public void ParseLibraryFolders_CurrentFormat_ReadsEveryPathKey()
    {
        const string vdf = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"/home/tester/.local/share/Steam"
            		"label"		""
            		"apps"
            		{
            			"304930"		"1234567"
            		}
            	}
            	"1"
            	{
            		"path"		"/mnt/games/SteamLibrary"
            	}
            }
            """;

        Assert.Equal(new[] { "/home/tester/.local/share/Steam", "/mnt/games/SteamLibrary" },
            UnturnedInstall.ParseLibraryFolders(vdf));
    }

    [Fact]
    public void ParseLibraryFolders_LegacyFormat_TakesTheNumberedValues()
    {
        // Pre-2021 Steam wrote the path straight into the numbered key, and Windows paths arrive
        // with escaped backslashes.
        const string vdf = """
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"1600000000"
            	"1"		"D:\\SteamLibrary"
            	"2"		"E:\\Games\\Steam"
            }
            """;

        Assert.Equal(new[] { @"D:\SteamLibrary", @"E:\Games\Steam" },
            UnturnedInstall.ParseLibraryFolders(vdf));
    }

    [Fact]
    public void ParseLibraryFolders_SkipsCommentsEmptyAndDuplicateValues()
    {
        const string vdf = """
            // a comment line
            "libraryfolders"
            {
            	"0" { "path" "/lib/one" } // trailing comment
            	"1" { "path" "" }
            	"2" { "path" "/lib/one" }
            	"3" { "contentstatsid" "-12345" }
            }
            """;

        Assert.Equal(new[] { "/lib/one" }, UnturnedInstall.ParseLibraryFolders(vdf));
    }

    [Fact]
    public void ParseLibraryFolders_HandlesBareWordsEscapesAndTruncation()
    {
        // Bare (unquoted) tokens are legal KeyValues; \n and \t are the escapes Steam emits; a file
        // cut off mid-string must not throw.
        Assert.Equal(new[] { "/lib/bare" }, UnturnedInstall.ParseLibraryFolders("path /lib/bare"));
        Assert.Equal(new[] { "/lib/bare" }, UnturnedInstall.ParseLibraryFolders("{ path /lib/bare }"));
        Assert.Equal(new[] { "/a\nb\tc" }, UnturnedInstall.ParseLibraryFolders("\"path\" \"/a\\nb\\tc\""));
        Assert.Equal(new[] { "/truncated" }, UnturnedInstall.ParseLibraryFolders("\"path\" \"/truncated"));
        Assert.Equal(new[] { "/lib/x" }, UnturnedInstall.ParseLibraryFolders("\"path\" \"/lib/x\" // no newline"));
        Assert.Empty(UnturnedInstall.ParseLibraryFolders(""));
        Assert.Empty(UnturnedInstall.ParseLibraryFolders("\"lonely-key\""));
        Assert.Empty(UnturnedInstall.ParseLibraryFolders("path{}")); // a block, not a value
    }

    [Fact]
    public void EnumerateLibraries_AddsTheExtraDrivesFromLibraryFolders_WithoutDuplicates()
    {
        using var dir = new TempDir();
        string root = Path.Combine(dir.Path, "Steam");
        string extra = Path.Combine(dir.Path, "SteamLibrary");
        Directory.CreateDirectory(root);
        dir.Write(Path.Combine("Steam", "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
            	"0" { "path" "{{root.Replace("\\", "\\\\")}}" }
            	"1" { "path" "{{extra.Replace("\\", "\\\\")}}" }
            }
            """);

        IReadOnlyList<string> libraries = UnturnedInstall.EnumerateLibraries(new[] { root, "", root });

        Assert.Equal(new[] { root, extra }, libraries);
    }

    [Fact]
    public void EnumerateLibraries_WithoutARegistry_JustReturnsTheRoots()
    {
        using var dir = new TempDir();
        Assert.Equal(new[] { dir.Path }, UnturnedInstall.EnumerateLibraries(new[] { dir.Path }));
    }

    [Fact]
    public void EnumerateLibraries_UnreadableRegistry_FallsBackToTheRoot()
    {
        using var dir = new TempDir();
        string vdf = Path.Combine(dir.Path, "steamapps", "libraryfolders.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(vdf)!);
        File.WriteAllText(vdf, "\"path\" \"/never/read\"");

        // Hold the file exclusively so the read throws instead of returning content.
        using (new FileStream(vdf, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(new[] { dir.Path }, UnturnedInstall.EnumerateLibraries(new[] { dir.Path }));
        }
    }

    [Fact]
    public void EnumerateLibraries_RegistryWithoutReadPermission_FallsBackToTheRoot()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX permissions only

        using var dir = new TempDir();
        string vdf = Path.Combine(dir.Path, "steamapps", "libraryfolders.vdf");
        Directory.CreateDirectory(Path.GetDirectoryName(vdf)!);
        File.WriteAllText(vdf, "\"path\" \"/never/read\"");
        File.SetUnixFileMode(vdf, UnixFileMode.None);

        try
        {
            File.ReadAllText(vdf);
            return; // running as root, where file modes do not apply: nothing to assert
        }
        catch (UnauthorizedAccessException)
        {
            // expected: this is the case the lookup has to survive
        }

        Assert.Equal(new[] { dir.Path }, UnturnedInstall.EnumerateLibraries(new[] { dir.Path }));
    }

    [Fact]
    public void FindInstall_FindsTheGameInAnExtraLibrary()
    {
        using var dir = new TempDir();
        string root = Path.Combine(dir.Path, "Steam");
        string extra = Path.Combine(dir.Path, "SteamLibrary");
        string game = Path.Combine(extra, "steamapps", "common", "Unturned");
        Directory.CreateDirectory(game);
        dir.Write(Path.Combine("Steam", "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{extra.Replace("\\", "\\\\")}\" }} }}");

        Assert.Equal(game, UnturnedInstall.FindInstall(new[] { root }));
    }

    [Fact]
    public void FindInstall_WithoutTheGame_ReturnsNull()
    {
        using var dir = new TempDir();
        Assert.Null(UnturnedInstall.FindInstall(new[] { dir.Path }));
        Assert.Null(UnturnedInstall.FindInstall(Array.Empty<string>()));
    }

    [Theory]
    [InlineData(UnturnedInstall.Platform.Windows, "core.masterbundle")]
    [InlineData(UnturnedInstall.Platform.Linux, "core_linux.masterbundle")]
    [InlineData(UnturnedInstall.Platform.Mac, "core_mac.masterbundle")]
    public void MasterBundleFileNames_PutTheNativeBundleFirst(UnturnedInstall.Platform platform,
        string expected)
    {
        IReadOnlyList<string> names = UnturnedInstall.MasterBundleFileNames(platform);

        Assert.Equal(expected, names[0]);
        Assert.Equal(3, names.Count); // the other two stay as fallbacks
        Assert.Contains("core.masterbundle", names);
        Assert.Contains("core_linux.masterbundle", names);
        Assert.Contains("core_mac.masterbundle", names);
    }

    [Fact]
    public void FindMasterBundle_PrefersTheNativeBundle_ThenAcceptsAnother()
    {
        using var dir = new TempDir();
        dir.Write(Path.Combine("Bundles", "core_linux.masterbundle"), new byte[] { 1 });

        // A Linux install: the native name wins.
        Assert.Equal(Path.Combine(dir.Path, "Bundles", "core_linux.masterbundle"),
            UnturnedInstall.FindMasterBundle(dir.Path, UnturnedInstall.Platform.Linux));

        // The same install read from Windows: no core.masterbundle, so the Linux one is used;
        // meshes, materials and textures are identical across the platform builds.
        Assert.Equal(Path.Combine(dir.Path, "Bundles", "core_linux.masterbundle"),
            UnturnedInstall.FindMasterBundle(dir.Path, UnturnedInstall.Platform.Windows));

        dir.Write(Path.Combine("Bundles", "core.masterbundle"), new byte[] { 1 });
        Assert.Equal(Path.Combine(dir.Path, "Bundles", "core.masterbundle"),
            UnturnedInstall.FindMasterBundle(dir.Path, UnturnedInstall.Platform.Windows));
    }

    [Fact]
    public void FindMasterBundle_MissingContent_ReturnsNull()
    {
        using var dir = new TempDir();
        Assert.Null(UnturnedInstall.FindMasterBundle(dir.Path, UnturnedInstall.Platform.Mac));
    }

    // With the game installed, the real lookup must land on a directory that actually holds it.
    [Fact]
    public void Find_ResolvesTheRealInstall_WhenPresent()
    {
        if (GameData.Install is not { } install)
            return;

        Assert.True(Directory.Exists(Path.Combine(install, "Bundles")));
        Assert.NotNull(UnturnedInstall.FindMasterBundle(install));
    }
}
