using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Data;

// How a group of placements over one mesh is cut into cells, and how far the batch that results keeps its
// full-detail level. Four numbers, because they are the four that trade against each other:
//
//   * `BaseCellMetres` is the cell size to start from, and zero means "do not cut at all" — the A/B
//     control. The trade it makes is that finer cells shed geometry at eye level, where most of a group is
//     behind the camera or beyond it, and cost draw calls in views that take in the whole map at once,
//     where nothing can be rejected.
//   * `MinCellTriangles` is the geometry an average cell must carry for its own draw call to be worth
//     taking. It is what lets one base size suit both a group of heavy copies and a group of tiny ones:
//     see CellSizeFor. Zero restores a single fixed cell size for every group.
//   * `MaxCellMetres` bounds CellSizeFor's coarsening walk. Past the widest map a cell holds every copy of
//     a group, so going further cannot change the partition — this only stops degenerate coordinates
//     spinning the loop.
//   * `RequireSpread` is whether a group that fits inside one cell is cut anyway. It cannot be, usefully:
//     the partition would emit one cell and the batch would be what it already was.
public readonly record struct PartitionSettings(float BaseCellMetres, long MinCellTriangles,
    float MaxCellMetres, bool RequireSpread)
{
    // Whether the group's spread satisfies the policy. The answer is passed in rather than measured here
    // because the caller needs the same reading for its own sparse-group gate, and ExceedsCellSpan walks
    // every placement.
    public bool SpreadSatisfied(bool spansMoreThanOneCell) => !RequireSpread || spansMoreThanOneCell;
}

// A batch's placement bounds: the centre its transforms are rebased around, and how far the furthest
// placement sits from that centre.
public readonly record struct BatchBounds(Vector3 Centre, float Radius);

// The spatial partitioning behind the object renderer's batches — which cell a placement lands in, how
// wide the cells for a group should be, and how far the batch that results holds its detail.
//
// It lives here rather than in the builder that calls it because it is the most consequential policy in
// the renderer and, inside a static Godot class, was reachable only through the structural-metrics gate:
// a run of the whole game compared against a golden JSON. That gate catches drift, but it cannot say which
// property broke, and the termination of CellSizeFor's doubling walk was asserted by a comment.
//
// Nothing here touches an engine resource. The Vector3/Transform3D it works in are managed structs core/
// already uses freely; the caller keeps everything that needs a Mesh — the triangle count and the bounding
// radius — on its side of the seam.
//
// The lists are `List<Transform3D>` rather than the wider IReadOnlyList on purpose: CellSizeFor walks
// every placement once per doubling, and the struct enumerator is what keeps that free of a per-element
// interface call.
public static class ObjectBatchPartition
{
    // Shortest range a batch may switch level at, whatever its mesh measures. A tiny mesh would otherwise
    // derive a switch distance the player is always inside of, which is a second batch drawn for nothing.
    public const float MinSwitchDistance = 16f;

    // The grid is laid out from the group's own lowest corner, not from the world origin. Anchoring it at
    // the origin makes the partition depend on where the map happens to sit relative to it: a group
    // straddling the origin is cut along it however large the cells are, so it can never fall below four
    // batches — and on a map centred there, that is most of them. Anchoring per group means the cell size
    // alone decides how a group is cut, wherever it lies.
    public static (int X, int Z) CellOf(Vector3 origin, Vector3 anchor, float cellSize) => (
        Mathf.FloorToInt((origin.X - anchor.X) / cellSize),
        Mathf.FloorToInt((origin.Z - anchor.Z) / cellSize));

    public static Vector3 AnchorOf(List<Transform3D> transforms)
    {
        Vector3 anchor = transforms[0].Origin;
        foreach (Transform3D transform in transforms)
            anchor = anchor.Min(transform.Origin);
        return anchor;
    }

    public static Dictionary<(int X, int Z), List<Transform3D>> Cells(
        List<Transform3D> transforms, float cellSize)
    {
        Vector3 anchor = AnchorOf(transforms);
        var cells = new Dictionary<(int X, int Z), List<Transform3D>>();
        foreach (Transform3D transform in transforms)
        {
            (int X, int Z) cell = CellOf(transform.Origin, anchor, cellSize);
            if (!cells.TryGetValue(cell, out List<Transform3D>? inCell))
                cells[cell] = inCell = new List<Transform3D>();
            inCell.Add(transform);
        }
        return cells;
    }

