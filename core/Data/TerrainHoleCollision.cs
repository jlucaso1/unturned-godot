using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Data;

// Cutting a map's terrain holes out of the PHYSICS surface.
//
// Rendering a hole is easy: the mesh is built cell by cell, so a hole cell simply does not emit its two
// triangles. Collision is not, because the tile collides as a HeightMapShape3D — a grid of SAMPLES, with
// no per-cell switch. Unity has one (`TerrainData.SetHoles` is cell-granular and the TerrainCollider
// honours it); Godot's heightfield does not.
//
// What Godot does have is a no-collision sample. Feeding a height of NaN into `HeightMapShape3D.MapData`
// makes Jolt drop every TRIANGLE that has that sample as a vertex — measured rather than assumed, by
// probing a heightfield with one NaN sample on a sub-cell grid (TerrainHoleCollisionTests pins the shape
// of what came back). That is a vertex-granular tool for a cell-granular job, and the mismatch cuts both
// ways: NaN-ing only the samples strictly inside a hole leaves part of the hole solid, while NaN-ing
// every sample the hole touches takes a ragged bite out of the ground AROUND it — up to a whole cell of
// missing collision beside an entrance, which is a player falling through the floor.
//
// So the cut is made deliberately too wide and then repaired. Every sample cornering a hole cell is
// marked no-collision, which guarantees the hole itself is gone whatever triangulation the engine picks;
// then every cell that lost geometry and is NOT a hole is handed back as explicit triangles, at the
// heights the heightfield would have had. The union is exactly the cells the map says exist.
//
// Restoring WHOLE cells rather than only the triangles Jolt actually removed is the deliberate half of
// that. Being exact would mean reproducing the engine's own triangulation here, and being wrong about it
// would open a silent gap in the ground. Being generous costs at most one duplicated triangle per
// repaired cell — and duplicated is the operative word: it is the same three vertices at the same three
// heights, coincident rather than merely close, so a ray finds one surface at one depth and a body rests
// on one plane. A gap cannot be traded for that. It also survives an engine that changes its mind about
// which way to split a quad, which an exact complement could not.
//
// The repaired triangles are the RENDER mesh's own triangles for those cells (same split, same winding,
// same world positions), so where the patch lands the drawn and walked surfaces are not merely close —
// they are the same triangle.
public static class TerrainHoleCollision
{
    // Marks every heightfield sample that corners a hole cell as no-collision, in place.
    //
    // `mapData` is in HeightMapShape3D's layout — data[depth * width + widthIndex], with the depth axis
    // reversed against the heightmap's x (TerrainHeightfield.MapData) — so the sample for heightmap index
    // (hx, hy) lives at ((res - 1 - hx) * res) + hy. Returns whether anything was marked, which is what
    // lets a tile whose file exists but cuts nothing skip the patch entirely.
    public static bool MarkNoCollisionSamples(float[] mapData, LandscapeHoles holes)
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        bool any = false;
        for (int cx = 0; cx < Landscape.HOLES_RESOLUTION; cx++)
        {
            for (int cy = 0; cy < Landscape.HOLES_RESOLUTION; cy++)
            {
                if (!holes.IsHole(cx, cy))
                    continue;
                // The cell's four corners, in heightmap indices.
                mapData[((res - 1 - cx) * res) + cy] = float.NaN;
                mapData[((res - 1 - cx) * res) + cy + 1] = float.NaN;
                mapData[((res - 1 - (cx + 1)) * res) + cy] = float.NaN;
                mapData[((res - 1 - (cx + 1)) * res) + cy + 1] = float.NaN;
                any = true;
            }
        }
        return any;
    }

    // The cells that lost their collision to the marking above without being holes themselves: every cell
    // sharing a corner with a hole cell, minus the holes. That is the 3x3 dilation of the hole set with
    // the holes taken back out, which is the same thing said in grid terms.
    public static List<(int X, int Y)> CellsToRepair(LandscapeHoles holes)
    {
        var repair = new List<(int, int)>();
        for (int cx = 0; cx < Landscape.HOLES_RESOLUTION; cx++)
            for (int cy = 0; cy < Landscape.HOLES_RESOLUTION; cy++)
                if (!holes.IsHole(cx, cy) && TouchesHole(holes, cx, cy))
                    repair.Add((cx, cy));
        return repair;
    }

    // A cell shares a corner with a hole exactly when one of the eight cells around it is a hole: two
    // cells share a corner iff they are neighbours in the 3x3 sense.
    private static bool TouchesHole(LandscapeHoles holes, int cx, int cy)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if ((dx != 0 || dy != 0) && holes.IsHole(cx + dx, cy + dy))
                    return true;
        return false;
    }

    // The repaired cells as a triangle soup for a ConcavePolygonShape3D, in the same world space the tile
    // mesh is built in (TerrainBuilder puts absolute world positions in its vertices and leaves the tile
    // node at the origin, so a shape on that node needs no transform of its own).
    //
    // The split and the winding are the render mesh's, deliberately: these triangles exist so that the
    // ground beside a hole is walkable, and the least surprising surface to walk on is the one being
    // drawn. `height` reads the tile's normalized sample at a heightmap index.
    public static Vector3[] RepairFaces(LandscapeHoles holes, int tileX, int tileY,
        System.Func<int, int, float> height)
    {
        List<(int X, int Y)> cells = CellsToRepair(holes);
        var faces = new Vector3[cells.Count * 6];
        int f = 0;
        foreach ((int cx, int cy) in cells)
        {
            Vector3 v00 = Corner(tileX, tileY, cx, cy, height);
            Vector3 v01 = Corner(tileX, tileY, cx, cy + 1, height);
            Vector3 v10 = Corner(tileX, tileY, cx + 1, cy, height);
            Vector3 v11 = Corner(tileX, tileY, cx + 1, cy + 1, height);
            // The two triangles TerrainBuilder emits for this cell, split (00)-(11) and wound so the
            // front faces point up.
            faces[f++] = v00;
            faces[f++] = v10;
            faces[f++] = v11;
            faces[f++] = v00;
            faces[f++] = v11;
            faces[f++] = v01;
        }
        return faces;
    }

    private static Vector3 Corner(int tileX, int tileY, int hx, int hy,
        System.Func<int, int, float> height) =>
        Landscape.UnityToGodot(Landscape.GetWorldPosition(tileX, tileY, hx, hy, height(hx, hy)));

    // The middle of each cut-away cell, flattened to the Godot XZ plane. A hole is a hole from every
    // distance, so this is what a decimated copy of the tile has to keep clear.
    public static Vector2[] HoleCentres(LandscapeHoles holes, int tileX, int tileY)
    {
        var centres = new List<Vector2>();
        Vector3 origin = Landscape.UnityToGodot(Landscape.GetWorldPosition(tileX, tileY, 0, 0, 0f));
        for (int cx = 0; cx < Landscape.HOLES_RESOLUTION; cx++)
            for (int cy = 0; cy < Landscape.HOLES_RESOLUTION; cy++)
                if (holes.IsHole(cx, cy))
                    centres.Add(new Vector2(
                        origin.X + ((cy + 0.5f) * TerrainHeightfield.CellSize),
                        origin.Z - ((cx + 0.5f) * TerrainHeightfield.CellSize)));
        return centres.ToArray();
    }

    // Whether an indexed triangle list still leaves every hole open.
    //
    // Asked of each level meshoptimizer generates, because it does not keep them: a hole is an open
    // boundary in the middle of the tile, and the simplifier welds it shut once it is coarse enough
    // (measured on a 257x257 tile: intact through the first three levels, closed from the fourth). A
    // level that has closed one is a level that draws ground over an entrance, so the chain is cut before
    // it — see TerrainBuilder. Nothing about that is version-pinned: if a future simplifier keeps them,
    // this keeps every level it is given.
    //
    // Cell CENTRES rather than areas: a triangle that has swallowed a hole covers all of it, and one that
    // only clipped a corner would be a partly-closed hole, which the base mesh cannot produce and the
    // simplifier does not either — it removes vertices, so a surviving triangle spans the ones it merged.
    public static bool KeepsHolesOpen(Vector3[] vertices, int[] indices, Vector2[] holeCentres)
    {
        for (int i = 0; i < indices.Length; i += 3)
        {
            Vector3 a3 = vertices[indices[i]];
            Vector3 b3 = vertices[indices[i + 1]];
            Vector3 c3 = vertices[indices[i + 2]];
            var a = new Vector2(a3.X, a3.Z);
            var b = new Vector2(b3.X, b3.Z);
            var c = new Vector2(c3.X, c3.Z);
            foreach (Vector2 centre in holeCentres)
                if (Covers(a, b, c, centre))
                    return false;
        }
        return true;
    }

    // Point in triangle by consistent winding sign; a point on an edge counts as covered.
    private static bool Covers(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
    {
        float d1 = Side(p, a, b), d2 = Side(p, b, c), d3 = Side(p, c, a);
        bool negative = d1 < 0f || d2 < 0f || d3 < 0f;
        bool positive = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(negative && positive);
    }

    private static float Side(Vector2 p, Vector2 a, Vector2 b) =>
        ((p.X - b.X) * (a.Y - b.Y)) - ((a.X - b.X) * (p.Y - b.Y));
}
