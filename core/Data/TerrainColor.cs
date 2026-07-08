using Godot;

namespace UnturnedGodot.Data;

// Per-vertex terrain color: splatmap blend when available, altitude banding otherwise.
public static class TerrainColor
{
    public static Color ForVertex(SplatmapTile? splat, int hx, int hy, float worldY)
    {
        if (splat == null)
            return HeightColor(worldY);

        // Heightmap is 257 wide, splatmap 256; clamp the shared edge onto the last texel.
        int sx = hx < Landscape.SPLATMAP_RESOLUTION ? hx : Landscape.SPLATMAP_RESOLUTION - 1;
        int sy = hy < Landscape.SPLATMAP_RESOLUTION ? hy : Landscape.SPLATMAP_RESOLUTION - 1;
        return TerrainPalette.Blend(splat, sx, sy);
    }

    public static Color HeightColor(float worldY)
    {
        if (worldY < 0.5f) return new Color(0.15f, 0.35f, 0.6f);
        if (worldY < 3f) return new Color(0.85f, 0.8f, 0.55f);
        if (worldY < 25f) return new Color(0.3f, 0.55f, 0.25f);
        if (worldY < 45f) return new Color(0.45f, 0.4f, 0.3f);
        return new Color(0.95f, 0.95f, 0.95f);
    }
}
