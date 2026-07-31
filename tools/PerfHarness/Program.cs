using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot.PerfHarness;

// Isolated micro-benchmarks over the Core parsers, run against the real game data (median of N runs
// after warmup, single process, Release). Each suite skips cleanly when its input isn't present, so the
// harness runs on any machine/OS that has some subset of the data. Usage:
//
//   dotnet run -c Release --project tools/PerfHarness              # all suites
//   dotnet run -c Release --project tools/PerfHarness -- foliage lz4
//
// The Unturned install is resolved from UNTURNED_PATH or common Steam locations; the map from MAP
// (default PEI). To A/B a candidate optimization, copy the current implementation into a local variant,
// benchmark both with Bench(), and gate the numbers on an output-equivalence check first — a variant
// that skips work the real code does (allocations, output structures) will "win" dishonestly.
public static class Program
{
    public static int Main(string[] args)
    {
        var wanted = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        bool all = wanted.Count == 0;
        string? unturned = FindUnturnedPath();
        string? map = unturned == null
            ? null
            : Path.Combine(unturned, "Maps", Environment.GetEnvironmentVariable("MAP") ?? "PEI");
        Console.WriteLine($"Unturned: {unturned ?? "(not found — file-based suites will skip)"}");

        if (all || wanted.Contains("lz4"))
            Lz4Suite();
        if (all || wanted.Contains("foliage"))
            FoliageSuite(map);
        if (all || wanted.Contains("heightmap"))
            HeightmapSuite(map);
        if (all || wanted.Contains("splat"))
            SplatSuite(map);
        if (all || wanted.Contains("objects"))
            ObjectsSuite(map);
        if (all || wanted.Contains("dat"))
            DatSuite(unturned);
        if (all || wanted.Contains("meshcache"))
            MeshCacheSuite();
        return 0;
    }

    // ---------------- suites (baselines over the current Core implementations) ----------------

    // Synthetic input (no game data needed): alternating long literal runs and short matches, the shape
    // the bulk literal copy was tuned on.
    private static void Lz4Suite()
    {
        Console.WriteLine("== lz4: Lz4.Decompress, synthetic 16MB block ==");
        (byte[] data, int rawLen) = BuildLz4Block(16 * 1024 * 1024);
        Bench("Lz4.Decompress", () => Lz4.Decompress(data, rawLen));
        Console.WriteLine();
    }

    private static void FoliageSuite(string? map)
    {
        if (Skip("foliage", TryFile(map, "Foliage.blob"), out string path))
            return;
        byte[] blob = File.ReadAllBytes(path);
        Console.WriteLine($"== foliage: LevelFoliage.Parse, real blob ({blob.Length / (1024 * 1024)}MB) ==");
        Bench("LevelFoliage.Parse", () => LevelFoliage.Parse(blob));
        Console.WriteLine();
    }

    private static void HeightmapSuite(string? map)
    {
        string? dir = map == null ? null : Path.Combine(map, "Landscape", "Heightmaps");
        if (Skip("heightmap", dir != null && Directory.Exists(dir) ? dir : null, out string found))
            return;
        var tiles = new List<(string path, int x, int y)>();
        foreach (string f in Directory.GetFiles(found, "Tile_*.heightmap"))
        {
            string[] parts = Path.GetFileNameWithoutExtension(f).Split('_'); // Tile_X_Y_Source
            tiles.Add((f, int.Parse(parts[1]), int.Parse(parts[2])));
        }
        Console.WriteLine($"== heightmap: HeightmapTile.Read x{tiles.Count} real tiles ==");
        Bench("HeightmapTile.Read (all tiles)", () =>
        {
            foreach ((string path, int x, int y) in tiles)
                HeightmapTile.Read(path, x, y);
        });
        Console.WriteLine();
    }

    private static void SplatSuite(string? map)
    {
        string? dir = map == null ? null : Path.Combine(map, "Landscape", "Splatmaps");
        if (Skip("splat", dir != null && Directory.Exists(dir) ? dir : null, out string found))
            return;
        var files = new List<byte[]>();
        foreach (string f in Directory.GetFiles(found, "Tile_*.splatmap"))
            files.Add(File.ReadAllBytes(f));
        Console.WriteLine($"== splat: SplatmapTile.Parse x{files.Count} real tiles ==");
        Bench("SplatmapTile.Parse (all tiles)", () =>
        {
            foreach (byte[] d in files)
                SplatmapTile.Parse(d, 0, 0);
        });
        Console.WriteLine();
    }

    private static void ObjectsSuite(string? map)
    {
        if (Skip("objects", TryFile(map, Path.Combine("Level", "Objects.dat")), out string path))
            return;
        Console.WriteLine("== objects: LevelObjects.Load, real placements ==");
        Bench("LevelObjects.Load", () => LevelObjects.Load(path));
        Console.WriteLine();
    }

