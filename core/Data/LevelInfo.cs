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

    public LevelInfo(string path) => Path = path;

    public string HeightmapsDir => System.IO.Path.Combine(Path, "Landscape", "Heightmaps");
    public string SplatmapsDir => System.IO.Path.Combine(Path, "Landscape", "Splatmaps");
    // Not every map has one: a map with no terrain holes never writes the folder at all.
    public string HolesDir => System.IO.Path.Combine(Path, "Landscape", "Holes");
    public string ObjectsDat => System.IO.Path.Combine(Path, "Level", "Objects.dat");

    private static readonly Regex TileRegex =
        new(@"^Tile_(-?\d+)_(-?\d+)_Source\.heightmap$", RegexOptions.Compiled);

    // Glob the directory instead of parsing the level hierarchy file to find tiles.
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

    public string HolePath(int x, int y) =>
        System.IO.Path.Combine(HolesDir, LandscapeHoles.FileName(x, y));
}
