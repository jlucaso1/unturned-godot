using System;
using System.IO;

namespace UnturnedGodot.Data;

// Ports LandscapeTile.readSplatmap: 256x256x8 bytes, x-outer/y-inner/layer-innermost, weight = byte/255.
// Weights are stored in a flat array ([x, y, layer] at (x * res + y) * LAYERS + layer), matching the source
// byte order, so the per-vertex blend can walk a layer run from a single base offset instead of doing
// three-dimensional array indexing (extra multiplies and bounds checks) eight times per vertex.
public sealed class SplatmapTile
{
    public const int LAYERS = 8; // SPLATMAP_COUNT(2) * SPLATMAP_CHANNELS(4)

    public readonly int CoordX;
    public readonly int CoordY;
    public readonly byte[] Weights; // authoritative source bytes; address with WeightIndex / WeightAt

    private SplatmapTile(int coordX, int coordY, byte[] weights)
    {
        CoordX = coordX;
        CoordY = coordY;
        Weights = weights;
    }

    public static int WeightIndex(int x, int y, int layer) =>
        (x * Landscape.SPLATMAP_RESOLUTION + y) * LAYERS + layer;

    public float WeightAt(int x, int y, int layer) => Weights[WeightIndex(x, y, layer)] / 255f;

    public static SplatmapTile Parse(byte[] data, int coordX, int coordY)
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        int expected = res * res * LAYERS;
        if (data.Length < expected)
            throw new IOException($"Splatmap has {data.Length} bytes, expected {expected}");

        // The renderer uploads these exact values into RGBA8 control maps. Expanding to float here used
        // four times the memory only to quantize every value back to its original byte during upload.
        byte[] weights = data.Length == expected ? data : data.AsSpan(0, expected).ToArray();
        return new SplatmapTile(coordX, coordY, weights);
    }

    public static SplatmapTile? TryRead(string filePath, int coordX, int coordY) =>
        File.Exists(filePath) ? Parse(File.ReadAllBytes(filePath), coordX, coordY) : null;

    // Which of the eight layers this tile actually paints: bit `i` is set when some texel gives layer `i`
    // a non-zero weight.
    //
    // A tile names eight layers but rarely paints eight. PEI's sixteen tiles paint 3.4 of them on
    // average, and three of them paint exactly one — so the splat shader was sampling four or five
    // anisotropically-filtered textures per pixel only to multiply each by a weight that is zero over the
    // whole tile. The renderer builds its per-tile material from this mask and leaves those layers out.
    public int ActiveLayerMask()
    {
        const int all = (1 << LAYERS) - 1;
        int mask = 0;
        byte[] weights = Weights;
        for (int texel = 0; texel < weights.Length; texel += LAYERS)
        {
            for (int layer = 0; layer < LAYERS; layer++)
                if (weights[texel + layer] != 0)
                    mask |= 1 << layer;
            if (mask == all)
                break; // nothing left to learn: every layer is painted somewhere
        }
        return mask;
    }

    // The per-texel argmax over the raw splatmap bytes (Landscape.getSplatmapHighestWeightLayerIndex),
    // one dominant-layer index per texel at [x * res + y]. Strict '>' keeps the FIRST layer on ties and
    // byte/255 is monotonic, so this matches an argmax over the normalized float weights exactly —
    // without ever materializing the 2 MB float tile the audio sampler used to retain.
    public static byte[] DominantLayers(byte[] data)
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        int expected = res * res * LAYERS;
        if (data.Length < expected)
            throw new IOException($"Splatmap has {data.Length} bytes, expected {expected}");

        var dominant = new byte[res * res];
        for (int cell = 0; cell < dominant.Length; cell++)
        {
            int baseIndex = cell * LAYERS;
            byte best = 0;
            byte bestWeight = data[baseIndex];
            for (int layer = 1; layer < LAYERS; layer++)
            {
                byte w = data[baseIndex + layer];
                if (w > bestWeight)
                {
                    bestWeight = w;
                    best = (byte)layer;
                }
            }
            dominant[cell] = best;
        }
        return dominant;
    }
}
