using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Data;

// Samples the terrain height at an arbitrary world XZ by bilinearly interpolating the heightmap tiles — the
// port of Unturned's LevelGround.getHeight. Roads and other ground-conforming features use it so they follow
// the same surface the terrain mesh is built from, instead of guessing an offset.
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

        // Bilinear over the four surrounding heightmap samples (interp along hx by tx, then along hy by ty).
        float a = Mathf.Lerp(heights[hx0, hy0], heights[hx0 + 1, hy0], tx);
        float b = Mathf.Lerp(heights[hx0, hy0 + 1], heights[hx0 + 1, hy0 + 1], tx);
        float h01 = Mathf.Lerp(a, b, ty);

        worldY = (-Landscape.TILE_HEIGHT / 2f) + (h01 * Landscape.TILE_HEIGHT);
        return true;
    }
}
