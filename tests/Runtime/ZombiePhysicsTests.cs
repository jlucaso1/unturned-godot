using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// What a zombie can see, shoot through, and walk into: the engine half of the zombie brain.
//
// The brain itself is pure and lives in core. This is the part that asks the world, and it draws a line
// the pure side cannot: SEEING and REACHING are different questions against different geometry.
//
// Sight stops at authored LARGE/MEDIUM occluders and deliberately stops SHORT of the target, because a
// ray that ran the whole way would end inside the thing it is looking at and report itself blocked. It is
// perception — not permission to damage, and not a reason to abandon a route.
//
// Reaching inspects the complete eye-to-eye segment against everything solid: terrain, resources, and the
// fences the collision policy promotes. Players sit on their own bit precisely so this ray cannot end
// inside the player's own capsule and report that it cannot reach them.
//
// Every query resolves its world LAZILY. The server builds its bodies before the node is in the tree, so
// a resolver that read the world at attach time would bind to nothing on every session.
public class ZombiePhysicsTests : TestClass
{
    public ZombiePhysicsTests(Node testScene) : base(testScene) { }

    // Before a world exists, nothing is blocked and nothing is deflected. This is the state a server is
    // in between attaching its zombies and entering the tree — and answering "blocked" there would have
    // every zombie start the session believing it is walled in.
    [Test]
    public void WithNoWorldYetNothingIsBlocked()
    {
        ZombieSystem zombies = System();
        ZombiePhysics.Attach(zombies, () => null, FlatGround);

        Assert.False(zombies.VisionBlocked!(Vector3.Zero, new Vector3(10f, 0f, 0f)));
        Assert.False(zombies.PhysicalLineBlocked!(Vector3.Zero, new Vector3(10f, 0f, 0f)));

        // And a move with nowhere to move in goes exactly where it was asked to.
        var to = new Vector3(2f, 0f, 0f);
        Assert.Equal(to, zombies.MoveResolver!(Vector3.Zero, to, 0.3f));
    }

