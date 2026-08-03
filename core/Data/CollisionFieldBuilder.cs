using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace UnturnedGodot.Data;

// Collects the solid world as the physics bodies for it are created, and freezes it into a CollisionField.
//
// Recording alongside the real build is what makes the CPU copy trustworthy: the shapes and poses handed
// here are the same objects handed to PhysicsServer3D, so the two cannot drift the way an independent
// re-derivation from the level files would. Callers add every shape they create, in creation order, so a
// shape index is shared between the two worlds and nothing has to be looked up.
//
// Only the geometry a downward navmesh probe can hit is worth recording — that probe masks
// CollisionLayers.World — so callers add instances for those bodies alone. Shapes nothing references are
// dropped by Build.
public sealed class CollisionFieldBuilder
{
    // Broadphase cell for the instance index. Object colliders are metres across, so this holds a few
    // dozen candidates per cell on dense maps while keeping the dictionary small on empty ones.
    private const float CellSize = 32f;

    // An instance covering more cells than this is indexed once, in a list every probe consults, rather
    // than written into thousands of buckets it would dominate.
    private const int MaxCells = 1024;

    private readonly List<Pending> _shapes = new();
    private readonly List<(int Shape, Transform3D Transform)> _instances = new();
    private readonly List<CollisionField.Heightfield> _tiles = new();
    private readonly object _gate = new();

    private readonly struct Pending
    {
        public readonly CollisionField.Kind Kind;
        public readonly Vector3 Size;   // Box: full extents
        public readonly float Radius;   // Sphere / Capsule
        public readonly float Height;   // Capsule: the whole shape, caps included
        public readonly Vector3[]? Faces;

        public Pending(CollisionField.Kind kind, Vector3 size, float radius, float height,
            Vector3[]? faces)
        {
            Kind = kind;
            Size = size;
            Radius = radius;
            Height = height;
            Faces = faces;
        }
    }

    public int ShapeCount { get { lock (_gate) return _shapes.Count; } }
    public int InstanceCount { get { lock (_gate) return _instances.Count; } }
    public int TileCount { get { lock (_gate) return _tiles.Count; } }

    public int AddBoxShape(Vector3 size) =>
        AddShape(new Pending(CollisionField.Kind.Box, size, 0f, 0f, null));

    public int AddSphereShape(float radius) =>
        AddShape(new Pending(CollisionField.Kind.Sphere, Vector3.Zero, radius, 0f, null));

    public int AddCapsuleShape(float radius, float height) =>
        AddShape(new Pending(CollisionField.Kind.Capsule, Vector3.Zero, radius, height, null));

    // `faces` is kept by reference, not copied: the caller has already built this array to hand to
    // ConcavePolygonShape3D, which copies it into native memory, so retaining it here adds no allocation.
    public int AddMeshShape(Vector3[] faces) =>
        AddShape(new Pending(CollisionField.Kind.Mesh, Vector3.Zero, 0f, 0f, faces));

    // A shape index the caller is not going to place — kept so indices stay aligned with the caller's own
    // shape table when it creates something this field does not model.
    public int AddUnsupportedShape() =>
        AddShape(new Pending(CollisionField.Kind.Mesh, Vector3.Zero, 0f, 0f, Array.Empty<Vector3>()));

    private int AddShape(Pending shape)
    {
        lock (_gate)
        {
            _shapes.Add(shape);
            return _shapes.Count - 1;
        }
    }

