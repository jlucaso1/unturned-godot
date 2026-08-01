using System;
using System.Buffers.Binary;

namespace UnturnedGodot.Unity;

// Decodes a Crunch (.crn) texture back into the DXT blocks it was built from. Unity's "Crunched"
// compression is this container wrapped around DXT1/DXT5, and workshop bundles use it for a fifth of
// their textures, so without it those surfaces have no albedo at all.
//
// Ported from Binomial LLC's reference decoder (crunch, crn_decomp.h; see NOTICE.md). The chunk layout,
// the palette delta coding and the DXT block writing follow it exactly.
public static class CrunchTexture
{
    // The DXT flavour a decoded stream carries.
    public enum BlockFormat
    {
        Dxt1,
        Dxt5,
    }

    private const ushort Signature = 0x4878;
    private const int MinHeaderSize = 62;

    // Crunch's own format ids (crn_format).
    private const int FormatDxt1 = 0;
    private const int FormatDxt5 = 2;
    private const int FormatDxt5CcxY = 3;
    private const int FormatDxt5XGxR = 4;
    private const int FormatDxt5XGbr = 5;
    private const int FormatDxt5Agbr = 6;

    // A chunk is 2x2 blocks; each encoding says which of its (up to four) tiles each block takes its
    // endpoints from.
    private static readonly byte[][] ChunkEncodingTiles =
    {
        new byte[] { 0, 0, 0, 0 },
        new byte[] { 0, 0, 1, 1 },
        new byte[] { 0, 1, 0, 1 },
        new byte[] { 0, 0, 1, 2 },
        new byte[] { 1, 2, 0, 0 },
        new byte[] { 0, 1, 0, 2 },
        new byte[] { 1, 0, 2, 0 },
        new byte[] { 0, 1, 2, 3 },
    };

    private static readonly byte[] ChunkEncodingTileCount = { 1, 2, 2, 3, 3, 3, 3, 4 };

    // DXT selector values are stored in a different order than they are indexed.
    private static readonly byte[] Dxt1FromLinear = { 0, 2, 3, 1 };
    private static readonly byte[] Dxt5FromLinear = { 0, 2, 3, 4, 5, 6, 7, 1 };

    // Every mip level's DXT blocks, back to back, ready for the GPU. Returns false for a stream this
    // decoder does not handle (a format other than DXT1/DXT5, or damaged data).
    public static bool TryDecode(byte[] crn, out byte[] blocks, out BlockFormat format,
        out int width, out int height, out int levels)
    {
        blocks = Array.Empty<byte>();
        format = BlockFormat.Dxt1;
        width = height = levels = 0;

        var header = CrunchHeader.Read(crn);
        if (header == null)
            return false;

        format = header.Format == FormatDxt1 ? BlockFormat.Dxt1 : BlockFormat.Dxt5;
        width = header.Width;
        height = header.Height;
        levels = header.Levels;

        var unpacker = new Unpacker(crn, header, format);
        return unpacker.TryUnpackAllLevels(out blocks);
    }

    private sealed class CrunchHeader
    {
        public int HeaderSize { get; private init; }
        public int DataSize { get; private init; }
        public int Width { get; private init; }
        public int Height { get; private init; }
        public int Levels { get; private init; }
        public int Faces { get; private init; }
        public int Format { get; private init; }

        public (int Offset, int Size, int Count) ColorEndpoints { get; private init; }
        public (int Offset, int Size, int Count) ColorSelectors { get; private init; }
        public (int Offset, int Size, int Count) AlphaEndpoints { get; private init; }
        public (int Offset, int Size, int Count) AlphaSelectors { get; private init; }

        public int TablesOffset { get; private init; }
        public int TablesSize { get; private init; }
        public int[] LevelOffsets { get; private init; } = Array.Empty<int>();

