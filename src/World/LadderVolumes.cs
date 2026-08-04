using System.Collections.Generic;
using Godot;
using UnturnedGodot.Player;

namespace UnturnedGodot;

// Every ladder in the built world, as one PhysicsServer body per placed ladder volume on
// CollisionLayers.Ladder — nothing collides with them, they exist to be raycast against.
//
// A body per volume rather than one body holding all of them, because the climb rules need the volume's
// own frame back: Unturned reads `ladder.collider.transform` for the axis the ladder faces along and the
// line its rungs run up, and a batched body would hand back only the shape that was hit. That is also why
// this cannot ride along in InstancedStaticBodies — same RID trick, different thing to look up.
//
// Ladders are rare enough for this to be free: 45 volumes on PEI, against ~4300 placed objects.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class LadderVolumes : Node3D
{
    private readonly struct Pending
    {
        public required Shape3D Shape { get; init; }
        public required Transform3D Placement { get; init; }
        public required Transform3D Frame { get; init; }
    }

    private List<Pending> _pending = new();
    private readonly Dictionary<Rid, Transform3D> _frames = new();
    // Each volume owns its shape — BuildLadderVolumes deliberately does not use the shared collision
    // pool — and Shape3D is RefCounted, so a reference has to outlive the body that holds its RID.
    private readonly List<Shape3D> _retained = new();

    public int Count => _frames.Count > 0 ? _frames.Count : _pending.Count;

    // `frame` is the ladder collider's own world transform: its origin is the ladder's position and its
    // Y axis the direction it faces. `placement` is where the shape goes, which is not the same thing —
    // a Unity collider carries its own centre offset on top of its GameObject's transform.
    public void Add(Shape3D shape, Transform3D placement, Transform3D frame) =>
        _pending.Add(new Pending { Shape = shape, Placement = placement, Frame = frame });

    public override void _Ready()
    {
        Rid space = GetWorld3D().Space;
        foreach (Pending volume in _pending)
        {
            _retained.Add(volume.Shape);
            Rid body = PhysicsServer3D.BodyCreate();
            PhysicsServer3D.BodySetMode(body, PhysicsServer3D.BodyMode.Static);
            PhysicsServer3D.BodySetCollisionLayer(body, CollisionLayers.Ladder);
            PhysicsServer3D.BodySetCollisionMask(body, 0); // it is queried, it never queries
            PhysicsServer3D.BodyAttachObjectInstanceId(body, GetInstanceId());
            PhysicsServer3D.BodyAddShape(body, volume.Shape.GetRid(), volume.Placement);
            PhysicsServer3D.BodySetSpace(body, space);
            _frames[body] = volume.Frame;
        }
        _pending = new List<Pending>();
    }

    public bool TryGetFrame(Rid body, out Transform3D frame) => _frames.TryGetValue(body, out frame);

    // A ray result from a climb probe, read back as the contact the ladder rules take. False when the ray
    // hit something that is not a ladder — which is the answer that matters: the first thing the probe
    // meets decides, so a wall in front of a ladder means there is no ladder to climb.
    public static bool TryResolve(Godot.Collections.Dictionary hit, out LadderContact contact)
    {
        contact = default;
        if (hit.Count == 0
            || !hit.TryGetValue("collider", out Variant collider)
            || collider.As<Node>() is not LadderVolumes volumes
            || !hit.TryGetValue("rid", out Variant rid)
            || !volumes.TryGetFrame(rid.As<Rid>(), out Transform3D frame))
        {
            return false;
        }

        contact = new LadderContact((Vector3)hit["position"], (Vector3)hit["normal"], frame);
        return true;
    }

    public override void _ExitTree()
    {
        foreach (Rid body in _frames.Keys)
            if (body.IsValid) PhysicsServer3D.FreeRid(body);
        _frames.Clear();
        _retained.Clear();
        _pending.Clear();
    }
}
