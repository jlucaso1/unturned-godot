using System.IO;

namespace UnturnedGodot.Data;

// Ports LandscapeTile.readSplatmap: 256x256x8 bytes, x-outer/y-inner/layer-innermost, weight = byte/255.
// Weights are stored in a flat array ([x, y, layer] at (x * res + y) * LAYERS + layer), matching the source
// byte order, so the per-vertex blend can walk a layer run from a single base offset instead of doing
// three-dimensional array indexing (extra multiplies and bounds checks) eight times per vertex.
public sealed class SplatmapTile
{
    public const int LAYERS = 8; // SPLATMAP_COUNT(2) * SPLATMAP_CHANNELS(4)

    // byte / 255 for all 256 byte values, precomputed once — the splatmap has ~524k of these per tile.
    private static readonly float[] ByteToUnitFloat = BuildByteToUnitFloat();

    private static float[] BuildByteToUnitFloat()
    {
        var lut = new float[256];
        for (int i = 0; i < 256; i++)
            lut[i] = i / 255f;
        return lut;
    }

    public readonly int CoordX;
    public readonly int CoordY;
    public readonly float[] Weights; // flat; address with WeightIndex / WeightAt

    private SplatmapTile(int coordX, int coordY, float[] weights)
    {
        CoordX = coordX;
        CoordY = coordY;
        Weights = weights;
    }

    public static int WeightIndex(int x, int y, int layer) =>
        (x * Landscape.SPLATMAP_RESOLUTION + y) * LAYERS + layer;

    public float WeightAt(int x, int y, int layer) => Weights[WeightIndex(x, y, layer)];

    public static SplatmapTile Parse(byte[] data, int coordX, int coordY)
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        int expected = res * res * LAYERS;
        if (data.Length < expected)
            throw new IOException($"Splatmap has {data.Length} bytes, expected {expected}");

        // The source byte order (x-outer, y-inner, layer-innermost) is exactly the flat layout. A 256-entry
        // lookup turns each of the ~524k per-tile normalizations from a float divide into a table load.
        var weights = new float[expected];
        for (int i = 0; i < expected; i++)
            weights[i] = ByteToUnitFloat[data[i]];

        return new SplatmapTile(coordX, coordY, weights);
    }

    public static SplatmapTile? TryRead(string filePath, int coordX, int coordY) =>
        File.Exists(filePath) ? Parse(File.ReadAllBytes(filePath), coordX, coordY) : null;

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
