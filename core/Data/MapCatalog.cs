using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnturnedGodot.Dat;

namespace UnturnedGodot.Data;

// Where a map came from. Workshop maps live outside the install, under Steam's workshop content.
public enum MapSource
{
    Official,
    Workshop,
}

// One installed map, as the menu needs to present it: the folder to load, a display name, and enough
// metadata to tell the player what they are picking and whether this port can render it.
public sealed class MapEntry
{
    public required string FolderName { get; init; }
    public required string Path { get; init; }
    public required string DisplayName { get; init; }
    public MapSource Source { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }

    // Folder names are not unique across Steam workshop items. Official map names remain convenient
    // command-line keys, while workshop selections carry the exact discovered path through the UI.
    public string SelectionKey => Source == MapSource.Workshop
        ? "workshop:" + System.IO.Path.GetFullPath(Path)
        : FolderName;

    // Landscape tiles found on disk, and the square they span (TILE_SIZE metres each).
    public int TileCount { get; init; }
    public float SizeMetres { get; init; }

    // Menu artwork shipped with the map, when present.
    public string? IconPath { get; init; }
    public string? PreviewPath { get; init; }
    public string? ChartPath { get; init; }

    // Pre-2020 maps store terrain as a single legacy heightmap instead of Landscape tiles, which this
    // port does not read yet. They are listed but cannot be loaded.
    public bool IsSupported => TileCount > 0;
}

// Finds the maps installed on this machine: the ones shipped with the game, the ones the game copied
// into Bundles/Workshop/Maps, and the ones Steam downloaded into the workshop content directory.
public static class MapCatalog
{
    // Unturned's Steam app id, which is also its workshop content folder.
    private const string WorkshopAppId = "304930";

    public static IReadOnlyList<MapEntry> Scan(string installRoot)
    {
        var maps = new List<MapEntry>();
        var preferredByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(PathComparer);

        foreach ((string directory, MapSource source) in SearchDirectories(installRoot))
            foreach (string candidate in SafeEnumerateDirectories(directory))
            {
                // Workshop items wrap their map in one more folder level (and may hold a mod instead
                // of a map at all), so look inside anything that is not a map itself.
                if (Read(candidate, source) is { } entry)
                {
                    Add(entry);
                    continue;
                }

                if (source != MapSource.Workshop)
                    continue;

                foreach (string nested in SafeEnumerateDirectories(candidate))
                    if (Read(nested, source) is { } nestedEntry)
                        Add(nestedEntry);
            }

        maps.Sort(CompareForMenu);
        return maps;

        void Add(MapEntry entry)
        {
            string fullPath = Path.GetFullPath(entry.Path);
            if (!seenPaths.Add(fullPath))
                return;

            // An unsupported official folder and the game's internal Workshop/Maps folder can be stale
            // placeholders for a subscribed map. The first Steam source replaces such a placeholder, but
            // the name slot is then released: subsequent Steam items with the same nested folder name are
            // independent maps and retain their own path-based selection keys.
            bool bundledCopy = IsBundledWorkshopCopy(installRoot, fullPath);
            bool placeholder = bundledCopy
                || (entry.Source == MapSource.Official && !entry.IsSupported);
            if (!preferredByName.TryGetValue(entry.FolderName, out int index))
            {
                if (placeholder)
                    preferredByName[entry.FolderName] = maps.Count;
                maps.Add(entry);
            }
            else if (entry.Source == MapSource.Workshop && !bundledCopy)
            {
                maps[index] = entry;
                preferredByName.Remove(entry.FolderName);
            }
            else
            {
                if (entry.IsSupported && !maps[index].IsSupported)
                    maps[index] = entry;
            }
        }
    }

    // Resolves only entries the catalog actually discovered. Unlike ResolvePath, this never invents a
    // fallback path, so command-line/server callers can reject typos and removed maps before listening.
    public static MapEntry? Find(string installRoot, string mapName)
    {
        IReadOnlyList<MapEntry> entries = Scan(installRoot);
        foreach (MapEntry entry in entries)
            if (string.Equals(entry.SelectionKey, mapName, PathComparison))
                return entry;
        foreach (MapEntry entry in entries)
            if (string.Equals(entry.FolderName, mapName, StringComparison.OrdinalIgnoreCase))
                return entry;
        return null;
    }

    // The folder a map name refers to: Maps/<name> when the game ships it, otherwise the workshop item
    // that holds it. Falls back to Maps/<name> so callers report a path the player recognizes.
    //
    // This has to agree with what Scan LISTED, or the player picks one map and loads another. Two folders
    // can carry the same name — the game copies subscribed maps into its own Maps folder, and a copy can
    // be left behind stale or be a pre-Landscape map this port cannot read. Scan resolves that by
    // preferring the copy with terrain, so the same preference applies here: search in the same order,
    // take the first LOADABLE match, and only fall back to a merely-present one when nothing is loadable.
    public static string ResolvePath(string installRoot, string mapName)
    {
        return Find(installRoot, mapName)?.Path ?? Path.Combine(installRoot, "Maps", mapName);
    }

