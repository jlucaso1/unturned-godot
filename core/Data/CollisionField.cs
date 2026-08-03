using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Data;

// A read-only CPU copy of the solid world, answering exactly one question: what is the highest surface on
// a vertical line, between two heights?
//
// That is the only question navmesh reconciliation ever asks the PhysicsServer (see
// ZombieNavigation.PruneAgainstCollisionAsync: every probe is a short ray straight down). Asking it through
// DirectSpaceState is what made the first session on a map take minutes rather than seconds — a direct-space
// query is only legal from inside a physics notification, so thousands of probes serialise into a quarter of
// a millisecond per tick and nothing else can overlap them.
//
// Answering it here instead costs no physics tick at all: the field is immutable once built, so probing runs
// on a worker, in parallel, at whatever rate the CPU allows. The server stays the authority — it is still
// asked about every probe this field cannot answer confidently (see Uncertain) and about every triangle whose
// decision sits close enough to the step threshold that the residual numeric difference could flip it.
//
// The geometry comes from the same sources the physics bodies are built from, recorded as they are created:
// terrain heightfields (TerrainBuilder) and object colliders (ObjectsBuilder). It is deliberately NOT a
// re-derivation from the level files — a second parse would be a second thing to keep in step.
public sealed class CollisionField
{
    // How close to a shape's silhouette a probe may pass before the CPU answer stops being trustworthy.
    // Inside this band the exact hit/miss depends on the collision engine's own margins, so the probe is
    // handed to the server instead of guessed at.
    public const float GrazeEpsilon = 0.02f;

    internal enum Kind : byte { Box, Sphere, Capsule, Mesh }

    internal readonly struct Shape
    {
        public readonly Kind Kind;
        public readonly Vector3 HalfExtents; // Box
        public readonly float Radius;        // Sphere / Capsule
        public readonly float HalfSegment;   // Capsule: half the distance between the two cap centres
        public readonly MeshGeometry? Mesh;
        public readonly Vector3 LocalMin;
        public readonly Vector3 LocalMax;

        public Shape(Kind kind, Vector3 halfExtents, float radius, float halfSegment, MeshGeometry? mesh,
            Vector3 localMin, Vector3 localMax)
        {
            Kind = kind;
            HalfExtents = halfExtents;
            Radius = radius;
            HalfSegment = halfSegment;
            Mesh = mesh;
            LocalMin = localMin;
            LocalMax = localMax;
        }
    }

    internal readonly struct Instance
    {
        public readonly int Shape;
        public readonly Transform3D ToLocal; // world -> shape local
        public readonly Vector3 Min;
        public readonly Vector3 Max;

        public Instance(int shape, Transform3D toLocal, Vector3 min, Vector3 max)
        {
            Shape = shape;
            ToLocal = toLocal;
            Min = min;
            Max = max;
        }
    }

    // A terrain tile's collision heightfield, in the layout HeightMapShape3D uses: data[d * width + w],
    // sample (w, d) sitting at world (originX + (w - (width-1)/2) * cell, data[..], originZ + (d - ...)).
    internal readonly struct Heightfield
    {
        public readonly float OriginX;
        // The placement's own height. The samples are heights in the shape's frame, so a tile placed with
        // a vertical offset sits that much higher in the world — the bounds already say so, and the
        // sampled height has to agree with them.
        public readonly float OriginY;
        public readonly float OriginZ;
        public readonly float CellSize;
        public readonly int Width;
        public readonly int Depth;
        public readonly float[] Data;
        public readonly float MinY;
        public readonly float MaxY;

        public Heightfield(float originX, float originY, float originZ, float cellSize, int width,
            int depth, float[] data, float minY, float maxY)
        {
            OriginX = originX;
            OriginY = originY;
            OriginZ = originZ;
            CellSize = cellSize;
            Width = width;
            Depth = depth;
            Data = data;
            MinY = minY;
            MaxY = maxY;
        }

        public float LowX => OriginX - ((Width - 1) * 0.5f * CellSize);
        public float LowZ => OriginZ - ((Depth - 1) * 0.5f * CellSize);
    }

