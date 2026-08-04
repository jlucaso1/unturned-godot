using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using UnturnedGodot.Repro;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Repro;

// What the dump's geometry has to behave like: the host's move resolver, without an engine. These are
// the behaviours a replay of MODIFIED code depends on — a body that walks through a wall here would
// "fix" any stuck-zombie bug by deleting the obstacle.
public class ReproCollisionWorldTests
{
    private const float Radius = BakedNavGraph.AgentRadius;

    private static ReproCollisionWorld World(ReproWorlds.Geometry geometry,
        ReproHeightPatch? ground = null) => new(
            ReproTriangles.From(geometry.Build(Vector3.Zero, 512f))!, ReproHeightSampler.From(ground));

    // A wall across x = 10, tall enough to matter to a capsule that spans 0.4..2.0 m.
    private static ReproWorlds.Geometry Wall() => new ReproWorlds.Geometry().Ground()
        .Box("wall", new Vector3(10f, 1.5f, 0f), new Vector3(0.25f, 1.5f, 20f));

    [Fact]
    public void OpenGround_DeliversTheWholeStep()
    {
        ReproCollisionWorld world = World(new ReproWorlds.Geometry().Ground());
        Vector3 to = new(1f, 0f, 0f);
        Assert.True(world.Resolve(Vector3.Zero, to, Radius).IsEqualApprox(to));
    }

    [Fact]
    public void AWall_StopsTheBodyInFrontOfIt()
    {
        ReproCollisionWorld world = World(Wall());
        // The wall's face is at x = 9.75, so a 0.4 m body stops with its centre at 9.35.
        Vector3 result = world.Resolve(new Vector3(9f, 0f, 0f), new Vector3(11f, 0f, 0f), Radius);
        Assert.Equal(9.35f, result.X, 2);
        Assert.Equal("wall", world.LastBlocker);
    }

    // Pushing into a wall at an angle slides along it — the behaviour that lets a crowd work itself
    // around an obstacle instead of grinding in place.
    [Fact]
    public void AGlancingStep_SlidesAlongTheWall()
    {
        ReproCollisionWorld world = World(Wall());
        Vector3 from = new(9f, 0f, 0f);
        Vector3 result = world.Resolve(from, from + new Vector3(0.5f, 0f, 0.5f), Radius);
        Assert.True(result.Z > 0.3f, $"no sliding happened: {result}");
        Assert.True(result.X <= 9.36f, $"passed into the wall: {result}");
    }

    // A kerb inside the CharacterController's step offset is climbed; a wall is not. (Anything under
    // 0.4 m never touches the capsule at all — it starts at the knees — and the ground snap lifts the
    // body over it, which is how the host models a CharacterController's grounding pass.)
    [Fact]
    public void AStepIsClimbedAndAWallIsNot()
    {
        var kerb = new ReproWorlds.Geometry().Ground()
            .Box("kerb", new Vector3(2f, 0.225f, 0f), new Vector3(1f, 0.225f, 8f));
        ReproCollisionWorld world = World(kerb);
        Vector3 climbed = world.Resolve(new Vector3(0.55f, 0f, 0f), new Vector3(1.05f, 0f, 0f), Radius);
        Assert.True(climbed.X > 1f, $"the step blocked the body: {climbed}");
        Assert.Equal(0.45f, climbed.Y, 2);

        ReproCollisionWorld wall = World(Wall());
        Vector3 stopped = wall.Resolve(new Vector3(9f, 0f, 0f), new Vector3(9.45f, 0f, 0f), Radius);
        Assert.Equal(9.35f, stopped.X, 2);
        Assert.Equal(0f, stopped.Y, 3);
    }

    [Fact]
    public void ADoorway_LetsTheBodyThrough()
    {
        var doorway = new ReproWorlds.Geometry().Ground()
            .Box("left", new Vector3(10f, 1.5f, -3f), new Vector3(0.25f, 1.5f, 2.5f))
            .Box("right", new Vector3(10f, 1.5f, 3f), new Vector3(0.25f, 1.5f, 2.5f));
        ReproCollisionWorld world = World(doorway);
        Vector3 result = world.Resolve(new Vector3(9f, 0f, 0f), new Vector3(10.5f, 0f, 0f), Radius);
        Assert.True(result.X > 10.4f, $"the doorway did not open: {result}");
    }

