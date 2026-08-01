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
            int x = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int y = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            tiles.Add((x, y));
        }
        return tiles;
    }

    public string HeightmapPath(int x, int y) =>
        System.IO.Path.Combine(HeightmapsDir, $"Tile_{x}_{y}_Source.heightmap");

    public string SplatmapPath(int x, int y) =>
        System.IO.Path.Combine(SplatmapsDir, $"Tile_{x}_{y}_Source.splatmap");
}