    private readonly Shape[] _shapes;
    private readonly Instance[] _instances;
    private readonly Heightfield[] _tiles;
    private readonly Dictionary<(int X, int Z), int[]> _grid;
    private readonly int[] _wide; // instances spanning too many cells to be worth indexing
    private readonly float _cellSize;

    public int ShapeCount => _shapes.Length;
    public int InstanceCount => _instances.Length;
    public int TileCount => _tiles.Length;
    public long TriangleCount { get; }

    internal CollisionField(Shape[] shapes, Instance[] instances, Heightfield[] tiles,
        Dictionary<(int X, int Z), int[]> grid, int[] wide, float cellSize, long triangles)
    {
        _shapes = shapes;
        _instances = instances;
        _tiles = tiles;
        _grid = grid;
        _wide = wide;
        _cellSize = cellSize;
        TriangleCount = triangles;
    }

    // The highest solid surface on the vertical line through `x, z`, between `bottomY` and `topY` —
    // the CPU answer to PhysicsDirectSpaceState3D.IntersectRay(from: (x, topY, z), to: (x, bottomY, z)).
    //
    // `Slack` is how far the reported height could move if the collision engine triangulated a heightfield
    // cell along its other diagonal; `Uncertain` says the answer must be confirmed against the server,
    // because either nothing was found at all or something passed close enough to a silhouette that
    // hit-versus-miss is not this code's call to make.
    public SurfaceSample Probe(float x, float z, float topY, float bottomY)
    {
        // Three heights, not one. `best` is the answer. `solid` is the highest surface hit clear of any
        // silhouette, and `graze` the highest one brushed close enough that hit-versus-miss belongs to the
        // collision engine. Comparing the last two is what separates a genuine ambiguity from the ordinary
        // case of a probe landing on the shared diagonal of two coplanar triangles: there the graze sits
        // level with a surface that was also hit squarely, so nothing about the answer is in doubt.
        var state = new Accumulator
        {
            Best = float.MinValue,
            Solid = float.MinValue,
            Graze = float.MinValue,
        };

        for (int i = 0; i < _tiles.Length; i++)
        {
            ref readonly Heightfield tile = ref _tiles[i];
            if (!Covers(in tile, x, z))
                continue;
            // The ground here is modelled, whatever the probe finds. That is what separates "nothing is
            // in range" — an answer — from "this column was never recorded", which is not one.
            state.Covered = true;
            if (tile.MaxY < bottomY || tile.MinY > topY)
                continue;
            SampleHeightfield(in tile, x, z, out float y, out float diagonal);
            if (y < bottomY || y > topY)
            {
                // Out of range settles only the height that was sampled, and on a twisted cell that is
                // the TALLER of the two triangulations with `diagonal` the drop to the other one. If the
                // shorter one lands inside the segment, the shape Godot actually built may answer where
                // this one says nothing, so the column is a question rather than a clean miss.
                float other = y - diagonal;
                if (other >= bottomY && other <= topY)
                    state.Doubt = true;
                continue;
            }
            if (y > state.Best)
            {
                state.Best = y;
                state.Slack = diagonal;
            }
            if (y > state.Solid)
                state.Solid = y;
            state.Hit = true;
        }

        ProbeInstances(_wide, x, z, topY, bottomY, ref state);
        if (_grid.TryGetValue((Floor(x, _cellSize), Floor(z, _cellSize)), out int[]? cell))
            ProbeInstances(cell, x, z, topY, bottomY, ref state);

        // Finding nothing is a real answer over ground this field models — every body a navmesh probe can
        // hit was recorded as it was built, so "nothing between these two heights" is a statement about
        // the world, not an admission of ignorance. Over a column nobody recorded it is the opposite, and
        // the server is asked.
        bool uncertain = (!state.Hit && !state.Covered) || state.Doubt
            || state.Graze > state.Solid + GrazeEpsilon;
        return new SurfaceSample(state.Hit, state.Hit ? state.Best : 0f, state.Slack, uncertain);
    }

