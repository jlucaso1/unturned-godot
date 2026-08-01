using System.Collections.Generic;
using Godot;

namespace UnturnedGodot;

// One lifecycle node for many independent PhysicsServer bodies. Spatial partitioning still creates the
// same RIDs with the same shapes, layers and transforms; only the otherwise-empty Node3D wrapper per body
// disappears. The RID/name table preserves collision diagnostics without changing query ownership.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class InstancedStaticBodies : Node3D
{
    private sealed class Definition
    {
        public required string Name;
        public required IReadOnlyList<Shape3D> Shapes;
        public required IReadOnlyList<(int Shape, Transform3D Transform)> Placements;
        public required uint CollisionLayer;
    }

    private List<Definition> _definitions = new();
    private readonly Dictionary<Rid, string> _names = new();
    // A body definition refers to an immutable shared shape pool. Retaining each pool once is enough to
    // keep every Shape3D RID alive and is considerably smaller than a HashSet entry per individual shape.
    private readonly HashSet<IReadOnlyList<Shape3D>> _retainedShapePools =
        new(ReferenceEqualityComparer.Instance);
    private int _bodyCount;
    public int BodyCount => _bodyCount > 0 ? _bodyCount : _definitions.Count;

    public void Add(string name, IReadOnlyList<Shape3D> shapes,
        IReadOnlyList<(int Shape, Transform3D Transform)> placements, uint collisionLayer) =>
        _definitions.Add(new Definition
        {
            Name = name,
            Shapes = shapes,
            Placements = placements,
            CollisionLayer = collisionLayer,
        });

    public override void _Ready()
    {
        Rid space = GetWorld3D().Space;
        int uploadedDefinitions = _definitions.Count;
        foreach (Definition definition in _definitions)
        {
            _retainedShapePools.Add(definition.Shapes);
            Rid body = PhysicsServer3D.BodyCreate();
            PhysicsServer3D.BodySetMode(body, PhysicsServer3D.BodyMode.Static);
            PhysicsServer3D.BodySetCollisionLayer(body, definition.CollisionLayer);
            PhysicsServer3D.BodySetCollisionMask(body, 1);
            PhysicsServer3D.BodyAttachObjectInstanceId(body, GetInstanceId());
            foreach ((int shape, Transform3D transform) in definition.Placements)
            {
                PhysicsServer3D.BodyAddShape(body, definition.Shapes[shape].GetRid(), transform);
            }
            PhysicsServer3D.BodySetSpace(body, space); // join once, after every shape
            _names[body] = definition.Name;
            _bodyCount++;
            if (OS.GetEnvironment("UG_KEEP_PHYSICS_PLACEMENTS") != "1")
                definition.Placements = System.Array.Empty<(int, Transform3D)>();
        }
        if (OS.GetEnvironment("UG_KEEP_RID_UPLOAD_METADATA") != "1"
            && OS.GetEnvironment("UG_KEEP_PHYSICS_PLACEMENTS") != "1")
        {
            _definitions = new List<Definition>();
            int retainedShapes = 0;
            foreach (IReadOnlyList<Shape3D> pool in _retainedShapePools)
                retainedShapes += pool.Count;
            Log.Print($"[physics] compacted RID upload metadata: {uploadedDefinitions} definitions -> "
                + $"{_retainedShapePools.Count} shape pool(s) / {retainedShapes} Shape3D resources");
        }
    }

    public string NameFor(Rid body) => _names.GetValueOrDefault(body, Name.ToString());

    public static string ColliderName(Godot.Collections.Dictionary hit)
    {
        Node? collider = hit.TryGetValue("collider", out Variant value) ? value.As<Node>() : null;
        if (collider is InstancedStaticBodies owner && hit.TryGetValue("rid", out Variant rid))
            return owner.NameFor(rid.As<Rid>());
        return collider?.Name.ToString() ?? "?";
    }

    public override void _ExitTree()
    {
        foreach (Rid body in _names.Keys)
            if (body.IsValid) PhysicsServer3D.FreeRid(body);
        _names.Clear();
        _retainedShapePools.Clear();
        _definitions.Clear();
    }
}