        // Every field is big-endian, packed with no alignment.
        public static CrunchHeader? Read(byte[] data)
        {
            if (data.Length < MinHeaderSize || U16(data, 0) != Signature)
                return null;

            int levels = data[16];
            int format = data[18];
            if (levels < 1 || levels > 16)
                return null;
            if (format is not (FormatDxt1 or FormatDxt5 or FormatDxt5CcxY or FormatDxt5XGxR
                or FormatDxt5XGbr or FormatDxt5Agbr))
            {
                return null;
            }

            int headerSize = U16(data, 2);
            if (headerSize < MinHeaderSize || headerSize > data.Length || 70 + (levels * 4) > headerSize)
                return null;

            var offsets = new int[levels];
            for (int i = 0; i < levels; i++)
                offsets[i] = (int)U32(data, 70 + (i * 4));

            var header = new CrunchHeader
            {
                HeaderSize = headerSize,
                DataSize = (int)U32(data, 6),
                Width = U16(data, 12),
                Height = U16(data, 14),
                Levels = levels,
                Faces = data[17],
                Format = format,
                ColorEndpoints = Palette(data, 33),
                ColorSelectors = Palette(data, 41),
                AlphaEndpoints = Palette(data, 49),
                AlphaSelectors = Palette(data, 57),
                TablesSize = U16(data, 65),
                TablesOffset = (int)U24(data, 67),
                LevelOffsets = offsets,
            };

            if (header.Faces != 1 || header.Width <= 0 || header.Height <= 0)
                return null;
            if (header.TablesOffset + header.TablesSize > data.Length)
                return null;

            foreach (int offset in offsets)
                if (offset <= 0 || offset > data.Length)
                    return null;

            return header;
        }

        // A palette reference: where its bitstream is, how long, and how many entries it holds.
        private static (int, int, int) Palette(byte[] data, int at) =>
            ((int)U24(data, at), (int)U24(data, at + 3), U16(data, at + 6));

        private static ushort U16(byte[] d, int at) => (ushort)((d[at] << 8) | d[at + 1]);

        private static uint U24(byte[] d, int at) =>
            ((uint)d[at] << 16) | ((uint)d[at + 1] << 8) | d[at + 2];

        private static uint U32(byte[] d, int at) =>
            ((uint)d[at] << 24) | ((uint)d[at + 1] << 16) | ((uint)d[at + 2] << 8) | d[at + 3];
    }

    private sealed class Unpacker
    {
        private readonly byte[] _data;
        private readonly CrunchHeader _header;
        private readonly BlockFormat _format;
        private readonly CrunchCodec _codec;

        private CrunchHuffmanTable? _chunkEncoding;
        private CrunchHuffmanTable?[] _endpointDelta = new CrunchHuffmanTable?[2];
        private CrunchHuffmanTable?[] _selectorDelta = new CrunchHuffmanTable?[2];

        private uint[] _colorEndpoints = Array.Empty<uint>();
        private uint[] _colorSelectors = Array.Empty<uint>();
        private ushort[] _alphaEndpoints = Array.Empty<ushort>();
        private ushort[] _alphaSelectors = Array.Empty<ushort>();

        public Unpacker(byte[] data, CrunchHeader header, BlockFormat format)
        {
            _data = data;
            _header = header;
            _format = format;
            _codec = new CrunchCodec(data);
        }

        public bool TryUnpackAllLevels(out byte[] blocks)
        {
            blocks = Array.Empty<byte>();
            if (!InitTables() || !DecodePalettes())
                return false;

            // Each block writer reads from every palette its format has, so a stream missing one of them
            // cannot produce blocks at all.
            bool complete = _header.ColorEndpoints.Count > 0 && _header.ColorSelectors.Count > 0
                && (_format == BlockFormat.Dxt1
                    || (_header.AlphaEndpoints.Count > 0 && _header.AlphaSelectors.Count > 0));
            if (!complete)
                return false;

            int blockBytes = _format == BlockFormat.Dxt1 ? 8 : 16;
            var levels = new byte[_header.Levels][];
            int total = 0;

            for (int level = 0; level < _header.Levels; level++)
            {
                int width = Math.Max(_header.Width >> level, 1);
                int height = Math.Max(_header.Height >> level, 1);
                int blocksX = (width + 3) >> 2;
                int blocksY = (height + 3) >> 2;

                var output = new byte[blocksX * blocksY * blockBytes];
                int start = _header.LevelOffsets[level];
                int end = level + 1 < _header.Levels ? _header.LevelOffsets[level + 1] : _header.DataSize;
                if (end <= start || end > _data.Length || !_codec.StartDecoding(start, end - start))
                    return false;

                if (_format == BlockFormat.Dxt1)
                    UnpackDxt1(output, blocksX * blockBytes, blocksX, blocksY);
                else
                    UnpackDxt5(output, blocksX * blockBytes, blocksX, blocksY);

                levels[level] = output;
                total += output.Length;
            }

            blocks = new byte[total];
            int at = 0;
            foreach (byte[] level in levels)
            {
                Buffer.BlockCopy(level, 0, blocks, at, level.Length);
                at += level.Length;
            }

            return true;
        }

