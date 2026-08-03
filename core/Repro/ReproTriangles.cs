using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Repro;

// The geometry a dump's collision slice is made of, and the two questions anyone asks of it: does a
// swept capsule hit anything, and does a ray. Both are pure functions of the triangles, which is the
// point — a replay running this has no engine, no physics server and no frame rate, and still answers
// the same questions the session's physics did.
//
// Everything is indexed on a flat XZ grid built once when the dump loads. The queries below run
// thousands of times per simulated second in a replay, so they allocate nothing: candidates are
// collected into a caller-owned list and the maths is all by value.
public sealed class ReproTriangles
{
    private const float CellSize = 4f;

    private readonly Vector3[] _vertices;
    private readonly int[] _triangles;
    private readonly int[] _layers;
    private readonly int[] _owners;
    private readonly IReadOnlyList<string> _ownerNames;

    // Counting-sorted grid: _cellStart[cell].._cellStart[cell+1] indexes into _cellItems.
    private readonly int[] _cellStart;
    private readonly int[] _cellItems;
    private readonly int _minCellX;
    private readonly int _minCellZ;
    private readonly int _cellsX;
    private readonly int _cellsZ;

    public int TriangleCount => _triangles.Length / 3;

    public ReproTriangles(Vector3[] vertices, int[] triangles, int[] layers, int[] owners,
        IReadOnlyList<string>? ownerNames = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(triangles);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(owners);
        _vertices = vertices;
        _triangles = triangles;
        _layers = layers;
        _owners = owners;
        _ownerNames = ownerNames ?? Array.Empty<string>();

        int count = TriangleCount;
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        for (int i = 0; i < count; i++)
        {
            (Vector3 a, Vector3 b, Vector3 c) = At(i);
            minX = MathF.Min(minX, MathF.Min(a.X, MathF.Min(b.X, c.X)));
            maxX = MathF.Max(maxX, MathF.Max(a.X, MathF.Max(b.X, c.X)));
            minZ = MathF.Min(minZ, MathF.Min(a.Z, MathF.Min(b.Z, c.Z)));
            maxZ = MathF.Max(maxZ, MathF.Max(a.Z, MathF.Max(b.Z, c.Z)));
        }
        if (count == 0)
        {
            minX = minZ = maxX = maxZ = 0f;
        }
        _minCellX = Cell(minX);
        _minCellZ = Cell(minZ);
        _cellsX = Cell(maxX) - _minCellX + 1;
        _cellsZ = Cell(maxZ) - _minCellZ + 1;

        var counts = new int[(_cellsX * _cellsZ) + 1];
        for (int i = 0; i < count; i++)
        {
            (int x0, int x1, int z0, int z1) = CellsOf(i);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                    counts[(x * _cellsZ) + z + 1]++;
        }
        for (int i = 1; i < counts.Length; i++)
            counts[i] += counts[i - 1];
        _cellStart = counts;
        _cellItems = new int[counts[^1]];
        var fill = new int[_cellsX * _cellsZ];
        for (int i = 0; i < count; i++)
        {
            (int x0, int x1, int z0, int z1) = CellsOf(i);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    int cell = (x * _cellsZ) + z;
                    _cellItems[_cellStart[cell] + fill[cell]++] = i;
                }
        }
    }

    public static ReproTriangles? From(ReproGeometryData? data)
    {
        if (data == null || data.Triangles.Length < 3)
            return null;
        var vertices = new Vector3[data.Vertices.Length / 3];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = new Vector3(data.Vertices[i * 3], data.Vertices[(i * 3) + 1],
                data.Vertices[(i * 3) + 2]);
        int count = data.Triangles.Length / 3;
        int[] layers = data.Layers.Length == count
            ? data.Layers
            : Fill(count, (int)CollisionLayers.World);
        int[] owners = data.Owners.Length == count ? data.Owners : new int[count];
        return new ReproTriangles(vertices, data.Triangles, layers, owners, data.OwnerNames);
    }

    private static int[] Fill(int count, int value)
    {
        var array = new int[count];
        Array.Fill(array, value);
        return array;
    }

    public string OwnerOf(int triangle) =>
        triangle >= 0 && triangle < _owners.Length && _owners[triangle] < _ownerNames.Count
            ? _ownerNames[_owners[triangle]]
            : "?";

    private (Vector3 A, Vector3 B, Vector3 C) At(int triangle) => (
        _vertices[_triangles[triangle * 3]],
        _vertices[_triangles[(triangle * 3) + 1]],
        _vertices[_triangles[(triangle * 3) + 2]]);

    private static int Cell(float value) => (int)MathF.Floor(value / CellSize);

    private (int X0, int X1, int Z0, int Z1) CellsOf(int triangle)
    {
        (Vector3 a, Vector3 b, Vector3 c) = At(triangle);
        return (
            Math.Clamp(Cell(MathF.Min(a.X, MathF.Min(b.X, c.X))) - _minCellX, 0, _cellsX - 1),
            Math.Clamp(Cell(MathF.Max(a.X, MathF.Max(b.X, c.X))) - _minCellX, 0, _cellsX - 1),
            Math.Clamp(Cell(MathF.Min(a.Z, MathF.Min(b.Z, c.Z))) - _minCellZ, 0, _cellsZ - 1),
            Math.Clamp(Cell(MathF.Max(a.Z, MathF.Max(b.Z, c.Z))) - _minCellZ, 0, _cellsZ - 1));
    }

    // Triangles whose cells overlap the XZ box, appended to `into` without duplicates. The dedupe is a
    // generation stamp rather than a set, so a query costs no allocation and no hashing.
    private int[]? _seen;
    private int _generation;

    public void Gather(float minX, float minZ, float maxX, float maxZ, List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (_cellItems.Length == 0)
            return;
        // Entirely off the slice: nothing here, rather than the nearest border cell's contents, which
        // a clamp would hand back and which is not what is at that spot.
        int requestedX0 = Cell(minX) - _minCellX, requestedX1 = Cell(maxX) - _minCellX;
        int requestedZ0 = Cell(minZ) - _minCellZ, requestedZ1 = Cell(maxZ) - _minCellZ;
        if (requestedX1 < 0 || requestedX0 >= _cellsX || requestedZ1 < 0 || requestedZ0 >= _cellsZ)
            return;
        _seen ??= new int[TriangleCount];
        _generation++;
        int x0 = Math.Clamp(requestedX0, 0, _cellsX - 1);
        int x1 = Math.Clamp(requestedX1, 0, _cellsX - 1);
        int z0 = Math.Clamp(requestedZ0, 0, _cellsZ - 1);
        int z1 = Math.Clamp(requestedZ1, 0, _cellsZ - 1);
        for (int x = x0; x <= x1; x++)
            for (int z = z0; z <= z1; z++)
            {
                int cell = (x * _cellsZ) + z;
                for (int i = _cellStart[cell]; i < _cellStart[cell + 1]; i++)
                {
                    int triangle = _cellItems[i];
                    if (_seen[triangle] == _generation)
                        continue;
                    _seen[triangle] = _generation;
                    into.Add(triangle);
                }
            }
    }

    public bool Matches(int triangle, uint mask) => ((uint)_layers[triangle] & mask) != 0u;

    // Closest approach between the capsule's core segment and a triangle. Returns the squared distance
    // and, through `direction`, where the capsule sits relative to the surface — the contact normal a
    // slide projects onto.
    public float DistanceSquared(int triangle, Vector3 p0, Vector3 p1, out Vector3 direction)
    {
        (Vector3 a, Vector3 b, Vector3 c) = At(triangle);
        return ReproGeometry.SegmentTriangleDistanceSquared(p0, p1, a, b, c, out direction);
    }

    public Vector3 NormalOf(int triangle)
    {
        (Vector3 a, Vector3 b, Vector3 c) = At(triangle);
        Vector3 normal = (b - a).Cross(c - a);
        return normal.LengthSquared() > 1e-12f ? normal.Normalized() : Vector3.Up;
    }

    public bool RayTriangle(int triangle, Vector3 from, Vector3 direction, out float distance)
    {
        (Vector3 a, Vector3 b, Vector3 c) = At(triangle);
        return ReproGeometry.RayTriangle(from, direction, a, b, c, out distance);
    }
}
