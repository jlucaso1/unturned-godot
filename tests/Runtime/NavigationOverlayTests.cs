using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The navmesh diagnostic overlay: what NAV_XRAY draws, and what the editor dock draws.
//
// It exists because a navmesh failure is invisible in a screenshot — a zombie that cannot leave a rooftop
// looks exactly like a zombie standing still. The survey behind it (islands, rim edges, pockets) is pure
// and unit tested in core/; this is the half that turns it into geometry, and it is shared by the two
// callers on purpose, so the in-game overlay and the editor preview cannot disagree about which triangles
// exist.
//
// Which makes the interesting property a structural one: the same survey has to produce the same layers
// either way, and a layer that is switched off must still be BUILT — the dock toggles them at runtime.
public class NavigationOverlayTests : TestClass
{
    public NavigationOverlayTests(Node testScene) : base(testScene) { }

    // Every layer is built, and the flags decide visibility rather than existence: the editor dock
    // toggles them after the fact, so a layer that was skipped could never be switched on.
    [Test]
    public void EveryLayerIsBuiltAndTheFlagsOnlyDecideVisibility()
    {
        Node3D root = NavigationOverlay.Build(
            new[] { Flag() }, new[] { Survey() }, new[] { Bound() },
            rootName: "NavOverlay", rim: false, beacons: false, bounds: false, xray: false,
            lift: NavigationOverlay.DefaultLift);
        TestScene.AddChild(root);

        Assert.Equal("NavOverlay", root.Name);
        // Lifted off the floor, or it z-fights with the very surface it is drawn to describe.
        Assert.True(root.Position.Y > 0f);

        Node3D? rim = root.GetNodeOrNull<Node3D>(NavigationOverlay.RimNode);
        Node3D? beacons = root.GetNodeOrNull<Node3D>(NavigationOverlay.BeaconNode);
        Node3D? bounds = root.GetNodeOrNull<Node3D>(NavigationOverlay.BoundsNode);

        Assert.NotNull(root.GetNodeOrNull<Node3D>(NavigationOverlay.SurfaceNode));
        Assert.NotNull(rim);
        Assert.NotNull(beacons);
        Assert.NotNull(bounds);
        Assert.False(rim!.Visible);
        Assert.False(beacons!.Visible);
        Assert.False(bounds!.Visible);

        root.QueueFree();
    }

    [Test]
    public void TheFlagsTurnTheLayersOn()
    {
        Node3D root = NavigationOverlay.Build(
            new[] { Flag() }, new[] { Survey() }, new[] { Bound() },
            rootName: "NavOverlay", rim: true, beacons: true, bounds: true, xray: false, lift: 0.55f);
        TestScene.AddChild(root);

        Assert.True(root.GetNode<Node3D>(NavigationOverlay.RimNode).Visible);
        Assert.True(root.GetNode<Node3D>(NavigationOverlay.BeaconNode).Visible);
        Assert.True(root.GetNode<Node3D>(NavigationOverlay.BoundsNode).Visible);

        root.QueueFree();
    }

    // One mesh per flag, with the faces expanded to three vertices each. That expansion is not incidental:
    // disconnected islands can meet at a single authored vertex, and one indexed vertex cannot carry two
    // island colours — so a shared vertex would blend two islands into one colour and hide the split the
    // overlay exists to show.
    [Test]
    public void TheSurfaceIsExpandedSoIslandsKeepTheirOwnColours()
    {
        MeshInstance3D? surface = NavigationOverlay.BuildSurface(Survey(), new StandardMaterial3D(), "Flag0");

        Assert.NotNull(surface);
        Godot.Collections.Array arrays = surface!.Mesh.SurfaceGetArrays(0);
        var vertices = (Vector3[])arrays[(int)Mesh.ArrayType.Vertex];
        var colors = (Color[])arrays[(int)Mesh.ArrayType.Color];

        // Two triangles, three vertices each, no sharing.
        Assert.Equal(6, vertices.Length);
        Assert.Equal(6, colors.Length);
        // The two triangles are on different islands, so their colours must differ.
        Assert.NotEqual(colors[0], colors[3]);

        surface.QueueFree();
    }

    // A flag whose faces were all dropped by collision reconciliation has nothing to draw, and building
    // an empty mesh for it would put an empty MeshInstance3D in the scene for every such flag.
    [Test]
    public void AFlagWithNothingWalkableDrawsNoSurface()
    {
        var empty = new NavFlagSurvey
        {
            Flag = Flag(),
            IslandOfTriangle = new[] { -1, -1 }, // both dropped
            Islands = System.Array.Empty<NavIsland>(),
            Rim = System.Array.Empty<NavRimEdge>(),
            DroppedTriangles = 2,
        };

        Assert.Null(NavigationOverlay.BuildSurface(empty, new StandardMaterial3D(), "Flag0"));
    }

