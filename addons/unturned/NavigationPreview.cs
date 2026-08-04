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

    private const string SurfaceNode = "Surface";
    private const string RimNode = "Rim";
    private const string BeaconNode = "Beacons";
    private const string BoundsNode = "Bounds";

    // How far a beacon rises above the pocket it marks. Tall enough to find from cruising height over a
    // 4 km map, and it draws through terrain, so a fragment in a cellar still shows from the air.
    private const float BeaconHeight = 120f;
    private const float BeaconSolidShare = 0.6f;
    // Fragments are numerous — PEI has 396 — and each is two vertices, so the cap is a backstop against
    // a pathological custom map rather than a budget. Whatever it drops is reported, never silent.
    private const int MaxBeacons = 4096;

    // How much of the surface to show at once. Alpha rather than opaque because the interesting part is
    // usually the relationship between the mesh and the ground under it.
    private const float SurfaceAlpha = 0.5f;

    public readonly record struct Options(bool Rim, bool Beacons, bool Bounds, bool XRay, float Lift)
    {
        // Rims and beacons on, because they are the two that point AT something. Bounds are context
        // rather than a defect, so they stay out of the way until asked for.
        public static Options Default => new(Rim: true, Beacons: true, Bounds: false, XRay: false,
            Lift: 0.15f);
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
            var surfaces = new Node3D { Name = SurfaceNode };
            var rims = new Node3D { Name = RimNode, Visible = options.Rim };
            root.AddChild(surfaces);
            root.AddChild(rims);

            StandardMaterial3D surfaceMaterial = OverlayMaterial(options.XRay, unshadedLines: false);
            StandardMaterial3D rimMaterial = OverlayMaterial(options.XRay, unshadedLines: true);

            for (int i = 0; i < survey.Count; i++)
            {
                onStatus($"Navmesh {i + 1}/{survey.Count}…");
                await Yield(yieldOn);

                NavFlagSurvey flag = survey[i];
                if (BuildSurface(flag, surfaceMaterial, $"Flag{i}") is { } surface)
                    surfaces.AddChild(surface);
                if (BuildRim(flag, rimMaterial, $"Flag{i}") is { } rim)
                    rims.AddChild(rim);
            }

            await Yield(yieldOn);
            if (BuildBeacons(survey, out int drawn, out int total) is { } beacons)
            {
                beacons.Visible = options.Beacons;
                root.AddChild(beacons);
            }
            if (BuildBounds(flags, spawnBounds) is { } bounds)
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
        SetVisible(root, RimNode, options.Rim);
        SetVisible(root, BeaconNode, options.Beacons);
        SetVisible(root, BoundsNode, options.Bounds);

        // Beacons and bounds are always drawn through geometry, so both are left out of the X-ray
        // sweep — it would otherwise turn their depth test back ON, which is not a state either was
        // ever built in. A marker you can only see once you are already looking at the thing it marks
        // is not a marker, and a spawn box you can only see from inside the terrain says nothing about
        // the surface under it. Sweeping them also made the same options render differently depending
        // on whether they were set before the build or after it.
        foreach (Node layer in root.GetChildren())
        {
            if (layer.Name == BeaconNode || layer.Name == BoundsNode)
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

    // The walkable faces, one colour per island. Expanded to three vertices per face rather than
    // indexed: islands can meet at a shared vertex without being connected, and a shared vertex cannot
    // carry both their colours. At PEI's 42k faces that is 128k vertices in nineteen meshes.
    private static MeshInstance3D? BuildSurface(NavFlagSurvey flag, Material material, string name)
    {
        int faces = 0;
        foreach (int island in flag.IslandOfTriangle)
            if (island >= 0)
                faces++;
        if (faces == 0)
            return null;

        var vertices = new Vector3[faces * 3];
        var colors = new Color[faces * 3];
        int at = 0;
        for (int triangle = 0; triangle < flag.IslandOfTriangle.Length; triangle++)
        {
            int island = flag.IslandOfTriangle[triangle];
            if (island < 0)
                continue;
            Color colour = IslandColour(island);
            for (int corner = 0; corner < 3; corner++)
            {
                vertices[at] = flag.Flag.Vertices[flag.Flag.Triangles[(triangle * 3) + corner]];
                colors[at] = colour;
                at++;
            }
        }
        return Instance(name, Mesh.PrimitiveType.Triangles, vertices, colors, material);
    }

    // Every stretch of edge with no floor across it: the outline of the walkable world, and — the part
    // worth flying out to see — the outline of anything missing from the middle of it.
    private static MeshInstance3D? BuildRim(NavFlagSurvey flag, Material material, string name)
    {
        if (flag.Rim.Count == 0)
            return null;

        var vertices = new Vector3[flag.Rim.Count * 2];
        var colors = new Color[flag.Rim.Count * 2];
        for (int i = 0; i < flag.Rim.Count; i++)
        {
            vertices[i * 2] = flag.Rim[i].A;
            vertices[(i * 2) + 1] = flag.Rim[i].B;
            colors[i * 2] = RimColour;
            colors[(i * 2) + 1] = RimColour;
        }
        return Instance(name, Mesh.PrimitiveType.Lines, vertices, colors, material);
    }

    // A marker over every island that is not the biggest one on its flag — every patch of navmesh, that
    // is, that nothing can walk to from the rest of the map. Drawn through terrain on purpose: the
    // point is to see where they are from the air, then go and look.
    private static MeshInstance3D? BuildBeacons(IReadOnlyList<NavFlagSurvey> survey, out int drawn,
        out int total)
    {
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        total = 0;
        drawn = 0;
        foreach (NavFlagSurvey flag in survey)
            for (int island = 1; island < flag.Islands.Count; island++)
            {
                total++;
                if (drawn >= MaxBeacons)
                    continue;
                drawn++;
                Vector3 anchor = flag.Islands[island].Anchor;
                Color colour = IslandColour(island) with { A = 1f };
                // Solid for most of its height so it is findable at range, then faded out rather than
                // cut off flat — a hard-topped line at this scale reads as a piece of geometry.
                Vector3 fadeFrom = anchor + new Vector3(0, BeaconHeight * BeaconSolidShare, 0);
                vertices.Add(anchor);
                vertices.Add(fadeFrom);
                colors.Add(colour);
                colors.Add(colour);
                vertices.Add(fadeFrom);
                vertices.Add(anchor + new Vector3(0, BeaconHeight, 0));
                colors.Add(colour);
                colors.Add(colour with { A = 0f });
            }

        return drawn == 0
            ? null
            : Instance(BeaconNode, Mesh.PrimitiveType.Lines, vertices.ToArray(), colors.ToArray(),
                OverlayMaterial(xray: true, unshadedLines: true));
    }

    // The two boxes the game reads for a flag, side by side: the baked navmesh's own extent, and
    // Bounds.dat's copy of it expanded by 64 m, which is where zombies are allowed to spawn. Spawn box
    // reaching well past coloured surface is spawn ground with nothing under it.
    private static MeshInstance3D? BuildBounds(IReadOnlyList<NavFlag> flags,
        IReadOnlyList<NavBound> spawnBounds)
    {
        var vertices = new List<Vector3>();
        var colors = new List<Color>();
        foreach (NavFlag flag in flags)
            AppendBox(vertices, colors, flag.Center, flag.Size, NavmeshBoxColour);
        foreach (NavBound bound in spawnBounds)
            AppendBox(vertices, colors, bound.Center, bound.Size, SpawnBoxColour);
        if (vertices.Count == 0)
            return null;

        return Instance(BoundsNode, Mesh.PrimitiveType.Lines, vertices.ToArray(), colors.ToArray(),
            OverlayMaterial(xray: true, unshadedLines: true));
    }

    private static void AppendBox(List<Vector3> vertices, List<Color> colors, Vector3 centre,
        Vector3 size, Color colour)
    {
        Vector3 half = size * 0.5f;
        Span<Vector3> corner = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
            corner[i] = centre + new Vector3(
                (i & 1) == 0 ? -half.X : half.X,
                (i & 2) == 0 ? -half.Y : half.Y,
                (i & 4) == 0 ? -half.Z : half.Z);
        // The twelve edges as pairs of corner indices: the three axes differ in exactly one bit.
        ReadOnlySpan<int> edges = stackalloc int[]
        {
            0, 1, 2, 3, 4, 5, 6, 7,
            0, 2, 1, 3, 4, 6, 5, 7,
            0, 4, 1, 5, 2, 6, 3, 7,
        };
        foreach (int index in edges)
        {
            vertices.Add(corner[index]);
            colors.Add(colour);
        }
    }

    private static MeshInstance3D Instance(string name, Mesh.PrimitiveType primitive, Vector3[] vertices,
        Color[] colors, Material material)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(primitive, arrays);
        return new MeshInstance3D
        {
            Name = name,
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
        };
    }

    // Unshaded and unlit throughout: this is a diagram over the world, not part of it, and a navmesh
    // that dims at dusk or picks up the sun's colour is harder to read for no gain. Culling is off so
    // the surface is still there when you fly under it, and depth writes are off so the lines drawn on
    // top of it are not fighting it.
    private static StandardMaterial3D OverlayMaterial(bool xray, bool unshadedLines) =>
        new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            NoDepthTest = xray,
            DisableFog = true,
            DisableReceiveShadows = true,
            // Lines draw after the surface they outline; without this the two sort by distance and a
            // rim line flickers in and out along the edge it is drawn on.
            RenderPriority = unshadedLines ? 1 : 0,
        };

    // The main surface of a flag stays a calm, desaturated blue, so the eye is not asked to look at
    // 42 000 faces of anything. Every fragment after it takes a fully saturated colour from a
    // golden-angle walk round the hue circle, which keeps two fragments that touch from sharing one.
    private static Color IslandColour(int index) => index == 0
        ? new Color(0.35f, 0.62f, 0.95f, SurfaceAlpha)
        : Color.FromHsv((0.08f + (index * 0.6180339f)) % 1f, 0.95f, 1f, SurfaceAlpha);

    private static readonly Color RimColour = new(1f, 0.25f, 0.2f, 0.9f);
    private static readonly Color NavmeshBoxColour = new(1f, 0.85f, 0.2f, 0.55f);
    private static readonly Color SpawnBoxColour = new(0.85f, 0.3f, 0.9f, 0.35f);

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
