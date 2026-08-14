using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace UnturnedGodot.Data;

public sealed class LevelInfo
{
    public readonly string Path;
    public string Name => System.IO.Path.GetFileName(Path);

    private LevelConfigData? _config;

    public LevelInfo(string path) => Path = path;

    // The map's own Config.json (SDG.Unturned.LevelInfo.configData), read once and cached. A map with no
    // config reads LevelInfoConfigData's constructor defaults, which is what the game falls back to too.
    public LevelConfigData Config => _config ??= LevelConfigData.Load(Path);

    // Which terrain system this map uses — from the map's own declaration, not from what happens to be
    // on disk. LevelGround.load branches on exactly this (LevelGround.cs:948):
    //
    //     if (!Level.info.configData.Use_Legacy_Ground) { loadTrees(); return; }
    //
    // and everything past that early return builds the single legacy Unity Terrain this port does not
    // read. Globbing Landscape/Heightmaps answered the same question by its side effect, which is not
    // the same question: a Landscape map whose tiles could not be listed (an unreadable directory, a
    // partial download) read as legacy and was reported unsupported for the wrong reason, and a legacy
    // map is not merely a map with no tiles — it needs a different loader.
    public bool UsesLandscapeTerrain => !Config.UseLegacyGround;

    public string HeightmapsDir => System.IO.Path.Combine(Path, "Landscape", "Heightmaps");
    public string SplatmapsDir => System.IO.Path.Combine(Path, "Landscape", "Splatmaps");
    public string ObjectsDat => System.IO.Path.Combine(Path, "Level", "Objects.dat");

    private static readonly Regex TileRegex =
        new(@"^Tile_(-?\d+)_(-?\d+)_Source\.heightmap$", RegexOptions.Compiled);

    // Which tiles this map ships. The level hierarchy names them too, but the files are the authority on
    // what can actually be read, and a tile named there but absent would still have to be skipped.
    public List<(int x, int y)> EnumerateTiles()
    {
        var tiles = new List<(int, int)>();
        if (!Directory.Exists(HeightmapsDir)) return tiles;

        string[] files;
        try
        {
            files = Directory.GetFiles(HeightmapsDir, "*.heightmap");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A map whose tiles cannot be listed has no landscape as far as everything downstream is
            // concerned: the browser marks it unsupported instead of the exception escaping and taking
            // the whole catalogue — and with it every other map — down with it.
            return tiles;
        }

        foreach (string file in files)
        {
            var m = TileRegex.Match(System.IO.Path.GetFileName(file));
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
                continue;
            tiles.Add((x, y));
        }
        return tiles;
    }

    public string HeightmapPath(int x, int y) =>
        System.IO.Path.Combine(HeightmapsDir, $"Tile_{x}_{y}_Source.heightmap");

    public string SplatmapPath(int x, int y) =>
        System.IO.Path.Combine(SplatmapsDir, $"Tile_{x}_{y}_Source.splatmap");
}
