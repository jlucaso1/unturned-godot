using Godot;

namespace UnturnedGodot.Data;

// Stand-in colors for the 8 splat layers (real textures live in AssetBundles). Blending the
// per-texel weights against this palette reproduces the terrain's material layout from real data.
public static class TerrainPalette
{
    public static readonly Color[] LayerColors =
    {
        new(0.30f, 0.45f, 0.22f), // 0 base grass
        new(0.83f, 0.78f, 0.55f), // 1 sand
        new(0.45f, 0.40f, 0.32f), // 2 dirt
        new(0.38f, 0.38f, 0.40f), // 3 rock
        new(0.25f, 0.25f, 0.27f), // 4 road
        new(0.52f, 0.50f, 0.30f), // 5 gravel
        new(0.20f, 0.38f, 0.18f), // 6 forest
        new(0.90f, 0.90f, 0.92f), // 7 snow
    };

    // Weighted blend of the layer colors; falls back to layer 0 when a texel has no weight.
    public static Color Blend(SplatmapTile splat, int x, int y)
    {
        float r = 0, g = 0, b = 0, total = 0;
        for (int layer = 0; layer < SplatmapTile.LAYERS; layer++)
        {
            float w = splat.Weights[x, y, layer];
            Color c = LayerColors[layer];
            r += c.R * w;
            g += c.G * w;
            b += c.B * w;
            total += w;
        }
        if (total <= 0f)
            return LayerColors[0];
        return new Color(r / total, g / total, b / total);
    }
}
