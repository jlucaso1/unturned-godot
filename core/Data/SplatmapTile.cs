using System.IO;

namespace UnturnedGodot.Data;

// Ports LandscapeTile.readSplatmap: 256x256x8 bytes, x-outer/y-inner/layer-innermost, weight = byte/255.
public sealed class SplatmapTile
{
    public const int LAYERS = 8; // SPLATMAP_COUNT(2) * SPLATMAP_CHANNELS(4)

    public readonly int CoordX;
    public readonly int CoordY;
    public readonly float[,,] Weights; // [x, y, layer]

    private SplatmapTile(int coordX, int coordY, float[,,] weights)
    {
        CoordX = coordX;
        CoordY = coordY;
        Weights = weights;
    }

    public static SplatmapTile Parse(byte[] data, int coordX, int coordY)
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        int expected = res * res * LAYERS;
        if (data.Length < expected)
            throw new IOException($"Splatmap has {data.Length} bytes, expected {expected}");

        var weights = new float[res, res, LAYERS];
        int p = 0;
        for (int x = 0; x < res; x++)
            for (int y = 0; y < res; y++)
                for (int layer = 0; layer < LAYERS; layer++)
                    weights[x, y, layer] = data[p++] / 255f;

        return new SplatmapTile(coordX, coordY, weights);
    }

    public static SplatmapTile? TryRead(string filePath, int coordX, int coordY) =>
        File.Exists(filePath) ? Parse(File.ReadAllBytes(filePath), coordX, coordY) : null;
}