        // The four models that drive the chunk stream live in one bitstream of their own.
        private bool InitTables()
        {
            if (!_codec.StartDecoding(_header.TablesOffset, _header.TablesSize))
                return false;

            _chunkEncoding = _codec.ReceiveModel();
            if (_chunkEncoding == null)
                return false;

            if (_header.ColorEndpoints.Count > 0)
            {
                _endpointDelta[0] = _codec.ReceiveModel();
                _selectorDelta[0] = _codec.ReceiveModel();
                if (_endpointDelta[0] == null || _selectorDelta[0] == null)
                    return false;
            }

            if (_header.AlphaEndpoints.Count > 0)
            {
                _endpointDelta[1] = _codec.ReceiveModel();
                _selectorDelta[1] = _codec.ReceiveModel();
                if (_endpointDelta[1] == null || _selectorDelta[1] == null)
                    return false;
            }

            return true;
        }

        private bool DecodePalettes()
        {
            if (_header.ColorEndpoints.Count > 0 && (!DecodeColorEndpoints() || !DecodeColorSelectors()))
                return false;

            if (_header.AlphaEndpoints.Count > 0 && (!DecodeAlphaEndpoints() || !DecodeAlphaSelectors()))
                return false;

            return true;
        }

        // Endpoints are 5:6:5 pairs, each channel delta-coded against the previous entry.
        private bool DecodeColorEndpoints()
        {
            (int offset, int size, int count) = _header.ColorEndpoints;
            if (!_codec.StartDecoding(offset, size))
                return false;

            CrunchHuffmanTable? low = _codec.ReceiveModel();
            CrunchHuffmanTable? high = _codec.ReceiveModel();
            if (low == null || high == null)
                return false;

            _colorEndpoints = new uint[count];
            uint a = 0, b = 0, c = 0, d = 0, e = 0, f = 0;

            for (int i = 0; i < count; i++)
            {
                a = (a + (uint)_codec.Decode(low)) & 31;
                b = (b + (uint)_codec.Decode(high)) & 63;
                c = (c + (uint)_codec.Decode(low)) & 31;
                d = (d + (uint)_codec.Decode(low)) & 31;
                e = (e + (uint)_codec.Decode(high)) & 63;
                f = (f + (uint)_codec.Decode(low)) & 31;
                _colorEndpoints[i] = c | (b << 5) | (a << 11) | (f << 16) | (e << 21) | (d << 27);
            }

            return true;
        }

        // Selectors are 16 two-bit indices, delta-coded in pairs.
        private bool DecodeColorSelectors()
        {
            (int offset, int size, int count) = _header.ColorSelectors;
            if (!_codec.StartDecoding(offset, size))
                return false;

            CrunchHuffmanTable? model = _codec.ReceiveModel();
            if (model == null)
                return false;

            BuildDeltaTables(3, out int[] delta0, out int[] delta1);
            var current = new int[16];
            _colorSelectors = new uint[count];

            for (int i = 0; i < count; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    int symbol = _codec.Decode(model);
                    if (symbol >= delta0.Length)
                        return false;

                    current[(j * 2) + 0] = (delta0[symbol] + current[(j * 2) + 0]) & 3;
                    current[(j * 2) + 1] = (delta1[symbol] + current[(j * 2) + 1]) & 3;
                }

                uint packed = 0;
                for (int j = 0; j < 16; j++)
                    packed |= (uint)Dxt1FromLinear[current[j]] << (j * 2);
                _colorSelectors[i] = packed;
            }

            return true;
        }

        private bool DecodeAlphaEndpoints()
        {
            (int offset, int size, int count) = _header.AlphaEndpoints;
            if (!_codec.StartDecoding(offset, size))
                return false;

            CrunchHuffmanTable? model = _codec.ReceiveModel();
            if (model == null)
                return false;

            _alphaEndpoints = new ushort[count];
            uint a = 0, b = 0;

            for (int i = 0; i < count; i++)
            {
                a = (a + (uint)_codec.Decode(model)) & 255;
                b = (b + (uint)_codec.Decode(model)) & 255;
                _alphaEndpoints[i] = (ushort)(a | (b << 8));
            }

            return true;
        }

