using System;
using System.Collections.Generic;

namespace UnturnedGodot.Tests.Helpers;

// Writes a minimal but genuine Crunch (.crn) file, so the decoder can be exercised without shipping
// bytes lifted from the game. Every model it emits is the simplest valid shape: two symbols carrying a
// one-bit code each, which is enough to drive the palettes and the chunk stream.
public sealed class CrnBuilder
{
    // The order the format sends the code-length code sizes in (crunch's g_most_probable_codelength_codes).
    public static readonly int[] MostProbableCodelengthCodes =
    {
        17, 18, 19, 20, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15, 16,
    };

    public const int FormatDxt1 = 0;
    public const int FormatDxt5 = 2;

    // Bits are written most significant first, the order the decoder consumes them in.
    public sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _bitCount;
        private byte _current;

        public void Bits(uint value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                _current = (byte)(((uint)_current << 1) | ((value >> i) & 1));
                if (++_bitCount != 8)
                    continue;

                _bytes.Add(_current);
                _current = 0;
                _bitCount = 0;
            }
        }

        // A model with `total` symbols where only `a` and `b` are used, one bit each.
        public void Model(int total, int a, int b)
        {
            Bits((uint)total, 14);
            Bits((uint)MostProbableCodelengthCodes.Length, 5);
            foreach (int code in MostProbableCodelengthCodes)
                Bits(code is 0 or 1 ? 1u : 0u, 3); // only the sizes "0" and "1" are ever sent

            for (int symbol = 0; symbol < total; symbol++)
                Bits(symbol == a || symbol == b ? 1u : 0u, 1);
        }

        // Picks one of the two symbols a model carries: the lower one is coded 0, the higher one 1.
        public void Symbol(bool second) => Bits(second ? 1u : 0u, 1);

        public byte[] ToArray()
        {
            var result = new List<byte>(_bytes);
            if (_bitCount > 0)
                result.Add((byte)(_current << (8 - _bitCount)));
            return result.ToArray();
        }
    }

    public int Width { get; set; } = 8;
    public int Height { get; set; } = 8;
    public int Format { get; set; } = FormatDxt1;
    public int Faces { get; set; } = 1;
    public int HeaderSizeOverride { get; set; }   // 0 keeps the real size
    public ushort SignatureOverride { get; set; } = 0x4878;
    public int LevelsOverride { get; set; }       // 0 keeps the real level count
    public int TablesOffsetOverride { get; set; } // 0 keeps the real offset
    public int[]? LevelOffsetsOverride { get; set; } // null keeps the real offsets

    public byte[] Tables { get; set; } = Array.Empty<byte>();
    public (byte[] Data, int Count) ColorEndpoints { get; set; } = (Array.Empty<byte>(), 0);
    public (byte[] Data, int Count) ColorSelectors { get; set; } = (Array.Empty<byte>(), 0);
    public (byte[] Data, int Count) AlphaEndpoints { get; set; } = (Array.Empty<byte>(), 0);
    public (byte[] Data, int Count) AlphaSelectors { get; set; } = (Array.Empty<byte>(), 0);
    public List<byte[]> Levels { get; } = new();

    public byte[] Build()
    {
        int levels = Levels.Count;
        int headerSize = 70 + (levels * 4);
        var file = new List<byte>();

        var offsets = new Dictionary<string, int>();
        int at = headerSize;

        int TablesOffset() => offsets["tables"];

        void Append(string name, byte[] data)
        {
            offsets[name] = at;
            at += data.Length;
        }

        Append("tables", Tables);
        Append("colorEndpoints", ColorEndpoints.Data);
        Append("colorSelectors", ColorSelectors.Data);
        Append("alphaEndpoints", AlphaEndpoints.Data);
        Append("alphaSelectors", AlphaSelectors.Data);

        var levelOffsets = new int[levels];
        for (int i = 0; i < levels; i++)
        {
            levelOffsets[i] = at;
            at += Levels[i].Length;
        }

        int dataSize = at;

        void U16(int value) { file.Add((byte)(value >> 8)); file.Add((byte)value); }
        void U24(int value) { file.Add((byte)(value >> 16)); file.Add((byte)(value >> 8)); file.Add((byte)value); }
        void U32(int value)
        {
            file.Add((byte)(value >> 24));
            file.Add((byte)(value >> 16));
            file.Add((byte)(value >> 8));
            file.Add((byte)value);
        }

        U16(SignatureOverride);
        U16(HeaderSizeOverride > 0 ? HeaderSizeOverride : headerSize);
        U16(0);                      // header crc16, unchecked by the decoder
        U32(dataSize);
        U16(0);                      // data crc16
        U16(Width);
        U16(Height);
        file.Add((byte)(LevelsOverride > 0 ? LevelsOverride : levels));
        file.Add((byte)Faces);
        file.Add((byte)Format);
        U16(0);                      // flags
        U32(0);                      // reserved
        U32(0);                      // userdata0
        U32(0);                      // userdata1

        void Palette(string name, (byte[] Data, int Count) palette)
        {
            U24(palette.Data.Length == 0 ? 0 : offsets[name]);
            U24(palette.Data.Length);
            U16(palette.Count);
        }

        Palette("colorEndpoints", ColorEndpoints);
        Palette("colorSelectors", ColorSelectors);
        Palette("alphaEndpoints", AlphaEndpoints);
        Palette("alphaSelectors", AlphaSelectors);

        U16(Tables.Length);
        U24(TablesOffsetOverride > 0 ? TablesOffsetOverride : TablesOffset());
        foreach (int offset in LevelOffsetsOverride ?? levelOffsets)
            U32(offset);

        file.AddRange(Tables);
        file.AddRange(ColorEndpoints.Data);
        file.AddRange(ColorSelectors.Data);
        file.AddRange(AlphaEndpoints.Data);
        file.AddRange(AlphaSelectors.Data);
        foreach (byte[] level in Levels)
            file.AddRange(level);

        return file.ToArray();
    }
}
