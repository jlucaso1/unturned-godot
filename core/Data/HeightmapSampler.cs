using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Data;

// Samples the terrain height at an arbitrary world XZ. It interpolates on the SAME triangulation the terrain
// mesh is built with (each heightmap quad split along its (hx,hy)-(hx+1,hy+1) diagonal, TerrainBuilder), so a
// conformed feature lands exactly on the rendered surface — not a bilinear approximation that drifts off the
// triangles on slopes and leaves a lip or z-fights. This is what lets roads hug the ground with only a hair's
// z-offset, the way Unturned's roads sit on Unity's (bilinear) terrain.
//
// "The same triangulation" is a claim about the whole grid, and it stopped being true once: the mesh was
// built at every second heightmap index while this stayed at every index, so a ridge or a gully on an odd
// index was kept here and interpolated away there, and a road lofted onto it buried itself in the drawn
// ground or floated over it. The mesh is built at the heightmap's own resolution again, which is what
// makes the sentence above a fact rather than an intention — TerrainHeightfieldTests holds it to that by
// sampling a tile MeshInstance's own vertices.
//
// Terrain HOLES are deliberately not consulted. A hole removes a cell from what is drawn and from what is
// collided with, but the heightmap underneath it still has values, and Landscape.getWorldHeight — which is
// what Unturned conforms roads and placements with — reads those values without asking about holes. A
// caller that needs to know whether there is ground here at all is asking a different question, and
// Landscape.IsPointInsideHole is what answers it.
public sealed class HeightmapSampler
{
    private static readonly bool CompactHeightmaps = EnvFlag.IsOn(System.Environment.GetEnvironmentVariable("UG_COMPACT_HEIGHTMAP"), whenUnset: true);
    private const int Res = Landscape.HEIGHTMAP_RESOLUTION_MINUS_ONE; // 256 cells across a tile
    private readonly Dictionary<(int X, int Y), TileData> _tiles = new();

    private readonly record struct TileData(float[,]? Floats, ushort[]? Raw)
    {
        public float At(int x, int y) => Raw != null
            ? Raw[(x * Landscape.HEIGHTMAP_RESOLUTION) + y] / (float)ushort.MaxValue
            : Floats![x, y];
    }

    public HeightmapSampler(IEnumerable<HeightmapTile> tiles)
    {
        foreach (HeightmapTile t in tiles)
            _tiles[(t.CoordX, t.CoordY)] = CompactHeightmaps && t.RawSamples != null
                ? new TileData(null, t.RawSamples) : new TileData(t.Heights, null);
    }

    public long StorageBytes
    {
        get
        {
            long bytes = 0;
            foreach (TileData tile in _tiles.Values)
                bytes += tile.Raw != null ? (long)tile.Raw.Length * sizeof(ushort)
                    : (long)tile.Floats!.Length * sizeof(float);
            return bytes;
        }
    }

    // Returns false when the point lies on a tile that wasn't loaded (caller keeps its own height then).
    public bool TrySampleHeight(float worldX, float worldZ, out float worldY)
    {
        if (!TryLocate(worldX, worldZ, out TileData heights, out _, out _,
            out int hx0, out int hy0, out float tx, out float ty))
        {
            worldY = 0f;
            return false;
        }

        // Planar-interpolate on the quad's triangle the point falls in. The mesh splits each quad along the
        // (0,0)-(1,1) diagonal: the tx >= ty half is the triangle (h00, h10, h11), the other (h00, h11, h01).
        float h00 = heights.At(hx0, hy0);
        float h11 = heights.At(hx0 + 1, hy0 + 1);
        float h01 = tx >= ty
            ? h00 + (tx * (heights.At(hx0 + 1, hy0) - h00)) + (ty * (h11 - heights.At(hx0 + 1, hy0)))
            : h00 + (ty * (heights.At(hx0, hy0 + 1) - h00)) + (tx * (h11 - heights.At(hx0, hy0 + 1)));

        worldY = (-Landscape.TILE_HEIGHT / 2f) + (h01 * Landscape.TILE_HEIGHT);
        return true;
    }

    // The SMOOTH surface normal (Unity space, +Z north like worldZ) at a world XZ: a normal is derived at
    // each of the cell's four heightmap corners by central difference, then bilinearly interpolated across
    // the cell. This is what Unturned actually gets — Road.buildMesh normals come from
    // LevelGround.getNormal, which is Landscape.getNormal, which is Unity's
    // TerrainData.GetInterpolatedNormal (Framework/Landscapes/Landscape.cs).
    //
    // It deliberately does NOT return the plane normal of the triangle TrySampleHeight interpolates on.
    // That is constant within a triangle and jumps at every triangle edge, and since a road takes this
    // normal for all four vertices of each loft row (Road.cs:899-902), the jumps show up as hard shading
    // seams running straight across the carriageway — measured at a 6% luminance step on PEI, with hue and
    // saturation unchanged, which is the signature of identical albedo lit at a different angle.
    //
    // Height still comes off the triangulation (TrySampleHeight): the surface a road sits ON is the
    // rendered mesh, while the normal it shades BY is the smooth field. That split is Unity's too.
    public bool TrySampleNormal(float worldX, float worldZ, out Vector3 normal)
    {
        if (!TryLocate(worldX, worldZ, out TileData heights, out int tileX, out int tileY,
            out int hx0, out int hy0, out float tx, out float ty))
        {
            normal = Vector3.Up;
            return false;
        }

        Vector3 n00 = CornerNormal(heights, tileX, tileY, hx0, hy0);
        Vector3 n10 = CornerNormal(heights, tileX, tileY, hx0 + 1, hy0);
        Vector3 n01 = CornerNormal(heights, tileX, tileY, hx0, hy0 + 1);
        Vector3 n11 = CornerNormal(heights, tileX, tileY, hx0 + 1, hy0 + 1);

        // tx runs along hx (worldZ), ty along hy (worldX) — the transposed indexing TryLocate sets up.
        normal = n00.Lerp(n10, tx).Lerp(n01.Lerp(n11, tx), ty).Normalized();
        return true;
    }

