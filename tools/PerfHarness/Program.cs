using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;
using UnturnedGodot.Zombies;
using Transform3D = Godot.Transform3D;
using Vector2 = Godot.Vector2;
using Vector3 = Godot.Vector3;

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
            : MapCatalog.ResolvePath(unturned, Environment.GetEnvironmentVariable("MAP") ?? "PEI");
        Console.WriteLine($"Unturned: {unturned ?? "(not found — file-based suites will skip)"}");
        Console.WriteLine($"Map: {map ?? "(not found)"}");

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
        if (all || wanted.Contains("previews"))
            PreviewSuite(unturned);
        if (all || wanted.Contains("navcache"))
            NavigationCacheSuite(map);
        if (wanted.Contains("nav"))
            NavigationDiagnostic(map);
        return 0;
    }

    // ---------------- suites (baselines over the current Core implementations) ----------------

    private static void PreviewSuite(string? unturned)
    {
        if (unturned == null)
        {
            Console.WriteLine("== previews: SKIP (Unturned install not found) ==\n");
            return;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MapEntry entry in MapCatalog.Scan(unturned))
        {
            Add(entry.IconPath);
            Add(entry.PreviewPath);
            Add(entry.ChartPath);
        }

        long rgbaBytes = 0;
        int measured = 0;
        foreach (string path in paths)
            if (TryPngDimensions(path, out int width, out int height))
            {
                rgbaBytes += (long)width * height * 4;
                measured++;
            }

        Console.WriteLine("== previews: decoded menu artwork lifetime ==");
        Console.WriteLine($"  {measured:N0}/{paths.Count:N0} PNGs: {rgbaBytes / 1048576.0:0.00} MiB RGBA "
            + "released with MapPicker (legacy static cache retains it for process lifetime)");
        Console.WriteLine();

        void Add(string? path)
        {
            if (!string.IsNullOrEmpty(path))
                paths.Add(path);
        }
    }

    private static bool TryPngDimensions(string path, out int width, out int height)
    {
        width = height = 0;
        Span<byte> header = stackalloc byte[24];
        try
        {
            using FileStream input = File.OpenRead(path);
            if (input.Read(header) != header.Length
                || !header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                return false;
            width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
            height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
            return width > 0 && height > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void NavigationCacheSuite(string? map)
    {
        string? userDir = FindGodotUserDir();
        string? path = map == null || userDir == null ? null : Path.Combine(userDir, "nav_reconcile",
            NavReconcileCache.MapKey(map) + ".cache");
        if (Skip("navcache", path != null && File.Exists(path) ? path : null, out string cachePath))
            return;

        string fingerprint;
        int[] triangles;
        using (var input = File.OpenRead(cachePath))
            if (!NavReconcileCache.TryReadMetadata(input, out fingerprint, out triangles))
                throw new InvalidDataException("Navigation reconciliation cache header is malformed.");

        GC.Collect();
        long before = GC.GetTotalMemory(forceFullCollection: true);
        List<HashSet<int>?> sets;
        using (FileStream input = File.OpenRead(cachePath))
            if (!NavReconcileCache.TryReadPartial(input, fingerprint, triangles, out sets))
                throw new InvalidDataException("Real navigation reconciliation cache did not round-trip.");
        long retained = GC.GetTotalMemory(forceFullCollection: true) - before;
        int indices = 0, completed = 0;
        foreach (HashSet<int>? set in sets)
            if (set != null)
            {
                completed++;
                indices += set.Count;
            }
        GC.KeepAlive(sets);
        sets.Clear();
        long afterClear = GC.GetTotalMemory(forceFullCollection: true) - before;

        Console.WriteLine("== navcache: published reconciliation state ==");
        Console.WriteLine($"  {indices:N0} rejected indices / {completed:N0}/{triangles.Length:N0} completed flags: "
            + $"HashSets retain ~{retained / 1024.0:0.0} KiB; after release ~{Math.Max(0, afterClear) / 1024.0:0.0} KiB");
        Console.WriteLine();
    }

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
        long fileBytes = new FileInfo(path).Length;
        Console.WriteLine($"== foliage: real blob ({fileBytes / (1024 * 1024)}MB) ==");
        Bench("LevelFoliage.Load (bounded file batches)", () => LevelFoliage.Load(path));
        Bench("legacy File.ReadAllBytes + Parse", () => LevelFoliage.Parse(File.ReadAllBytes(path)));
        byte[] blob = File.ReadAllBytes(path);
        Bench("Parse CPU only (preloaded input)", () => LevelFoliage.Parse(blob));
        LevelFoliage foliage = LevelFoliage.Parse(blob);
        LevelFoliageChunks direct = LevelFoliageChunks.Load(path, 4)
            ?? throw new InvalidDataException("Direct foliage load unexpectedly returned null.");
        var indexWatch = Stopwatch.StartNew();
        FoliageResidencyIndex residency = FoliageResidencyIndex.Build(path, 4);
        indexWatch.Stop();
        var allChunkIndices = new int[residency.Chunks.Count];
        for (int i = 0; i < allChunkIndices.Length; i++) allChunkIndices[i] = i;
        IReadOnlyList<FoliageChunk> indexedChunks = residency.DecodeChunks(allChunkIndices);
        direct.RebaseAll(parallel: false);
        if (direct.Chunks.Count != indexedChunks.Count)
            throw new InvalidOperationException("Residency index changed the foliage chunk count.");
        for (int i = 0; i < indexedChunks.Count; i++)
            if (direct.Chunks[i].Key != indexedChunks[i].Key
                || direct.Chunks[i].Bounds != indexedChunks[i].Bounds
                || direct.Chunks[i].Origin != indexedChunks[i].Origin
                || !direct.Chunks[i].Packed.AsSpan().SequenceEqual(indexedChunks[i].Packed))
                throw new InvalidOperationException($"Residency chunk {i} differs from all-resident output.");
        string indexCache = Path.Combine(Path.GetTempPath(),
            $"unturned-foliage-{Guid.NewGuid():N}.fidx");
        residency.Write(indexCache);
        long indexBytes = new FileInfo(indexCache).Length;
        var cacheWatch = Stopwatch.StartNew();
        FoliageResidencyIndex? reused;
        try
        {
            if (!FoliageResidencyIndex.TryRead(indexCache, path, 4, out reused))
                throw new InvalidOperationException("Fresh foliage residency index did not reload.");
            cacheWatch.Stop();
        }
        finally
        {
            File.Delete(indexCache);
        }
        Console.WriteLine($"  residency index: {residency.Chunks.Count:N0} chunks / "
            + $"{residency.IndexedInstances:N0} instances, {indexBytes / 1048576.0:0.00} MiB sidecar, "
            + $"build {indexWatch.Elapsed.TotalMilliseconds:0.0} ms, validated reload "
            + $"{cacheWatch.Elapsed.TotalMilliseconds:0.0} ms; all chunks byte-identical");
        GC.KeepAlive(reused);
        long instances = 0;
        foreach (FoliageChunk chunk in direct.Chunks) instances += chunk.Count;
        long legacyInstances = 0;
        foreach (FoliageTile tile in foliage.Tiles)
            foreach (FoliageInstances run in tile.Instances) legacyInstances += run.Count;
        if (instances != legacyInstances)
            throw new InvalidOperationException("Direct foliage chunks changed the instance count.");
        Console.WriteLine($"  final transforms: {direct.StorageBytes / 1048576.0:0.00} MiB; "
            + $"old parsed+final peak~{direct.StorageBytes * 2 / 1048576.0:0.00} MiB; "
            + $"direct final+decode peak~{(direct.StorageBytes + direct.DecodeBatchPeakBytes) / 1048576.0:0.00} MiB "
            + $"(decode batch {direct.DecodeBatchPeakBytes / 1048576.0:0.00} MiB)");
        Bench("LevelFoliageChunks.Load (direct final chunks)", () =>
            GC.KeepAlive(LevelFoliageChunks.Load(path, 4)), warmup: 1, iters: 5);
        Bench("direct load + sequential rebase", () =>
        {
            LevelFoliageChunks loaded = LevelFoliageChunks.Load(path, 4)!;
            loaded.RebaseAll(parallel: false);
        }, warmup: 1, iters: 5);
        Bench("direct load + parallel rebase", () =>
        {
            LevelFoliageChunks loaded = LevelFoliageChunks.Load(path, 4)!;
            loaded.RebaseAll(parallel: true);
        }, warmup: 1, iters: 5);
        FoliageBounds rescanned = MeasureFoliageBounds(foliage, useStoredBounds: false);
        FoliageBounds aggregated = MeasureFoliageBounds(foliage, useStoredBounds: true);
        if (rescanned != aggregated)
            throw new InvalidOperationException("Stored foliage bounds do not match a packed-buffer rescan.");
        Bench("bounds: old packed-buffer rescan", () =>
            GC.KeepAlive(MeasureFoliageBounds(foliage, useStoredBounds: false)));
        Bench("bounds: aggregate during parsing", () =>
            GC.KeepAlive(MeasureFoliageBounds(foliage, useStoredBounds: true)));
        var available = new HashSet<Guid>(foliage.AssetGuids);
        FoliageGroups compact = FoliageGroups.Build(foliage.Tiles, 4, available);
        Dictionary<FoliageGroupKey, List<FoliageInstances>> legacy = LegacyFoliageGroups(foliage, 4, available);
        int legacyRuns = 0;
        foreach (List<FoliageInstances> runs in legacy.Values) legacyRuns += runs.Count;
        if (compact.Count != legacy.Count || compact.Runs.Length != legacyRuns)
            throw new InvalidOperationException("Compact foliage grouping differs from legacy grouping.");
        Console.WriteLine($"  grouping: {compact.Count:N0} chunks / {compact.Runs.Length:N0} runs, "
            + $"compact arrays={compact.StorageBytes / 1048576.0:0.00} MiB");
        Bench("grouping: legacy Dictionary<List<run>>", () =>
            GC.KeepAlive(LegacyFoliageGroups(foliage, 4, available)), warmup: 1, iters: 5);
        Bench("grouping: compact CSR arrays", () =>
            GC.KeepAlive(FoliageGroups.Build(foliage.Tiles, 4, available)), warmup: 1, iters: 5);
        if (Environment.GetEnvironmentVariable("FOLIAGE_STATS") == "1")
            PrintFoliageStats(foliage);
        Console.WriteLine();
    }

    private static FoliageBounds MeasureFoliageBounds(LevelFoliage foliage, bool useStoredBounds)
    {
        FoliageBounds bounds = FoliageBounds.Empty;
        foreach (FoliageTile tile in foliage.Tiles)
            foreach (FoliageInstances run in tile.Instances)
                bounds = bounds.Include(useStoredBounds ? run.Bounds : FoliageBounds.Measure(run.Packed));
        return bounds;
    }

    private static Dictionary<FoliageGroupKey, List<FoliageInstances>> LegacyFoliageGroups(
        LevelFoliage foliage, int chunkTiles, IReadOnlySet<Guid> available)
    {
        var groups = new Dictionary<FoliageGroupKey, List<FoliageInstances>>();
        foreach (FoliageTile tile in foliage.Tiles)
        {
            int cx = (int)Math.Floor(tile.X / (double)chunkTiles);
            int cy = (int)Math.Floor(tile.Y / (double)chunkTiles);
            foreach (FoliageInstances run in tile.Instances)
            {
                if (!available.Contains(run.Asset)) continue;
                var key = new FoliageGroupKey(cx, cy, run.Asset);
                if (!groups.TryGetValue(key, out List<FoliageInstances>? list))
                    groups[key] = list = new List<FoliageInstances>();
                list.Add(run);
            }
        }
        return groups;
    }

    private static void PrintFoliageStats(LevelFoliage foliage)
    {
        float px = -469.37f, pz = 608.35f, radius = 200f;
        if (Environment.GetEnvironmentVariable("FOLIAGE_POINT") is { Length: > 0 } spec)
        {
            string[] p = spec.Split(',');
            if (p.Length >= 2)
            {
                px = float.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture);
                pz = float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
            }
            if (p.Length >= 3)
                radius = float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture);
        }

        var stats = new Dictionary<Guid, (int Total, int Near, float MinScale, float MaxScale,
            float MinDistance)>();
        foreach (FoliageTile tile in foliage.Tiles)
            foreach (FoliageInstances run in tile.Instances)
                for (int i = 0; i < run.Count; i++)
                {
                    Transform3D t = run.InstanceTransform(i);
                    Vector3 scale = t.Basis.Scale;
                    float maxScale = MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z));
                    float distance = new Vector2(t.Origin.X - px, t.Origin.Z - pz).Length();
                    stats.TryGetValue(run.Asset, out var s);
                    stats[run.Asset] = (s.Total + 1, s.Near + (distance <= radius ? 1 : 0),
                        s.Total == 0 ? maxScale : MathF.Min(s.MinScale, maxScale),
                        MathF.Max(s.MaxScale, maxScale),
                        s.Total == 0 ? distance : MathF.Min(s.MinDistance, distance));
                }

        var ranked = new List<KeyValuePair<Guid, (int Total, int Near, float MinScale, float MaxScale,
            float MinDistance)>>(stats);
        ranked.Sort((a, b) => b.Value.Near.CompareTo(a.Value.Near));
        Console.WriteLine($"  assets near ({px:0.##},{pz:0.##}) within {radius:0} m:");
        foreach (var e in ranked)
            if (e.Value.Near > 0)
            {
                Console.WriteLine($"    {e.Key:N}: near={e.Value.Near:N0} total={e.Value.Total:N0} "
                    + $"scale={e.Value.MinScale:0.###}..{e.Value.MaxScale:0.###} "
                    + $"closest={e.Value.MinDistance:0.##}m");
                PrintCachedMesh(e.Key);
            }
    }

    // FOLIAGE_STATS is also a correctness diagnostic: instance scale alone cannot distinguish a bad
    // Foliage.blob matrix from a wrongly extracted card or an unresolved alpha texture. Show the cached
    // mesh bounds and each material key next to the spatial counts so visual foliage regressions can be
    // traced without attaching a debugger to a 4-million-instance map.
    private static void PrintCachedMesh(Guid guid)
    {
        string? user = FindGodotUserDir();
        if (user == null)
            return;
        string path = Path.Combine(user, "model_cache", guid.ToString("N") + ".mesh");
        if (!File.Exists(path) || !MeshCache.IsCurrent(path))
        {
            Console.WriteLine("      mesh cache: missing/stale");
            return;
        }

        (Vector3[] vertices, _, _, List<CachedSubmesh> submeshes) = MeshCache.Read(File.ReadAllBytes(path));
        if (vertices.Length == 0)
        {
            Console.WriteLine("      mesh cache: 0 vertices");
            return;
        }
        Vector3 min = vertices[0], max = vertices[0];
        foreach (Vector3 v in vertices)
        {
            min = new Vector3(MathF.Min(min.X, v.X), MathF.Min(min.Y, v.Y), MathF.Min(min.Z, v.Z));
            max = new Vector3(MathF.Max(max.X, v.X), MathF.Max(max.Y, v.Y), MathF.Max(max.Z, v.Z));
        }
        Console.WriteLine($"      mesh: vertices={vertices.Length} bounds={min}..{max} size={max - min}");
        foreach (CachedSubmesh sm in submeshes)
        {
            string texture = sm.TextureKey.Length == 0 ? "(none)" : sm.TextureKey;
            string texturePath = Path.Combine(user, "texture_cache", texture + ".tex");
            string cached = sm.TextureKey.Length > 0 && File.Exists(texturePath)
                ? TextureCache.IsCurrent(texturePath) ? "current" : "stale"
                : "missing";
            Console.WriteLine($"      material: texture={texture} [{cached}] color={sm.Color} "
                + $"blend={sm.Blend} cull={sm.Cull}");
        }
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
        long samples = (long)tiles.Count * Landscape.HEIGHTMAP_RESOLUTION * Landscape.HEIGHTMAP_RESOLUTION;
        Console.WriteLine($"  resident sampler: float={samples * 4 / 1048576.0:0.00} MiB, "
            + $"compact ushort={samples * 2 / 1048576.0:0.00} MiB");
        Console.WriteLine($"  read-time samples: legacy raw+float={samples * 6 / 1048576.0:0.00} MiB, "
            + $"raw-only={samples * 2 / 1048576.0:0.00} MiB");
        Console.WriteLine($"  collision-build peak: old retained+grid+MapData={samples * 12 / 1048576.0:0.00} MiB, "
            + $"ushort+one-tile MapData={(samples * 2 + Landscape.HEIGHTMAP_RESOLUTION * Landscape.HEIGHTMAP_RESOLUTION * 4L) / 1048576.0:0.00} MiB");
        Bench("HeightmapTile.Read (all tiles)", () =>
        {
            foreach ((string path, int x, int y) in tiles)
                HeightmapTile.Read(path, x, y);
        });
        var loaded = new List<HeightmapTile>(tiles.Count);
        foreach ((string path, int x, int y) in tiles) loaded.Add(HeightmapTile.Read(path, x, y));
        int materialized = 0;
        foreach (HeightmapTile tile in loaded) if (tile.HasMaterializedHeights) materialized++;
        Console.WriteLine($"  float grids materialized after read: {materialized}/{loaded.Count}");
        var sampler = new HeightmapSampler(loaded);
        Console.WriteLine($"  actual sampler storage: {sampler.StorageBytes / 1048576.0:0.00} MiB");
        Bench("HeightmapSampler.TrySampleHeight x52k", () =>
        {
            float sink = 0f;
            for (int repeat = 0; repeat < 1000; repeat++)
                foreach (HeightmapTile tile in loaded)
                    sampler.TrySampleHeight((tile.CoordX + 0.37f) * Landscape.TILE_SIZE,
                        (tile.CoordY + 0.61f) * Landscape.TILE_SIZE, out sink);
            GC.KeepAlive(sink);
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
        long weights = (long)files.Count * Landscape.SPLATMAP_RESOLUTION * Landscape.SPLATMAP_RESOLUTION
            * SplatmapTile.LAYERS;
        Console.WriteLine($"  retained weights: float={weights * 4 / 1048576.0:0.00} MiB, "
            + $"source bytes={weights / 1048576.0:0.00} MiB");
        Bench("SplatmapTile.Parse (all tiles)", () =>
        {
            foreach (byte[] d in files)
                SplatmapTile.Parse(d, 0, 0);
        });
        Bench("legacy expand all weights to float", () =>
        {
            foreach (byte[] d in files)
            {
                var expanded = new float[d.Length];
                for (int i = 0; i < d.Length; i++) expanded[i] = d[i] / 255f;
                GC.KeepAlive(expanded);
            }
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
        var paths = new List<(string Item, string Path)>();
        foreach (string path in Directory.GetFiles(found, "*.mesh"))
            paths.Add((path, path));
        IReadOnlyList<ExactFileGroups.Group<string>> groups = ExactFileGroups.Build(paths);
        Console.WriteLine($"  exact pre-groups: {paths.Count:N0} files -> {groups.Count:N0} preparations "
            + $"({paths.Count - groups.Count:N0} ImporterMesh builds avoided)");
        Bench("ExactFileGroups.Build (size filter + exact hash)", () =>
            GC.KeepAlive(ExactFileGroups.Build(paths)), warmup: 1, iters: 7);
        var colliderPaths = new List<(string Item, string Path)>();
        foreach (string path in Directory.GetFiles(found, "*.collider"))
            colliderPaths.Add((path, path));
        IReadOnlyList<ExactFileGroups.Group<string>> colliderGroups = ExactFileGroups.Build(colliderPaths);
        Console.WriteLine($"  exact colliders: {colliderPaths.Count:N0} files -> {colliderGroups.Count:N0} contents "
            + $"({colliderPaths.Count - colliderGroups.Count:N0} parsed data/Shape3D aliases)");
        Bench("collider exact-content grouping", () =>
            GC.KeepAlive(ExactFileGroups.Build(colliderPaths)), warmup: 1, iters: 7);
        Console.WriteLine();
    }

    // Correctness diagnostic rather than a timed suite. It makes a reported in-game position
    // reproducible by showing every nearby authored zombie spawn, endpoint snapping, and the first
    // direction selected by the baked graph. NAV_POINT=x,y,z overrides the default PEI report.
    private static void NavigationDiagnostic(string? map)
    {
        if (map == null)
            return;
        Vector3 target = ParsePoint(Environment.GetEnvironmentVariable("NAV_POINT")
            ?? "-604.64,35.23,-91.11");
        string environment = Path.Combine(map, "Environment");
        List<NavFlag> flags = LevelNavmesh.Load(environment);
        List<NavBound> bounds = LevelNavigationData.Load(environment);
        List<ZombieSpawnpointData> spawns = LevelZombiesData.LoadSpawnpoints(
            Path.Combine(map, "Spawns", "Animals.dat"));
        BakedNavGraph? measuredGraph = null;
        Bench("BakedNavGraph.Build (temporary arrays)", () =>
            measuredGraph = BakedNavGraph.Build(flags), warmup: 1, iters: 3);
        var graph = measuredGraph!;
        (int connections, long adjacencyBytes) = graph.AdjacencyStorage;
        int triangles = 0;
        foreach (NavFlag flag in flags) triangles += flag.Triangles.Length / 3;
        long legacyEstimate = ((long)triangles * (32 + 24 + 36)) + ((long)triangles * 8);
        Console.WriteLine($"  adjacency: {connections:N0} connections, CSR={adjacencyBytes / 1048576.0:0.00} MiB, "
            + $"legacy List estimate={legacyEstimate / 1048576.0:0.00} MiB");
        Console.WriteLine($"  builder scratch arrays: {graph.BuildScratchBytes / 1048576.0:0.00} MiB");
        byte[] csr;
        using (var output = new MemoryStream())
        {
            graph.Write(output, "perf-harness-v1");
            csr = output.ToArray();
        }
        Console.WriteLine($"  persisted CSR cache: {csr.Length / 1048576.0:0.00} MiB");
        Bench("BakedNavGraph.Write CSR cache", () =>
        {
            using var output = new MemoryStream(csr.Length);
            graph.Write(output, "perf-harness-v1");
        }, warmup: 1, iters: 5);
        Bench("BakedNavGraph.TryRead CSR cache", () =>
        {
            using var input = new MemoryStream(csr, writable: false);
            if (!BakedNavGraph.TryRead(input, "perf-harness-v1", flags, out BakedNavGraph? loaded))
                throw new InvalidDataException("Fresh CSR benchmark cache did not load.");
            GC.KeepAlive(loaded);
        }, warmup: 1, iters: 5);

        LevelNavmesh.SnapXZ(flags, target, out Vector3 snappedTarget);
        Console.WriteLine($"== nav: target={target} snapped={snappedTarget} "
            + $"bound={LevelNavigationData.TryGetBound(bounds, target)} flags={flags.Count} ==");
        for (int i = 0; i < flags.Count; i++)
            if (flags[i].ContainsXZ(target))
                PrintConnectivity(i, flags[i]);

        var nearby = new List<(float Distance, Vector3 Position)>();
        foreach (ZombieSpawnpointData spawn in spawns)
        {
            Vector3 position = new(spawn.Point.X, spawn.Point.Y, -spawn.Point.Z);
            float distance = new Vector2(position.X - target.X, position.Z - target.Z).Length();
            if (distance <= 64f)
                nearby.Add((distance, position));
        }
        nearby.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        foreach ((float distance, Vector3 position) in nearby)
        {
            LevelNavmesh.SnapXZ(flags, position, out Vector3 snappedFrom);
            var path = new List<Vector3>();
            bool found = graph.TryPath(snappedFrom, snappedTarget, path);
            Vector3 first = path.Count > 1 ? path[1] - snappedFrom : Vector3.Zero;
            Vector3 direct = snappedTarget - snappedFrom;
            first.Y = 0f;
            direct.Y = 0f;
            float alignment = first.LengthSquared() > 0f && direct.LengthSquared() > 0f
                ? first.Normalized().Dot(direct.Normalized())
                : 0f;
            float walked = 0f;
            for (int i = 1; i < path.Count; i++)
                walked += path[i - 1].DistanceTo(path[i]);
            Console.WriteLine($"  spawn={position} d={distance:0.##} snap={snappedFrom} "
                + $"route={found}/{path.Count} first={first} toward={alignment:0.###} walked={walked:0.##}");
        }
        var queries = new List<(Vector3 From, Vector3 To)>();
        foreach (NavFlag flag in flags)
        {
            int triangleCount = flag.Triangles.Length / 3;
            if (triangleCount < 2) continue;
            for (int i = 0; i < 32; i++)
            {
                int a = (i * 7919) % triangleCount;
                int b = ((i + 17) * 3571) % triangleCount;
                Vector3 Centre(int triangle) => (flag.Vertices[flag.Triangles[triangle * 3]] +
                    flag.Vertices[flag.Triangles[(triangle * 3) + 1]] +
                    flag.Vertices[flag.Triangles[(triangle * 3) + 2]]) / 3f;
                queries.Add((Centre(a), Centre(b)));
            }
            break;
        }
        var route = new List<Vector3>();
        Bench($"A* reusable workspace ({queries.Count} routes)", () =>
        {
            foreach ((Vector3 from, Vector3 to) in queries)
            {
                route.Clear();
                graph.TryPath(from, to, route);
            }
        }, warmup: 1, iters: 7);
        Console.WriteLine($"  reusable A* workspaces retained: {graph.SearchWorkspaceCount}");
        Console.WriteLine();
    }

    private static void PrintConnectivity(int index, NavFlag flag)
    {
        int count = flag.Triangles.Length / 3;
        var parent = new int[count];
        for (int i = 0; i < count; i++)
            parent[i] = i;
        int Find(int item)
        {
            while (parent[item] != item)
            {
                parent[item] = parent[parent[item]];
                item = parent[item];
            }
            return item;
        }
        void Join(int a, int b)
        {
            a = Find(a);
            b = Find(b);
            if (a != b)
                parent[b] = a;
        }

        var byEdge = new Dictionary<(int, int), int>();
        for (int triangle = 0; triangle < count; triangle++)
            for (int edge = 0; edge < 3; edge++)
            {
                int a = flag.Triangles[(triangle * 3) + edge];
                int b = flag.Triangles[(triangle * 3) + ((edge + 1) % 3)];
                (int, int) key = a < b ? (a, b) : (b, a);
                if (byEdge.TryGetValue(key, out int other))
                    Join(triangle, other);
                else
                    byEdge[key] = triangle;
            }
        var sizes = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            int root = Find(i);
            sizes.TryGetValue(root, out int size);
            sizes[root] = size + 1;
        }
        int largest = 0;
        foreach (int size in sizes.Values)
            largest = Math.Max(largest, size);
        Console.WriteLine($"  flag {index}: triangles={count} exact-edge components={sizes.Count} "
            + $"largest={largest}");
    }

    private static Vector3 ParsePoint(string spec)
    {
        string[] values = spec.Split(',');
        return new Vector3(
            float.Parse(values[0], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(values[1], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(values[2], System.Globalization.CultureInfo.InvariantCulture));
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
