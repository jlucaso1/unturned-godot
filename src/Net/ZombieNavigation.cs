using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;

namespace UnturnedGodot;

// Feeds the level's PRE-BAKED navmesh (Environment/Navigation_<N>.dat) to Godot's NavigationServer:
// one region per nav flag on a dedicated navigation map, with the exact triangles the original game
// pathfinds over — no runtime baking at all. Exposes the ZombiePathQuery the zombie brain uses for
// its Seeker port. Works headless, so the dedicated server gets real pathfinding from the .dat
// files alone.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class ZombieNavigation
{
    private readonly Rid _map;
    private readonly List<Rid> _regions = new();
    private bool _synced; // the map's FIRST (async) synchronization pass has completed (map_changed)
    private bool _ready;  // a real route resolved: the map actually answers queries
    private Vector3 _probeFrom;
    private Vector3 _probeTo;

    public ZombiePathQuery Query { get; }

    public static ZombieNavigation? Build(IReadOnlyList<NavFlag>? flags)
    {
        if (flags == null || flags.Count == 0)
            return null;
        return new ZombieNavigation(flags);
    }

    // The navigation map syncs asynchronously over a few seconds after its regions appear; kicked
    // off at the START of the world load, the sync finishes long before the player can aggro
    // anything, so no zombie ever falls back to the straight-line seek.
    private static ZombieNavigation? PreloadedInstance;

    public static void Preload(string levelDir)
    {
        if (PreloadedInstance != null)
            return;
        List<NavFlag> flags = LevelNavmesh.Load(System.IO.Path.Combine(levelDir, "Environment"));
        PreloadedInstance = Build(flags);
    }

    public static ZombieNavigation? TakePreloaded()
    {
        ZombieNavigation? nav = PreloadedInstance;
        PreloadedInstance = null;
        return nav;
    }

    private ZombieNavigation(IReadOnlyList<NavFlag> flags)
    {
        _map = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetActive(_map, true);
        // Readiness probe: two corners of the first flag's first triangle. map_changed only says the
        // FIRST synchronization pass finished — measured live, real routes still resolve empty for a
        // few more seconds while the edge merge completes, so readiness is only declared once this
        // route actually resolves (probing earlier than map_changed would spam console errors).
        if (flags[0].Triangles.Length >= 3)
        {
            _probeFrom = flags[0].Vertices[flags[0].Triangles[0]];
            _probeTo = flags[0].Vertices[flags[0].Triangles[1]];
        }
        // The pre-baked mesh was recast at cellSize 0.1 (doorways and dense clutter produce edges
        // that close together); the map's default 0.25 rasterization cell collapses distinct edges
        // into the same cell and DROPS their connections — houses lost their doorway link and
        // zombies pushed at windows. Match the map grid to the source data's resolution.
        NavigationServer3D.MapSetCellSize(_map, 0.1f);
        NavigationServer3D.MapSetCellHeight(_map, 0.1f);

        int triangles = 0;
        foreach (NavFlag flag in flags)
        {
            var mesh = new NavigationMesh { Vertices = flag.Vertices };
            for (int i = 0; i + 2 < flag.Triangles.Length; i += 3)
            {
                mesh.AddPolygon(new[] { flag.Triangles[i], flag.Triangles[i + 1], flag.Triangles[i + 2] });
                triangles++;
            }
            Rid region = NavigationServer3D.RegionCreate();
            NavigationServer3D.RegionSetNavigationMesh(region, mesh);
            NavigationServer3D.RegionSetMap(region, _map);
            _regions.Add(region);
        }

        // The map's first synchronization runs ASYNC and takes seconds at this triangle count
        // (verified live: ~5 s for PEI's 42k triangles). Querying earlier fails with a console
        // error per call, so queries are gated on the server's map_changed signal — until it fires
        // the brain keeps its straight-line fallback, exactly like a map with no navmesh. In
        // practice the world's streaming load outlasts the sync, so players never see the gap.
        NavigationServer3D.Singleton.MapChanged += OnMapChanged;
        GD.Print($"[nav] navmesh up: {flags.Count} regions, {triangles} triangles (pre-baked)");

        Rid map = _map;
        bool debug = OS.GetEnvironment("NAV_DEBUG") == "1";
        bool loggedFirst = false;
        Query = (Vector3 from, Vector3 to, List<Vector3> path) =>
        {
            if (!_ready)
            {
                // map_changed fired but the edge merge may still be running: declare readiness only
                // when a real probe route resolves (each due repath retries — a few per tick at most).
                if (!_synced || NavigationServer3D.MapGetPath(map, _probeFrom, _probeTo, optimize: true).Length < 2)
                    return false; // still building: the brain waits, like a zombie with no route
                _ready = true;
                GD.Print("[nav] navmesh answering queries; zombie pathfinding live");
            }
            Vector3[] points = NavigationServer3D.MapGetPath(map, from, to, optimize: true);
            if (points.Length < 2)
                return false; // no route (start/target unreachable): the brain seeks straight
            for (int i = 0; i < points.Length; i++)
                path.Add(points[i]);
            if (debug && (!loggedFirst || points.Length > 3))
            {
                loggedFirst = true;
                GD.Print($"[nav] path {from} -> {to}: {points.Length} waypoints");
            }
            return true;
        };
    }

    private void OnMapChanged(Rid map)
    {
        if (map != _map || _synced)
            return;
        _synced = true;
        GD.Print("[nav] navmesh first synchronization done");
    }

    public void Free()
    {
        NavigationServer3D.Singleton.MapChanged -= OnMapChanged;
        foreach (Rid region in _regions)
            NavigationServer3D.FreeRid(region);
        _regions.Clear();
        NavigationServer3D.FreeRid(_map);
    }
}