    private static bool IsBundledWorkshopCopy(string installRoot, string path)
    {
        string root = Path.GetFullPath(Path.Combine(installRoot, "Bundles", "Workshop", "Maps"));
        return path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            PathComparison);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    // The directories that contain map folders, most authoritative first.
    public static IReadOnlyList<(string Directory, MapSource Source)> SearchDirectories(string installRoot)
    {
        var directories = new List<(string, MapSource)>
        {
            (Path.Combine(installRoot, "Maps"), MapSource.Official),
            (Path.Combine(installRoot, "Bundles", "Workshop", "Maps"), MapSource.Workshop),
        };

        // <library>/steamapps/common/Unturned -> <library>/steamapps/workshop/content/304930
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installRoot)) is { } common
            && Path.GetDirectoryName(common) is { } steamapps)
        {
            directories.Add((Path.Combine(steamapps, "workshop", "content", WorkshopAppId),
                MapSource.Workshop));
        }

        return directories;
    }

    // Reads one map folder, or returns null when it is not a map (Level.dat is what every map has).
    public static MapEntry? Read(string mapDirectory, MapSource source)
    {
        if (!File.Exists(Path.Combine(mapDirectory, "Level.dat")))
            return null;

        string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(mapDirectory));
        (string? localizedName, string? description) = ReadLocalization(mapDirectory);
        (int tiles, float size) = MeasureLandscape(mapDirectory);

        return new MapEntry
        {
            FolderName = folder,
            Path = mapDirectory,
            DisplayName = string.IsNullOrWhiteSpace(localizedName) ? folder : localizedName,
            Source = source,
            Description = description,
            Category = ReadCategory(mapDirectory),
            TileCount = tiles,
            SizeMetres = size,
            IconPath = ExistingFile(mapDirectory, "Icon.png"),
            PreviewPath = ExistingFile(mapDirectory, "Preview.png"),
            ChartPath = ExistingFile(mapDirectory, "Chart.png"),
        };
    }

    // The order the menu lists maps in: playable ones first, then official before workshop, then by name.
    public static int CompareForMenu(MapEntry a, MapEntry b)
    {
        if (a.IsSupported != b.IsSupported)
            return a.IsSupported ? -1 : 1;
        if (a.Source != b.Source)
            return a.Source == MapSource.Official ? -1 : 1;
        return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    // English.dat carries the localized name (only when it differs from the folder) and the blurb the
    // game shows on its own map picker.
    private static (string? Name, string? Description) ReadLocalization(string mapDirectory)
    {
        string path = Path.Combine(mapDirectory, "English.dat");
        string? text = TryReadAllText(path);
        if (text == null)
            return (null, null);

        DatDictionary root = DatParser.Parse(text);
        return (root.GetString("Name"), root.GetString("Description"));
    }

    // Config.json's Category ("Official", "Curated", ...). Unknown or malformed config is not fatal.
    private static string? ReadCategory(string mapDirectory)
    {
        string? text = TryReadAllText(Path.Combine(mapDirectory, "Config.json"));
        if (text == null)
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(text,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("Category", out JsonElement category)
                && category.ValueKind == JsonValueKind.String)
            {
                return category.GetString();
            }
        }
        catch (JsonException)
        {
            // A map we cannot read the config of still loads; the category is cosmetic.
        }

        return null;
    }

    // Tile count and the edge of the square the tiles span, in metres.
    private static (int Count, float SizeMetres) MeasureLandscape(string mapDirectory)
    {
        List<(int x, int y)> tiles = new LevelInfo(mapDirectory).EnumerateTiles();
        if (tiles.Count == 0)
            return (0, 0f);

        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach ((int x, int y) in tiles)
        {
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        int span = Math.Max(maxX - minX, maxY - minY) + 1;
        return (tiles.Count, span * Landscape.TILE_SIZE);
    }

    private static string? ExistingFile(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        return File.Exists(path) ? path : null;
    }

    // The listing is materialized inside the try rather than handed back as a lazy sequence. .NET's
    // enumerator opens the directory in its constructor, so an unreadable library does throw in this catch
    // today — but nothing in the API guarantees that, and if the open ever moved to the first MoveNext the
    // throw would surface in the CALLER's foreach, with this catch long out of scope. A map listing is a
    // handful of entries, so reading it eagerly costs nothing and does not rely on the distinction.
    private static IReadOnlyList<string> SafeEnumerateDirectories(string directory)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<string>();

        try
        {
            return new List<string>(Directory.EnumerateDirectories(directory));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>(); // unreadable library: skip it rather than fail the menu
        }
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
