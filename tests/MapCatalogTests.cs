using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class MapCatalogTests
{
    // Builds <root>/Maps/<name> with the files a real map has, so Read/Scan see a map.
    private static string WriteMap(TempDir dir, string relative, string? english = null,
        string? config = null, params (int X, int Y)[] tiles)
    {
        dir.Write(Path.Combine(relative, "Level.dat"), new byte[] { 4 });
        if (english != null)
            dir.Write(Path.Combine(relative, "English.dat"), english);
        if (config != null)
            dir.Write(Path.Combine(relative, "Config.json"), config);
        foreach ((int x, int y) in tiles)
            dir.Write(Path.Combine(relative, "Landscape", "Heightmaps", $"Tile_{x}_{y}_Source.heightmap"),
                new byte[] { 0, 0 });
        return Path.Combine(dir.Path, relative);
    }

    [Fact]
    public void Read_TakesNameDescriptionCategoryAndSize()
    {
        using var dir = new TempDir();
        string path = WriteMap(dir, Path.Combine("Maps", "PEI_Folder"),
            english: "Name Prince Edward Island\nDescription Sunny island.\n",
            config: "{\n\t\"Category\": \"Official\",\n\t\"Batching_Version\": 2\n}",
            tiles: new[] { (0, 0), (1, 0), (0, 1), (1, 1) });
        dir.Write(Path.Combine("Maps", "PEI_Folder", "Icon.png"), new byte[] { 1 });
        dir.Write(Path.Combine("Maps", "PEI_Folder", "Preview.png"), new byte[] { 1 });
        dir.Write(Path.Combine("Maps", "PEI_Folder", "Chart.png"), new byte[] { 1 });

        MapEntry? map = MapCatalog.Read(path, MapSource.Official);

        Assert.NotNull(map);
        Assert.Equal("PEI_Folder", map!.FolderName);
        Assert.Equal("Prince Edward Island", map.DisplayName);
        Assert.Equal("Sunny island.", map.Description);
        Assert.Equal("Official", map.Category);
        Assert.Equal(MapSource.Official, map.Source);
        Assert.Equal(4, map.TileCount);
        Assert.Equal(2 * Landscape.TILE_SIZE, map.SizeMetres); // 2x2 tiles
        Assert.True(map.IsSupported);
        Assert.NotNull(map.IconPath);
        Assert.NotNull(map.PreviewPath);
        Assert.NotNull(map.ChartPath);
    }

    [Fact]
    public void Read_WithoutLocalizationOrConfig_FallsBackToTheFolderName()
    {
        using var dir = new TempDir();
        string path = WriteMap(dir, Path.Combine("Maps", "Bare"), tiles: new[] { (0, 0) });

        MapEntry? map = MapCatalog.Read(path, MapSource.Workshop);

        Assert.NotNull(map);
        Assert.Equal("Bare", map!.DisplayName);
        Assert.Null(map.Description);
        Assert.Null(map.Category);
        Assert.Null(map.IconPath);
        Assert.Equal(MapSource.Workshop, map.Source);
    }

    [Fact]
    public void Read_LegacyTerrainMapIsListedButNotSupported()
    {
        using var dir = new TempDir();
        string path = WriteMap(dir, Path.Combine("Maps", "Destruction"),
            english: "Description Demo.\n");

        MapEntry? map = MapCatalog.Read(path, MapSource.Official);

        Assert.NotNull(map);
        Assert.False(map!.IsSupported);
        Assert.Equal(0, map.TileCount);
        Assert.Equal(0f, map.SizeMetres);
    }

    [Fact]
    public void Read_NonMapDirectoryOrBrokenConfig_IsHandled()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "NotAMap"));
        Assert.Null(MapCatalog.Read(Path.Combine(dir.Path, "NotAMap"), MapSource.Official));

        // A config that is not JSON, is not an object, or has a non-string category: all cosmetic.
        string broken = WriteMap(dir, Path.Combine("Maps", "Broken"), config: "{ not json");
        Assert.Null(MapCatalog.Read(broken, MapSource.Official)!.Category);

        string list = WriteMap(dir, Path.Combine("Maps", "ListConfig"), config: "[1, 2]");
        Assert.Null(MapCatalog.Read(list, MapSource.Official)!.Category);

        string number = WriteMap(dir, Path.Combine("Maps", "NumberCategory"), config: "{ \"Category\": 7 }");
        Assert.Null(MapCatalog.Read(number, MapSource.Official)!.Category);
    }

    // A \uD800 escape with no low half is JSON the parser accepts and GetString refuses: it throws
    // InvalidOperationException, not JsonException, so the obvious catch misses it. That is not one map
    // losing a cosmetic label — the throw leaves Read and leaves Scan, so a single hand-edited workshop
    // config took the whole map list down with it.
    [Fact]
    public void Read_ConfigWithAnUnpairedSurrogate_LosesOnlyTheCategory()
    {
        using var dir = new TempDir();

        string high = WriteMap(dir, Path.Combine("Maps", "LoneHigh"), config: "{ \"Category\": \"\\uD800\" }",
            tiles: new[] { (0, 0) });
        MapEntry? map = MapCatalog.Read(high, MapSource.Official);
        Assert.NotNull(map);
        Assert.Null(map!.Category);
        Assert.True(map.IsSupported); // still a listable, loadable map

        string low = WriteMap(dir, Path.Combine("Maps", "LoneLow"), config: "{ \"Category\": \"\\uDC00\" }");
        Assert.Null(MapCatalog.Read(low, MapSource.Official)!.Category);

        // A complete pair is a perfectly good category and must survive.
        string pair = WriteMap(dir, Path.Combine("Maps", "Astral"), config: "{ \"Category\": \"\\uD800\\uDC00\" }");
        Assert.Equal("\U00010000", MapCatalog.Read(pair, MapSource.Official)!.Category);

        // And the scan as a whole keeps listing every map, including the two broken ones.
        List<string> names = MapCatalog.Scan(dir.Path).Select(entry => entry.FolderName).ToList();
        Assert.Contains("LoneHigh", names);
        Assert.Contains("LoneLow", names);
        Assert.Contains("Astral", names);
    }

    [Fact]
    public void Read_TrailingSeparatorStillYieldsTheFolderName()
    {
        using var dir = new TempDir();
        string path = WriteMap(dir, Path.Combine("Maps", "Yukon"), tiles: new[] { (0, 0) });

        MapEntry? map = MapCatalog.Read(path + Path.DirectorySeparatorChar, MapSource.Official);

        Assert.Equal("Yukon", map!.FolderName);
    }

    [Fact]
    public void SizeMetres_UsesTheSpanOfTheTiles_NotTheirCount()
    {
        using var dir = new TempDir();
        // Two tiles three apart: a 4-tile span with holes, like a coastline.
        string path = WriteMap(dir, Path.Combine("Maps", "Sparse"), tiles: new[] { (-2, 0), (1, 0) });

        MapEntry? map = MapCatalog.Read(path, MapSource.Official);

        Assert.Equal(2, map!.TileCount);
        Assert.Equal(4 * Landscape.TILE_SIZE, map.SizeMetres);
    }

    [Fact]
    public void Scan_ListsPlayableFirst_ThenOfficialBeforeWorkshop_ThenByName()
    {
        using var dir = new TempDir();
        WriteMap(dir, Path.Combine("Maps", "Zulu"), tiles: new[] { (0, 0) });
        WriteMap(dir, Path.Combine("Maps", "Alpha"), tiles: new[] { (0, 0) });
        WriteMap(dir, Path.Combine("Maps", "Legacy"));                       // no tiles: last
        WriteMap(dir, Path.Combine("Bundles", "Workshop", "Maps", "Bravo"), tiles: new[] { (0, 0) });
        // Sorts before every official map by name, so it can only come after them by source.
        WriteMap(dir, Path.Combine("Bundles", "Workshop", "Maps", "Aardvark"), tiles: new[] { (0, 0) });
        // A folder under Maps/ that is not a map at all (the game keeps editor scratch there).
        Directory.CreateDirectory(Path.Combine(dir.Path, "Maps", "NotAMap"));

        IReadOnlyList<MapEntry> maps = MapCatalog.Scan(dir.Path);

        var names = new List<string>();
        foreach (MapEntry map in maps)
            names.Add(map.FolderName);

        Assert.Equal(new[] { "Alpha", "Zulu", "Aardvark", "Bravo", "Legacy" }, names);
    }

    [Fact]
    public void Scan_FindsWorkshopMapsNestedInTheirItemFolder_AndDedupes()
    {
        using var dir = new TempDir();
        // A Steam library: <lib>/steamapps/common/Unturned, with items under <lib>/steamapps/workshop.
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        Directory.CreateDirectory(install);
        dir.Write(Path.Combine("steamapps", "workshop", "content", "304930", "3707778928",
            "California2", "Level.dat"), new byte[] { 4 });
        dir.Write(Path.Combine("steamapps", "workshop", "content", "304930", "3707778928",
            "California2", "Landscape", "Heightmaps", "Tile_0_0_Source.heightmap"), new byte[] { 0, 0 });
        // A mod item with no map inside must not break the scan.
        dir.Write(Path.Combine("steamapps", "workshop", "content", "304930", "1418436222",
            "MoreStacking.txt"), "not a map");
        // The game also copies subscribed maps next to its own; the copy must not double up.
        dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles", "Workshop", "Maps",
            "California2", "Level.dat"), new byte[] { 4 });

        IReadOnlyList<MapEntry> maps = MapCatalog.Scan(install);

        MapEntry map = Assert.Single(maps);
        Assert.Equal("California2", map.FolderName);
        Assert.Equal(MapSource.Workshop, map.Source);
        Assert.True(map.IsSupported); // the workshop copy with tiles won, not the empty one
    }

    [Fact]
    public void Scan_RetainsSameNamedMapsFromDifferentWorkshopItems_AndResolvesTheirSelectionKeys()
    {
        using var dir = new TempDir();
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        Directory.CreateDirectory(install);
        // A game-managed copy must not turn the folder name into a permanent deduplication key.
        WriteMap(dir, Path.Combine("steamapps", "common", "Unturned", "Bundles", "Workshop", "Maps",
            "Arena"));
        string first = WriteMap(dir,
            Path.Combine("steamapps", "workshop", "content", "304930", "111", "Arena"),
            english: "Name First Arena\n", tiles: new[] { (0, 0) });
        string second = WriteMap(dir,
            Path.Combine("steamapps", "workshop", "content", "304930", "222", "Arena"),
            english: "Name Second Arena\n", tiles: new[] { (0, 0), (1, 0) });

        IReadOnlyList<MapEntry> matches = MapCatalog.Scan(install)
            .Where(map => map.FolderName == "Arena").ToArray();

        Assert.Equal(2, matches.Count);
        Assert.Equal(2, matches.Select(map => map.SelectionKey).Distinct().Count());
        Assert.Equal(first, MapCatalog.ResolvePath(install,
            Assert.Single(matches, map => map.DisplayName == "First Arena").SelectionKey));
        Assert.Equal(second, MapCatalog.ResolvePath(install,
            Assert.Single(matches, map => map.DisplayName == "Second Arena").SelectionKey));
    }

    [Fact]
    public void Scan_MissingInstall_ReturnsNothing()
    {
        using var dir = new TempDir();
        Assert.Empty(MapCatalog.Scan(Path.Combine(dir.Path, "nope")));
    }

    [Fact]
    public void SearchDirectories_CoversMapsWorkshopCopyAndSteamWorkshop()
    {
        string install = Path.Combine("lib", "steamapps", "common", "Unturned");
        IReadOnlyList<(string Directory, MapSource Source)> directories =
            MapCatalog.SearchDirectories(install);

        Assert.Equal(3, directories.Count);
        Assert.Equal(Path.Combine(install, "Maps"), directories[0].Directory);
        Assert.Equal(MapSource.Official, directories[0].Source);
        Assert.Equal(Path.Combine("lib", "steamapps", "workshop", "content", "304930"),
            directories[2].Directory);
        Assert.Equal(MapSource.Workshop, directories[2].Source);
    }

    [Fact]
    public void SearchDirectories_AtAFilesystemRoot_SkipsTheWorkshopGuess()
    {
        // No two parent directories to walk up: only the in-install locations are searched.
        IReadOnlyList<(string Directory, MapSource Source)> directories =
            MapCatalog.SearchDirectories(Path.GetPathRoot(Path.GetTempPath())!);

        Assert.Equal(2, directories.Count);
    }

    [Fact]
    public void ResolvePath_PrefersTheShippedMap_ThenFindsWorkshopOnes()
    {
        using var dir = new TempDir();
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        string shipped = Path.Combine(install, "Maps", "PEI");
        Directory.CreateDirectory(shipped);
        dir.Write(Path.Combine("steamapps", "workshop", "content", "304930", "999", "California2",
            "Level.dat"), new byte[] { 4 });
        dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles", "Workshop", "Maps",
            "Flat", "Level.dat"), new byte[] { 4 });

        Assert.Equal(shipped, MapCatalog.ResolvePath(install, "PEI"));
        Assert.Equal(Path.Combine(dir.Path, "steamapps", "workshop", "content", "304930", "999",
            "California2"), MapCatalog.ResolvePath(install, "california2")); // case-insensitive
        Assert.Equal(Path.Combine(install, "Bundles", "Workshop", "Maps", "Flat"),
            MapCatalog.ResolvePath(install, "Flat"));
    }

    // Scan prefers the copy with terrain when two folders share a name, so ResolvePath has to as well —
    // otherwise the player picks the workshop map the menu listed and the stale official copy loads.
    [Fact]
    public void ResolvePath_StaleOfficialCopy_DoesNotShadowTheLoadableWorkshopMap()
    {
        using var dir = new TempDir();
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        string relative = Path.Combine("steamapps", "common", "Unturned");

        // Left behind by an unsubscribe: a map folder with no Landscape tiles to load.
        WriteMap(dir, Path.Combine(relative, "Maps", "California2"));
        string workshop = WriteMap(dir,
            Path.Combine("steamapps", "workshop", "content", "304930", "999", "California2"),
            tiles: new[] { (0, 0), (1, 0) });

        Assert.Equal(workshop, MapCatalog.ResolvePath(install, "California2"));

        // And the menu agrees: the entry it lists is the one that resolves.
        MapEntry listed = Assert.Single(MapCatalog.Scan(install), m => m.FolderName == "California2");
        Assert.Equal(workshop, listed.Path);
    }

    // Nothing is loadable: still resolve to a real folder rather than inventing one, so the failure the
    // player sees is "this map has no terrain", not "this map does not exist".
    [Fact]
    public void ResolvePath_NoLoadableCopy_FallsBackToTheOneThatExists()
    {
        using var dir = new TempDir();
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        string stale = WriteMap(dir,
            Path.Combine("steamapps", "common", "Unturned", "Maps", "Yukon"));

        Assert.Equal(stale, MapCatalog.ResolvePath(install, "Yukon"));
    }

    [Fact]
    public void ResolvePath_UnknownMap_FallsBackToTheShippedLocation()
    {
        using var dir = new TempDir();
        Assert.Equal(Path.Combine(dir.Path, "Maps", "Ghost"), MapCatalog.ResolvePath(dir.Path, "Ghost"));
    }

    [Fact]
    public void Find_ReturnsOnlyCataloguedMapsAndPreservesSupportState()
    {
        using var dir = new TempDir();
        WriteMap(dir, Path.Combine("Maps", "PEI"), tiles: new[] { (0, 0) });
        WriteMap(dir, Path.Combine("Maps", "Legacy"));

        Assert.True(MapCatalog.Find(dir.Path, "pei")!.IsSupported);
        Assert.False(MapCatalog.Find(dir.Path, "Legacy")!.IsSupported);
        Assert.Null(MapCatalog.Find(dir.Path, "Ghost"));
    }

    [Fact]
    public void Scan_UnreadableDirectory_IsSkipped()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX permissions only

        using var dir = new TempDir();
        string maps = Path.Combine(dir.Path, "Maps");
        Directory.CreateDirectory(maps);
        File.SetUnixFileMode(maps, UnixFileMode.None);

        try
        {
            Directory.EnumerateDirectories(maps).GetEnumerator().MoveNext();
            return; // running as root: modes do not apply, nothing to assert
        }
        catch (UnauthorizedAccessException)
        {
            // expected
        }

        Assert.Empty(MapCatalog.Scan(dir.Path));
        File.SetUnixFileMode(maps, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void CompareForMenu_RanksPlayableThenOfficialThenName_Symmetrically()
    {
        MapEntry Make(string name, MapSource source, int tiles) => new()
        {
            FolderName = name,
            Path = name,
            DisplayName = name,
            Source = source,
            TileCount = tiles,
        };

        MapEntry playable = Make("Zulu", MapSource.Official, 1);
        MapEntry legacy = Make("Alpha", MapSource.Official, 0);
        MapEntry workshop = Make("Alpha", MapSource.Workshop, 1);

        Assert.True(MapCatalog.CompareForMenu(playable, legacy) < 0);   // playable wins over legacy
        Assert.True(MapCatalog.CompareForMenu(legacy, playable) > 0);
        Assert.True(MapCatalog.CompareForMenu(playable, workshop) < 0); // official wins over workshop
        Assert.True(MapCatalog.CompareForMenu(workshop, playable) > 0);
        Assert.True(MapCatalog.CompareForMenu(workshop, Make("Bravo", MapSource.Workshop, 1)) < 0); // by name
        Assert.Equal(0, MapCatalog.CompareForMenu(workshop, workshop));
    }

    [Fact]
    public void Read_UnreadableMetadata_StillYieldsTheMap()
    {
        using var dir = new TempDir();
        string path = WriteMap(dir, Path.Combine("Maps", "Locked"),
            english: "Name Locked Map\n", config: "{ \"Category\": \"Curated\" }", tiles: new[] { (0, 0) });

        // English.dat held exclusively: the name falls back to the folder, the map still lists.
        using (new FileStream(Path.Combine(path, "English.dat"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            MapEntry? map = MapCatalog.Read(path, MapSource.Official);
            Assert.Equal("Locked", map!.DisplayName);
            Assert.Null(map.Description);
            Assert.Equal("Curated", map.Category); // the config is still readable
        }

        if (OperatingSystem.IsWindows())
            return; // POSIX permissions only

        string config = Path.Combine(path, "Config.json");
        File.SetUnixFileMode(config, UnixFileMode.None);
        try
        {
            File.ReadAllText(config);
            return; // running as root: modes do not apply
        }
        catch (UnauthorizedAccessException)
        {
            // expected
        }

        Assert.Null(MapCatalog.Read(path, MapSource.Official)!.Category);
        File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // With the game installed, the real scan must find PEI and agree with what is on disk.
    [RealDataFact]
    public void Scan_RealInstall_FindsTheShippedMaps()
    {
        string install = GameData.Install!;

        IReadOnlyList<MapEntry> maps = MapCatalog.Scan(install);

        MapEntry pei = Assert.Single(maps, m => m.FolderName == "PEI");
        Assert.True(pei.IsSupported);
        Assert.Equal(16, pei.TileCount);                     // PEI is 4x4 landscape tiles
        Assert.Equal(4 * Landscape.TILE_SIZE, pei.SizeMetres);
        Assert.Equal("Official", pei.Category);
        Assert.Contains("island", pei.Description ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(pei.Path, MapCatalog.ResolvePath(install, "PEI"));
    }

    [Fact]
    public void Scan_MapWhoseTilesCannotBeListed_IsUnsupportedRatherThanFatal()
    {
        // One workshop item with a damaged Landscape folder used to take the whole catalogue with it:
        // the exception escaped the per-map read and the browser listed nothing at all.
        if (!PosixPermissions.AreEnforced)
            return; // this is a POSIX mode check, and the mode bits do not bite a privileged user

        using var dir = new TempDir();
        WriteMap(dir, Path.Combine("Maps", "Good"), tiles: new[] { (0, 0) });
        WriteMap(dir, Path.Combine("Maps", "Damaged"), tiles: new[] { (0, 0) });

        string sealedOff = Path.Combine(dir.Path, "Maps", "Damaged", "Landscape", "Heightmaps");
        File.SetUnixFileMode(sealedOff, UnixFileMode.None);
        try
        {
            IReadOnlyList<MapEntry> maps = MapCatalog.Scan(dir.Path);

            Assert.Contains(maps, m => m.FolderName == "Good" && m.IsSupported);
            MapEntry damaged = Assert.Single(maps, m => m.FolderName == "Damaged");
            Assert.False(damaged.IsSupported);
            Assert.Equal(0, damaged.TileCount);
        }
        finally
        {
            File.SetUnixFileMode(sealedOff, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
    }
}