        // Alpha selectors are 16 three-bit indices, stored as three 16-bit words per entry.
        private bool DecodeAlphaSelectors()
        {
            (int offset, int size, int count) = _header.AlphaSelectors;
            if (!_codec.StartDecoding(offset, size))
                return false;

            CrunchHuffmanTable? model = _codec.ReceiveModel();
            if (model == null)
                return false;

            BuildDeltaTables(7, out int[] delta0, out int[] delta1);
            var current = new int[16];
            _alphaSelectors = new ushort[count * 3];
            int at = 0;

            for (int i = 0; i < count; i++)
            {
                for (int j = 0; j < 8; j++)
                {
                    int symbol = _codec.Decode(model);
                    if (symbol >= delta0.Length)
                        return false;

                    current[(j * 2) + 0] = (delta0[symbol] + current[(j * 2) + 0]) & 7;
                    current[(j * 2) + 1] = (delta1[symbol] + current[(j * 2) + 1]) & 7;
                }

                var s = new int[16];
                for (int j = 0; j < 16; j++)
                    s[j] = Dxt5FromLinear[current[j]];

                _alphaSelectors[at++] = (ushort)(s[0] | (s[1] << 3) | (s[2] << 6) | (s[3] << 9)
                    | (s[4] << 12) | (s[5] << 15));
                _alphaSelectors[at++] = (ushort)((s[5] >> 1) | (s[6] << 2) | (s[7] << 5) | (s[8] << 8)
                    | (s[9] << 11) | (s[10] << 14));
                _alphaSelectors[at++] = (ushort)((s[10] >> 2) | (s[11] << 1) | (s[12] << 4) | (s[13] << 7)
                    | (s[14] << 10) | (s[15] << 13));
            }

            return true;
        }

        // The encoder codes selector pairs as one symbol out of a (2n+1)² grid of deltas.
        private static void BuildDeltaTables(int maxValue, out int[] delta0, out int[] delta1)
        {
            int unique = (maxValue * 2) + 1;
            delta0 = new int[unique * unique];
            delta1 = new int[unique * unique];

            int l = -maxValue, m = -maxValue;
            for (int i = 0; i < delta0.Length; i++)
            {
                delta0[i] = l;
                delta1[i] = m;
                if (++l > maxValue)
                {
                    l = -maxValue;
                    m++;
                }
            }
        }

        private void UnpackDxt1(byte[] output, int rowPitch, int blocksX, int blocksY)
        {
            const int blockBytes = 8;
            int chunksX = (blocksX + 1) >> 1;
            int chunksY = (blocksY + 1) >> 1;
            uint chunkEncodingBits = 1;
            int prevEndpoint = 0, prevSelector = 0;

            for (int y = 0; y < chunksY; y++)
            {
                (int startX, int endX, int dirX) = Serpentine(y, chunksX);
                int rowStart = y * rowPitch * 2;
                bool skipBottom = y == chunksY - 1 && (blocksY & 1) != 0;

                for (int x = startX; x != endX; x += dirX)
                {
                    int chunkStart = rowStart + (x * blockBytes * 2);
                    if (chunkEncodingBits == 1)
                        chunkEncodingBits = (uint)_codec.Decode(_chunkEncoding!) | 512u;

                    int encoding = (int)(chunkEncodingBits & 7);
                    chunkEncodingBits >>= 3;

                    var endpoints = new uint[4];
                    for (int i = 0; i < ChunkEncodingTileCount[encoding]; i++)
                    {
                        prevEndpoint = Advance(prevEndpoint, _endpointDelta[0]!, _colorEndpoints.Length);
                        endpoints[i] = _colorEndpoints[prevEndpoint];
                    }

                    byte[] tiles = ChunkEncodingTiles[encoding];
                    bool skipRight = (blocksX & 1) != 0 && x == chunksX - 1;

                    for (int by = 0; by < 2; by++)
                        for (int bx = 0; bx < 2; bx++)
                        {
                            prevSelector = Advance(prevSelector, _selectorDelta[0]!, _colorSelectors.Length);
                            if ((bx == 1 && skipRight) || (by == 1 && skipBottom))
                                continue;

                            int at = chunkStart + (by * rowPitch) + (bx * blockBytes);
                            WriteUInt32(output, at, endpoints[tiles[bx + (by * 2)]]);
                            WriteUInt32(output, at + 4, _colorSelectors[prevSelector]);
                        }
                }
            }
        }

