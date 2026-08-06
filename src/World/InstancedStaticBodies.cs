using System.Collections.Generic;
using Godot;

namespace UnturnedGodot;

// One lifecycle node for many independent PhysicsServer bodies. Spatial partitioning still creates the
// same RIDs with the same shapes, layers and transforms; only the otherwise-empty Node3D wrapper per body
// disappears. The RID/name table preserves collision diagnostics without changing query ownership.
public partial class InstancedStaticBodies : Node3D
{
    private sealed class Definition
    {
        public required string Name;
        public required IReadOnlyList<Shape3D> Shapes;
        public required IReadOnlyList<CollisionPlacement> Placements;
        public required uint CollisionLayer;
    }

    private List<Definition> _definitions = new();
    private readonly Dictionary<Rid, string> _names = new();

    // Which surface each of a body's shapes is made of, in the order BodyAddShape took them — so the shape
    // index a raycast reports indexes straight into it. Kept as bytes into a table shared with every other
    // body here, and omitted entirely (no entry) for a body whose colliders name no material at all.
    private readonly Dictionary<Rid, byte[]> _surfaces = new();
    private IReadOnlyList<string> _surfaceNames = System.Array.Empty<string>();
    // A body definition refers to an immutable shared shape pool. Retaining each pool once is enough to
    // keep every Shape3D RID alive and is considerably smaller than a HashSet entry per individual shape.
    private readonly HashSet<IReadOnlyList<Shape3D>> _retainedShapePools =
        new(ReferenceEqualityComparer.Instance);
    private int _bodyCount;
    public int BodyCount => _bodyCount > 0 ? _bodyCount : _definitions.Count;

    public void Add(string name, IReadOnlyList<Shape3D> shapes,
        IReadOnlyList<CollisionPlacement> placements, uint collisionLayer,
        IReadOnlyList<string>? surfaceNames = null)
    {
        // Every builder that uploads here shares one surface table, so the last one wins and they agree by
        // construction. Held rather than copied per body: the names cost nothing next to the bodies.
        if (surfaceNames is { Count: > 0 })
            _surfaceNames = surfaceNames;
        _definitions.Add(new Definition
        {
            Name = name,
            Shapes = shapes,
            Placements = placements,
            CollisionLayer = collisionLayer,
        });
    }

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
            byte[]? surfaces = null;
            for (int i = 0; i < definition.Placements.Count; i++)
            {
                CollisionPlacement placement = definition.Placements[i];
                PhysicsServer3D.BodyAddShape(body, definition.Shapes[placement.Shape].GetRid(),
                    placement.Transform);
                // Allocated on the first placement that actually names a surface, so a body made entirely
                // of unmaterialed colliders costs nothing at all.
                if (placement.Material == 0 && surfaces == null)
                    continue;
                surfaces ??= new byte[definition.Placements.Count];
                surfaces[i] = placement.Material;
            }
            PhysicsServer3D.BodySetSpace(body, space); // join once, after every shape
            _names[body] = definition.Name;
            if (surfaces != null)
                _surfaces[body] = surfaces;
            _bodyCount++;
            if (!EnvFlag.IsOn(OS.GetEnvironment("UG_KEEP_PHYSICS_PLACEMENTS"), whenUnset: false))
                definition.Placements = System.Array.Empty<CollisionPlacement>();
        }
        if (!EnvFlag.IsOn(OS.GetEnvironment("UG_KEEP_RID_UPLOAD_METADATA"), whenUnset: false)
            && !EnvFlag.IsOn(OS.GetEnvironment("UG_KEEP_PHYSICS_PLACEMENTS"), whenUnset: false))
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

    // The physics-material name of one shape on one body — PhysicsTool.GetColliderSharedPhysicsMaterialName,
    // answered from the table built at upload. Empty for a shape whose collider carried no material, which
    // is what the original's null sharedMaterial means and what makes an impact silent and unmarked.
    //
    // Only ever called on a hit (a punch, a bullet), never per frame, so a dictionary lookup and a bounds
    // check are the whole cost.
    public string MaterialFor(Rid body, int shapeIndex)
    {
        if (shapeIndex < 0 || !_surfaces.TryGetValue(body, out byte[]? surfaces)
            || shapeIndex >= surfaces.Length)
        {
            return string.Empty;
        }
        byte index = surfaces[shapeIndex];
        return index == 0 || index > _surfaceNames.Count ? string.Empty : _surfaceNames[index - 1];
    }

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
        _surfaces.Clear();
        _retainedShapePools.Clear();
        _definitions.Clear();
    }
}
