using Godot;

namespace UnturnedGodot.Data;

// Stand-in colors for the 8 splat layers (real textures live in AssetBundles). Blending the
// per-texel weights against this palette reproduces the terrain's material layout from real data.
public static class TerrainPalette
{
    // Average colors of PEI's real splat-layer textures (Terrain/Materials/PEI/*), in the layer order
    // the map's landscape stores them. Sand dominates by area (the underwater seabed); the island itself
    // is the grass layers, which is why the terrain must read green, not sandy.
    public static readonly Color[] LayerColors =
    {
        new(0.545f, 0.328f, 0.227f), // 0 Dirt_01
        new(0.691f, 0.631f, 0.406f), // 1 Farm_Wheat_00
        new(0.224f, 0.447f, 0.227f), // 2 Grass_00
        new(0.494f, 0.317f, 0.249f), // 3 Gravel_00
        new(0.290f, 0.294f, 0.290f), // 4 Road_00
        new(0.697f, 0.549f, 0.360f), // 5 Sand_01
        new(0.224f, 0.447f, 0.227f), // 6 Grass_00
        new(0.556f, 0.308f, 0.184f), // 7 Stone_01
    };

    // The Materials.unity3d texture each splat layer uses, in the same layer order as LayerColors. Lets the
    // textured terrain map each layer to its real texture (the bundle stores them in a different order).
    public static readonly string[] LayerTextureNames =
    {
        "Dirt", "Farm", "Grass", "Gravel", "Road", "Sand", "Grass", "Stone",
    };

    // Weighted blend of the layer colors; falls back to layer 0 when a texel has no weight.
    // The layer run for a texel is contiguous in the flat weight array, so we compute the base offset once
    // and read weights[base + layer]; the float accumulation order is unchanged, so the result is identical.
    public static Color Blend(SplatmapTile splat, int x, int y)
    {
        byte[] weights = splat.Weights;
        Color[] palette = LayerColors;
        int baseIndex = SplatmapTile.WeightIndex(x, y, 0);

        float r = 0, g = 0, b = 0, total = 0;
        for (int layer = 0; layer < SplatmapTile.LAYERS; layer++)
        {
            float w = weights[baseIndex + layer] / 255f;
            Color c = palette[layer];
            r += c.R * w;
            g += c.G * w;
            b += c.B * w;
            total += w;
        }
        if (total <= 0f)
            return palette[0];
        return new Color(r / total, g / total, b / total);
    }
}