    public static int CellCount(List<Transform3D> transforms, float cellSize)
    {
        Vector3 anchor = AnchorOf(transforms);
        var seen = new HashSet<(int X, int Z)>();
        foreach (Transform3D transform in transforms)
            seen.Add(CellOf(transform.Origin, anchor, cellSize));
        return seen.Count;
    }

    // One cell size cannot suit every group. A split trades a draw call for the chance to reject geometry,
    // so it pays in proportion to how much geometry lands in each cell it creates: cutting a group of
    // heavy copies into cells sheds most of it at eye level, while cutting a group of tiny ones scatters
    // near-empty batches across the map that reject almost nothing and are pure cost in any view that
    // takes the whole map in at once. Both live in the same scene, and the count of cells a fixed size
    // produces also grows with the map, so a single tuned number is either too fine for one map or too
    // coarse for the other.
    //
    // Coarsen per group instead, from the configured size, until an average cell carries enough geometry
    // to earn its draw call. Doubling keeps the grids nested, so the cell count falls monotonically and
    // the walk always terminates — in the limit at the whole group in one cell, which is what a group too
    // light to be worth splitting should be. Both of those are asserted rather than asserted-in-a-comment;
    // see ObjectBatchPartitionTests.
    public static float CellSizeFor(List<Transform3D> transforms, long trianglesPerInstance,
        in PartitionSettings settings)
    {
        if (settings.MinCellTriangles <= 0 || trianglesPerInstance <= 0)
            return settings.BaseCellMetres;
        float metres = settings.BaseCellMetres;
        while (metres < settings.MaxCellMetres)
        {
            int cells = CellCount(transforms, metres);
            if (cells <= 1
                || trianglesPerInstance * transforms.Count / cells >= settings.MinCellTriangles)
                return metres;
            metres *= 2f;
        }
        return metres;
    }

    // Whether the group reaches across more than one cell of this size. Cheaper than counting the cells:
    // it stops at the first placement that proves the span, which for a map-wide group is the second one.
    public static bool ExceedsCellSpan(List<Transform3D> transforms, float cellSize)
    {
        float minX = transforms[0].Origin.X, maxX = minX;
        float minZ = transforms[0].Origin.Z, maxZ = minZ;
        foreach (Transform3D transform in transforms)
        {
            Vector3 p = transform.Origin;
            minX = Mathf.Min(minX, p.X); maxX = Mathf.Max(maxX, p.X);
            minZ = Mathf.Min(minZ, p.Z); maxZ = Mathf.Max(maxZ, p.Z);
            if (maxX - minX > cellSize || maxZ - minZ > cellSize)
                return true;
        }
        return false;
    }

    public static BatchBounds BoundsOf(List<Transform3D> transforms)
    {
        Vector3 min = transforms[0].Origin, max = min;
        foreach (Transform3D t in transforms)
        {
            min = new Vector3(Mathf.Min(min.X, t.Origin.X), Mathf.Min(min.Y, t.Origin.Y),
                Mathf.Min(min.Z, t.Origin.Z));
            max = new Vector3(Mathf.Max(max.X, t.Origin.X), Mathf.Max(max.Y, t.Origin.Y),
                Mathf.Max(max.Z, t.Origin.Z));
        }
        return new BatchBounds((min + max) * 0.5f, (max - min).Length() * 0.5f);
    }

    // Unity switches level by projected screen height, so the threshold scales with the object: a tree
    // holds its detail much further out than a crate. Approximated by a multiple of the mesh's bounding
    // radius, which the caller measures — that is the one thing here that needs the mesh resource.
    //
    // The screen size a placement covers is that radius times the scale it was placed at, and the batch
    // shares one visibility range, so the LARGEST placement in the batch sets the distance: a 2x-scaled
    // copy must keep its detail twice as far out, or it visibly swaps down while the unscaled copies
    // beside it do not.
    public static float SwitchDistance(float meshRadius, List<Transform3D> transforms, float switchRadii)
    {
        float maxScale = 0f;
        foreach (Transform3D transform in transforms)
        {
            Vector3 scale = transform.Basis.Scale.Abs();
            maxScale = Mathf.Max(maxScale, Mathf.Max(scale.X, Mathf.Max(scale.Y, scale.Z)));
        }
        return Mathf.Max(MinSwitchDistance, meshRadius * maxScale * switchRadii);
    }
}
