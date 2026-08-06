using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot;

// Bodies that have been killed and thrown — RagdollTool, for whatever dies.
//
// Deliberately knows nothing about zombies. RagdollTool has three near-identical entry points
// (ragdollZombie, ragdollAnimal, ragdollPlayer) differing only in which prefab they instantiate and which
// clothing they apply; everything else — the lift, the scatter, the force on the spine, the debris
// lifetime — is the same code three times. This is that shared half, and it takes a body someone else
// already built.
//
// What it is NOT is a skeletal ragdoll. The original throws a prefab with a Rigidbody per bone; here the
// body keeps the pose it died in and tumbles as one piece. That is a deliberate stop: per-bone physics
// needs a PhysicalBoneSimulator3D built against each skeleton, which is a subsystem rather than a detail,
// and the thing a player reads — the corpse flying off in the direction it was hit, further for a harder
// hit — is entirely in the impulse.
public sealed partial class Ragdolls : Node3D
{
    // GraphicsSettings.effect at HIGH: Random.Range(40, 56) seconds before the debris is destroyed.
    public const float LifetimeSeconds = 48f;
    public const float LifetimeSpread = 8f;

    // How many corpses may lie around at once. Each is a rigid body the physics server steps, so this is
    // a real cost — and the original bounds it too, by destroying debris on the same timer.
    public const int MaxBodies = 16;

    // The capsule a thrown body tumbles as, in metres. A character's own bounds: 2 m tall, and wide
    // enough not to stand on its edge.
    public const float BodyHeight = 2f;
    public const float BodyRadius = 0.35f;

    // The original's AddForce is against a ragdoll of a particular Unity mass, and Godot's ApplyImpulse
    // is not the same operator on the same body — so this converts rather than pretends. It is the one
    // number here chosen by eye instead of read off the source, and it is a single scalar precisely so
    // that everything the player can read (the direction, and harder hits throwing further) still comes
    // from the game's own arithmetic.
    public const float ImpulseScale = 0.02f;

    private readonly RandomNumberGenerator _rng = new();
    private readonly List<(RigidBody3D Body, double Expiry)> _bodies = new();

    public Ragdolls() => Name = "Ragdolls";

    // Throws `body` from where it stands. Takes OWNERSHIP: the node is reparented under a rigid body of
    // this pool's making, and freed with it.
    //
    // `impulse` is RagdollForce.Thrown's output — already lifted, scattered and scaled. Zero is a body
    // that simply drops, which is what a death with no direction behind it does.
    public void Throw(Node3D body, Vector3 impulse)
    {
        ArgumentNullException.ThrowIfNull(body);

        Retire();
        var rigid = new RigidBody3D
        {
            GlobalTransform = body.GlobalTransform,
            // Corpses collide with the world and with nothing else. The original's debris is on its own
            // layer for the same reason: a pile of them must not push a player around or block a shot.
            CollisionLayer = 0,
            CollisionMask = CollisionLayers.World,
        };
        rigid.AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Height = BodyHeight, Radius = BodyRadius },
            Position = new Vector3(0f, BodyHeight * 0.5f, 0f),
        });
        AddChild(rigid);

        // Reparented rather than copied: it is already the right mesh in the right pose, and building a
        // second one would mean importing the character again to throw it away in a minute.
        body.GetParent()?.RemoveChild(body);
        rigid.AddChild(body);
        body.Position = Vector3.Zero;
        body.Rotation = Vector3.Zero;

        if (impulse != Vector3.Zero)
            rigid.ApplyImpulse(impulse * ImpulseScale, new Vector3(0f, BodyHeight * 0.5f, 0f));

        _bodies.Add((rigid, (Time.GetTicksMsec() / 1000.0) + LifetimeSeconds
            + _rng.RandfRange(-LifetimeSpread, LifetimeSpread)));
    }

    // Frees whatever has outstayed its lifetime, then the oldest bodies until the pool is inside its
    // bound. Called on each throw rather than every frame: nothing changes in between.
    private void Retire()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        for (int i = _bodies.Count - 1; i >= 0; i--)
        {
            if (_bodies[i].Expiry > now)
                continue;
            _bodies[i].Body.QueueFree();
            _bodies.RemoveAt(i);
        }

        // The list is in throw order, so the front is the oldest.
        while (_bodies.Count >= MaxBodies)
        {
            _bodies[0].Body.QueueFree();
            _bodies.RemoveAt(0);
        }
    }

    // How many corpses are lying around. Read by the tests and the diagnostics.
    public int Count => _bodies.Count;
}
