using System;
using System.Buffers.Binary;
using System.IO;

namespace UnturnedGodot.Data;

// Ports LandscapeTile.readHeightmap: big-endian uint16, x-outer/y-inner, normalized to [0,1].
public sealed class HeightmapTile
{
    public readonly int CoordX;
    public readonly int CoordY;
    private float[,]? _heights;
    public float[,] Heights => _heights ??= MaterializeHeights(); // compatibility/test view, indexed [x,y]
    public readonly ushort[]? RawSamples;
    public bool HasMaterializedHeights => _heights != null;

    private HeightmapTile(int coordX, int coordY, float[,]? heights, ushort[]? rawSamples = null)
    {
        CoordX = coordX;
        CoordY = coordY;
        _heights = heights;
        RawSamples = rawSamples;
    }

    // For tests and callers that already have a height grid (e.g. building a HeightmapSampler).
    public static HeightmapTile FromHeights(int coordX, int coordY, float[,] heights)
        => new(coordX, coordY, heights);

    public static HeightmapTile Read(string filePath, int coordX, int coordY)
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        var raw = new ushort[res * res];

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
                ushort sample = BinaryPrimitives.ReadUInt16BigEndian(s.Slice(p));
                raw[(x * res) + y] = sample;
                p += 2;
            }
        }

        return new HeightmapTile(coordX, coordY, null, raw);
    }

    public float HeightAt(int x, int y) => RawSamples != null
        ? RawSamples[(x * Landscape.HEIGHTMAP_RESOLUTION) + y] / (float)ushort.MaxValue
        : _heights![x, y];

    private float[,] MaterializeHeights()
    {
        if (RawSamples == null)
            throw new InvalidOperationException("Height tile has no samples");
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        var result = new float[res, res];
        for (int x = 0; x < res; x++)
            for (int y = 0; y < res; y++)
                result[x, y] = RawSamples[(x * res) + y] / (float)ushort.MaxValue;
        return result;
    }
}
