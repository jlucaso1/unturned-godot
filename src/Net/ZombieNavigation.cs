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

    public ZombiePathQuery Query { get; }

    public static ZombieNavigation? Build(IReadOnlyList<NavFlag>? flags)
    {
        if (flags == null || flags.Count == 0)
            return null;
        return new ZombieNavigation(flags);
    }

    private ZombieNavigation(IReadOnlyList<NavFlag> flags)
    {
        _map = NavigationServer3D.MapCreate();
        NavigationServer3D.MapSetActive(_map, true);

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
            NavigationServer3D.RegionSetMap(region, _map);
            NavigationServer3D.RegionSetNavigationMesh(region, mesh);
            _regions.Add(region);
        }

        // Regions sync on the next physics step of the navigation server; until then queries come
        // back empty and the brain's straight-line fallback covers the gap (one repath, 0.5 s).
        GD.Print($"[nav] navmesh up: {flags.Count} regions, {triangles} triangles (pre-baked)");

        Rid map = _map;
        bool debug = OS.GetEnvironment("NAV_DEBUG") == "1";
        bool loggedFirst = false;
        Query = (Vector3 from, Vector3 to, List<Vector3> path) =>
        {
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

    public void Free()
    {
        foreach (Rid region in _regions)
            NavigationServer3D.FreeRid(region);
        _regions.Clear();
        NavigationServer3D.FreeRid(_map);
    }
}
