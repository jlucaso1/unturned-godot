using System;
using System.IO;

namespace UnturnedGodot.Data;

// Ports LandscapeTile.ReadHoles: which of a tile's 4 m terrain cells are cut away entirely.
//
// This is how a map has an underground entrance. `LandscapeHoleVolume`s placed in the editor are the
// authoring tool; what they write, and what the game ships and reads back, is one bit per heightmap CELL
// saying whether that quad of ground exists. Unity is handed the result through `TerrainData.SetHoles`,
// which removes the cell from the drawn terrain AND from the `TerrainCollider` — one grid, both roles.
//
// The file is `Landscape/Holes/Tile_X_Y.bin`: a version byte, then HOLES_RESOLUTION rows of
// HOLES_RESOLUTION bits, LSB first, eight cells to a byte. A SET bit is ground; a CLEAR bit is the hole.
// That polarity is not a choice made here — `LandscapeTile.reset` fills the array with `true` and the
// writer stores that array verbatim, so "all ones" is the intact tile a map without holes would have
// written. It is also why a MISSING file is not an error: old maps and maps with no holes never write
// one, and the SDK deliberately does not warn about it.
//
// The version byte is read and kept but not enforced, exactly as the SDK reads it: it has only ever been
// 1, and the game does not branch on it. Keeping it means a future version that does change the layout
// shows up as a value nobody has seen rather than as silently misread bits.
public sealed class LandscapeHoles
{
    private const int Res = Landscape.HOLES_RESOLUTION;
    private const int BytesPerRow = Res / 8; // 32
    public const int BODY_BYTES = Res * BytesPerRow; // 8,192
    public const int FILE_BYTES = 1 + BODY_BYTES;    // 8,193

    public readonly int CoordX;
    public readonly int CoordY;
    public readonly byte Version;

    // The file's own bits, kept as they were read. Row x starts at x * BytesPerRow and bit `y & 7` of
    // byte `y >> 3` is cell y — the same packing the SDK's reader walks, so nothing is transposed or
    // re-packed on the way in and the count below can be taken straight off the source bytes.
    private readonly byte[] _bits;

    private LandscapeHoles(int coordX, int coordY, byte version, byte[] bits)
    {
        CoordX = coordX;
        CoordY = coordY;
        Version = version;
        _bits = bits;
    }

    // True when cell (x, y) is cut away. `x` runs along world Z and `y` along world X, the same
    // transposition Landscape.GetWorldPosition applies to heightmap indices — SplatmapCoord, which is
    // what Landscape.IsPointInsideHole indexes this array with, derives x from worldPosition.z.
    //
    // Out-of-range cells are solid rather than throwing: callers walk a heightmap whose sample grid is
    // one wider than the cell grid, and clamping the edge here keeps that arithmetic off every call site.
    public bool IsHole(int x, int y)
    {
        if ((uint)x >= Res || (uint)y >= Res)
            return false;
        return (_bits[(x * BytesPerRow) + (y >> 3)] & (1 << (y & 7))) == 0;
    }

    // How many cells the tile cuts away. Worth having as a number rather than a bool: a tile that cuts
    // none still ships a file on a map that has holes elsewhere, and the whole hole path can then be
    // skipped for it.
    public int HoleCount
    {
        get
        {
            int holes = 0;
            for (int i = 0; i < _bits.Length; i++)
                holes += 8 - System.Numerics.BitOperations.PopCount(_bits[i]);
            return holes;
        }
    }

    public bool HasAnyHoles
    {
        get
        {
            for (int i = 0; i < _bits.Length; i++)
                if (_bits[i] != 0xFF)
                    return true;
            return false;
        }
    }

    public static LandscapeHoles Parse(byte[] data, int coordX, int coordY)
    {
        if (data.Length < FILE_BYTES)
            throw new IOException(
                $"Holes tile has {data.Length} bytes, expected {FILE_BYTES}");
        var bits = new byte[BODY_BYTES];
        Array.Copy(data, 1, bits, 0, BODY_BYTES);
        return new LandscapeHoles(coordX, coordY, data[0], bits);
    }

    // Null when the map has no holes on this tile — the common case, and not a failure. A file that
    // exists but is short or unreadable IS reported, because that is a damaged install rather than an
    // old map, and silently treating it as "no holes" would fill in an entrance the map has.
    public static LandscapeHoles? TryRead(string filePath, int coordX, int coordY) =>
        File.Exists(filePath) ? Parse(File.ReadAllBytes(filePath), coordX, coordY) : null;

    // The holes beside a tile's heightmap.
    //
    // The tile is what carries its holes into the mesh build, and what a tile is read from is a heightmap
    // PATH — `Landscape/Heightmaps/Tile_X_Y_Source.heightmap`. Its holes are the sibling
    // `Landscape/Holes/Tile_X_Y.bin`, which is the same folder layout LevelInfo names and the SDK builds
    // from `Level.info.path`. Resolving it from the one path the reader is given is what lets holes reach
    // the mesh without every caller in between having to pass a LevelInfo it does not otherwise need.
    public static LandscapeHoles? TryReadBeside(string heightmapPath, int coordX, int coordY)
    {
        // Two levels up from the heightmap file is the map's Landscape/. The span overloads answer an
        // empty range rather than null for a path with nothing above it, so one test covers the case.
        ReadOnlySpan<char> landscape =
            Path.GetDirectoryName(Path.GetDirectoryName(heightmapPath.AsSpan()));
        if (landscape.IsEmpty)
            return null;
        return TryRead(Path.Join(landscape, "Holes", FileName(coordX, coordY)), coordX, coordY);
    }

    public static string FileName(int coordX, int coordY) =>
        FormattableString.Invariant($"Tile_{coordX}_{coordY}.bin");
}