    public void AddInstance(int shape, Transform3D transform)
    {
        lock (_gate)
        {
            if ((uint)shape >= (uint)_shapes.Count)
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "No such shape.");
            _instances.Add((shape, transform));
        }
    }

    // A terrain tile's collision heightfield, in HeightMapShape3D's own layout and placed by the transform
    // that shape is given. The placement is an axis-aligned scale plus a translation (see
    // TerrainHeightfield.CollisionTransform); a rotated one is refused rather than silently mis-sampled.
    public void AddHeightfield(Transform3D placement, int width, int depth, float[] data)
    {
        if (width < 2 || depth < 2)
            throw new ArgumentException("A heightfield needs at least one cell.", nameof(width));
        if (data.Length != width * depth)
            throw new ArgumentException("Heightfield sample count does not match its resolution.",
                nameof(data));
        Vector3 scale = placement.Basis.Scale;
        if (!placement.Basis.Orthonormalized().IsEqualApprox(Basis.Identity))
            throw new ArgumentException("A rotated heightfield placement is not supported.",
                nameof(placement));
        if (!Mathf.IsEqualApprox(scale.X, scale.Z) || !Mathf.IsEqualApprox(scale.Y, 1f))
            throw new ArgumentException("Heightfield cells must be square with unscaled heights.",
                nameof(placement));

        float min = float.MaxValue, max = float.MinValue;
        foreach (float height in data)
        {
            if (height < min) min = height;
            if (height > max) max = height;
        }
        lock (_gate)
        {
            _tiles.Add(new CollisionField.Heightfield(placement.Origin.X, placement.Origin.Y,
                placement.Origin.Z, scale.X, width, depth, data,
                min + placement.Origin.Y, max + placement.Origin.Y));
        }
    }

    // Freezes what has been collected. Pure CPU and safe to call from a worker; the per-shape BVHs are
    // built in parallel because a large map has thousands of distinct collision meshes and building them
    // one at a time is the only part of this that is not trivially cheap.
    public CollisionField Build()
    {
        Pending[] shapes;
        (int Shape, Transform3D Transform)[] instances;
        CollisionField.Heightfield[] tiles;
        lock (_gate)
        {
            shapes = _shapes.ToArray();
            instances = _instances.ToArray();
            tiles = _tiles.ToArray();
        }

        var used = new bool[shapes.Length];
        foreach ((int shape, Transform3D _) in instances)
            used[shape] = true;

        var geometry = new CollisionField.MeshGeometry?[shapes.Length];
        Parallel.For(0, shapes.Length, i =>
        {
            if (!used[i] || shapes[i].Kind != CollisionField.Kind.Mesh)
                return;
            Vector3[] faces = shapes[i].Faces ?? Array.Empty<Vector3>();
            if (faces.Length >= 3)
                geometry[i] = new CollisionField.MeshGeometry(faces);
        });

        var built = new CollisionField.Shape[shapes.Length];
        long triangles = 0;
        for (int i = 0; i < shapes.Length; i++)
        {
            Pending pending = shapes[i];
            switch (pending.Kind)
            {
                case CollisionField.Kind.Box:
                    Vector3 half = pending.Size.Abs() * 0.5f;
                    built[i] = new CollisionField.Shape(pending.Kind, half, 0f, 0f, null, -half, half);
                    break;
                case CollisionField.Kind.Sphere:
                    float r = MathF.Abs(pending.Radius);
                    built[i] = new CollisionField.Shape(pending.Kind, Vector3.Zero, r, 0f, null,
                        new Vector3(-r, -r, -r), new Vector3(r, r, r));
                    break;
                case CollisionField.Kind.Capsule:
                    float radius = MathF.Abs(pending.Radius);
                    float halfSegment = MathF.Max(0f, (MathF.Abs(pending.Height) * 0.5f) - radius);
                    var extent = new Vector3(radius, halfSegment + radius, radius);
                    built[i] = new CollisionField.Shape(pending.Kind, Vector3.Zero, radius, halfSegment,
                        null, -extent, extent);
                    break;
                default:
                    CollisionField.MeshGeometry? mesh = geometry[i];
                    built[i] = new CollisionField.Shape(pending.Kind, Vector3.Zero, 0f, 0f, mesh,
                        mesh?.Min ?? Vector3.Zero, mesh?.Max ?? Vector3.Zero);
                    if (mesh != null)
                        triangles += mesh.Triangles;
                    break;
            }
        }

        var placed = new List<CollisionField.Instance>(instances.Length);
        foreach ((int shape, Transform3D transform) in instances)
        {
            CollisionField.Shape geometryShape = built[shape];
            if (geometryShape.Kind == CollisionField.Kind.Mesh && geometryShape.Mesh == null)
                continue; // nothing to hit: an unsupported or empty collider
            WorldBounds(geometryShape, transform, out Vector3 min, out Vector3 max);
            placed.Add(new CollisionField.Instance(shape, transform.AffineInverse(), min, max));
        }

        var buckets = new Dictionary<(int X, int Z), List<int>>();
        var wide = new List<int>();
        for (int i = 0; i < placed.Count; i++)
        {
            // Bucketed by the graze-widened footprint, matching the widened reject in ProbeInstances: a
            // probe just outside a collider still has to reach it, or the escalation never happens.
            CollisionField.Instance instance = placed[i];
            const float Pad = CollisionField.GrazeEpsilon;
            int x0 = (int)MathF.Floor((instance.Min.X - Pad) / CellSize);
            int x1 = (int)MathF.Floor((instance.Max.X + Pad) / CellSize);
            int z0 = (int)MathF.Floor((instance.Min.Z - Pad) / CellSize);
            int z1 = (int)MathF.Floor((instance.Max.Z + Pad) / CellSize);
            long cells = ((long)x1 - x0 + 1) * ((long)z1 - z0 + 1);
            if (cells > MaxCells || cells <= 0)
            {
                wide.Add(i);
                continue;
            }
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!buckets.TryGetValue((x, z), out List<int>? bucket))
                        buckets[(x, z)] = bucket = new List<int>();
                    bucket.Add(i);
                }
        }

        var grid = new Dictionary<(int X, int Z), int[]>(buckets.Count);
        foreach (((int x, int z), List<int> bucket) in buckets)
            grid[(x, z)] = bucket.ToArray();

        return new CollisionField(built, placed.ToArray(), tiles, grid, wide.ToArray(), CellSize,
            triangles);
    }

    // Drops everything collected. Reconciliation is a one-shot pass, so once Build has handed the field
    // over there is no second consumer — and what is being held is the map's whole collision geometry:
    // a quarter of a megabyte per terrain tile before a single collider. The shapes the field kept stay
    // alive through it; the rest (MEDIUM furniture's meshes, every unplaced shape) goes here.
    public void Release()
    {
        lock (_gate)
        {
            _shapes.Clear();
            _shapes.TrimExcess();
            _instances.Clear();
            _instances.TrimExcess();
            _tiles.Clear();
            _tiles.TrimExcess();
        }
    }

    private static void WorldBounds(in CollisionField.Shape shape, Transform3D transform, out Vector3 min,
        out Vector3 max)
    {
        Vector3 localMin = shape.LocalMin, localMax = shape.LocalMax;
        min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3(
                (corner & 1) == 0 ? localMin.X : localMax.X,
                (corner & 2) == 0 ? localMin.Y : localMax.Y,
                (corner & 4) == 0 ? localMin.Z : localMax.Z);
            Vector3 world = transform * point;
            min = min.Min(world);
            max = max.Max(world);
        }
    }
}
