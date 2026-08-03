using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Repro;

namespace UnturnedGodot;

// Lifts the collision geometry around a point out of the physics server and into a dump.
//
// Every collider kind the world is built from — trimeshes for object colliders, primitives for the ones
// the game data authors as boxes/spheres/capsules, the terrain heightfield — is tessellated into world
// space triangles here. That happens once, when someone captures, and it is what lets the replay side
// (core/Repro) run without an engine at all: one triangle soup instead of a physics server.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class ReproGeometryCapture
{
    // How finely the round primitives are tessellated. The bodies these represent are lamp posts,
    // barrels and fence posts — eight sides is what the source meshes themselves have.
    private const int Segments = 8;
    private const int Rings = 4;

    // A capture that hits this many triangles stops adding: a dump is a bug report, not a level export.
    private const int MaxTriangles = 60_000;

    public static ReproGeometryData? Capture(PhysicsDirectSpaceState3D? space, Vector3 focus,
        float radius, List<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        if (space == null)
            return null;

        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new SphereShape3D { Radius = radius },
            Transform = new Transform3D(Basis.Identity, focus),
            CollisionMask = uint.MaxValue,
            CollideWithBodies = true,
            CollideWithAreas = false,
        };
        Godot.Collections.Array<Godot.Collections.Dictionary> hits = space.IntersectShape(query, 4096);

        var builder = new Builder(focus, radius);
        var seen = new HashSet<(ulong Body, int Shape)>();
        foreach (Godot.Collections.Dictionary hit in hits)
        {
            if (!hit.TryGetValue("rid", out Variant bodyValue)
                || !hit.TryGetValue("shape", out Variant shapeValue))
                continue;
            Rid body = bodyValue.As<Rid>();
            int shapeIndex = shapeValue.AsInt32();
            if (!seen.Add((body.Id, shapeIndex)))
                continue;

            uint layer = PhysicsServer3D.BodyGetCollisionLayer(body);
            string owner = InstancedStaticBodies.ColliderName(hit);
            Transform3D transform =
                PhysicsServer3D.BodyGetState(body, PhysicsServer3D.BodyState.Transform)
                    .AsTransform3D() * PhysicsServer3D.BodyGetShapeTransform(body, shapeIndex);
            Rid shape = PhysicsServer3D.BodyGetShape(body, shapeIndex);
            builder.Begin(layer, owner);
            Add(builder, PhysicsServer3D.ShapeGetType(shape), PhysicsServer3D.ShapeGetData(shape),
                transform, warnings);
            if (builder.TriangleCount >= MaxTriangles)
            {
                warnings.Add($"the collision slice hit its {MaxTriangles} triangle ceiling; "
                    + "capture with a smaller REPRO_GEOMETRY_RADIUS for a complete one");
                break;
            }
        }
        return builder.Build();
    }

    private static void Add(Builder builder, PhysicsServer3D.ShapeType type, Variant data,
        in Transform3D transform, List<string> warnings)
    {
        switch (type)
        {
            case PhysicsServer3D.ShapeType.ConcavePolygon:
                AddConcave(builder, data, transform);
                break;
            case PhysicsServer3D.ShapeType.Box:
                AddBox(builder, data.AsVector3(), transform);
                break;
            case PhysicsServer3D.ShapeType.Sphere:
                AddSphere(builder, (float)data.AsDouble(), transform);
                break;
            case PhysicsServer3D.ShapeType.Capsule:
                AddCapsule(builder, data, transform);
                break;
            case PhysicsServer3D.ShapeType.Cylinder:
                AddCylinder(builder, data, transform);
                break;
            case PhysicsServer3D.ShapeType.Heightmap:
                AddHeightmap(builder, data, transform);
                break;
            default:
                // Convex hulls and world boundaries are not produced by this project's world build. If
                // one ever is, the dump says it was left out rather than replaying a hole in the world.
                warnings.Add($"collision shape of type {type} was not captured (unsupported)");
                break;
        }
    }

    private static void AddConcave(Builder builder, Variant data, in Transform3D transform)
    {
        if (data.VariantType != Variant.Type.Dictionary)
            return;
        var dictionary = data.AsGodotDictionary();
        if (!dictionary.TryGetValue("faces", out Variant faces))
            return;
        Vector3[] vertices = faces.AsVector3Array();
        for (int i = 0; i + 2 < vertices.Length; i += 3)
            builder.Triangle(transform * vertices[i], transform * vertices[i + 1],
                transform * vertices[i + 2]);
    }

    private static void AddBox(Builder builder, Vector3 halfExtents, in Transform3D transform)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
            corners[i] = transform * new Vector3(
                (i & 1) == 0 ? -halfExtents.X : halfExtents.X,
                (i & 2) == 0 ? -halfExtents.Y : halfExtents.Y,
                (i & 4) == 0 ? -halfExtents.Z : halfExtents.Z);
        // Six faces, two triangles each, wound consistently outward.
        Quad(builder, corners[0], corners[2], corners[3], corners[1]); // -Z
        Quad(builder, corners[4], corners[5], corners[7], corners[6]); // +Z
        Quad(builder, corners[0], corners[1], corners[5], corners[4]); // -Y
        Quad(builder, corners[2], corners[6], corners[7], corners[3]); // +Y
        Quad(builder, corners[0], corners[4], corners[6], corners[2]); // -X
        Quad(builder, corners[1], corners[3], corners[7], corners[5]); // +X
    }

    private static void Quad(Builder builder, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        builder.Triangle(a, b, c);
        builder.Triangle(a, c, d);
    }

    private static void AddSphere(Builder builder, float radius, in Transform3D transform) =>
        AddLathe(builder, transform, radius, 0f, Rings * 2);

    private static void AddCapsule(Builder builder, Variant data, in Transform3D transform)
    {
        (float radius, float height) = RadiusHeight(data);
        AddLathe(builder, transform, radius, MathF.Max(0f, (height / 2f) - radius), Rings * 2);
    }

    private static void AddCylinder(Builder builder, Variant data, in Transform3D transform)
    {
        (float radius, float height) = RadiusHeight(data);
        float half = height / 2f;
        for (int i = 0; i < Segments; i++)
        {
            (Vector3 a, Vector3 b) = Ring(i, radius);
            Quad(builder,
                transform * new Vector3(a.X, -half, a.Z), transform * new Vector3(b.X, -half, b.Z),
                transform * new Vector3(b.X, half, b.Z), transform * new Vector3(a.X, half, a.Z));
            builder.Triangle(transform * new Vector3(0f, half, 0f),
                transform * new Vector3(a.X, half, a.Z), transform * new Vector3(b.X, half, b.Z));
            builder.Triangle(transform * new Vector3(0f, -half, 0f),
                transform * new Vector3(b.X, -half, b.Z), transform * new Vector3(a.X, -half, a.Z));
        }
    }

    // A sphere (cylinderHalf 0) or a capsule: latitude bands over the two hemispheres, with a straight
    // section between them when the body has one.
    private static void AddLathe(Builder builder, in Transform3D transform, float radius,
        float cylinderHalf, int rings)
    {
        for (int ring = 0; ring < rings; ring++)
        {
            float t0 = (ring / (float)rings * MathF.PI) - (MathF.PI / 2f);
            float t1 = ((ring + 1) / (float)rings * MathF.PI) - (MathF.PI / 2f);
            for (int i = 0; i < Segments; i++)
            {
                (Vector3 a, Vector3 b) = Ring(i, 1f);
                Quad(builder,
                    transform * Lathe(a, t0, radius, cylinderHalf),
                    transform * Lathe(b, t0, radius, cylinderHalf),
                    transform * Lathe(b, t1, radius, cylinderHalf),
                    transform * Lathe(a, t1, radius, cylinderHalf));
            }
        }
    }

    private static Vector3 Lathe(Vector3 direction, float latitude, float radius, float cylinderHalf)
    {
        float cos = MathF.Cos(latitude), sin = MathF.Sin(latitude);
        return new Vector3(direction.X * radius * cos,
            (sin * radius) + (sin >= 0f ? cylinderHalf : -cylinderHalf),
            direction.Z * radius * cos);
    }

    private static (Vector3 A, Vector3 B) Ring(int segment, float radius)
    {
        float a = segment / (float)Segments * MathF.Tau;
        float b = (segment + 1) / (float)Segments * MathF.Tau;
        return (new Vector3(MathF.Cos(a) * radius, 0f, MathF.Sin(a) * radius),
            new Vector3(MathF.Cos(b) * radius, 0f, MathF.Sin(b) * radius));
    }

    private static (float Radius, float Height) RadiusHeight(Variant data)
    {
        if (data.VariantType != Variant.Type.Dictionary)
            return (0f, 0f);
        var dictionary = data.AsGodotDictionary();
        float radius = dictionary.TryGetValue("radius", out Variant r) ? (float)r.AsDouble() : 0f;
        float height = dictionary.TryGetValue("height", out Variant h) ? (float)h.AsDouble() : 0f;
        return (radius, height);
    }

    // The terrain tile's HeightMapShape3D, but only the cells the capture sphere reaches: a full PEI
    // tile is 257x257 samples, and a bug report needs the twenty metres under the incident.
    private static void AddHeightmap(Builder builder, Variant data, Transform3D transform)
    {
        if (data.VariantType != Variant.Type.Dictionary)
            return;
        var dictionary = data.AsGodotDictionary();
        if (!dictionary.TryGetValue("width", out Variant widthValue)
            || !dictionary.TryGetValue("depth", out Variant depthValue)
            || !dictionary.TryGetValue("heights", out Variant heightsValue))
            return;
        int width = widthValue.AsInt32(), depth = depthValue.AsInt32();
        float[] heights = heightsValue.AsFloat32Array();
        if (width < 2 || depth < 2 || heights.Length < width * depth)
            return;

        // The shape's samples sit on a unit grid centred on its origin; the body transform scales and
        // places it. Walking the capture window in LOCAL space keeps the cell arithmetic exact.
        Transform3D inverse = transform.AffineInverse();
        Vector3 local = inverse * builder.Focus;
        float localRadius = builder.Radius / MathF.Max(1e-3f, transform.Basis.Scale.X);
        int x0 = Math.Clamp((int)MathF.Floor(local.X + ((width - 1) / 2f) - localRadius), 0, width - 2);
        int x1 = Math.Clamp((int)MathF.Ceiling(local.X + ((width - 1) / 2f) + localRadius), 0, width - 2);
        int z0 = Math.Clamp((int)MathF.Floor(local.Z + ((depth - 1) / 2f) - localRadius), 0, depth - 2);
        int z1 = Math.Clamp((int)MathF.Ceiling(local.Z + ((depth - 1) / 2f) + localRadius), 0, depth - 2);

        Vector3 At(int x, int z) => transform * new Vector3(
            x - ((width - 1) / 2f), heights[(z * width) + x], z - ((depth - 1) / 2f));

        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                builder.Triangle(At(x, z), At(x, z + 1), At(x + 1, z));
                builder.Triangle(At(x + 1, z), At(x, z + 1), At(x + 1, z + 1));
            }
    }

    // Collects triangles, deduplicating vertices and dropping anything outside the capture sphere.
    private sealed class Builder
    {
        private readonly Dictionary<(int, int, int), int> _indices = new();
        private readonly List<float> _vertices = new();
        private readonly List<int> _triangles = new();
        private readonly List<int> _layers = new();
        private readonly List<int> _owners = new();
        private readonly List<string> _ownerNames = new();
        private readonly Dictionary<string, int> _ownerIndices = new();
        private readonly float _radiusSquared;
        private uint _layer;
        private int _owner;

        public Builder(Vector3 focus, float radius)
        {
            Focus = focus;
            Radius = radius;
            _radiusSquared = radius * radius;
        }

        public Vector3 Focus { get; }

        public float Radius { get; }

        public int TriangleCount => _triangles.Count / 3;

        public void Begin(uint layer, string owner)
        {
            _layer = layer;
            if (!_ownerIndices.TryGetValue(owner, out _owner))
            {
                _owner = _ownerNames.Count;
                _ownerIndices[owner] = _owner;
                _ownerNames.Add(owner);
            }
        }

        public void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            // Kept whole whenever the sphere touches it ANYWHERE — corner, edge or face. A corner-only
            // test drops exactly the triangles that matter most: a building's floor slab is two huge
            // triangles whose vertices are all outside a 16 m capture taken while standing on it, and
            // dropping them puts a hole in the replay's world where the reporter was standing.
            // (Clipping instead would put edges where the world has none, and a capsule sliding along
            // one would stop for no reason.)
            if (!Meets(a, b, c))
                return;
            _triangles.Add(Index(a));
            _triangles.Add(Index(b));
            _triangles.Add(Index(c));
            _layers.Add((int)_layer);
            _owners.Add(_owner);
        }

        private bool Meets(Vector3 a, Vector3 b, Vector3 c) =>
            (ReproGeometry.ClosestPointOnTriangle(Focus, a, b, c) - Focus).LengthSquared()
                <= _radiusSquared;

        private int Index(Vector3 point)
        {
            // Dedupe at the dump's own precision, so the vertex count matches what gets written.
            float x = ReproVector.Round(point.X), y = ReproVector.Round(point.Y);
            float z = ReproVector.Round(point.Z);
            var key = (BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(y),
                BitConverter.SingleToInt32Bits(z));
            if (_indices.TryGetValue(key, out int index))
                return index;
            index = _vertices.Count / 3;
            _indices[key] = index;
            _vertices.Add(x);
            _vertices.Add(y);
            _vertices.Add(z);
            return index;
        }

        public ReproGeometryData Build() => new()
        {
            Center = ReproVector.From(Focus),
            Radius = Radius,
            Vertices = _vertices.ToArray(),
            Triangles = _triangles.ToArray(),
            Layers = _layers.ToArray(),
            Owners = _owners.ToArray(),
            OwnerNames = _ownerNames,
        };
    }
}