    private struct Accumulator
    {
        public float Best;
        public float Solid;
        public float Graze;
        public float Slack;
        public bool Hit;
        public bool Covered;
        public bool Doubt;
    }

    private static bool Covers(in Heightfield tile, float x, float z) =>
        x >= tile.LowX && z >= tile.LowZ
        && x <= tile.LowX + ((tile.Width - 1) * tile.CellSize)
        && z <= tile.LowZ + ((tile.Depth - 1) * tile.CellSize);

    public SurfaceSample Probe(Vector3 point, float topY, float bottomY) =>
        Probe(point.X, point.Z, topY, bottomY);

    private static int Floor(float value, float cell) => (int)MathF.Floor(value / cell);

    private void ProbeInstances(int[] candidates, float x, float z, float topY, float bottomY,
        ref Accumulator state)
    {
        for (int n = 0; n < candidates.Length; n++)
        {
            ref readonly Instance instance = ref _instances[candidates[n]];
            // Widened by the graze band on purpose: a probe just outside a collider is exactly the case
            // the shape tests have to see, so that they can escalate it rather than let this reject
            // silently answer "nothing there".
            if (x < instance.Min.X - GrazeEpsilon || x > instance.Max.X + GrazeEpsilon
                || z < instance.Min.Z - GrazeEpsilon || z > instance.Max.Z + GrazeEpsilon
                || instance.Max.Y < bottomY || instance.Min.Y > topY)
                continue;

            // The ray is expressed in the shape's own frame; the instance transforms recorded by
            // ObjectsBuilder are rigid (scale is baked into the shape itself), so this is a plain change
            // of basis and the parameter t stays a world distance.
            Vector3 origin = instance.ToLocal * new Vector3(x, topY, z);
            Vector3 direction = instance.ToLocal.Basis * Vector3.Down;
            float length = topY - bottomY;

            Crossing crossing = Intersect(in _shapes[instance.Shape], origin, direction, length);
            if (crossing.Hit)
            {
                float y = topY - crossing.T;
                if (y > state.Best)
                {
                    state.Best = y;
                    // Slack belongs to whichever surface won. A collider has no triangulation to be
                    // ambiguous about — what it does have is the graze band, tracked separately — so a
                    // collider outranking the terrain takes the terrain's slack off the answer with it.
                    state.Slack = 0f;
                }
                state.Hit = true;
            }
            if (crossing.Solid)
            {
                float y = topY - crossing.SolidT;
                if (y > state.Solid)
                    state.Solid = y;
            }
            if (crossing.Graze)
            {
                float y = topY - crossing.GrazeT;
                if (y > state.Graze)
                    state.Graze = y;
            }
        }
    }

    // What a ray does to one shape, in that shape's own frame.
    //
    // `Hit` is the geometric crossing and `T` where it happens. `Solid` narrows that to a crossing clear
    // of the silhouette by at least GrazeEpsilon — the part of the answer this code is entitled to state
    // on its own. `Graze` is a crossing within that band, hit or miss; the collision engine's margins
    // decide those, so they are reported and escalated rather than guessed.
    internal readonly record struct Crossing(bool Hit, float T, bool Solid, float SolidT, bool Graze,
        float GrazeT);

    // A ray that starts INSIDE a convex shape reports no crossing, which is what
    // PhysicsRayQueryParameters3D does with its default HitFromInside = false.
    private static Crossing Intersect(in Shape shape, Vector3 origin, Vector3 direction, float length)
    {
        switch (shape.Kind)
        {
            case Kind.Box:
                return IntersectBox(shape.HalfExtents, origin, direction, length);
            case Kind.Sphere:
                return IntersectSphere(shape.Radius, origin, direction, length);
            case Kind.Capsule:
                return IntersectCapsule(shape.Radius, shape.HalfSegment, origin, direction, length);
            default:
                return shape.Mesh!.Intersect(origin, direction, length);
        }
    }