    // The rim is the outer boundary and the edge of every hole — the lines a router will not step over.
    // A survey with no rim edges draws nothing rather than an empty line mesh.
    [Test]
    public void TheRimIsDrawnFromItsEdgesAndSkippedWhenThereAreNone()
    {
        MeshInstance3D? rim = NavigationOverlay.BuildRim(Survey(), new StandardMaterial3D(), "Flag0");
        Assert.NotNull(rim);
        rim!.QueueFree();

        var noRim = new NavFlagSurvey
        {
            Flag = Flag(),
            IslandOfTriangle = new[] { 0, 0 },
            Islands = new[] { new NavIsland(2, 1.0, Vector3.Zero, Vector3.Zero, Vector3.One) },
            Rim = System.Array.Empty<NavRimEdge>(),
            DroppedTriangles = 0,
        };
        Assert.Null(NavigationOverlay.BuildRim(noRim, new StandardMaterial3D(), "Flag0"));
    }

    // X-ray is what makes the overlay usable at all: without it the diagnostic is hidden by the very
    // terrain it is describing, so the one thing you cannot see is the mesh under the building.
    [Test]
    public void XrayDrawsThroughTheWorldAndTheNormalModeDoesNot()
    {
        Node3D through = NavigationOverlay.Build(
            new[] { Flag() }, new[] { Survey() }, System.Array.Empty<NavBound>(),
            "Xray", rim: true, beacons: false, bounds: false, xray: true, lift: 0.55f);
        Node3D occluded = NavigationOverlay.Build(
            new[] { Flag() }, new[] { Survey() }, System.Array.Empty<NavBound>(),
            "Plain", rim: true, beacons: false, bounds: false, xray: false, lift: 0.55f);
        TestScene.AddChild(through);
        TestScene.AddChild(occluded);

        // MaterialOverride, not a surface material: the overlay builds one material per mode and hangs
        // it on every instance, so all the layers of a mode share exactly one.
        var xrayMaterial = (StandardMaterial3D)FirstMesh(through)!.MaterialOverride;
        var plainMaterial = (StandardMaterial3D)FirstMesh(occluded)!.MaterialOverride;

        Assert.True(xrayMaterial.NoDepthTest, "x-ray did not draw through geometry");
        Assert.False(plainMaterial.NoDepthTest);

        through.QueueFree();
        occluded.QueueFree();
    }

    // Beacons mark the pockets — an island small enough to be a defect rather than a place, which is
    // where a zombie gets stuck for the session. They are what you fly to.
    [Test]
    public void BeaconsAreRaisedOverTheSurveyedFlags()
    {
        MeshInstance3D? beacons = NavigationOverlay.BuildBeacons(new[] { Survey() }, out int count, out int _);

        Assert.NotNull(beacons);
        Assert.True(count > 0, "no beacons were raised at all");

        beacons!.QueueFree();
    }

    // Spawn bounds are the zombie regions, drawn as boxes so a region with no navmesh under it is
    // visible as exactly that: a box over bare ground.
    [Test]
    public void SpawnBoundsAreDrawnAsBoxes()
    {
        MeshInstance3D? bounds = NavigationOverlay.BuildBounds(new[] { Flag() }, new[] { Bound() });

        Assert.NotNull(bounds);
        Assert.True(bounds!.Mesh.GetSurfaceCount() > 0, "the bounds mesh has no geometry");

        bounds.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static MeshInstance3D? FirstMesh(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is MeshInstance3D mesh && mesh.Mesh != null && mesh.Mesh.GetSurfaceCount() > 0)
                return mesh;
            if (FirstMesh(child) is { } nested)
                return nested;
        }
        return null;
    }

    // Two triangles sharing an edge, on two different islands — the shape that proves the per-face vertex
    // expansion, since a shared index could not carry both colours.
    private static NavFlag Flag() => new()
    {
        Center = Vector3.Zero,
        Size = new Vector3(64f, 16f, 64f),
        Vertices = new[]
        {
            new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f),
            new Vector3(1f, 0f, 1f),
        },
        Triangles = new[] { 0, 1, 2, 1, 3, 2 },
    };

    private static NavFlagSurvey Survey() => new()
    {
        Flag = Flag(),
        IslandOfTriangle = new[] { 0, 1 }, // deliberately two islands
        Islands = new[]
        {
            new NavIsland(1, 0.5, new Vector3(0.3f, 0f, 0.3f), Vector3.Zero, Vector3.One),
            new NavIsland(1, 0.5, new Vector3(0.7f, 0f, 0.7f), Vector3.Zero, Vector3.One),
        },
        Rim = new[]
        {
            new NavRimEdge(new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f)),
            new NavRimEdge(new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 1f)),
        },
        DroppedTriangles = 0,
    };

    private static NavBound Bound() => new()
    {
        Center = new Vector3(10f, 0f, 10f),
        Size = new Vector3(32f, 16f, 32f),
        MaxZombies = 24,
        SpawnZombies = true,
    };
}
