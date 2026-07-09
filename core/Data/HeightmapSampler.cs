using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Data;

// Samples the terrain height at an arbitrary world XZ. It interpolates on the SAME triangulation the terrain
// mesh is built with (each heightmap quad split along its (hx,hy)-(hx+1,hy+1) diagonal, TerrainBuilder), so a
// conformed feature lands exactly on the rendered surface — not a bilinear approximation that drifts off the
// triangles on slopes and leaves a lip or z-fights. This is what lets roads hug the ground with only a hair's
// z-offset, the way Unturned's roads sit on Unity's (bilinear) terrain.
public sealed class HeightmapSampler
{
    private const int Res = Landscape.HEIGHTMAP_RESOLUTION_MINUS_ONE; // 256 cells across a tile
    private readonly Dictionary<(int X, int Y), float[,]> _tiles = new();

    public HeightmapSampler(IEnumerable<HeightmapTile> tiles)
    {
        foreach (HeightmapTile t in tiles)
            _tiles[(t.CoordX, t.CoordY)] = t.Heights;
    }

    // Returns false when the point lies on a tile that wasn't loaded (caller keeps its own height then).
    public bool TrySampleHeight(float worldX, float worldZ, out float worldY)
    {
        if (!TryLocate(worldX, worldZ, out float[,]? heights, out int hx0, out int hy0, out float tx, out float ty))
        {
            worldY = 0f;
            return false;
        }

        // Planar-interpolate on the quad's triangle the point falls in. The mesh splits each quad along the
        // (0,0)-(1,1) diagonal: the tx >= ty half is the triangle (h00, h10, h11), the other (h00, h11, h01).
        float h00 = heights[hx0, hy0];
        float h11 = heights[hx0 + 1, hy0 + 1];
        float h01 = tx >= ty
            ? h00 + (tx * (heights[hx0 + 1, hy0] - h00)) + (ty * (h11 - heights[hx0 + 1, hy0]))
            : h00 + (ty * (heights[hx0, hy0 + 1] - h00)) + (tx * (h11 - heights[hx0, hy0 + 1]));

        worldY = (-Landscape.TILE_HEIGHT / 2f) + (h01 * Landscape.TILE_HEIGHT);
        return true;
    }

    // The surface normal (Unity space, +Z north like worldZ) of the SAME triangle TrySampleHeight reads, so a
    // feature oriented by it (road banking) lies exactly on the rendered surface. Unturned asks Unity's
    // TerrainData.GetInterpolatedNormal here; our terrain is this triangulation, so its plane normal is the
    // faithful translation. Falls back to straight up when the tile isn't loaded.
    public bool TrySampleNormal(float worldX, float worldZ, out Vector3 normal)
    {
        if (!TryLocate(worldX, worldZ, out float[,]? heights, out int hx0, out int hy0, out float tx, out float ty))
        {
            normal = Vector3.Up;
            return false;
        }

        // Each triangle is a plane: constant height slopes along X and Z (hy indexes worldX, hx worldZ).
        const float cell = Landscape.TILE_SIZE / (float)Res; // metres between height samples
        float h00 = heights[hx0, hy0];
        float h11 = heights[hx0 + 1, hy0 + 1];
        float slopeX, slopeZ; // dY/dX, dY/dZ in world units
        if (tx >= ty)
        {
            float h10 = heights[hx0 + 1, hy0];
            slopeZ = (h10 - h00) * Landscape.TILE_HEIGHT / cell;
            slopeX = (h11 - h10) * Landscape.TILE_HEIGHT / cell;
        }
        else
        {
            float h01 = heights[hx0, hy0 + 1];
            slopeX = (h01 - h00) * Landscape.TILE_HEIGHT / cell;
            slopeZ = (h11 - h01) * Landscape.TILE_HEIGHT / cell;
        }

        normal = new Vector3(-slopeX, 1f, -slopeZ).Normalized(); // upward plane normal of y = x*slopeX + z*slopeZ
        return true;
    }

    // Resolves a world XZ to its tile, cell indices and in-cell fractions (shared by height and normal).
    private bool TryLocate(float worldX, float worldZ,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out float[,]? heights,
        out int hx0, out int hy0, out float tx, out float ty)
    {
        int tileX = Mathf.FloorToInt(worldX / Landscape.TILE_SIZE);
        int tileY = Mathf.FloorToInt(worldZ / Landscape.TILE_SIZE);
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
