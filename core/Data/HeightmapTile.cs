using System.IO;

namespace UnturnedGodot.Data;

// Ports LandscapeTile.readHeightmap: big-endian uint16, x-outer/y-inner, normalized to [0,1].
public sealed class HeightmapTile
{
    public readonly int CoordX;
    public readonly int CoordY;
    public readonly float[,] Heights; // indexed [x, y]

    private HeightmapTile(int coordX, int coordY, float[,] heights)
    {
        CoordX = coordX;
        CoordY = coordY;
        Heights = heights;
    }

    public static HeightmapTile Read(string filePath, int coordX, int coordY)
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        var heights = new float[res, res];

        byte[] data = File.ReadAllBytes(filePath);
        int expected = res * res * 2;
        if (data.Length < expected)
            throw new IOException($"Heightmap {filePath} tem {data.Length} bytes, esperado {expected}");

        int p = 0;
        for (int x = 0; x < res; x++)
        {
            for (int y = 0; y < res; y++)
            {
                ushort raw = (ushort)((data[p] << 8) | data[p + 1]); // high byte first

                p += 2;
                heights[x, y] = raw / (float)ushort.MaxValue;
            }
        }

        return new HeightmapTile(coordX, coordY, heights);
    }
}
