#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot.EditorTools;

// Draws a map's baked navigation into the edited scene so it can be flown through and looked at.
//
// The point is diagnosis. A navmesh defect is almost never visible in the game — a zombie takes a long
// way round, or stands somewhere forever, and nothing on screen says why — but every one of them has a
// shape, and the shapes are easy to recognise once drawn:
//
//   * a HOLE is a rim line in the middle of open floor, where the surface simply stops;
//   * a POCKET is a patch of surface in its own colour with a beacon over it, cut off from everything
//     around it, so anything that spawns there is stuck there for the session;
//   * a GAP between authored bounds and baked surface shows as spawn boxes reaching past the mesh.
//
// It reads only Environment/*.dat, so it needs no masterbundle, no cache and no map preview: on PEI the
// whole thing is about a fifth of a second from pressing the button. Everything it shows comes from
// BakedNavGraph's survey — the adjacency the game's own pathfinder walks — rather than from the raw
// triangles, which disagree with it about 3 449 of PEI's edges.
//
// What it shows is the navmesh AS BAKED. A session additionally reconciles it against collision
// (ZombieNavigation.PruneAgainstCollisionAsync) and drops the faces that turn out to sit inside
// geometry, so a live graph can be a little smaller than this one. That pass is not reproducible here:
// its verdict comes from probing realised colliders, which the editor deliberately never builds, and
// even its cached result is fingerprinted against a collider cache a session had to populate first. So
// the report says which of the two it is rather than implying a parity it does not have.
//
// Drawn to stay smooth to fly through: one mesh per flag (so the renderer culls the ones behind you),
// one shared unshaded material per layer, no shadows, no depth writes, and every toggle except the
// build itself is a property write rather than a rebuild.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class NavigationPreview
{
    public const string RootName = "UnturnedNavigation";

    public readonly record struct Options(bool Rim, bool Beacons, bool Bounds, bool XRay, float Lift)
    {
        // Rims and beacons on, because they are the two that point AT something. Bounds are context
        // rather than a defect, so they stay out of the way until asked for.
        public static Options Default => new(Rim: true, Beacons: true, Bounds: false, XRay: false,
            Lift: NavigationOverlay.DefaultLift);
    }

    // Builds the overlay detached, a flag at a time, handing the editor its frame back between flags.
    // The parsing and the survey are pure CPU and run on a worker; only the meshes are built here.
    public static async Task<Node3D> BuildAsync(string mapPath, Options options, Node yieldOn,
        Action<string> onStatus, List<string> report)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        string environmentDir = Path.Combine(mapPath, "Environment");

        onStatus("Reading navmesh…");
        (IReadOnlyList<NavFlag> flags, IReadOnlyList<NavFlagSurvey> survey, List<NavBound> spawnBounds) =
            await Task.Run(() =>
            {
                List<NavFlag> loaded = LevelNavmesh.Load(environmentDir);
                IReadOnlyList<NavFlagSurvey> surveyed = BakedNavGraph.Build(loaded).Survey();
                return ((IReadOnlyList<NavFlag>)loaded, surveyed,
                    LevelNavigationData.Load(environmentDir));
            });

        // Detached until the very end, and a Node is manually managed: nothing collects it if this
        // throws part-way. Yield throws exactly that way when the dock is freed mid-build — the plugin
        // disabled, or the project closed — and the whole half-built tree would then be leaked
        // instances for the life of the editor process.
        var root = new Node3D { Name = RootName, Position = new Vector3(0, options.Lift, 0) };
        try
        {
            var surfaces = new Node3D { Name = NavigationOverlay.SurfaceNode };
            var rims = new Node3D { Name = NavigationOverlay.RimNode, Visible = options.Rim };
            root.AddChild(surfaces);
            root.AddChild(rims);

            StandardMaterial3D surfaceMaterial = NavigationOverlay.Material(options.XRay, lines: false);
            StandardMaterial3D rimMaterial = NavigationOverlay.Material(options.XRay, lines: true);

            for (int i = 0; i < survey.Count; i++)
            {
                onStatus($"Navmesh {i + 1}/{survey.Count}…");
                await Yield(yieldOn);

                NavFlagSurvey flag = survey[i];
                if (NavigationOverlay.BuildSurface(flag, surfaceMaterial, $"Flag{i}") is { } surface)
                    surfaces.AddChild(surface);
                if (NavigationOverlay.BuildRim(flag, rimMaterial, $"Flag{i}") is { } rim)
                    rims.AddChild(rim);
            }

            await Yield(yieldOn);
            if (NavigationOverlay.BuildBeacons(survey, out int drawn, out int total) is { } beacons)
            {
                beacons.Visible = options.Beacons;
                root.AddChild(beacons);
            }
            if (NavigationOverlay.BuildBounds(flags, spawnBounds) is { } bounds)
            {
                bounds.Visible = options.Bounds;
                root.AddChild(bounds);
            }

            Summarize(survey, drawn, total, timer.ElapsedMilliseconds, report);
            return root;
        }
        catch
        {
            root.Free(); // frees the children with it
            throw;
        }
    }

    // Applies every option that is not baked into a vertex, so the checkboxes are instant instead of
    // rebuilding. Flying around with a knob in hand is most of how this gets used.
    public static void Apply(Node3D root, Options options)
    {
        root.Position = new Vector3(0, options.Lift, 0);
        SetVisible(root, NavigationOverlay.RimNode, options.Rim);
        SetVisible(root, NavigationOverlay.BeaconNode, options.Beacons);
        SetVisible(root, NavigationOverlay.BoundsNode, options.Bounds);

        // Beacons and bounds are always drawn through geometry, so both are left out of the X-ray
        // sweep — it would otherwise turn their depth test back ON, which is not a state either was
        // ever built in. A marker you can only see once you are already looking at the thing it marks
        // is not a marker, and a spawn box you can only see from inside the terrain says nothing about
        // the surface under it. Sweeping them also made the same options render differently depending
        // on whether they were set before the build or after it.
        foreach (Node layer in root.GetChildren())
        {
            if (layer.Name == NavigationOverlay.BeaconNode || layer.Name == NavigationOverlay.BoundsNode)
                continue;
            foreach (MeshInstance3D mesh in Meshes(layer))
                if (mesh.MaterialOverride is StandardMaterial3D material)
                    material.NoDepthTest = options.XRay;
        }
    }

    private static IEnumerable<MeshInstance3D> Meshes(Node node)
    {
        if (node is MeshInstance3D self)
            yield return self;
        foreach (Node child in node.GetChildren())
            foreach (MeshInstance3D mesh in Meshes(child))
                yield return mesh;
    }

    private static void SetVisible(Node3D root, string child, bool visible)
    {
        if (root.GetNodeOrNull<Node3D>(child) is { } node)
            node.Visible = visible;
    }

    private static void Summarize(IReadOnlyList<NavFlagSurvey> survey, int beaconsDrawn, int beacons,
        long milliseconds, List<string> report)
    {
        int faces = 0, islands = 0, rim = 0, dropped = 0;
        foreach (NavFlagSurvey flag in survey)
        {
            faces += flag.TriangleCount;
            islands += flag.Islands.Count;
            rim += flag.Rim.Count;
            dropped += flag.DroppedTriangles;
        }

        report.Add($"{survey.Count} flags, {faces:N0} faces, {islands:N0} islands, "
            + $"{rim:N0} rim segments in {milliseconds} ms  (as baked — not reconciled with collision)");
        if (dropped > 0)
            report.Add($"  {dropped:N0} faces dropped");

        // The flags worth flying to first: the ones whose surface is in pieces rather than whole.
        var worst = new List<(int Index, NavFlagSurvey Flag)>();
        for (int i = 0; i < survey.Count; i++)
            if (survey[i].LargestIslandShare < 0.95 && survey[i].Islands.Count > 1)
                worst.Add((i, survey[i]));
        worst.Sort((x, y) => x.Flag.LargestIslandShare.CompareTo(y.Flag.LargestIslandShare));
        for (int i = 0; i < worst.Count && i < 5; i++)
        {
            NavFlagSurvey flag = worst[i].Flag;
            NavIsland biggest = flag.Islands[1]; // the largest piece that is NOT the main surface
            report.Add($"  flag {worst[i].Index}: {flag.WalkableTriangles:N0} faces in "
                + $"{flag.Islands.Count} pieces, largest holds {flag.LargestIslandShare:P0}; "
                + $"next is {biggest.TriangleCount} faces at {Where(biggest.Anchor)}");
        }
        if (worst.Count > 5)
            report.Add($"  …and {worst.Count - 5} more fragmented flags");

        report.Add(beaconsDrawn == beacons
            ? $"  {beacons:N0} islands are cut off from their flag's main surface"
            : $"  {beacons:N0} islands are cut off from their flag's main surface; "
              + $"only the first {beaconsDrawn:N0} carry a beacon");
    }

    // Formatted the way SHOT_CAM wants it, so a suspicious island can go straight from this log into a
    // headless screenshot or into the dock's own camera field.
    private static string Where(Vector3 point) =>
        $"{point.X.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}," +
        $"{point.Y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}," +
        $"{point.Z.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}";

    private static async Task Yield(Node yieldOn) =>
        await yieldOn.ToSignal(yieldOn.GetTree(), SceneTree.SignalName.ProcessFrame);

    // Its own root, separate from the map preview's: the two are built from different data at different
    // costs, and clearing one must not take the other with it.
    public static Node3D Attach(Node parent, Node3D preview)
    {
        Clear(parent);
        parent.AddChild(preview);
        return preview; // Owner stays null, so the editor never writes it into the .tscn
    }

    public static bool Clear(Node parent)
    {
        if (parent.GetNodeOrNull<Node3D>(RootName) is not { } existing)
            return false;
        existing.Name = RootName + "_freeing";
        existing.QueueFree();
        return true;
    }

    public static Node3D? Find(Node parent) => parent.GetNodeOrNull<Node3D>(RootName);
}
#endif