    // Attaching to nothing is refused at the call rather than at the first tick, when the failure would
    // arrive as a null delegate inside the zombie loop.
    [Test]
    public void AttachingToNothingIsRefusedImmediately()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ZombiePhysics.Attach(null!, () => null, FlatGround));
        Assert.Throws<ArgumentNullException>(() =>
            ZombiePhysics.Attach(System(), null!, FlatGround));
        Assert.Throws<ArgumentNullException>(() =>
            ZombiePhysics.Attach(System(), () => null, null!));
    }

    // Open ground blocks neither sight nor reach.
    [Test]
    public async Task AcrossOpenGroundAZombieSeesAndReaches()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(60f, 1f, 60f), CollisionLayers.World);
        await sandbox.Settle();

        ZombieSystem zombies = Attached(sandbox);

        Assert.False(zombies.VisionBlocked!(Eye(-10f), Eye(10f)));
        Assert.False(zombies.PhysicalLineBlocked!(Eye(-10f), Eye(10f)));
    }

    // A wall on the vision-blocking bit stops both: it is an authored occluder, so it is a building.
    [Test]
    public async Task AWallStopsSightAndReachAlike()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(60f, 1f, 60f), CollisionLayers.World);
        sandbox.AddBox(new Vector3(0f, 2f, 0f), new Vector3(1f, 4f, 20f), CollisionLayers.VisionBlocker);
        await sandbox.Settle();

        ZombieSystem zombies = Attached(sandbox);

        Assert.True(zombies.VisionBlocked!(Eye(-10f), Eye(10f)));
        Assert.True(zombies.PhysicalLineBlocked!(Eye(-10f), Eye(10f)));
    }

    // Terrain and promoted fences are on the WORLD bit, which reach inspects and sight does not. A zombie
    // can see you over a fence it cannot shoot or walk through — that asymmetry is the whole reason the
    // two queries carry different masks rather than sharing one.
    [Test]
    public async Task AZombieCanSeeOverWhatItCannotReachThrough()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(60f, 1f, 60f), CollisionLayers.World);
        sandbox.AddBox(new Vector3(0f, 2f, 0f), new Vector3(1f, 4f, 20f), CollisionLayers.World);
        await sandbox.Settle();

        ZombieSystem zombies = Attached(sandbox);

        Assert.False(zombies.VisionBlocked!(Eye(-10f), Eye(10f)));
        Assert.True(zombies.PhysicalLineBlocked!(Eye(-10f), Eye(10f)));
    }

    // Sight stops short of the target. A ray that ran the whole way would end inside whatever it is
    // looking at and report itself blocked, so a zombie standing next to a player would lose sight of
    // them at exactly the moment it mattered.
    [Test]
    public async Task SightStopsShortOfWhatItIsLookingAt()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(60f, 1f, 60f), CollisionLayers.World);
        // An occluder sitting ON the target, which a full-length ray would strike.
        sandbox.AddBox(new Vector3(10f, 1f, 0f), new Vector3(1f, 4f, 4f), CollisionLayers.VisionBlocker);
        await sandbox.Settle();

        ZombieSystem zombies = Attached(sandbox);

        Assert.False(zombies.VisionBlocked!(Eye(-10f), Eye(10f)),
            "the sight ray reached the target itself and called it an occluder");
    }

    // Movement slides along what it cannot pass rather than stopping dead or tunnelling through. A
    // resolver that returned the requested point would walk zombies through fences; one that returned the
    // start would pin them against every wall they brushed.
    [Test]
    public async Task MovementIsDeflectedByAWallRatherThanCrossingIt()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(60f, 1f, 60f), CollisionLayers.World);
        sandbox.AddBox(new Vector3(0f, 2f, 0f), new Vector3(1f, 4f, 20f), CollisionLayers.World);
        await sandbox.Settle();

        ZombieSystem zombies = Attached(sandbox);

        var from = new Vector3(-2f, 0f, 0f);
        Vector3 resolved = zombies.MoveResolver!(from, new Vector3(2f, 0f, 0f), 0.3f);

        Assert.True(resolved.X < 0f, $"the zombie walked through the wall to {resolved}");
    }

    // And over open ground it goes where it was asked. This is the common tick, and a resolver that
    // deflected here would make every zombie in the map drift.
    [Test]
    public async Task MovementOverOpenGroundIsNotDeflected()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(60f, 1f, 60f), CollisionLayers.World);
        await sandbox.Settle();

        ZombieSystem zombies = Attached(sandbox);

        var to = new Vector3(-9.5f, 0f, 0f);
        Vector3 resolved = zombies.MoveResolver!(new Vector3(-10f, 0f, 0f), to, 0.3f);

        Assert.True(resolved.DistanceTo(to) < 0.2f, $"a clear step was deflected to {resolved}");
    }

    // A navigation probe several metres long may BEGIN inside geometry — the router asks about points it
    // has not walked to — and must not be waved through on the strength of ending somewhere clean. That
    // recovery is what stops a route being planned straight through a fence.
    [Test]
    public async Task ALongProbeStartingInsideGeometryIsNotWavedThrough()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(60f, 1f, 60f), CollisionLayers.World);
        sandbox.AddBox(new Vector3(0f, 2f, 0f), new Vector3(4f, 4f, 20f), CollisionLayers.World);
        await sandbox.Settle();

        ZombieSystem zombies = Attached(sandbox);

        // Starts embedded in the block, aims well past it.
        Vector3 resolved = zombies.MoveResolver!(Vector3.Zero, new Vector3(12f, 0f, 0f), 0.3f);

        Assert.True(resolved.X < 12f, $"a probe starting inside the block was allowed to finish at {resolved}");
    }

    // --- helpers -------------------------------------------------------------------------------------

    // Eye height, at either end of the map's x axis.
    private static Vector3 Eye(float x) => new(x, ZombieBody.CapsuleCenter, 0f);

    private static ZombieSystem System() =>
        new(Array.Empty<ZombieTable>(), Array.Empty<NavBound>(), FlatGround);

    private static ZombieSystem Attached(PhysicsSandbox sandbox)
    {
        ZombieSystem zombies = System();
        ZombiePhysics.Attach(zombies, () => sandbox.Root.GetWorld3D(), FlatGround);
        return zombies;
    }

    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }
}