    // Megas are nearly twice as wide, and the gap they do not fit through is the point of the radius
    // travelling with the query.
    [Fact]
    public void AWideBodyDoesNotFitAThinGap()
    {
        var gap = new ReproWorlds.Geometry().Ground()
            .Box("left", new Vector3(10f, 1.5f, -1.5f), new Vector3(0.25f, 1.5f, 1f))
            .Box("right", new Vector3(10f, 1.5f, 1.5f), new Vector3(0.25f, 1.5f, 1f));
        ReproCollisionWorld world = World(gap);
        Assert.True(world.Resolve(new Vector3(9f, 0f, 0f), new Vector3(10.5f, 0f, 0f), 0.4f).X > 10.4f);
        Assert.True(world.Resolve(new Vector3(9f, 0f, 0f), new Vector3(10.5f, 0f, 0f), 0.75f).X < 10f);
    }

    [Fact]
    public void AZeroLengthStepGoesNowhere()
    {
        ReproCollisionWorld world = World(Wall());
        Assert.Equal(Vector3.Zero, world.Resolve(Vector3.Zero, Vector3.Zero, Radius));
    }

    [Fact]
    public void GroundSnap_FindsTheSurfaceUnderfoot()
    {
        var floor = new ReproWorlds.Geometry().Ground()
            .Box("porch", new Vector3(4f, 0.25f, 0f), new Vector3(2f, 0.25f, 2f));
        ReproCollisionWorld world = World(floor);
        Assert.True(world.GroundSnap(new Vector3(4f, 0.4f, 0f), out float porch));
        Assert.Equal(0.5f, porch, 2);
        Assert.True(world.GroundSnap(new Vector3(20f, 0f, 0f), out float open));
        Assert.Equal(0f, open, 3);
    }

    [Fact]
    public void NavSurfaceProbeUsesTheCapturedColliderThenGround()
    {
        var geometry = new ReproWorlds.Geometry().Ground()
            .Box("sill", new Vector3(4f, 0.5f, 0f), new Vector3(1f, 0.5f, 1f));
        ReproCollisionWorld world = World(geometry);

        Assert.True(world.ProbeNavSurface(new Vector3(4f, 0f, 0f), 0.5f, out float sill));
        Assert.Equal(1f, sill, 2);
        Assert.True(world.ProbeNavSurface(new Vector3(8f, 0f, 0f), 0.5f, out float ground));
        Assert.Equal(0f, ground, 2);
    }

    // Off the slice entirely: the heightfield patch answers, and where there is none, nothing does.
    [Fact]
    public void GroundSnap_FallsBackToTheHeightPatch()
    {
        var patch = ReproCapture.SampleGround(
            (float x, float z, out float y) =>
            {
                y = 3f;
                return true;
            },
            new Vector3(100f, 0f, 100f), radius: 8f, cell: 1f);
        // A slice with geometry somewhere else entirely: the probe finds no triangle and the patch
        // is what answers.
        var elsewhere = new ReproWorlds.Geometry();
        elsewhere.Triangle("far", CollisionLayers.World, new Vector3(-300f, 0f, -300f),
            new Vector3(-299f, 0f, -300f), new Vector3(-300f, 0f, -299f));
        ReproCollisionWorld world = World(elsewhere, patch);
        Assert.True(world.GroundSnap(new Vector3(100f, 3f, 100f), out float y));
        Assert.Equal(3f, y, 3);
        Assert.False(world.GroundSnap(new Vector3(500f, 0f, 500f), out _));
    }