    // Central-difference normal at one heightmap grid point. Sampling steps into the neighbouring tile
    // when the point sits on a tile edge, so the field stays continuous across tile seams — clamping there
    // instead would reintroduce exactly the shading seam this method exists to remove, just at tile
    // boundaries rather than triangle ones.
    private Vector3 CornerNormal(TileData own, int tileX, int tileY, int hx, int hy)
    {
        const float cell = Landscape.TILE_SIZE / (float)Res; // metres between height samples

        (float minus, float plus, float span) alongZ = Span(own, tileX, tileY, hx, hy, stepHx: 1, stepHy: 0);
        (float minus, float plus, float span) alongX = Span(own, tileX, tileY, hx, hy, stepHx: 0, stepHy: 1);

        // span is never zero: the index runs 0..Res, so when one side steps outside the tile the other
        // is still inside it, and Span always resolves at least one neighbour.
        float slopeZ = (alongZ.plus - alongZ.minus) * Landscape.TILE_HEIGHT / (alongZ.span * cell);
        float slopeX = (alongX.plus - alongX.minus) * Landscape.TILE_HEIGHT / (alongX.span * cell);

        return new Vector3(-slopeX, 1f, -slopeZ).Normalized();
    }

    // The two samples straddling a grid point along one axis, plus how many cells actually separate them.
    // At the very edge of the loaded world a neighbour is missing, so that side falls back to the centre
    // sample and the span halves — a one-sided difference, still the correct slope for a linear surface.
    private (float Minus, float Plus, float Span) Span(TileData own, int tileX, int tileY, int hx, int hy,
        int stepHx, int stepHy)
    {
        float centre = own.At(hx, hy);
        bool hasMinus = TryHeightAt(tileX, tileY, hx - stepHx, hy - stepHy, out float minus);
        bool hasPlus = TryHeightAt(tileX, tileY, hx + stepHx, hy + stepHy, out float plus);
        return (hasMinus ? minus : centre, hasPlus ? plus : centre,
            (hasMinus ? 1f : 0f) + (hasPlus ? 1f : 0f));
    }

    // Reads a height by grid index, rolling indices outside [0, Res] over into the adjacent tile. Adjacent
    // tiles duplicate their shared edge (index Res of one is index 0 of the next), so one step past the
    // edge is index 1 of the neighbour.
    private bool TryHeightAt(int tileX, int tileY, int hx, int hy, out float value)
    {
        if (hx < 0)
        {
            tileY--;
            hx += Res;
        }
        else if (hx > Res)
        {
            tileY++;
            hx -= Res;
        }

        if (hy < 0)
        {
            tileX--;
            hy += Res;
        }
        else if (hy > Res)
        {
            tileX++;
            hy -= Res;
        }

        if (_tiles.TryGetValue((tileX, tileY), out TileData heights))
        {
            value = heights.At(hx, hy);
            return true;
        }

        value = 0f;
        return false;
    }

    // Resolves a world XZ to its tile, cell indices and in-cell fractions (shared by height and normal).
    private bool TryLocate(float worldX, float worldZ,
        out TileData heights,
        out int tileX, out int tileY, out int hx0, out int hy0, out float tx, out float ty)
    {
        tileX = Mathf.FloorToInt(worldX / Landscape.TILE_SIZE);
        tileY = Mathf.FloorToInt(worldZ / Landscape.TILE_SIZE);
        if (!_tiles.TryGetValue((tileX, tileY), out heights))
        {
            hx0 = hy0 = 0;
            tx = ty = 0f;
            return false;
        }

        // GetWorldPosition maps hy -> worldX and hx -> worldZ (transposed), height indices in [0, 256].
        float hy = ((worldX / Landscape.TILE_SIZE) - tileX) * Res;
        float hx = ((worldZ / Landscape.TILE_SIZE) - tileY) * Res;
        hx0 = Mathf.Clamp((int)hx, 0, Res - 1);
        hy0 = Mathf.Clamp((int)hy, 0, Res - 1);
        tx = hx - hx0;
        ty = hy - hy0;
        return true;
    }
}

// Heightmap files retain Unity's +Z convention, while loaded Godot positions have already been mirrored
// to -Z. Keep that boundary in core so every runtime/editor caller samples the same physical point.
public static class TerrainCoordinates
{
    public static bool TrySampleGodotHeight(HeightmapSampler sampler, float godotX, float godotZ,
        out float worldY) => sampler.TrySampleHeight(godotX, -godotZ, out worldY);
}
