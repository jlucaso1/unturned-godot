using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Repro;
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
        if (all || wanted.Contains("navprobe"))
            NavigationProbeSuite(map);
        if (all || wanted.Contains("repro"))
            ReproSuite();
        if (all || wanted.Contains("bundle"))
            BundleSuite(unturned);
        if (wanted.Contains("nav"))
            NavigationDiagnostic(map);
        if (wanted.Contains("ress"))
            ResSDiagnostic(unturned, map);
        if (wanted.Contains("lzma"))
            LzmaDiagnostic(unturned);
        return 0;
    }

    // ---------------- suites (baselines over the current Core implementations) ----------------

    // What leaving the bug-repro recorder armed costs the tick it is recording. Both sides run the
    // SAME simulation over the same synthetic world (a flat field, one hunter, collision answered by
    // core's own triangle sweep), so the difference is the recorder and nothing else.
    private static void ReproSuite()
    {
        Console.WriteLine("\n== repro recorder ==");
        const int Ticks = 2000;

        static (ZombieSystem System, ZombiePlayerView[] Players) Session()
        {
            var flag = new NavFlag
            {
                Center = new Vector3(20f, 0f, 20f),
                Size = new Vector3(44f, 100f, 44f),
                Vertices = new Vector3[41 * 41],
                Triangles = new int[40 * 40 * 6],
            };
            for (int x = 0; x < 41; x++)
                for (int z = 0; z < 41; z++)
                    flag.Vertices[(x * 41) + z] = new Vector3(x, 0f, z);
            int at = 0;
            for (int x = 0; x < 40; x++)
                for (int z = 0; z < 40; z++)
                {
                    int a = (x * 41) + z, b = a + 1, c = a + 41, d = c + 1;
                    flag.Triangles[at++] = a;
                    flag.Triangles[at++] = b;
                    flag.Triangles[at++] = c;
                    flag.Triangles[at++] = b;
                    flag.Triangles[at++] = d;
                    flag.Triangles[at++] = c;
                }

            var bound = new NavBound
            {
                Center = new Vector3(20f, 0f, 20f),
                Size = new Vector3(160f, 100f, 160f),
                MaxZombies = 64,
                SpawnZombies = true,
            };
            var graph = BakedNavGraph.Build(new[] { flag });
            var system = new ZombieSystem(new[] { new ZombieTable { Name = "Bench", Damage = 5 } },
                new[] { bound }, Flat, new[] { flag })
            {
                GroundSnap = Snap,
                VisionBlocked = (from, to) => false,
                PathQuery = graph.TryPath,
                PathReady = () => true,
                MoveResolver = (from, to, radius) => to,
            };
            var spawns = new List<ZombieSpawnpointData>();
            for (int i = 0; i < 64; i++)
                spawns.Add(new ZombieSpawnpointData(0, new Vector3(2f + (i % 8 * 4f), 0f,
                    -(2f + (i / 8 * 4f)))));
            system.Spawn(spawns, new ReproRandom(1));
            return (system, new[]
            {
                new ZombiePlayerView(1, new Vector3(30f, 0f, 30f),
                    UnturnedGodot.Player.EPlayerStance.Sprint, true),
            });
        }

        static bool Flat(float x, float z, out float y)
        {
            y = 0f;
            return true;
        }

        static bool Snap(Vector3 position, out float y)
        {
            y = 0f;
            return true;
        }

        (ZombieSystem plain, ZombiePlayerView[] players) = Session();
        double bare = Bench($"{Ticks} ticks, no recorder", () =>
        {
            for (int i = 0; i < Ticks; i++)
                plain.Tick(players, ServerSimulation.TickRate);
        }, warmup: 1, iters: 5);

        (ZombieSystem recorded, ZombiePlayerView[] samePlayers) = Session();
        var recorder = new ReproRecorder(recorded);
        recorder.Attach();
        double armed = Bench($"{Ticks} ticks, recorder armed", () =>
        {
            for (int i = 0; i < Ticks; i++)
                recorded.Tick(samePlayers, ServerSimulation.TickRate);
        }, warmup: 1, iters: 5);

        Console.WriteLine($"  overhead {(armed - bare) / bare * 100:0.0}% "
            + $"({(armed - bare) / Ticks * 1000:0.0} us per tick over {recorded.Zombies.Count} zombies)");

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Ticks; i++)
            recorded.Tick(samePlayers, ServerSimulation.TickRate);
        long perTick = (GC.GetAllocatedBytesForCurrentThread() - before) / Ticks;
        Console.WriteLine($"  steady allocation {perTick} bytes per tick (the ring is reused)");

        var stopwatch = Stopwatch.StartNew();
        ReproZombieSection section = recorder.Build(out _, out int ticks);
        Console.WriteLine($"  capture of a {ticks}-tick window {stopwatch.Elapsed.TotalMilliseconds:0.0} ms, "
            + $"{section.Oracle.MoveCount} recorded moves");
    }

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

    // The probe pass navmesh reconciliation now runs on workers instead of on the physics thread.
    //
    // The engine version of this loop could only run inside a physics notification, which capped it at a
    // fraction of a millisecond per tick however much CPU was idle — a map's first session spent minutes
    // there. This measures the replacement against the map's real terrain and its real navmesh, and says
    // how many faces the CPU pass still hands to the physics server. Object colliders are not here (they
    // are built by the game, not by Core), so the probe count is real while the field is terrain-only;
    // treat the throughput as the number and the confirmation rate as a floor.
    private static void NavigationProbeSuite(string? map)
    {
        string? heightmaps = map == null ? null : Path.Combine(map, "Landscape", "Heightmaps");
        string? environment = map == null ? null : Path.Combine(map, "Environment");
        bool present = heightmaps != null && Directory.Exists(heightmaps)
            && environment != null && Directory.Exists(environment);
        if (Skip("navprobe", present ? map : null, out string _))
            return;

        var field = new CollisionFieldBuilder();
        int tiles = 0;
        foreach (string file in Directory.GetFiles(heightmaps!, "Tile_*.heightmap"))
        {
            string[] parts = Path.GetFileNameWithoutExtension(file).Split('_'); // Tile_X_Y_Source
            HeightmapTile tile = HeightmapTile.Read(file, int.Parse(parts[1]), int.Parse(parts[2]));
            field.AddHeightfield(
                TerrainHeightfield.CollisionTransform(tile.CoordX, tile.CoordY),
                Landscape.HEIGHTMAP_RESOLUTION, Landscape.HEIGHTMAP_RESOLUTION,
                tile.RawSamples != null
                    ? TerrainHeightfield.MapData(tile.RawSamples)
                    : TerrainHeightfield.MapData(tile.Heights));
            tiles++;
        }

        List<NavFlag> flags = LevelNavmesh.Load(environment!);
        if (tiles == 0 || flags.Count == 0)
        {
            Console.WriteLine("== navprobe: SKIP (no terrain tiles or no navmesh flags) ==\n");
            return;
        }

        CollisionField world = field.Build();
        int faces = 0;
        foreach (NavFlag flag in flags)
            faces += flag.Triangles.Length / 3;
        long probes = (long)faces * 7; // NavmeshReachability samples every face seven times

        Console.WriteLine($"== navprobe: {faces:N0} navmesh faces over {tiles} terrain tiles ==");
        const float StepOffset = 0.5f;
        double median = Bench("CollisionField probe pass (all flags)", () =>
        {
            foreach (NavFlag flag in flags)
                GC.KeepAlive(NavmeshSurfaceSampling.Sample(flag, world, StepOffset));
        }, warmup: 1, iters: 5);
        Console.WriteLine($"  {probes:N0} probes -> {probes / Math.Max(0.001, median) / 1000.0:N2} M probes/s");

        // The reachability arithmetic around the probes. It runs per flag on a worker, twice — once to
        // choose the confirmation set and once to reach the verdict — so it is on the same critical path
        // the probes are, and worth seeing next to them.
        var sampledFlags = new List<NavmeshSurfaceSampling.FlagSurfaces>(flags.Count);
        foreach (NavFlag flag in flags)
            sampledFlags.Add(NavmeshSurfaceSampling.Sample(flag, world, StepOffset));
        Bench("NeedsConfirmation (all flags)", () =>
        {
            for (int i = 0; i < flags.Count; i++)
                GC.KeepAlive(NavmeshReachability.NeedsConfirmation(flags[i], StepOffset,
                    sampledFlags[i].Clearance, sampledFlags[i].Known, sampledFlags[i].Slack,
                    sampledFlags[i].Uncertain, margin: 0.05f));
        }, warmup: 1, iters: 5);
        Bench("Unreachable (all flags)", () =>
        {
            for (int i = 0; i < flags.Count; i++)
                GC.KeepAlive(NavmeshReachability.Unreachable(flags[i], StepOffset,
                    sampledFlags[i].Clearance, sampledFlags[i].Known));
        }, warmup: 1, iters: 5);

        int confirm = 0, uncertain = 0, unknown = 0;
        for (int i = 0; i < flags.Count; i++)
        {
            NavmeshSurfaceSampling.FlagSurfaces sampled = sampledFlags[i];
            confirm += NavmeshReachability.NeedsConfirmation(flags[i], StepOffset, sampled.Clearance,
                sampled.Known, sampled.Slack, sampled.Uncertain, margin: 0.05f).Count;
            uncertain += sampled.UncertainSamples;
            for (int t = 0; t < sampled.Known.Length; t++)
                if (!sampled.Known[t])
                    unknown++;
        }
        Console.WriteLine($"  hands {confirm:N0}/{faces:N0} faces ({100.0 * confirm / faces:0.0}%) to the "
            + $"physics server; {uncertain:N0} uncertain probes, {unknown:N0} faces with no surface found");
        Console.WriteLine();
    }

    // Classes a bundle-wide loop decodes EVERY object of, on any load of this bundle, whatever the map:
    // `PrefabGraph.ReadContainer` (AssetBundle), `BuildTransformMaps` (Transform), the MeshFilter /
    // SkinnedMeshRenderer sweep, and the collider sweep. Each is `foreach (o in file.Objects)` filtered on
    // class id alone, so the suite's figure for these is load work measured, not bounded.
    private static readonly int[] ScannedClasses =
    {
        4,   // Transform          — BuildTransformMaps
        33,  // MeshFilter         — the mesh-part sweep
        64,  // MeshCollider       — the collider sweep
        65,  // BoxCollider        — the collider sweep
        135, // SphereCollider     — the collider sweep
        136, // CapsuleCollider    — the collider sweep
        137, // SkinnedMeshRenderer— the mesh-part sweep
        142, // AssetBundle        — ReadContainer
    };

    // Classes the port reads only through a targeted path-id or GUID lookup from an asset that names them:
    // `ModelExtractor` skips GUIDs the map does not need before it touches a Mesh/Material/Texture2D,
    // `MaterialsOf` reaches a MeshRenderer through its GameObject's component list, and `GameObjectOf`
    // caches GameObjects by id. A map placing a small subset of the bundle decodes a small subset of these,
    // so the whole-file figure is an UPPER BOUND on load work and must not be read as a measurement of it.
    private static readonly int[] TargetedClasses =
    {
        1,   // GameObject     — GameObjectOf(goId), by id
        21,  // Material       — from a renderer's material list
        23,  // MeshRenderer   — from its GameObject's component list
        28,  // Texture2D      — from a material's texture properties
        43,  // Mesh           — from a MeshFilter/collider part, GUID-filtered
        48,  // Shader         — from a material
        83,  // AudioClip      — from an audio definition
        114, // MonoBehaviour  — the audio definitions themselves
    };

    // A class in neither list is not a class the port never reads: CharacterModel walks the player rig's
    // own AnimationClips by id, for instance. It means no pass decodes every object of that class here.

    // Unity class ids worth naming in the per-class table; anything else prints as its bare id.
    private static readonly Dictionary<int, string> ClassNames = new()
    {
        [1] = "GameObject",
        [4] = "Transform",
        [21] = "Material",
        [23] = "MeshRenderer",
        [25] = "Renderer",
        [28] = "Texture2D",
        [33] = "MeshFilter",
        [43] = "Mesh",
        [48] = "Shader",
        [64] = "MeshCollider",
        [65] = "BoxCollider",
        [74] = "AnimationClip",
        [82] = "AudioSource",
        [83] = "AudioClip",
        [95] = "Animator",
        [108] = "Light",
        [111] = "Animation",
        [114] = "MonoBehaviour",
        [115] = "MonoScript",
        [135] = "SphereCollider",
        [136] = "CapsuleCollider",
        [137] = "SkinnedMeshRenderer",
        [142] = "AssetBundle",
        [198] = "ParticleSystem",
        [199] = "ParticleSystemRenderer",
        [205] = "LODGroup",
        [212] = "SpriteRenderer",
        [213] = "Sprite",
    };

    // What the masterbundle's object metadata costs to turn into values: the SerializedFile table, then
    // the TypeTree-driven object reader over it. The cold load's single biggest Core cost after LZMA, and
    // until now the only major decode path with no suite at all. Splits what every load decodes from what
    // only a map that places it decodes, because conflating the two prices the wrong thing.
    private static void BundleSuite(string? unturned)
    {
        string? bundlePath = Environment.GetEnvironmentVariable("BUNDLE_PATH")
            ?? (unturned == null ? null : UnturnedInstall.FindMasterBundle(unturned));
        if (Skip("bundle", bundlePath != null && File.Exists(bundlePath) ? bundlePath : null, out string path))
            return;

        using MasterBundleStream? stream = MasterBundleStream.OpenFile(path);
        if (stream == null)
        {
            Console.WriteLine($"== bundle: SKIP ({Path.GetFileName(path)} is not the single-LZMA-block "
                + "shape the streaming reader handles) ==\n");
            return;
        }

        // EVERY serialized node, not just the first: the streaming load processes each in physical order,
        // so measuring one of several would report too few objects and hide the later files' cost. Nodes
        // sitting behind a stream node are counted and skipped rather than scanned — reaching them means
        // decoding the ~1.18 GB of pixels in between, which is the cost this suite exists to stay off.
        var ordered = new List<MasterBundleStream.Node>(stream.Nodes);
        ordered.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        Console.WriteLine($"== bundle: object decode over {Path.GetFileName(path)} ==");
        Console.WriteLine($"  {Mb(new FileInfo(path).Length)} on disk, {Mb(stream.TotalSize)} decompressed");

        var files = new List<(string Name, SerializedFile File, byte[] Bytes)>();
        int skippedBehindStream = 0;
        bool pastFirstStream = false;
        foreach (MasterBundleStream.Node node in ordered)
        {
            if (IsResSNode(node.Path))
            {
                pastFirstStream = true;
                continue;
            }

            if (pastFirstStream)
            {
                skippedBehindStream++;
                continue;
            }

            // The stream is forward-only, so anything between the cursor and this node has to be consumed
            // before it can be read — otherwise SerializedFile is handed a slice of somebody else's bytes.
            if (!WindTo(stream, node.Offset))
            {
                Console.WriteLine($"  note: the stream ended before {LastSegment(node.Path)} — not measured");
                break;
            }

            var timer = Stopwatch.StartNew();
            byte[] bytes = stream.Read((int)node.Size);
            timer.Stop();
            Console.WriteLine($"  serialized: {LastSegment(node.Path)} {Mb(bytes.Length)} LZMA-decoded in "
                + $"{timer.Elapsed.TotalSeconds:0.00} s "
                + $"({bytes.Length / 1048576.0 / timer.Elapsed.TotalSeconds:0.0} MiB/s) — setup, not a measurement");
            files.Add((LastSegment(node.Path), SerializedFile.Read(bytes), bytes));
        }

        if (files.Count == 0)
        {
            Console.WriteLine($"== bundle: SKIP ({Path.GetFileName(path)} declares no readable serialized "
                + "file ahead of its stream nodes) ==\n");
            return;
        }

        if (skippedBehindStream > 0)
        {
            Console.WriteLine($"  note: {skippedBehindStream} serialized file(s) sit behind a stream node and "
                + "were not scanned — their objects are missing from the numbers below");
        }

        byte[] tableBytes = files[0].Bytes;
        Bench("SerializedFile.Read (first node)", () => SerializedFile.Read(tableBytes));
        long tableAlloc = AllocatedBy(() => SerializedFile.Read(tableBytes));
        int totalObjects = 0, totalTrees = 0;
        foreach ((string _, SerializedFile f, byte[] _) in files)
        {
            totalObjects += f.Objects.Count;
            totalTrees += f.TypeTreesByClassId.Count;
        }
        Console.WriteLine($"    {totalObjects:N0} objects across {files.Count} serialized file(s), "
            + $"{totalTrees} class type trees, {Mb(tableAlloc)} allocated for the first");

        // Objects whose type tree was stripped cannot be decoded without a donor file; they are not part
        // of what this measures, so they are excluded from every set rather than silently throwing.
        var readable = new List<(SerializedObject Obj, SerializedFile File)>(totalObjects);
        foreach ((string _, SerializedFile f, byte[] _) in files)
            foreach (SerializedObject o in f.Objects)
                if (o.TypeTree.Count > 0)
                    readable.Add((o, f));

        var scanned = new HashSet<int>(ScannedClasses);
        var targeted = new HashSet<int>(TargetedClasses);
        var scannedSet = new List<(SerializedObject Obj, SerializedFile File)>();
        var targetedSet = new List<(SerializedObject Obj, SerializedFile File)>();
        var containerSet = new List<(SerializedObject Obj, SerializedFile File)>();
        foreach ((SerializedObject o, SerializedFile f) in readable)
        {
            if (o.ClassId == AssetBundleClassId)
                containerSet.Add((o, f));
            if (scanned.Contains(o.ClassId))
                scannedSet.Add((o, f));
            else if (targeted.Contains(o.ClassId))
                targetedSet.Add((o, f));
        }

        if (readable.Count < totalObjects)
        {
            Console.WriteLine($"    note: {totalObjects - readable.Count:N0} object(s) carry no type tree "
                + "and are excluded from the decode below");
        }

        // Every row is the cost of decoding each object ONCE. That is not the same as what a load pays:
        // the AssetBundle container is re-decoded by every pass that walks it, which is why it is also
        // timed on its own below.
        //
        // Far fewer iterations than the default 15: one pass over every object is seconds, not
        // milliseconds, and fifteen of them would cost more than the rest of the harness put together.
        // The coarser the row, the smaller its budget.
        ReadPass("TypeTreeReader.Read (scanned classes, once each)", scannedSet, iters: 5);
        ReadPass("TypeTreeReader.Read (targeted classes — a bound)", targetedSet, iters: 5);
        ReadPass("TypeTreeReader.Read (every object — a bound)", readable, iters: 3);

        ContainerRescanCost(containerSet);
        PerClassTable(readable);
        Console.WriteLine();
    }

    private const int AssetBundleClassId = 142;

    // Median time and allocation for decoding one set of objects. Allocation is reported alongside because
    // this reader's cost is dominated by it — boxed leaves, a Dictionary per struct and a List per array.
    private static void ReadPass(string label, List<(SerializedObject Obj, SerializedFile File)> objects,
        int iters)
    {
        if (objects.Count == 0)
        {
            Console.WriteLine($"  {label,-44} (no objects)");
            return;
        }

        void Pass()
        {
            foreach ((SerializedObject o, SerializedFile f) in objects)
                TypeTreeReader.Read(o.TypeTree, f.ReaderFor(o));
        }

        Bench($"{label} x{objects.Count:N0}", Pass, warmup: 1, iters: iters);
        long alloc = AllocatedBy(Pass);
        long input = 0;
        foreach ((SerializedObject o, SerializedFile _) in objects)
            input += o.ByteSize;
        Console.WriteLine($"    {Mb(alloc)} allocated over {Mb(input)} of object bytes "
            + $"({(input > 0 ? alloc / (double)input : 0):0.0}x), {alloc / (double)objects.Count / 1024:0.0} KiB per object");
    }

    // The AssetBundle object holds `m_Container`, the path -> asset table, and every pass that needs to
    // resolve a name walks it by decoding the whole object again from scratch. Five independent scans of
    // it exist in the port — PrefabGraph.ReadContainer, BundleTextures.Locate, AudioExtractor.Plan,
    // RoadsBuilder and CharacterModel — and at least the first three run against the core masterbundle on
    // a cold load. The rows above decode each object once, so they price ONE of those; this prices the
    // repeat, because on this bundle a single container decode is a large share of the scanned row.
    private static void ContainerRescanCost(List<(SerializedObject Obj, SerializedFile File)> containers)
    {
        if (containers.Count == 0)
            return;

        void Pass()
        {
            foreach ((SerializedObject o, SerializedFile f) in containers)
                TypeTreeReader.Read(o.TypeTree, f.ReaderFor(o));
        }

        double median = Bench($"one AssetBundle container scan x{containers.Count:N0}", Pass,
            warmup: 1, iters: 5);
        long alloc = AllocatedBy(Pass);
        Console.WriteLine($"    {Mb(alloc)} allocated per scan; the port scans this table from {ContainerScanSites} "
            + "independent places, so a cold load pays a multiple of this line, not one of it");
        Console.WriteLine($"    at {ContainerScanSites} scans that is {median * ContainerScanSites:N0} ms and "
            + $"{Mb(alloc * ContainerScanSites)} — re-decoding one object the load already had");
    }

    // PrefabGraph.ReadContainer, BundleTextures.Locate and AudioExtractor.Plan all scan the core bundle's
    // container on a cold load. RoadsBuilder and CharacterModel scan their own bundles, so they are not
    // counted here: this is deliberately the low end.
    private const int ContainerScanSites = 3;

    // Where the decode time and allocation actually sit, per Unity class, from a single pass. Every object
    // is individually timed here, so the column sums above the uninstrumented pass — read it for the shape,
    // and take the totals from the passes above. A class nothing scans can still dominate this table, which
    // is exactly why the scanned subset is timed on its own.
    private static void PerClassTable(List<(SerializedObject Obj, SerializedFile File)> objects)
    {
        var scannedClasses = new HashSet<int>(ScannedClasses);
        var targetedClasses = new HashSet<int>(TargetedClasses);
        var perClass = new Dictionary<int, (int Count, double Ms, long Input, long Alloc)>();
        foreach ((SerializedObject o, SerializedFile f) in objects)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            TypeTreeReader.Read(o.TypeTree, f.ReaderFor(o));
            sw.Stop();
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            perClass.TryGetValue(o.ClassId, out (int Count, double Ms, long Input, long Alloc) v);
            perClass[o.ClassId] = (v.Count + 1, v.Ms + sw.Elapsed.TotalMilliseconds, v.Input + o.ByteSize,
                v.Alloc + alloc);
        }

        var ordered = new List<KeyValuePair<int, (int Count, double Ms, long Input, long Alloc)>>(perClass);
        ordered.Sort((a, b) => b.Value.Ms.CompareTo(a.Value.Ms));

        Console.WriteLine("    by class (individually timed; 'how' is scan = every object decoded, "
            + "targeted = by id)");
        Console.WriteLine("      class                      count      input        ms      alloc  how");
        int shown = 0;
        foreach (KeyValuePair<int, (int Count, double Ms, long Input, long Alloc)> entry in ordered)
        {
            if (shown++ == 8)
                break;
            string name = ClassNames.TryGetValue(entry.Key, out string? n) ? $"{n} ({entry.Key})" : entry.Key.ToString();
            Console.WriteLine($"      {name,-24} {entry.Value.Count,7:N0} {Mb(entry.Value.Input),10} "
                + $"{entry.Value.Ms,9:0} {Mb(entry.Value.Alloc),10}  {(scannedClasses.Contains(entry.Key) ? "scan" : targetedClasses.Contains(entry.Key) ? "targeted" : "-"),-8}");
        }
    }

    // Consumes the forward-only stream up to `offset`, returning false if it ended first.
    private static bool WindTo(MasterBundleStream stream, long offset)
    {
        if (stream.Cursor >= offset)
            return stream.Cursor == offset;

        var skip = new byte[1 << 20];
        while (stream.Cursor < offset)
        {
            int n = stream.Read(skip, 0, (int)Math.Min(skip.Length, offset - stream.Cursor));
            if (n <= 0)
                return false;
        }
        return true;
    }

    // Bytes allocated on this thread by one call, with the GC levelled first so a collection triggered by
    // the previous measurement is not billed to this one.
    private static long AllocatedBy(Action act)
    {
        GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        act();
        return GC.GetAllocatedBytesForCurrentThread() - before;
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
        IReadOnlyList<NavFlagSurvey> surveys = graph.Survey();
        for (int i = 0; i < flags.Count; i++)
            if (flags[i].ContainsXZ(target))
            {
                PrintConnectivity(i, flags[i]);
                PrintSurvey(i, surveys[i]);
            }

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

    private static void PrintSurvey(int index, NavFlagSurvey survey)
    {
        Console.WriteLine($"  flag {index}: stitched components={survey.Islands.Count} "
            + $"largest={survey.Islands[0].TriangleCount} ({survey.LargestIslandShare:P1}), "
            + $"rim={survey.Rim.Count}");
        for (int i = 0; i < Math.Min(10, survey.Islands.Count); i++)
        {
            NavIsland island = survey.Islands[i];
            Console.WriteLine($"    island {i}: faces={island.TriangleCount}, area={island.Area:0.##}, "
                + $"anchor={island.Anchor}, min={island.Min}, max={island.Max}");
        }
    }

    // Correctness/shape diagnostic rather than a timed suite. The cold load's texture pass is one
    // forward LZMA pass over a ~1.18 GB .resS, and it stops at the END of the last range anyone asked
    // for — so a handful of wanted textures sitting near the tail cost the whole file. This prints where
    // the wanted ranges actually sit, and what the pass would read if the last k of them were deferred.
    // If the ranges are spread evenly there is nothing to win by deferring and the idea dies here.
    //
    // Decode rate as a function of WHERE in the masterbundle's single LZMA stream the pass has reached.
    // `ress` prices a deferral with one 64 MiB sample taken at the head of the first stream node and
    // applies it to the whole node; this walks the entire blob and reports the rate per window, which is
    // the measurement that says how wrong a single sample is. Only run when named: it decodes the whole
    // ~1.4 GB blob and so costs far more than every timed suite put together.
    private static void LzmaDiagnostic(string? unturned)
    {
        string? bundlePath = Environment.GetEnvironmentVariable("BUNDLE_PATH")
            ?? (unturned == null ? null : UnturnedInstall.FindMasterBundle(unturned));
        if (Skip("lzma", bundlePath != null && File.Exists(bundlePath) ? bundlePath : null, out string path))
            return;

        using MasterBundleStream? stream = MasterBundleStream.OpenFile(path);
        if (stream == null)
        {
            Console.WriteLine($"== lzma: SKIP ({Path.GetFileName(path)} is not the single-LZMA-block "
                + "shape the streaming reader handles) ==\n");
            return;
        }

        var ordered = new List<MasterBundleStream.Node>(stream.Nodes);
        ordered.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        // 32 MiB keeps the per-window table readable over a 1.4 GB blob; LZMA_WINDOW=8 resolves the tail
        // finely enough to see where a node's rate changes.
        long window = 32L << 20;
        if (long.TryParse(Environment.GetEnvironmentVariable("LZMA_WINDOW"), out long configured)
            && configured > 0)
            window = configured << 20;

        Console.WriteLine($"== lzma: decode rate by region over {Path.GetFileName(path)} ==");
        Console.WriteLine($"  {Mb(new FileInfo(path).Length)} on disk, {Mb(stream.TotalSize)} decompressed, "
            + $"{Mb(window)} windows");
        foreach (MasterBundleStream.Node node in ordered)
            Console.WriteLine($"    node {LastSegment(node.Path),-52} at {Mb(node.Offset),12}, {Mb(node.Size)}");
        Console.WriteLine("       from         to        rate     window   node");

        var buffer = new byte[4 << 20];
        long done = 0;
        double total = 0;
        double slowest = double.MaxValue, fastest = 0;
        // Per-node totals: the figure a caller deferring work actually needs, since the node a range sits
        // in predicts its decode cost far better than any single sample of the stream does.
        var perNode = new Dictionary<string, (long Bytes, double Seconds)>(StringComparer.Ordinal);
        while (done < stream.TotalSize)
        {
            // Clip to the next node boundary so no window spans two nodes: the whole point of the table
            // below is that a node's content, not its position, is what sets its rate, and a straddling
            // window would average the two together and hide exactly the difference being measured.
            long boundary = stream.TotalSize;
            foreach (MasterBundleStream.Node node in ordered)
                if (node.Offset > done && node.Offset < boundary)
                    boundary = node.Offset;

            long want = Math.Min(window, boundary - done);
            var timer = Stopwatch.StartNew();
            long got = 0;
            while (got < want)
            {
                int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, want - got));
                if (n <= 0)
                    break;
                got += n;
            }
            timer.Stop();
            if (got <= 0)
                break;

            double seconds = timer.Elapsed.TotalSeconds;
            double rate = got / 1048576.0 / seconds;
            total += seconds;
            slowest = Math.Min(slowest, rate);
            fastest = Math.Max(fastest, rate);

            string where = "(before first node)";
            foreach (MasterBundleStream.Node node in ordered)
                if (node.Offset <= done)
                    where = LastSegment(node.Path);
            perNode.TryGetValue(where, out (long Bytes, double Seconds) acc);
            perNode[where] = (acc.Bytes + got, acc.Seconds + seconds);

            Console.WriteLine($"  {Mb(done),10} {Mb(done + got),10} {rate,8:0.0} MiB/s {seconds,7:0.00} s   {where}");
            done += got;
        }

        Console.WriteLine("\n  by node (windows are clipped to node boundaries, so these are exact)");
        foreach (MasterBundleStream.Node node in ordered)
        {
            if (!perNode.TryGetValue(LastSegment(node.Path), out (long Bytes, double Seconds) acc)
                || acc.Seconds <= 0)
                continue;
            Console.WriteLine($"    {LastSegment(node.Path),-52} {Mb(acc.Bytes),12} in {acc.Seconds,6:0.00} s "
                + $"= {acc.Bytes / 1048576.0 / acc.Seconds,7:0.0} MiB/s");
        }

        Console.WriteLine($"\n  whole blob: {Mb(done)} in {total:0.0} s = {done / 1048576.0 / total:0.0} MiB/s average");
        Console.WriteLine($"  slowest window {slowest:0.0} MiB/s, fastest {fastest:0.0} MiB/s "
            + $"({(slowest > 0 ? fastest / slowest : 0):0}x) — one sampled rate cannot price a deferral "
            + "elsewhere in the blob");
        Console.WriteLine();
    }

    // Only metadata is decoded: the ranges come from the SerializedFile, so the .resS itself is never
    // read except for the short sample that measures this machine's decode rate.
    // RESS_BUNDLE overrides the bundle; RESS_TAG the cache tag when it is not derived from the file name.
    private static void ResSDiagnostic(string? unturned, string? map)
    {
        string? bundlePath = Environment.GetEnvironmentVariable("RESS_BUNDLE")
            ?? (unturned == null ? null : UnturnedInstall.FindMasterBundle(unturned));
        if (Skip("ress", bundlePath != null && File.Exists(bundlePath) ? bundlePath : null, out string path))
            return;

        using MasterBundleStream? stream = MasterBundleStream.OpenFile(path);
        if (stream == null)
        {
            Console.WriteLine($"== ress: SKIP ({Path.GetFileName(path)} is not the single-LZMA-block "
                + "shape the streaming reader handles) ==\n");
            return;
        }

        // The cache tag has to be the one the game writes with, not one derived from the file name: the
        // runtime takes it from the bundle's declared name ("core.masterbundle" -> "core"), while the file
        // on disk is core_linux.masterbundle, whose name would give "core-linux-<hash>". Getting this
        // wrong does not fail loudly — it makes every cache key miss and reports that nothing is wanted.
        string? tag = Environment.GetEnvironmentVariable("RESS_TAG") ?? CacheTagFor(unturned, path);
        if (tag == null)
        {
            Console.WriteLine($"== ress: SKIP (no content source in {unturned ?? "(no install)"} ships "
                + $"{Path.GetFileName(path)}, so its cache tag is unknown — set RESS_TAG to name it) ==\n");
            return;
        }

        Console.WriteLine($"== ress: forward-pass read extent over {Path.GetFileName(path)} ==");
        Console.WriteLine($"  bundle: {path}");
        Console.WriteLine($"  {Mb(new FileInfo(path).Length)} on disk, {Mb(stream.TotalSize)} decompressed, "
            + $"tag '{tag}'");

        var ordered = new List<MasterBundleStream.Node>(stream.Nodes);
        ordered.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        // Every streamed Texture2D the bundle declares, by the stream file that holds its pixels. Reading
        // past the first stream node would cost the very ~1.18 GB decode this diagnostic exists to avoid,
        // so serialized files sitting behind one are counted and skipped rather than scanned.
        var declared = new Dictionary<string, List<StreamRange>>(StringComparer.Ordinal);
        // Textures this bundle stores inline. A map whose meshes want only these needs no stream bytes at
        // all, which has to be told apart from a want set belonging to some other bundle entirely.
        var inlineKeys = new HashSet<string>(StringComparer.Ordinal);
        var fileTags = new List<string>();
        int scanned = 0, skippedSerialized = 0;
        bool pastFirstStream = false;
        foreach (MasterBundleStream.Node node in ordered)
        {
            if (IsResSNode(node.Path))
            {
                pastFirstStream = true;
                continue;
            }

            if (pastFirstStream)
            {
                skippedSerialized++;
                continue;
            }

            string fileTag = scanned == 0 ? tag : $"{tag}-{scanned + 1}";
            fileTags.Add(fileTag);
            scanned++;
            var timer = Stopwatch.StartNew();
            SerializedFile file = SerializedFile.Read(stream.Read((int)node.Size));
            timer.Stop();
            Console.WriteLine($"  serialized: {LastSegment(node.Path)} {Mb(node.Size)} "
                + $"({timer.Elapsed.TotalSeconds:0.00} s to decode+parse)");
            CollectStreamRanges(file, fileTag, declared, inlineKeys);
        }

        if (skippedSerialized > 0)
        {
            Console.WriteLine($"  note: {skippedSerialized} serialized file(s) sit behind a stream node "
                + "and were not scanned — their textures are missing from the numbers below");
        }

        // Two want sets, and the answer is bracketed between them.
        //
        // The upper bound is every streamed Texture2D the bundle declares. The lower bound is what THIS
        // map's placed objects depend on, read out of the mesh cache through the same index the runtime
        // consults — scoped to one map, unlike the texture cache directory, which is shared by every map
        // and would hand back the union of everything ever extracted.
        //
        // The reference throughout is a COLD load — an empty texture cache — so the dependencies are taken
        // unfiltered by what happens to be cached now. Filtering them by TextureCache.IsCurrentForSource
        // would describe what a resumed pass still owes, which is a different question and one whose
        // answer moves with incidental cache state.
        //
        // It is a lower bound twice over: a real load also wants foliage, tree and terrain-layer textures
        // that Level/Objects.dat does not reach, and a prefab whose mesh this bundle has not extracted yet
        // contributes nothing here. That is the useful direction — if even the lower bound already runs to
        // the end of the node, no larger set can end earlier.
        HashSet<string>? mapWantKeys = MapScopedWantKeys(map, fileTags, path, out string why);
        if (mapWantKeys == null)
            Console.WriteLine($"  note: no map-scoped want set ({why})");
        else if (!AnyKeyMatches(declared, mapWantKeys))
        {
            // Keys resolved against another bundle would otherwise filter every range away and be reported
            // as "nothing wanted" — a wrong answer that looks like a measured one. A map wanting only
            // INLINE textures of this bundle produces the same empty filter but is a true zero, so the two
            // are told apart before the fallback fires.
            bool inlineOnly = false;
            foreach (string key in mapWantKeys)
                if (inlineKeys.Contains(key))
                {
                    inlineOnly = true;
                    break;
                }

            if (inlineOnly)
            {
                Console.WriteLine($"  note: all {mapWantKeys.Count:N0} textures this map wants from "
                    + $"'{tag}' are stored inline — the pass reads no stream bytes for it");
            }
            else
            {
                Console.WriteLine($"  note: the mesh cache names {mapWantKeys.Count:N0} textures for tag "
                    + $"'{tag}' but none of them are in this bundle; falling back to the superset");
                mapWantKeys = null;
            }
        }

        // Only a complete scan makes the declared set an upper bound. With a serialized file skipped, the
        // textures it names — possibly in a later stream node — are absent, so the extent below can be
        // SHORTER than the real load's and cannot bracket anything.
        Console.WriteLine(mapWantKeys == null
            ? skippedSerialized > 0
                ? "  want set: every streamed Texture2D the SCANNED serialized files declare (INCOMPLETE — "
                    + "the skipped files may name ranges that push the real extent further out)"
                : "  want set: every streamed Texture2D in the bundle (SUPERSET — an upper bound on the "
                    + "read extent, not the set a real load asks for)"
            : $"  want set: {mapWantKeys.Count:N0} textures the mesh cache says this map's placed objects "
                + "need on a cold load (a LOWER bound — foliage, trees, terrain layers and any prefab not "
                + "extracted yet are wanted too but not counted)");

        // The pass cannot seek, so a node with nothing wanted in it is still decoded in full when a LATER
        // node is owed something. Which node is last decides that, so it has to be known before reporting.
        // A stream node is also fully consumed when a SERIALIZED file still lies ahead of it: the real pass
        // has to reach that metadata, and it cannot seek there either. This diagnostic deliberately skips
        // those files, so without counting them a stream node in front of one reads as untouched.
        int lastSerialized = -1;
        for (int i = 0; i < ordered.Count; i++)
            if (!IsResSNode(ordered[i].Path))
                lastSerialized = i;

        var streamNodes = new List<(string Name, long Size, List<StreamRange> Wants)>();
        int lastWanted = -1;
        int forcedByMetadata = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            MasterBundleStream.Node node = ordered[i];
            if (!IsResSNode(node.Path))
                continue;

            if (i < lastSerialized)
                forcedByMetadata = streamNodes.Count;

            string name = LastSegment(node.Path);
            declared.TryGetValue(name, out List<StreamRange>? all);
            var wants = new List<StreamRange>();
            foreach (StreamRange range in all ?? new List<StreamRange>())
                if (range.Offset >= 0 && range.Size > 0 && range.Offset + range.Size <= node.Size
                    && (mapWantKeys == null || mapWantKeys.Contains(range.Key)))
                {
                    wants.Add(range);
                }

            if (wants.Count > 0)
                lastWanted = streamNodes.Count;
            streamNodes.Add((name, node.Size, wants));
        }

        // Either reason forces a node to be read whole; the later one decides where the pass can stop.
        int lastForced = Math.Max(lastWanted, forcedByMetadata);

        long totalRead = 0, streamBytes = 0;
        for (int i = 0; i < streamNodes.Count; i++)
        {
            (string name, long size, List<StreamRange> wants) = streamNodes[i];
            streamBytes += size;
            totalRead += ReportNode(name, size, wants, i, lastForced, i <= forcedByMetadata);
        }

        // Summed from the stream nodes themselves: subtracting one serialized file from the blob would
        // leave every further serialized file inside a figure labelled as the size of the stream nodes.
        Console.WriteLine($"\n  whole pass: {Mb(totalRead)} of the {Mb(streamBytes)} of stream nodes "
            + "is decoded");
        MeasureDecodeRate(stream, ordered);
        Console.WriteLine();
    }

    // One wanted texture's pixels inside a stream file: where they sit, the cache key that names them, and
    // the asset folder it belongs to (so a subset drawn from one folder can be told from the whole bundle).
    private readonly record struct StreamRange(string Key, string Group, long Offset, int Size)
    {
        public long End => Offset + Size;
    }

    private static void CollectStreamRanges(SerializedFile file, string fileTag,
        Dictionary<string, List<StreamRange>> declared, HashSet<string> inlineKeys)
    {
        const int texture2DClassId = 28;
        Dictionary<long, string> groupById = ReadContainerGroups(file);
        foreach (SerializedObject obj in file.Objects)
        {
            if (obj.ClassId != texture2DClassId)
                continue;

            UnityTexture texture = UnityTexture.Read(TypeTreeReader.Read(obj.TypeTree, file.ReaderFor(obj)));
            if (texture.StreamPath.Length == 0 || texture.StreamSize <= 0)
            {
                // Pixels stored inline: the pass never waits on the stream for these, but they are still
                // this bundle's textures, and a want set made only of them is a real answer of zero.
                inlineKeys.Add(TextureKey.For(fileTag, obj.PathId));
                continue;
            }

            string streamFile = texture.StreamFileName;
            if (!declared.TryGetValue(streamFile, out List<StreamRange>? list))
                declared[streamFile] = list = new List<StreamRange>();
            list.Add(new StreamRange(TextureKey.For(fileTag, obj.PathId),
                groupById.TryGetValue(obj.PathId, out string? group) ? group : "(uncontained)",
                texture.StreamOffset, texture.StreamSize));
        }
    }

    // The asset folder each object sits in, from the AssetBundle object that names every asset by its
    // container path ("assets/coremasterbundle/objects/..." -> "objects"). A real load wants textures
    // reachable from the map's placed prefabs, so the per-folder spans below bound what ANY such subset
    // can look like — a folder spread over the whole node cannot have a deferrable tail.
    private static Dictionary<long, string> ReadContainerGroups(SerializedFile file)
    {
        const int assetBundleClassId = 142;
        var groups = new Dictionary<long, string>();
        foreach (SerializedObject obj in file.Objects)
        {
            if (obj.ClassId != assetBundleClassId)
                continue;

            Dictionary<string, object> bundle = TypeTreeReader.Read(obj.TypeTree, file.ReaderFor(obj));
            foreach (object entry in (List<object>)bundle["m_Container"])
            {
                var pair = (Dictionary<string, object>)entry;
                var info = (Dictionary<string, object>)pair["second"];
                long assetId = Convert.ToInt64(((Dictionary<string, object>)info["asset"])["m_PathID"]);
                // "assets/<bundle name>/<folder>/..." — the folder is the third segment.
                string[] segments = ((string)pair["first"]).Split('/');
                groups.TryAdd(assetId, segments.Length >= 3 ? segments[2] : "(root)");
            }
        }
        return groups;
    }

    // The whole point of the diagnostic: read extent is set by the single furthest range, so the curve
    // below says how much of the file the tail alone is responsible for. Returns the bytes this node
    // costs the pass, which is not the same as what is wanted from it — see the traversal cases.
    private static long ReportNode(string name, long size, List<StreamRange> wants, int index,
        int lastForced, bool forMetadata)
    {
        // `forMetadata` covers the node ITSELF, not just the ones before it: a serialized file sitting
        // after this node has to be reached, so the decoder runs off its end either way. Testing only
        // `index < lastForced` let the last such node report its own extent and understate the pass.
        bool whole = index < lastForced || forMetadata;
        string reason = forMetadata ? "a later serialized file has to be reached" : "a later node is owed";
        Console.WriteLine($"\n  {name} {Mb(size)}");
        if (wants.Count == 0)
        {
            // A forward-only decoder cannot skip a node to reach the next one, so "nothing wanted here"
            // only means "free" once nothing after it is owed either.
            if (whole)
            {
                Console.WriteLine($"    nothing wanted here, but {reason} — all {Mb(size)} is decoded "
                    + "just to get past it");
                return size;
            }

            Console.WriteLine("    nothing wanted here, and nothing after it — the pass never touches it");
            return 0;
        }

        if (whole)
        {
            // Its own furthest range does not end the pass: something after it still has to be reached.
            long pixelsHere = 0;
            foreach (StreamRange want in wants)
                pixelsHere += want.Size;
            Console.WriteLine($"    wanted: {wants.Count:N0} ranges, {Mb(pixelsHere)} of pixels — but "
                + $"{reason}, so all {Mb(size)} is decoded regardless");
            return size;
        }

        wants.Sort((a, b) => a.End.CompareTo(b.End));
        long extent = wants[^1].End;

        // Two Texture2D objects can name the same (or an overlapping) stream region, so summing range
        // sizes counts those physical bytes twice. Waste is extent minus the bytes actually covered, and
        // the sum would understate it — far enough, with enough sharing, to print a negative number.
        long pixels = UnionLength(wants);
        long summed = 0;
        foreach (StreamRange want in wants)
            summed += want.Size;

        Console.WriteLine($"    wanted:      {wants.Count:N0} ranges covering {Mb(pixels)} "
            + $"({Percent(pixels, size)} of the node)"
            + (summed == pixels ? "" : $"; {Mb(summed - pixels)} of that is shared between ranges"));
        Console.WriteLine($"    read extent: {Mb(extent)} ({Percent(extent, size)}) — "
            + $"{Mb(extent - pixels)} decoded for nothing");

        // Where the wanted ranges sit, in tenths of the node. An even spread here means no tail to defer.
        var perDecile = new int[10];
        foreach (StreamRange want in wants)
            perDecile[Math.Clamp((int)(want.Offset * 10 / Math.Max(size, 1)), 0, 9)]++;
        var histogram = new List<string>(10);
        for (int i = 0; i < 10; i++)
            histogram.Add(perDecile[i].ToString("N0"));
        Console.WriteLine($"    ranges per tenth of the node: {string.Join(" ", histogram)}");

        // Only the printed rows are computed, so each one can afford to union its own tail. A running sum
        // would count bytes two aliasing ranges share once per range, letting "deferred px" claim more
        // texture data than the node holds — beside a coverage figure that is already unioned.
        var steps = new List<int>();
        foreach (int k in new[] { 1, 2, 3, 4, 8, 16, 32, 64, wants.Count })
            if (k <= wants.Count && !steps.Contains(k))
                steps.Add(k);
        steps.Sort();

        Console.WriteLine("    deferring the last k ranges (by end offset):");
        Console.WriteLine($"      {"k",5}  {"read extent",13}  {"saved",11}  {"of node",8}  {"deferred px",12}");
        foreach (int k in steps)
        {
            long remaining = k == wants.Count ? 0 : wants[^(k + 1)].End;
            long deferred = UnionLength(wants.GetRange(wants.Count - k, k));
            Console.WriteLine($"      {k,5}  {Mb(remaining),13}  {Mb(extent - remaining),11}  "
                + $"{Percent(extent - remaining, size),8}  {Mb(deferred),12}");
        }

        // The decision numbers: how few ranges have to move for the pass to read materially less. A node
        // whose ranges all end early cannot reach the larger targets at all — deferring every one of them
        // only saves `extent`, so those targets have to be reported as unreachable rather than as a count.
        foreach (int target in new[] { 10, 25, 50 })
        {
            int k = 0;
            bool reached = false;
            while (k < wants.Count)
            {
                long remaining = k + 1 == wants.Count ? 0 : wants[^(k + 2)].End;
                k++;
                if ((extent - remaining) * 100 >= (long)target * size)
                {
                    reached = true;
                    break;
                }
            }

            Console.WriteLine(reached
                ? $"    reading {target}% less of the node needs the last {k:N0} range(s) deferred "
                    + $"({Percent(k, wants.Count)} of what is wanted)"
                : $"    reading {target}% less of the node is unreachable — deferring all {wants.Count:N0} "
                    + $"ranges saves only {Mb(extent)} ({Percent(extent, size)})");
        }

        // A deferral is only worth anything where the wanted ranges thin out. The biggest gap in the last
        // quarter is the best single cut available anywhere near the tail.
        //
        // Swept in START order against a running maximum end, like UnionLength. `wants` is in END order,
        // where one long range can follow ranges that begin after it — differencing neighbours there
        // measures nothing in particular and can step straight over a real gap between clusters.
        var byStart = new List<StreamRange>(wants);
        byStart.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        // Clamped to zero for a node whose ranges all end inside the first quarter of it: the window then
        // covers everything read, which is the intent. The sweep below never measured from a negative
        // offset — `reach` starts at zero and only grows — but saying so here beats relying on it.
        long quarter = Math.Max(0, extent - (size / 4));
        long widest = 0, widestCut = 0, reach = 0;
        foreach (StreamRange want in byStart)
        {
            // Measured from the window's edge, never from a range that ended before it. A node whose
            // ranges stop early and resume near the tail would otherwise have the whole empty middle
            // counted as a gap "in the last quarter" and look far sparser there than it is.
            long from = Math.Max(reach, quarter);
            if (want.Offset > from && want.Offset - from > widest)
            {
                widest = want.Offset - from;
                widestCut = want.Offset;
            }
            reach = Math.Max(reach, want.End);
        }

        if (widest == 0)
        {
            Console.WriteLine("    widest gap in the last quarter: none — the wanted ranges are "
                + "back to back there, so no cut is cheaper than any other");
        }
        else
        {
            // Cutting at that offset defers everything still unfinished there, counted once at the end
            // rather than inside the sweep.
            int widestAt = 0;
            foreach (StreamRange want in wants)
                if (want.End > widestCut)
                    widestAt++;

            Console.WriteLine($"    widest gap in the last quarter: {Bytes(widest)} — cutting there defers "
                + $"{widestAt:N0} range(s)");
        }

        ReportGroups(wants, size, extent);
        return extent;
    }

    // What the pass would read if a whole asset folder were wanted. This is an UPPER bound on the folder's
    // subsets and nothing more: a map placing only some of a folder's assets can still stop much earlier,
    // so a folder spanning the node does not prove that every subset of it does. What the table does show
    // is which folders are spread rather than contiguous — where a clustered subset is even possible.
    // Only the real want set (the texture cache above) settles it for an actual load.
    private static void ReportGroups(List<StreamRange> wants, long size, long extent)
    {
        var byGroup = new Dictionary<string, (int Count, long Pixels, long First, long Extent)>(
            StringComparer.Ordinal);
        foreach (StreamRange want in wants)
        {
            byGroup.TryGetValue(want.Group, out (int Count, long Pixels, long First, long Extent) g);
            byGroup[want.Group] = (g.Count + 1, g.Pixels + want.Size,
                g.Count == 0 ? want.Offset : Math.Min(g.First, want.Offset), Math.Max(g.Extent, want.End));
        }

        var groups = new List<KeyValuePair<string, (int Count, long Pixels, long First, long Extent)>>(byGroup);
        groups.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
        Console.WriteLine($"    by asset folder ({groups.Count} of them), {"count",8}  {"pixels",10}  "
            + $"{"first",10}  {"extent",10}  {"of node",8}");
        for (int i = 0; i < groups.Count && i < 12; i++)
        {
            (string name, (int count, long pixels, long first, long groupExtent)) = groups[i];
            Console.WriteLine($"      {name,-28} {count,8:N0}  {Mb(pixels),10}  {Mb(first),10}  "
                + $"{Mb(groupExtent),10}  {Percent(groupExtent, size),8}");
        }
        if (groups.Count > 12)
            Console.WriteLine($"      ... {groups.Count - 12} more");
        Console.WriteLine($"    (the full want set's extent is {Mb(extent)}; a folder's extent is an upper "
            + "bound on its subsets — a map placing only part of a folder can still stop earlier)");
    }

    // The tag the game's caches actually use for this bundle, taken from the content source that ships it
    // — the game's own bundle is keyed by its declared name ("core.masterbundle" -> "core") and a workshop
    // bundle additionally by its item directory, neither of which the file name on disk reproduces.
    private static string? CacheTagFor(string? unturned, string bundlePath)
    {
        if (unturned == null)
            return null;

        // Match the filesystem's own idea of path equality: on Windows a RESS_BUNDLE differing from the
        // discovered path only in case opens fine, and comparing it ordinally would report the tag as
        // unresolvable for a bundle that is plainly installed.
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        string full = Path.GetFullPath(bundlePath);
        foreach (ContentSource source in ContentSource.Discover(unturned))
            if (source.BundlePath.Length > 0
                && string.Equals(Path.GetFullPath(source.BundlePath), full, comparison))
            {
                return source.CacheTag;
            }

        return null;
    }

    // Whether the want set says anything at all about this bundle. "The index named some textures" and
    // "it named textures that are in here" are different questions, and only the second one makes the
    // filter below meaningful rather than silently emptying it.
    private static bool AnyKeyMatches(Dictionary<string, List<StreamRange>> declared, HashSet<string> keys)
    {
        foreach (List<StreamRange> ranges in declared.Values)
            foreach (StreamRange range in ranges)
                if (keys.Contains(range.Key))
                    return true;

        return false;
    }

    // LZMA decode rate on this machine, from the head of the first stream node — what turns the megabytes
    // above into the seconds a deferral would actually save. Consumes the sample, so it runs last.
    private static void MeasureDecodeRate(MasterBundleStream stream, List<MasterBundleStream.Node> ordered)
    {
        long remaining = stream.TotalSize - stream.Cursor;
        int sample = (int)Math.Min(64L << 20, remaining);
        if (sample <= 0)
            return;

        var buffer = new byte[1 << 20];
        var timer = Stopwatch.StartNew();
        int read = 0;
        while (read < sample)
        {
            int n = stream.Read(buffer, 0, Math.Min(buffer.Length, sample - read));
            if (n <= 0)
                break;
            read += n;
        }
        timer.Stop();
        double rate = read / 1048576.0 / timer.Elapsed.TotalSeconds;
        Console.WriteLine($"\n  decode rate: {rate:0.0} MiB/s over a {Mb(read)} sample "
            + $"({timer.Elapsed.TotalSeconds:0.00} s) — every 100 MiB not read is ~{100 / rate:0.00} s");
    }

    // Physical bytes covered by a set of ranges, counting shared regions once. The list arrives sorted by
    // END offset, which is not the same order as by start, so this sorts a copy by start before sweeping.
    private static long UnionLength(List<StreamRange> ranges)
    {
        var byStart = new List<StreamRange>(ranges);
        byStart.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        long covered = 0, reach = long.MinValue;
        foreach (StreamRange range in byStart)
        {
            long from = Math.Max(range.Offset, reach);
            if (range.End > from)
                covered += range.End - from;
            reach = Math.Max(reach, range.End);
        }
        return covered;
    }

    // The textures this map's placed objects depend on, taken from the mesh cache through the same index
    // the runtime uses to decide what a load still owes. Scoped to one map, unlike the texture cache
    // directory, which every map writes into and which therefore answers a different question.
    private static HashSet<string>? MapScopedWantKeys(string? map, List<string> fileTags, string bundlePath,
        out string why)
    {
        // Every way this can come back empty is a different measurement, so the caller says which one it
        // got rather than quietly reporting the superset as though it had been asked for.
        why = string.Empty;
        if (map == null)
        {
            why = "no map resolved";
            return null;
        }

        string? meshCache = FindGodotUserDir() is { } user ? Path.Combine(user, "model_cache") : null;
        if (meshCache == null || !Directory.Exists(meshCache))
        {
            why = "no mesh cache on this machine — run the game once to populate it";
            return null;
        }

        string objects = Path.Combine(map, "Level", "Objects.dat");
        if (!File.Exists(objects))
        {
            why = $"{objects} is missing";
            return null;
        }

        // Only GUIDs this bundle demonstrably extracted, at its current revision. The mesh cache is shared
        // between installs and survives a bundle update, and NeededTextureIds trusts any entry in the
        // current format — so a mesh left by another source would contribute path ids that mean nothing
        // here. One stale id landing inside this bundle is enough for a late unrelated texture to set the
        // reported extent, which is exactly the number the whole diagnostic turns on.
        long stamp = ExtractionIndex.StampFor(bundlePath);

        // A map repeats one prefab GUID across thousands of placements and MeshBelongsTo reads files, so
        // the distinct set comes first and each GUID is proved at most once.
        var placed = new HashSet<Guid>();
        foreach (PlacedObject entry in LevelObjects.Load(objects))
            if (entry.Guid != Guid.Empty)
                placed.Add(entry.Guid);

        var guids = new HashSet<Guid>();
        foreach (Guid guid in placed)
            if (ExtractionIndex.MeshBelongsTo(meshCache, guid, bundlePath, stamp))
                guids.Add(guid);

        if (guids.Count == 0)
        {
            why = $"none of the map's {placed.Count:N0} placed prefabs has a mesh this bundle extracted "
                + "at its current revision";
            return null;
        }

        // Once per serialized file this bundle carries. NeededTextureIds matches one owner tag exactly, so
        // asking only for the base tag would drop every dependency a second file owns — and a map whose
        // textures all live in one of those would lose its want set entirely.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string fileTag in fileTags)
            foreach (long id in TextureDependencyIndex.NeededTextureIds(meshCache, fileTag, guids))
                keys.Add(TextureKey.For(fileTag, id));

        if (keys.Count == 0)
        {
            why = $"the {guids.Count:N0} cached meshes for this map name no texture owned by "
                + string.Join("/", fileTags);
        }
        return keys.Count > 0 ? keys : null;
    }

    private static bool IsResSNode(string path) =>
        path.EndsWith(".resS", StringComparison.Ordinal)
        || path.EndsWith(".resource", StringComparison.Ordinal);

    private static string LastSegment(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:N1} MiB";

    // Gaps between wanted ranges are the one figure that can land far below a megabyte, and rounding
    // those to "0.0 MB" would hide the difference between a thin tail and no tail at all.
    private static string Bytes(long bytes) => bytes < 1048576
        ? $"{bytes:N0} B"
        : Mb(bytes);

    private static string Percent(long part, long whole) =>
        whole <= 0 ? "n/a" : $"{part * 100.0 / whole:0.0}%";

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

        // Per host, the way check-structural-metrics.sh does it. XDG_DATA_HOME decides only on Linux —
        // it is often set on Windows and macOS too, by shells and porting layers, and probing it there
        // would hand back some unrelated tree ahead of the directory Godot actually writes.
        var candidates = new List<string>(2);
        if (OperatingSystem.IsLinux())
        {
            string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } configured
                ? configured
                : Path.Combine(home, ".local", "share");
            candidates.Add(Path.Combine(xdg, "godot", "app_userdata", "unturned-godot"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add(Path.Combine(home, "Library", "Application Support", "Godot", "app_userdata",
                "unturned-godot"));
        }
        else
        {
            candidates.Add(Path.Combine(appdata, "Godot", "app_userdata", "unturned-godot"));
        }

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
