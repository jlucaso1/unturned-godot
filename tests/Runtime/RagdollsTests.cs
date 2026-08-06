using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Damage;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The pool that owns thrown bodies. Written for whatever dies rather than for zombies specifically —
// RagdollTool's three entry points differ only in the prefab they instantiate — so these tests hand it
// plain nodes, which is the interface a player or an animal will use.
public class RagdollsTests : TestClass
{
    public RagdollsTests(Node testScene) : base(testScene) { }

    private SignalAwaiter NextPhysicsFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);

    // Freed by the harness rather than at the end of each test: a failing assertion throws past a
    // trailing QueueFree, and the pool would then outlive the test and keep stepping bodies under the
    // next one.
    private readonly List<Ragdolls> _pools = new();

    private Ragdolls Pool()
    {
        var pool = new Ragdolls();
        TestScene.AddChild(pool);
        _pools.Add(pool);
        return pool;
    }

    [Cleanup]
    public void FreePools()
    {
        foreach (Ragdolls pool in _pools)
            pool.QueueFree();
        _pools.Clear();
    }

    // A body handed over is reparented under a rigid body of the pool's making, and the pool owns it
    // from then on: nobody else is left holding a node that is about to be freed.
    [Test]
    public void AThrownBodyIsAdoptedByThePool()
    {
        Ragdolls pool = Pool();
        var body = new Node3D { Name = "Corpse" };
        TestScene.AddChild(body);

        pool.Throw(body, Vector3.Zero);

        Assert.Equal(1, pool.Count);
        Assert.IsType<RigidBody3D>(body.GetParent());
        Assert.Equal(pool, body.GetParent()!.GetParent());
    }

    // The body keeps the place it died in. A corpse that teleports to the origin on death is the most
    // visible way this can go wrong, and reparenting is exactly where it would happen.
    [Test]
    public void AThrownBodyKeepsWhereItDied()
    {
        Ragdolls pool = Pool();
        var body = new Node3D { Position = new Vector3(12f, 3f, -40f) };
        TestScene.AddChild(body);

        pool.Throw(body, Vector3.Zero);

        var rigid = (RigidBody3D)body.GetParent()!;
        Assert.Equal(new Vector3(12f, 3f, -40f), rigid.GlobalPosition);
    }

    // The point of the whole feature: a shove actually moves the body, and in the direction it was hit.
    [Test]
    public async Task AShovedBodyMovesInTheDirectionOfTheShove()
    {
        Ragdolls pool = Pool();
        var body = new Node3D();
        TestScene.AddChild(body);

        pool.Throw(body, RagdollForce.Thrown(
            RagdollForce.Impulse(Vector3.Forward, 500), sideways: 0f, forward: 0f));
        var rigid = (RigidBody3D)body.GetParent()!;

        for (int i = 0; i < 5; i++)
            await NextPhysicsFrame();

        Assert.True(rigid.GlobalPosition.Z < -0.1f,
            $"the body did not travel down -Z; it is at {rigid.GlobalPosition}");
    }

    // A body with no shove behind it drops rather than being flung — a death with no direction (a burn,
    // a despawn) leaves the corpse where it stood.
    [Test]
    public async Task AnUnshovedBodyDropsWhereItStood()
    {
        Ragdolls pool = Pool();
        var body = new Node3D();
        TestScene.AddChild(body);

        pool.Throw(body, Vector3.Zero);
        var rigid = (RigidBody3D)body.GetParent()!;

        for (int i = 0; i < 5; i++)
            await NextPhysicsFrame();

        Assert.True(Mathf.Abs(rigid.GlobalPosition.X) < 0.1f);
        Assert.True(Mathf.Abs(rigid.GlobalPosition.Z) < 0.1f);
        // And it DROPS: the name promises a fall, and lateral stillness alone would also pass for a body
        // frozen in mid-air with gravity disabled.
        Assert.True(rigid.GlobalPosition.Y < 0f,
            $"the body did not fall; it is at {rigid.GlobalPosition}");
    }

    // Corpses collide with the world and with nothing else: a pile of them must not push a player
    // around or block a shot, which is why the original's debris is on a layer of its own.
    [Test]
    public void CorpsesCollideWithTheWorldAndNothingElse()
    {
        Ragdolls pool = Pool();
        var body = new Node3D();
        TestScene.AddChild(body);

        pool.Throw(body, Vector3.Zero);
        var rigid = (RigidBody3D)body.GetParent()!;

        Assert.Equal(0u, rigid.CollisionLayer);
        Assert.Equal(CollisionLayers.World, rigid.CollisionMask);
    }

    // The pool is bounded: each corpse is a rigid body the physics server steps every frame, so an
    // unbounded pile is a session that gets slower the longer it runs.
    [Test]
    public void ThePoolIsBounded()
    {
        Ragdolls pool = Pool();

        for (int i = 0; i < Ragdolls.MaxBodies * 3; i++)
        {
            var body = new Node3D();
            TestScene.AddChild(body);
            pool.Throw(body, Vector3.Zero);
        }

        Assert.True(pool.Count <= Ragdolls.MaxBodies,
            $"the pool grew to {pool.Count}, past its bound of {Ragdolls.MaxBodies}");
    }

    [Test]
    public void ThrowingNothingIsRefused()
    {
        Ragdolls pool = Pool();

        Assert.Throws<System.ArgumentNullException>(() => pool.Throw(null!, Vector3.Zero));
    }
}
