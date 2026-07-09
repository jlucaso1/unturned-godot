using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class LevelHierarchyTests
{
    // Mirrors Level.hierarchy's shape: an early Tiles array with coords only (the hole data) and a later
    // one carrying each tile's Materials list (LandscapeTile.write).
    private const string Sample = """
        "Landscape"
        {
            "Version" "2"
            "Tiles"
            [
                {
                    "Coord"
                    {
                        "X" "0"
                        "Y" "0"
                    }
                }
            ]
        }
        "Other"
        {
            "Tiles"
            [
                {
                    "Coord"
                    {
                        "X" "0"
                        "Y" "0"
                    }
                    "Materials"
                    [
                        {
                            "GUID" "92cb5a3afd534054a64eb320b50c48de"
                        }
                        {
                            "GUID" "3d7717c2bc074401853b2fdacd9db1ba"
                        }
                    ]
                }
                {
                    "Coord"
                    {
                        "X" "-1"
                        "Y" "2"
                    }
                    "Materials"
                    [
                        {
                            "GUID" "22b77c4c51514b0fbb66765eedf1a7f4"
                        }
                        [
                            {
                                "GUID" "not-a-guid"
                            }
                        ]
                    ]
                }
                {
                    "Coord"
                    {
                        "X" "oops"
                        "Y" {
                    }
                }
                {
                    "Coord"
                    {
                        "X" "9"
                        "Y" "9"
                    }
                    "Materials" "not-an-array"
                }
            ]
        }
        "Trailer" "Coord" "notabrace"
        "Coord"
        {
            "X" "7"
            "Y" "7"
        }
        "Coord" { "X" "unterminated
        """;

    [Fact]
    public void ReadTileMaterials_ParsesCoordsAndGuids()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Sample);
            Dictionary<(int x, int y), Guid[]> tiles = LevelHierarchy.ReadTileMaterials(path);

            Assert.Equal(2, tiles.Count); // coord-only/broken entries don't clobber or add tiles
            Assert.Equal(
                new[]
                {
                    Guid.Parse("92cb5a3afd534054a64eb320b50c48de"),
                    Guid.Parse("3d7717c2bc074401853b2fdacd9db1ba"),
                },
                tiles[(0, 0)]);
            Assert.Equal(2, tiles[(-1, 2)].Length); // the nested array's malformed GUID reads as Empty
            Assert.Equal(Guid.Empty, tiles[(-1, 2)][1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadTileMaterials_MaterialsArrayCutOffAtEndOfFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            // The Materials array never closes: the walk stops at EOF with what it read so far.
            File.WriteAllText(path, """
                "Coord"
                {
                    "X" "3"
                    "Y" "4"
                }
                "Materials"
                [
                    {
                        "GUID" "92cb5a3afd534054a64eb320b50c48de"
                    }
                """);
            Dictionary<(int x, int y), Guid[]> tiles = LevelHierarchy.ReadTileMaterials(path);
            Assert.Single(tiles[(3, 4)]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadTileMaterials_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(LevelHierarchy.ReadTileMaterials("/nonexistent/Level.hierarchy"));
    }
}
