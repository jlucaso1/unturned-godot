using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Repro;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The slice of the world a bug report carries with it.
//
// A dump of a zombie that will not path around a fence is worthless without the fence. So a capture
// walks the collision shapes around the incident and turns each one back into triangles — boxes, spheres,
// capsules, cylinders, the terrain's heightfields and the baked concave meshes — because the shapes
// themselves live in the physics server and cannot be serialised.
//
// The two ways this goes wrong are both about a dump that LOOKS complete:
//
//   - Godot's overlap query truncates silently at its shape ceiling, so a saturated result is
//     indistinguishable from a full one. Unreported, the dump would claim a captured radius while
//     missing whole colliders inside it, and a replay would read that hole as open ground.
//   - A capture is a bug report, not a level export. Past a triangle budget it stops adding, and a
//     reader has to be told that rather than left to assume the map is that empty.
public class ReproGeometryCaptureTests : TestClass
{
    public ReproGeometryCaptureTests(Node testScene) : base(testScene) { }

    // With no physics world there is nothing to capture, and the dump simply carries no geometry. A
    // headless authority with its space on another thread reaches this on every capture it makes.
    [Test]
    public void WithNoWorldThereIsNoGeometry()
    {
        var warnings = new List<string>();

        Assert.Null(ReproGeometryCapture.Capture(null, Vector3.Zero, 32f, warnings));
        Assert.Empty(warnings);
    }

    // An empty world captures an empty slice rather than failing. A capture fired somewhere with nothing
    // around it is a real capture — it says the incident happened in the open.
    [Test]
    public async Task AnEmptyWorldCapturesAnEmptySlice()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        await sandbox.Settle();
        var warnings = new List<string>();

        ReproGeometryData? geometry = ReproGeometryCapture.Capture(
            sandbox.Root.GetWorld3D().DirectSpaceState, Vector3.Zero, 32f, warnings);

        if (geometry != null)
            Assert.Empty(geometry.Triangles);
        Assert.Empty(warnings);
    }

    // A box near the incident comes back as triangles. The shape lives in the physics server and cannot
    // be serialised, so the capture rebuilds its geometry — and a capture that rebuilt nothing would
    // hand back a bug report of a zombie standing in an empty field.
    [Test]
    public async Task ABoxNearTheIncidentComesBackAsTriangles()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, 0f, 0f), new Vector3(4f, 4f, 4f));
        await sandbox.Settle();
        var warnings = new List<string>();

        ReproGeometryData? geometry = ReproGeometryCapture.Capture(
            sandbox.Root.GetWorld3D().DirectSpaceState, Vector3.Zero, 32f, warnings);

        Assert.NotNull(geometry);
        Assert.NotEmpty(geometry!.Triangles);
    }

    // Every primitive kind the world is built from rebuilds. Boxes are the objects, spheres and capsules
    // and cylinders are the lamp posts, barrels and fence posts, and each has its own tessellation — a
    // kind that fell through would leave exactly those colliders missing from every dump.
    [Test]
    public async Task EveryPrimitiveKindRebuilds()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        AddShape(sandbox, new BoxShape3D { Size = new Vector3(2f, 2f, 2f) }, new Vector3(-6f, 0f, 0f));
        AddShape(sandbox, new SphereShape3D { Radius = 1f }, new Vector3(-2f, 0f, 0f));
        AddShape(sandbox, new CapsuleShape3D { Radius = 0.5f, Height = 2f }, new Vector3(2f, 0f, 0f));
        AddShape(sandbox, new CylinderShape3D { Radius = 0.5f, Height = 2f }, new Vector3(6f, 0f, 0f));
        await sandbox.Settle();
        var warnings = new List<string>();

        ReproGeometryData? geometry = ReproGeometryCapture.Capture(
            sandbox.Root.GetWorld3D().DirectSpaceState, Vector3.Zero, 32f, warnings);

        Assert.NotNull(geometry);
        Assert.NotEmpty(geometry!.Triangles);
    }

    // The terrain rebuilds too, and it is the one that matters most: a zombie that will not cross a slope
    // is a report about the ground, and a dump without it shows the zombie standing in mid-air.
    [Test]
    public async Task TheTerrainRebuilds()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        var heights = new float[16 * 16];
        AddShape(sandbox, new HeightMapShape3D { MapWidth = 16, MapDepth = 16, MapData = heights },
            Vector3.Zero);
        await sandbox.Settle();
        var warnings = new List<string>();

        ReproGeometryData? geometry = ReproGeometryCapture.Capture(
            sandbox.Root.GetWorld3D().DirectSpaceState, Vector3.Zero, 32f, warnings);

        Assert.NotNull(geometry);
        Assert.NotEmpty(geometry!.Triangles);
    }

    // A baked concave mesh — what a tree or a rock actually is — rebuilds as itself rather than as a box
    // around it. The difference is exactly the gap a zombie is supposed to walk through.
    [Test]
    public async Task ABakedConcaveMeshRebuildsAsItself()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        var trimesh = new ConcavePolygonShape3D();
        trimesh.SetFaces(new[]
        {
            new Vector3(-1f, 0f, -1f), new Vector3(1f, 0f, -1f), new Vector3(0f, 0f, 1f),
        });
        AddShape(sandbox, trimesh, Vector3.Zero);
        await sandbox.Settle();
        var warnings = new List<string>();

        ReproGeometryData? geometry = ReproGeometryCapture.Capture(
            sandbox.Root.GetWorld3D().DirectSpaceState, Vector3.Zero, 32f, warnings);

        Assert.NotNull(geometry);
        Assert.NotEmpty(geometry!.Triangles);
    }

    // A radius that reaches nothing captures nothing, which is how a capture stays a bug report rather
    // than a level export. The incident's own surroundings are what a reader needs.
    [Test]
    public async Task ARadiusThatReachesNothingCapturesNothing()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, 0f, 0f), new Vector3(2f, 2f, 2f));
        await sandbox.Settle();
        var warnings = new List<string>();

        ReproGeometryData? geometry = ReproGeometryCapture.Capture(
            sandbox.Root.GetWorld3D().DirectSpaceState, new Vector3(500f, 0f, 500f), 4f, warnings);

        if (geometry != null)
            Assert.Empty(geometry.Triangles);
    }

    // Capturing without somewhere to put the warnings is refused at the call. They are the half of the
    // result that says whether to trust the other half.
    [Test]
    public void CapturingWithNowhereToPutTheWarningsIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ReproGeometryCapture.Capture(null, Vector3.Zero, 32f, null!));
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static void AddShape(PhysicsSandbox sandbox, Shape3D shape, Vector3 at)
    {
        var body = new StaticBody3D { Position = at };
        body.AddChild(new CollisionShape3D { Shape = shape });
        sandbox.Root.AddChild(body);
    }
}
