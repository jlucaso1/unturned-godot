using System.Collections.Generic;
using Godot;

namespace UnturnedGodot;

// A single static physics body holding many instanced shapes, driven directly through PhysicsServer3D. One
// StaticBody3D node per instance would explode the node count, and adding shapes to a StaticBody3D via the
// server is wiped when the node syncs its (absent) CollisionShape3D children. So we own a raw static body:
// created once in _Ready (in the tree, so a space exists), every instance's shape added by RID, freed on exit.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class InstancedStaticBody : Node3D
{
    // The distinct shapes this body uses, kept referenced so their RIDs stay valid.
    public IReadOnlyList<Shape3D> Shapes { get; set; } = System.Array.Empty<Shape3D>();

    // Each instance: which shape (index into Shapes) and its world transform.
    public IReadOnlyList<(int Shape, Transform3D Transform)> Placements { get; set; }
        = System.Array.Empty<(int, Transform3D)>();

    private Rid _body;

    public override void _Ready()
    {
        _body = PhysicsServer3D.BodyCreate();
        PhysicsServer3D.BodySetMode(_body, PhysicsServer3D.BodyMode.Static);
        PhysicsServer3D.BodySetSpace(_body, GetWorld3D().Space);
        PhysicsServer3D.BodySetCollisionLayer(_body, 1); // the layer the player + rays collide with
        PhysicsServer3D.BodySetCollisionMask(_body, 1);
        foreach ((int shape, Transform3D transform) in Placements)
            PhysicsServer3D.BodyAddShape(_body, Shapes[shape].GetRid(), transform);
    }

    public override void _ExitTree()
    {
        if (_body.IsValid)
            PhysicsServer3D.FreeRid(_body);
        _body = default;
    }
}
