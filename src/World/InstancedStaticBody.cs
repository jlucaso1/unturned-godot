using System.Collections.Generic;
using Godot;

namespace UnturnedGodot;

// A single static physics body holding many instanced shapes, driven directly through PhysicsServer3D. One
// StaticBody3D node per instance would explode the node count, and adding shapes to a StaticBody3D via the
// server is wiped when the node syncs its (absent) CollisionShape3D children. So we own a raw static body:
// created once in _Ready (in the tree, so a space exists), every instance's shape added by RID, freed on exit.
public partial class InstancedStaticBody : Node3D
{
    // The distinct shapes this body uses, kept referenced so their RIDs stay valid.
    public IReadOnlyList<Shape3D> Shapes { get; set; } = System.Array.Empty<Shape3D>();

    // Each instance: which shape (index into Shapes), its world transform, and which surface it is made of.
    public IReadOnlyList<CollisionPlacement> Placements { get; set; }
        = System.Array.Empty<CollisionPlacement>();

    // The physics-material names the placements' surface bytes index into (1-based; 0 is "no material").
    // Shared with every other body from the same builder.
    public IReadOnlyList<string> SurfaceNames { get; set; } = System.Array.Empty<string>();

    // Which surface each added shape is made of, in BodyAddShape order — the same index a raycast reports.
    // Null when no collider on this body named a material.
    private byte[]? _surfaces;

    // Layer bit 1 is the solid world (player movement, camera rays). Callers add extra bits for
    // Unturned layer semantics — e.g. LARGE/MEDIUM objects carry the vision-blocker bit so alert
    // raycasts can hit exactly what RayMasks.BLOCK_VISION masks in the original.
    public uint CollisionLayer { get; set; } = 1;

    private Rid _body;

    public override void _Ready()
    {
        _body = PhysicsServer3D.BodyCreate();
        PhysicsServer3D.BodySetMode(_body, PhysicsServer3D.BodyMode.Static);
        PhysicsServer3D.BodySetCollisionLayer(_body, CollisionLayer);
        PhysicsServer3D.BodySetCollisionMask(_body, 1);
        // Point the body back at this node, so a ray/shape hit resolves to a real object instead of a
        // bare RID. A StaticBody3D does this for you; a server-owned body does not, and without it every
        // query result on the object world is anonymous — which made the collision diagnostics report
        // "?" as the owner and left no way to get from a bad surface back to the GUID that produced it.
        PhysicsServer3D.BodyAttachObjectInstanceId(_body, GetInstanceId());

        // Shapes go on BEFORE the body joins a space. Each body_add_shape on a body that is already in a
        // space makes the physics server re-register it with the broadphase, so adding thousands of shapes
        // that way costs far more than adding them first and joining once
        // (godotengine/godot#24026).
        for (int i = 0; i < Placements.Count; i++)
        {
            CollisionPlacement placement = Placements[i];
            PhysicsServer3D.BodyAddShape(_body, Shapes[placement.Shape].GetRid(), placement.Transform);
            if (placement.Material == 0 && _surfaces == null)
                continue;
            _surfaces ??= new byte[Placements.Count];
            _surfaces[i] = placement.Material;
        }

        PhysicsServer3D.BodySetSpace(_body, GetWorld3D().Space);
        // PhysicsServer copied every shape RID/transform above. Keeping thousands of managed tuples on
        // every body serves no later query and inflated steady-state RAM for the whole session. The
        // surface bytes survive, at one byte per shape, because they answer a question the server cannot.
        if (!EnvFlag.IsOn(OS.GetEnvironment("UG_KEEP_PHYSICS_PLACEMENTS"), whenUnset: false))
            Placements = System.Array.Empty<CollisionPlacement>();
    }

    // The physics-material name of one shape on this body, or empty when it carries none. Mirrors
    // InstancedStaticBodies.MaterialFor, for the one-node-per-body mode (UG_NODE_PHYSICS=1).
    public string MaterialFor(int shapeIndex)
    {
        if (_surfaces == null || shapeIndex < 0 || shapeIndex >= _surfaces.Length)
            return string.Empty;
        byte index = _surfaces[shapeIndex];
        return index == 0 || index > SurfaceNames.Count ? string.Empty : SurfaceNames[index - 1];
    }

    public override void _ExitTree()
    {
        if (_body.IsValid)
            PhysicsServer3D.FreeRid(_body);
        _body = default;
    }
}
