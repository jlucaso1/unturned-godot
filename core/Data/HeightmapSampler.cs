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
        int tileX = Mathf.FloorToInt(worldX / Landscape.TILE_SIZE);
        int tileY = Mathf.FloorToInt(worldZ / Landscape.TILE_SIZE);
        if (!_tiles.TryGetValue((tileX, tileY), out float[,]? heights))
        {
            worldY = 0f;
            return false;
        }

        // GetWorldPosition maps hy -> worldX and hx -> worldZ (transposed), height indices in [0, 256].
        float hy = ((worldX / Landscape.TILE_SIZE) - tileX) * Res;
        float hx = ((worldZ / Landscape.TILE_SIZE) - tileY) * Res;
        int hx0 = Mathf.Clamp((int)hx, 0, Res - 1);
        int hy0 = Mathf.Clamp((int)hy, 0, Res - 1);
        float tx = hx - hx0;
        float ty = hy - hy0;

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
}