        private void UnpackDxt5(byte[] output, int rowPitch, int blocksX, int blocksY)
        {
            const int blockBytes = 16;
            int chunksX = (blocksX + 1) >> 1;
            int chunksY = (blocksY + 1) >> 1;
            uint chunkEncodingBits = 1;
            int prevColorEndpoint = 0, prevColorSelector = 0;
            int prevAlphaEndpoint = 0, prevAlphaSelector = 0;
            int alphaSelectorCount = _header.AlphaSelectors.Count;

            for (int y = 0; y < chunksY; y++)
            {
                (int startX, int endX, int dirX) = Serpentine(y, chunksX);
                int rowStart = y * rowPitch * 2;
                bool skipBottom = y == chunksY - 1 && (blocksY & 1) != 0;

                for (int x = startX; x != endX; x += dirX)
                {
                    int chunkStart = rowStart + (x * blockBytes * 2);
                    if (chunkEncodingBits == 1)
                        chunkEncodingBits = (uint)_codec.Decode(_chunkEncoding!) | 512u;

                    int encoding = (int)(chunkEncodingBits & 7);
                    chunkEncodingBits >>= 3;
                    int tileCount = ChunkEncodingTileCount[encoding];

                    var alphaEndpoints = new ushort[4];
                    for (int i = 0; i < tileCount; i++)
                    {
                        prevAlphaEndpoint = Advance(prevAlphaEndpoint, _endpointDelta[1]!, _alphaEndpoints.Length);
                        alphaEndpoints[i] = _alphaEndpoints[prevAlphaEndpoint];
                    }

                    var colorEndpoints = new uint[4];
                    for (int i = 0; i < tileCount; i++)
                    {
                        prevColorEndpoint = Advance(prevColorEndpoint, _endpointDelta[0]!, _colorEndpoints.Length);
                        colorEndpoints[i] = _colorEndpoints[prevColorEndpoint];
                    }

                    byte[] tiles = ChunkEncodingTiles[encoding];
                    bool skipRight = (blocksX & 1) != 0 && x == chunksX - 1;

                    for (int by = 0; by < 2; by++)
                        for (int bx = 0; bx < 2; bx++)
                        {
                            prevAlphaSelector = Advance(prevAlphaSelector, _selectorDelta[1]!, alphaSelectorCount);
                            prevColorSelector = Advance(prevColorSelector, _selectorDelta[0]!, _colorSelectors.Length);
                            if ((bx == 1 && skipRight) || (by == 1 && skipBottom))
                                continue;

                            int tile = tiles[bx + (by * 2)];
                            int selector = prevAlphaSelector * 3;
                            int at = chunkStart + (by * rowPitch) + (bx * blockBytes);

                            WriteUInt32(output, at,
                                alphaEndpoints[tile] | ((uint)_alphaSelectors[selector] << 16));
                            WriteUInt32(output, at + 4,
                                _alphaSelectors[selector + 1] | ((uint)_alphaSelectors[selector + 2] << 16));
                            WriteUInt32(output, at + 8, colorEndpoints[tile]);
                            WriteUInt32(output, at + 12, _colorSelectors[prevColorSelector]);
                        }
                }
            }
        }

        // Chunks are walked left to right on even rows and right to left on odd ones.
        private static (int Start, int End, int Direction) Serpentine(int y, int chunksX) =>
            (y & 1) == 0 ? (0, chunksX, 1) : (chunksX - 1, -1, -1);

        // Palette indices are delta-coded and wrap by subtraction, never by modulo.
        private int Advance(int index, CrunchHuffmanTable model, int count)
        {
            index += _codec.Decode(model);
            if (index >= count)
                index -= count;
            return index < 0 || index >= count ? 0 : index;
        }

        // Every write lands inside the level buffer: the block offsets come from the same block counts the
        // buffer was sized with, and the span overload turns any future slip into a throw rather than a
        // silently corrupted image.
        private static void WriteUInt32(byte[] buffer, int at, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at), value);
    }
}
