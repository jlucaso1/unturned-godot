using Godot;

namespace UnturnedGodot.Data;

// Builds a Godot HeightMapShape3D placement for a terrain tile, so the player collides with a cheap
// heightfield (257x257 cells) that reproduces the render mesh surface exactly — instead of a per-tile
// concave trimesh of ~131k triangles. Every grid sample lands on the render mesh vertex (see
// TerrainHeightfieldTests); the only difference is the intra-cell diagonal, sub-4 m and imperceptible.
public static class TerrainHeightfield
{
    // Cell size in world metres: TILE_SIZE spread across the 256 cells between the 257 samples.
    public const float CellSize = Landscape.TILE_SIZE / Landscape.HEIGHTMAP_RESOLUTION_MINUS_ONE; // 4 m

    // HeightMapShape3D.MapData: a HEIGHTMAP_RESOLUTION x HEIGHTMAP_RESOLUTION grid of world-space heights,
    // laid out row-major as data[depth * width + widthIndex]. Landscape transposes heightmap x->world Z and
    // y->world X, and negates Z for Godot; storing the depth (Z) axis reversed lets the placement transform
    // use a plain positive scale rather than a mirrored/negative-scaled collision shape.
    public static float[] MapData(float[,] heights)
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        var data = new float[res * res];
        for (int hx = 0; hx < res; hx++)
            for (int hy = 0; hy < res; hy++)
                data[(res - 1 - hx) * res + hy] =
                    (-Landscape.TILE_HEIGHT / 2f) + (heights[hx, hy] * Landscape.TILE_HEIGHT);
        return data;
    }

    public static float[] MapData(ushort[] heights)
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        if (heights.Length != res * res)
            throw new System.ArgumentException("Heightmap sample count does not match its resolution.", nameof(heights));
        var data = new float[heights.Length];
        for (int hx = 0; hx < res; hx++)
            for (int hy = 0; hy < res; hy++)
                data[(res - 1 - hx) * res + hy] = (-Landscape.TILE_HEIGHT / 2f)
                    + ((heights[(hx * res) + hy] / (float)ushort.MaxValue) * Landscape.TILE_HEIGHT);
        return data;
    }

    // Places the unit-cell, origin-centred heightfield onto the tile's world region: 4 m cells in X and Z,
    // heights already in world units (Y scale 1), centred so sample (hx, hy) matches the render vertex.
    public static Transform3D CollisionTransform(int tileX, int tileY)
    {
        Basis basis = Basis.Identity.Scaled(new Vector3(CellSize, 1f, CellSize));
        var origin = new Vector3(
            (tileX * Landscape.TILE_SIZE) + (Landscape.TILE_SIZE / 2f),
            0f,
            -((tileY * Landscape.TILE_SIZE) + (Landscape.TILE_SIZE / 2f)));
        return new Transform3D(basis, origin);
    }
}