    // Solved three times: against the box as authored, and against it grown and shrunk by GrazeEpsilon.
    // The grown and shrunk answers agreeing is what says the ray is clear of the silhouette; them
    // disagreeing is exactly the band where the collision engine's own margins decide.
    private static Crossing IntersectBox(Vector3 half, Vector3 origin, Vector3 direction, float length)
    {
        bool exact = Slabs(half, origin, direction, length, out float entry);
        var grow = new Vector3(GrazeEpsilon, GrazeEpsilon, GrazeEpsilon);
        bool outer = Slabs(half + grow, origin, direction, length, out float loose);
        bool inner = Slabs(half.Max(grow) - grow, origin, direction, length, out float _);
        bool graze = outer != inner;
        return new Crossing(exact, entry, exact && !graze, entry, graze, graze ? loose : 0f);
    }

    private static bool Slabs(Vector3 half, Vector3 origin, Vector3 direction, float length, out float entry)
    {
        float near = 0f, far = length;
        entry = 0f;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = origin[axis], d = direction[axis], h = half[axis];
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < -h || o > h)
                    return false;
                continue;
            }
            float inverse = 1f / d;
            float t0 = (-h - o) * inverse;
            float t1 = (h - o) * inverse;
            if (t0 > t1)
                (t0, t1) = (t1, t0);
            if (t0 > near)
                near = t0;
            if (t1 < far)
                far = t1;
            if (near > far)
                return false;
        }
        entry = near;
        // near == 0 means the ray began inside the slabs: no entry crossing to report.
        return near > 0f;
    }

    private static Crossing IntersectSphere(float radius, Vector3 origin, Vector3 direction, float length)
    {
        bool exact = SphereEntry(Vector3.Zero, radius, origin, direction, length, out float entry);
        bool outer = SphereEntry(Vector3.Zero, radius + GrazeEpsilon, origin, direction, length,
            out float loose);
        bool inner = SphereEntry(Vector3.Zero, MathF.Max(0f, radius - GrazeEpsilon), origin, direction,
            length, out float _);
        bool graze = outer != inner;
        return new Crossing(exact, entry, exact && !graze, entry, graze, graze ? loose : 0f);
    }

    private static bool SphereEntry(Vector3 centre, float radius, Vector3 origin, Vector3 direction,
        float length, out float t)
    {
        t = 0f;
        Vector3 m = origin - centre;
        float b = m.Dot(direction);
        float c = m.Dot(m) - (radius * radius);
        if (c < 0f)
            return false; // started inside
        if (b > 0f)
            return false; // pointing away
        float discriminant = (b * b) - c;
        if (discriminant < 0f)
            return false;
        float entry = -b - MathF.Sqrt(discriminant);
        if (entry < 0f || entry > length)
            return false;
        t = entry;
        return true;
    }

    private static Crossing IntersectCapsule(float radius, float halfSegment, Vector3 origin,
        Vector3 direction, float length)
    {
        bool exact = CapsuleEntry(radius, halfSegment, origin, direction, length, out float entry);
        bool outer = CapsuleEntry(radius + GrazeEpsilon, halfSegment, origin, direction, length,
            out float loose);
        bool inner = CapsuleEntry(MathF.Max(0f, radius - GrazeEpsilon), halfSegment, origin, direction,
            length, out float _);
        bool graze = outer != inner;
        return new Crossing(exact, entry, exact && !graze, entry, graze, graze ? loose : 0f);
    }

    // Godot's CapsuleShape3D is Y-aligned and its Height spans the whole shape, so the two cap centres sit
    // at +/- (Height/2 - Radius) — recorded as HalfSegment. The body is the cylinder between them plus a
    // sphere at each end; the crossing is the nearest entry of the three.
    private static bool CapsuleEntry(float radius, float halfSegment, Vector3 origin, Vector3 direction,
        float length, out float t)
    {
        t = float.MaxValue;
        bool found = false;

        // Containment is tested against the WHOLE capsule before any of its three pieces is. The pieces
        // overlap, so a ray starting inside the cylinder is still outside each cap sphere, and the cap
        // would happily report the ray entering its upper hemisphere — a surface buried inside the solid,
        // reported as ground, and agreed on by the grown and shrunk tests alike so nothing would flag it.
        // A ray beginning inside a shape reports no crossing; that is the whole shape's business, not a
        // piece's.
        if (Inside(radius, halfSegment, origin))
        {
            t = 0f;
            return false;
        }

        float ox = origin.X, oz = origin.Z, dx = direction.X, dz = direction.Z;
        float a = (dx * dx) + (dz * dz);
        float c = (ox * ox) + (oz * oz) - (radius * radius);
        if (a > 1e-12f)
        {
            float b = (ox * dx) + (oz * dz);
            float discriminant = (b * b) - (a * c);
            if (discriminant >= 0f && c >= 0f)
            {
                float entry = (-b - MathF.Sqrt(discriminant)) / a;
                if (entry >= 0f && entry <= length)
                {
                    float y = origin.Y + (direction.Y * entry);
                    if (y >= -halfSegment && y <= halfSegment)
                    {
                        t = entry;
                        found = true;
                    }
                }
            }
        }

        for (int side = 0; side < 2; side++)
        {
            var centre = new Vector3(0f, side == 0 ? halfSegment : -halfSegment, 0f);
            if (SphereEntry(centre, radius, origin, direction, length, out float cap) && cap < t)
            {
                t = cap;
                found = true;
            }
        }

        if (!found)
            t = 0f;
        return found;
    }

    // Inside the capsule as a whole: within `radius` of the segment between the two cap centres.
    private static bool Inside(float radius, float halfSegment, Vector3 point)
    {
        float y = Math.Clamp(point.Y, -halfSegment, halfSegment);
        float dy = point.Y - y;
        return (point.X * point.X) + (dy * dy) + (point.Z * point.Z) < radius * radius;
    }

    // Bilinear cell of a heightfield, triangulated both ways. Godot's heightfield and this one need not
    // pick the same diagonal, and on a twisted cell the two differ; the taller answer is reported and the
    // gap between them is handed back as slack, so the caller knows how much of its margin is this.
    //
    // Only called for a point Covers has already placed inside the tile, so there is no out-of-range case
    // to answer here.
    private static void SampleHeightfield(in Heightfield tile, float x, float z, out float y,
        out float slack)
    {
        float u = (x - tile.LowX) / tile.CellSize;
        float v = (z - tile.LowZ) / tile.CellSize;
        int i = Math.Min((int)u, tile.Width - 2);
        int j = Math.Min((int)v, tile.Depth - 2);
        float fx = u - i;
        float fz = v - j;

        float h00 = tile.Data[(j * tile.Width) + i];
        float h10 = tile.Data[(j * tile.Width) + i + 1];
        float h01 = tile.Data[((j + 1) * tile.Width) + i];
        float h11 = tile.Data[((j + 1) * tile.Width) + i + 1];

        // Split along the 00-11 diagonal: triangles (00, 10, 11) and (00, 01, 11).
        float first = fx >= fz
            ? h00 + ((h10 - h00) * fx) + ((h11 - h10) * fz)
            : h00 + ((h11 - h01) * fx) + ((h01 - h00) * fz);
        // Split along the 10-01 diagonal: triangles (00, 10, 01) and (11, 10, 01).
        float second = fx + fz <= 1f
            ? h00 + ((h10 - h00) * fx) + ((h01 - h00) * fz)
            : h11 + ((h01 - h11) * (1f - fx)) + ((h10 - h11) * (1f - fz));

        y = MathF.Max(first, second) + tile.OriginY;
        slack = MathF.Abs(first - second);
    }

    // Triangle soup with a median-split BVH. One of these is shared by every instance of a collider, so a
    // map with a hundred thousand fences stores one fence.
    internal sealed class MeshGeometry
    {
        private readonly Vector3[] _faces; // 3 vertices per triangle, shape-local
        private readonly int[] _order;     // triangle indices, leaf-contiguous
        private readonly Node[] _nodes;

        private struct Node
        {
            public Vector3 Min;
            public Vector3 Max;
            public int First;  // interior: left child; leaf: first entry in _order
            public int Second; // interior: right child
            public int Count;  // 0 for an interior node
        }

        public Vector3 Min { get; }
        public Vector3 Max { get; }
        public int Triangles => _order.Length;

        public MeshGeometry(Vector3[] faces)
        {
            _faces = faces;
            int triangles = faces.Length / 3;
            _order = new int[triangles];
            for (int i = 0; i < triangles; i++)
                _order[i] = i;
            // At least one triangle by construction: CollisionFieldBuilder only builds geometry for a
            // shape that has one, and drops the instances of any shape that does not.
            _nodes = new Node[(triangles * 2) - 1];
            _nodeCount = 0;
            Build(0, triangles);
            Min = _nodes[0].Min;
            Max = _nodes[0].Max;
        }

        private int _nodeCount;
        private const int LeafSize = 8;

        private int Build(int start, int count)
        {
            int index = _nodeCount++;
            Bounds(start, count, out Vector3 min, out Vector3 max);
            _nodes[index].Min = min;
            _nodes[index].Max = max;
            if (count <= LeafSize)
            {
                _nodes[index].First = start;
                _nodes[index].Count = count;
                return index;
            }

            Vector3 extent = max - min;
            int axis = extent.X > extent.Y ? (extent.X > extent.Z ? 0 : 2) : (extent.Y > extent.Z ? 1 : 2);
            int middle = start + (count / 2);
            NthElement(start, count, middle, axis);
            _nodes[index].Count = 0;
            int left = Build(start, middle - start);
            int right = Build(middle, start + count - middle);
            _nodes[index].First = left;
            _nodes[index].Second = right;
            return index;
        }

        private void Bounds(int start, int count, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int n = start; n < start + count; n++)
            {
                int face = _order[n] * 3;
                for (int v = 0; v < 3; v++)
                {
                    min = min.Min(_faces[face + v]);
                    max = max.Max(_faces[face + v]);
                }
            }
        }

        private float Centroid(int triangle, int axis)
        {
            int face = triangle * 3;
            return (_faces[face][axis] + _faces[face + 1][axis] + _faces[face + 2][axis]) / 3f;
        }

        // Partial sort: only the pivot position has to be correct, which is all a median split needs.
        private void NthElement(int start, int count, int nth, int axis)
        {
            int low = start, high = start + count - 1;
            while (low < high)
            {
                float pivot = Centroid(_order[(low + high) / 2], axis);
                int i = low, j = high;
                while (i <= j)
                {
                    while (Centroid(_order[i], axis) < pivot) i++;
                    while (Centroid(_order[j], axis) > pivot) j--;
                    if (i <= j)
                    {
                        (_order[i], _order[j]) = (_order[j], _order[i]);
                        i++;
                        j--;
                    }
                }
                if (nth <= j) high = j;
                else if (nth >= i) low = i;
                else break;
            }
        }

        // Every triangle the ray crosses inside the segment, reduced to the nearest of each class. A
        // collision mesh is a tessellated shell, so a probe landing on the diagonal shared by two coplanar
        // triangles grazes both — and is corroborated by neither. Reporting the classes separately is what
        // lets the caller tell that apart from a probe brushing the edge of a real sill.
        public Crossing Intersect(Vector3 origin, Vector3 direction, float length)
        {
            float nearest = float.MaxValue, solid = float.MaxValue;
            // The nearest cluster of edge crossings, and how many triangles are in it. A shell's INTERNAL
            // edges are shared by two faces, so a probe in the band around one crosses both at the same
            // height — and a surface two faces agree on is not a silhouette, whatever the band says. Only
            // the nearest cluster is tracked because only it can outrank the answer; anything below the
            // highest solid surface cannot change which surface that is.
            float edgeAt = float.MaxValue;
            float edgeInsideAt = float.MaxValue;
            int edgeFaces = 0;
            bool edgeInside = false;
            // Deliberately NOT tracked separately: whether the face is turned toward the ray or away from
            // it. ObjectsBuilder leaves BackfaceCollision off on its ConcavePolygonShape3Ds, so in
            // principle the server culls a back-facing crossing and this should escalate rather than
            // claim one. Measured on PEI's 42,642 faces, it does not. Escalating them took the uncertain
            // probes from 842 to 99,525 — a third of every probe on the map — and the confirmation set
            // from 8,054 faces to 21,488 (19% to 50%), while the audit's disagreement count did not move
            // at all (90 either way) and the verdict differences went 19 to 15. A third of mesh crossings
            // being back-facing while the server reports the same surfaces the two-sided test does says
            // the server is not culling them here: the cost was the whole saving and the correction was
            // noise, so this stays two-sided.
            //
            // If that changes — a Godot release that culls, or a collider set wound the other way — the
            // audit is what would show it, as a jump in "the CPU field invented" rather than in timing.
            Span<int> stack = stackalloc int[128];
            int depth = 0;
            stack[depth++] = 0;
            while (depth > 0)
            {
                int index = stack[--depth];
                ref Node node = ref _nodes[index];
                // The bound is the whole segment, not the nearest crossing so far: a farther triangle can
                // still be the one that grazes, and the caller needs to hear about it.
                if (!SlabOverlap(node.Min, node.Max, origin, direction, length))
                    continue;
                if (node.Count == 0)
                {
                    stack[depth++] = node.First;
                    stack[depth++] = node.Second;
                    continue;
                }
                for (int n = node.First; n < node.First + node.Count; n++)
                {
                    int face = _order[n] * 3;
                    Classify(_faces[face], _faces[face + 1], _faces[face + 2], origin, direction, length,
                        out float t, out bool inside, out bool edge);
                    if (inside && t < nearest)
                        nearest = t;
                    if (inside && !edge && t < solid)
                        solid = t;
                    if (!edge)
                        continue;
                    if (t < edgeAt - GrazeEpsilon)
                    {
                        edgeAt = t; // a nearer cluster supersedes whatever was found below it
                        edgeFaces = 1;
                        edgeInside = inside;
                        edgeInsideAt = inside ? t : float.MaxValue;
                    }
                    else if (t <= edgeAt + GrazeEpsilon)
                    {
                        edgeAt = MathF.Min(edgeAt, t);
                        edgeFaces++;
                        edgeInside |= inside;
                        if (inside && t < edgeInsideAt)
                            edgeInsideAt = t;
                    }
                }
            }

            // Two faces crossed at the same height with the probe inside one of them: the shared edge of a
            // tessellated surface, corroborated by the neighbour rather than in doubt. Two faces crossed
            // with the probe inside NEITHER is the same edge seen from outside the shell, which is exactly
            // the silhouette this band exists to catch — so the inside test is what separates them.
            //
            // The height promoted is the interior crossing's, never the cluster's lowest. A collision mesh
            // can hold disconnected shells, and nothing stops one's silhouette from falling within the
            // band of another's interior; taking the cluster minimum would then report a surface at a
            // height no face was actually entered through. Only the crossing the probe passed inside of
            // is evidence of one.
            bool corroborated = edgeFaces >= 2 && edgeInside;
            if (corroborated && edgeInsideAt < solid)
                solid = edgeInsideAt;
            float unresolved = edgeFaces > 0 && !corroborated ? edgeAt : float.MaxValue;

            return new Crossing(nearest < float.MaxValue, nearest < float.MaxValue ? nearest : 0f,
                solid < float.MaxValue, solid < float.MaxValue ? solid : 0f,
                unresolved < float.MaxValue, unresolved < float.MaxValue ? unresolved : 0f);
        }

        // Widened by the graze band, like the instance bounds are and for the same reason: a probe a
        // millimetre outside a face is exactly the one the band exists to catch, and node bounds trimmed
        // to the geometry would cull it before any triangle got the chance to report it.
        private static bool SlabOverlap(Vector3 min, Vector3 max, Vector3 origin, Vector3 direction,
            float length)
        {
            float near = 0f, far = length;
            for (int axis = 0; axis < 3; axis++)
            {
                float o = origin[axis], d = direction[axis];
                if (MathF.Abs(d) < 1e-9f)
                {
                    if (o < min[axis] - GrazeEpsilon || o > max[axis] + GrazeEpsilon)
                        return false;
                    continue;
                }
                float inverse = 1f / d;
                float t0 = (min[axis] - GrazeEpsilon - o) * inverse;
                float t1 = (max[axis] + GrazeEpsilon - o) * inverse;
                if (t0 > t1)
                    (t0, t1) = (t1, t0);
                near = MathF.Max(near, t0);
                far = MathF.Min(far, t1);
                if (near > far)
                    return false;
            }
            return true;
        }

        // Moller-Trumbore. `inside` is the plain geometric answer; `edge` says the crossing landed within
        // GrazeEpsilon of the triangle's boundary — inside it or just outside — which is the band where
        // the collision engine decides and this code does not.
        //
        // Two-sided on purpose — a collision mesh is a shell and the probe may enter it from either face.
        // See the note in Intersect for why the facing is not used to cull.
        private static void Classify(Vector3 a, Vector3 b, Vector3 c, Vector3 origin, Vector3 direction,
            float length, out float t, out bool inside, out bool edge)
        {
            t = 0f;
            inside = false;
            edge = false;
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 p = direction.Cross(ac);
            float determinant = ab.Dot(p);
            if (MathF.Abs(determinant) < 1e-12f)
                return; // edge-on: contributes no surface to a ray running along it
            float inverse = 1f / determinant;
            Vector3 s = origin - a;
            float u = s.Dot(p) * inverse;
            Vector3 q = s.Cross(ab);
            float v = direction.Dot(q) * inverse;
            float w = 1f - u - v;

            // A barycentric coordinate is a fraction of the triangle, so the metric band has to be
            // converted into one — and the conversion is per coordinate, not per triangle. The distance
            // from a point to the edge opposite a vertex is that vertex's coordinate times the ALTITUDE
            // to that edge, so the band in barycentric terms is GrazeEpsilon over that altitude, which is
            // |opposite edge| / twice the area. Dividing by one length for all three (the longest side,
            // say) is only right for an equilateral triangle: on a long sliver — which is most of what a
            // collision mesh is made of — it understates the band along the long edge by the aspect ratio,
            // and a probe a millimetre off that edge would be called a certain miss.
            float twiceArea = ab.Cross(ac).Length();
            if (twiceArea < 1e-9f)
                return; // no area: nothing to stand on, and no altitude to measure a band against
            float scale = GrazeEpsilon / twiceArea;
            float tolerateU = scale * (a - c).Length(); // u weights B, whose opposite edge is CA
            float tolerateV = scale * ab.Length();      // v weights C, opposite AB
            float tolerateW = scale * (c - b).Length(); // w weights A, opposite BC
            if (u < -tolerateU || v < -tolerateV || w < -tolerateW)
                return;

            float hit = ac.Dot(q) * inverse;
            if (hit < 0f || hit > length)
                return;
            t = hit;
            inside = u >= 0f && v >= 0f && w >= 0f;
            edge = u < tolerateU || v < tolerateV || w < tolerateW;
        }
    }
}

// One probe's answer. `Slack` is the height uncertainty the CPU field knows about (heightfield
// triangulation); `Uncertain` means the answer itself has to be confirmed against the physics server.
public readonly record struct SurfaceSample(bool Hit, float Y, float Slack, bool Uncertain);