    private static void DatSuite(string? unturned)
    {
        string? dir = unturned == null ? null : Path.Combine(unturned, "Bundles", "Objects");
        if (Skip("dat", dir != null && Directory.Exists(dir) ? dir : null, out string found))
            return;
        var texts = new List<string>();
        foreach (string f in Directory.EnumerateFiles(found, "*.dat", SearchOption.AllDirectories))
            texts.Add(File.ReadAllText(f));
        Console.WriteLine($"== dat: DatParser.Parse x{texts.Count} real asset files ==");
        Bench("DatParser.Parse (all files)", () =>
        {
            foreach (string t in texts)
                DatParser.Parse(t);
        });
        Console.WriteLine();
    }

    private static void MeshCacheSuite()
    {
        string? cache = FindGodotUserDir() is { } user ? Path.Combine(user, "model_cache") : null;
        if (Skip("meshcache", cache != null && Directory.Exists(cache) ? cache : null, out string found))
            return;
        var meshes = new List<byte[]>();
        foreach (string f in Directory.GetFiles(found, "*.mesh"))
            meshes.Add(File.ReadAllBytes(f));
        if (meshes.Count == 0)
        {
            Console.WriteLine("== meshcache: SKIP (cache dir empty — run the game once to populate it) ==\n");
            return;
        }
        Console.WriteLine($"== meshcache: MeshCache.Read x{meshes.Count} cached meshes ==");
        Bench("MeshCache.Read (whole cache)", () =>
        {
            foreach (byte[] d in meshes)
            {
                try
                {
                    MeshCache.Read(d);
                }
                catch (InvalidDataException)
                {
                    // stale format from another checkout; the game re-extracts, the harness just skips it
                }
            }
        });
        Console.WriteLine();
    }

    // ---------------- measurement + environment helpers ----------------

    private static double Bench(string name, Action act, int warmup = 3, int iters = 15)
    {
        for (int i = 0; i < warmup; i++)
            act();
        var times = new List<double>(iters);
        for (int i = 0; i < iters; i++)
        {
            GC.Collect(); // level the GC field so one iteration's garbage doesn't bill the next
            var sw = Stopwatch.StartNew();
            act();
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        double median = times[times.Count / 2];
        Console.WriteLine($"  {name,-44} {median,9:0.000} ms  (min {times[0]:0.000})");
        return median;
    }

    // The Unturned install: UNTURNED_PATH env var first, then the Steam libraries for this OS.
    private static string? FindUnturnedPath() => UnturnedInstall.Find();

    // Godot's per-project user:// directory, where the game keeps its mesh cache, per OS.
    private static string? FindGodotUserDir()
    {
        string appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] candidates =
        {
            Path.Combine(home, ".local", "share", "godot", "app_userdata", "unturned-godot"),   // Linux
            Path.Combine(appdata, "Godot", "app_userdata", "unturned-godot"),                   // Windows
            Path.Combine(home, "Library", "Application Support", "Godot", "app_userdata", "unturned-godot"), // macOS
        };
        foreach (string c in candidates)
            if (Directory.Exists(c))
                return c;
        return null;
    }

    private static string? TryFile(string? map, string relative)
    {
        if (map == null)
            return null;
        string path = Path.Combine(map, relative);
        return File.Exists(path) ? path : null;
    }

    private static bool Skip(string suite, string? resolved, out string path)
    {
        if (resolved == null)
        {
            Console.WriteLine($"== {suite}: SKIP (input not found on this machine) ==\n");
            path = string.Empty;
            return true;
        }
        path = resolved;
        return false;
    }

    // A valid LZ4 block with long literal runs and short overlapping matches, deterministic content.
    private static (byte[] data, int rawLen) BuildLz4Block(int targetRaw)
    {
        var raw = new List<byte>(targetRaw);
        var output = new List<byte>(targetRaw / 2);
        var rng = new Random(42);
        while (raw.Count < targetRaw)
        {
            int litLen = 200 + rng.Next(600);
            int matchLen = 8 + rng.Next(24); // >= the format's 4-byte minimum
            output.Add((byte)((Math.Min(litLen, 15) << 4) | Math.Min(matchLen - 4, 15)));
            if (litLen >= 15)
            {
                int rest = litLen - 15;
                while (rest >= 255) { output.Add(255); rest -= 255; }
                output.Add((byte)rest);
            }
            for (int i = 0; i < litLen; i++)
            {
                byte b = (byte)rng.Next(256);
                output.Add(b);
                raw.Add(b);
            }
            const ushort offset = 64;
            output.Add(offset & 0xFF);
            output.Add(offset >> 8);
            if (matchLen - 4 >= 15)
            {
                int rest = matchLen - 4 - 15;
                while (rest >= 255) { output.Add(255); rest -= 255; }
                output.Add((byte)rest);
            }
            for (int i = 0; i < matchLen; i++)
                raw.Add(raw[raw.Count - offset]);
        }
        output.Add(5 << 4); // final literals-only sequence, as the format requires
        for (int i = 0; i < 5; i++)
        {
            output.Add(1);
            raw.Add(1);
        }
        return (output.ToArray(), raw.Count);
    }
}
