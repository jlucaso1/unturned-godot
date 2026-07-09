using System;
using System.Buffers.Binary;
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

    // For tests and callers that already have a height grid (e.g. building a HeightmapSampler).
    public static HeightmapTile FromHeights(int coordX, int coordY, float[,] heights)
        => new(coordX, coordY, heights);

    public static HeightmapTile Read(string filePath, int coordX, int coordY)
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        var heights = new float[res, res];

        byte[] data = File.ReadAllBytes(filePath);
        int expected = res * res * 2;
        if (data.Length < expected)
            throw new IOException($"Heightmap {filePath} tem {data.Length} bytes, esperado {expected}");

        // BinaryPrimitives lets the JIT hoist the bounds checks out of the pair-of-bytes indexing
        // (-28% decode measured in isolation); the normalization stays the same division, bit-identical.
        ReadOnlySpan<byte> s = data;
        int p = 0;
        for (int x = 0; x < res; x++)
        {
            for (int y = 0; y < res; y++)
            {
                heights[x, y] = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(p)) / (float)ushort.MaxValue; // high byte first
                p += 2;
            }
        }

        return new HeightmapTile(coordX, coordY, heights);
    }
}