    [Fact]
    public void Vision_IsBlockedByObjectCollidersAndNotByTerrain()
    {
        // The pole carries the vision-blocker bit, the ground does not (terrain never blocks a zombie's
        // alert ray in the original either).
        var geometry = new ReproWorlds.Geometry().Ground()
            .Box("pole", new Vector3(5f, 1.5f, 0f), new Vector3(0.5f, 1.5f, 0.5f));
        ReproCollisionWorld world = World(geometry);
        Assert.True(world.VisionBlocked(new Vector3(0f, 1f, 0f), new Vector3(10f, 1f, 0f)));
        Assert.False(world.VisionBlocked(new Vector3(0f, 1f, 5f), new Vector3(10f, 1f, 5f)));
        // The ray stops short of the target, so a blocker at the very end does not count.
        Assert.False(world.VisionBlocked(new Vector3(0f, 1f, 0f), new Vector3(4.6f, 1f, 0f)));
    }

    [Fact]
    public void PhysicalLine_SeesTheFinalFivePercentAndWorldOnlyGeometry()
    {
        var geometry = new ReproWorlds.Geometry()
            .Box("near-target fence", new Vector3(9.8f, 1f, 0f),
                new Vector3(0.1f, 1f, 1f))
            .Box("world resource", new Vector3(5f, 1f, 3f),
                new Vector3(0.1f, 1f, 1f), CollisionLayers.World);
        ReproCollisionWorld world = World(geometry);

        Vector3 from = new(0f, 1f, 0f), to = new(10f, 1f, 0f);
        Assert.False(world.VisionBlocked(from, to));
        Assert.True(world.PhysicalLineBlocked(from, to));
        Assert.False(world.VisionBlocked(new Vector3(from.X, from.Y, 3f),
            new Vector3(to.X, to.Y, 3f)));
        Assert.True(world.PhysicalLineBlocked(new Vector3(from.X, from.Y, 3f),
            new Vector3(to.X, to.Y, 3f)));
    }

    [Fact]
    public void ARayOfNoLengthHitsNothing()
    {
        ReproCollisionWorld world = World(Wall());
        Assert.False(world.Raycast(Vector3.Zero, Vector3.Zero, CollisionLayers.World, out _, out _));
    }

    // MEDIUM furniture is on its own layer: a zombie shoves through it, but still stands on it.
    [Fact]
    public void FurnitureDoesNotBlockMovementButIsStandableGround()
    {
        var geometry = new ReproWorlds.Geometry().Ground()
            .Box("bench", new Vector3(5f, 0.3f, 0f), new Vector3(1f, 0.3f, 1f),
                CollisionLayers.MediumFurniture | CollisionLayers.VisionBlocker);
        ReproCollisionWorld world = World(geometry);
        Vector3 result = world.Resolve(new Vector3(3.5f, 0f, 0f), new Vector3(4.5f, 0f, 0f), Radius);
        Assert.True(result.X > 4.4f, $"furniture stopped the body: {result}");
        Assert.True(world.GroundSnap(new Vector3(5f, 0.5f, 0f), out float y));
        Assert.Equal(0.6f, y, 2);
    }

    [Fact]
    public void AMediumFence_BlocksZombieMovementAndVision()
    {
        DatDictionary data = DatParser.Parse(
            "GUID 40921a1a3cd742f69cc25cc25b856572\nType Medium\nID 2\n");
        Assert.True(ObjectAsset.TryParse(data, null, out ObjectAsset? fence));
        fence.Directory = "/Bundles/Objects/Medium/Fences/Fence_Wood_0";
        uint layer = ObjectCollisionPolicy.PhysicsLayer(fence);

        var geometry = new ReproWorlds.Geometry().Ground()
            .Box("Fence_Wood_0", new Vector3(0f, 1.1f, 0f),
                new Vector3(20f, 1.1f, 0.1f), layer);
        ReproCollisionWorld world = World(geometry);

        Vector3 stopped = world.Resolve(new Vector3(0f, 0f, -1f),
            new Vector3(0f, 0f, 1f), Radius);
        Assert.True(stopped.Z < -0.45f, $"the zombie crossed the fence: {stopped}");
        Assert.Equal("Fence_Wood_0", world.LastBlocker);
        Assert.True(world.VisionBlocked(new Vector3(0f, 1f, -1f),
            new Vector3(0f, 1f, 1f)));
    }
}
