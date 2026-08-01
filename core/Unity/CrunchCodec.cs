using System;

namespace UnturnedGodot.Unity;

// The entropy layer of Crunch (.crn), the compression Unity applies on top of DXT when a texture is
// imported as "Crunched". Workshop content leans on it heavily; without it those textures decode to
// nothing and their materials render untextured.
//
// Ported from the reference decoder in Binomial LLC's crunch (crn_decomp.h; see NOTICE.md). The bit
// layout, the canonical-code table construction and the code-length RLE all have to match it exactly or
// the stream desynchronizes.
internal sealed class CrunchHuffmanTable
{
    // A code is at most 16 bits; symbol counts are bounded by the format.
    private const int MaxCodeSize = 16;
    private const int MaxTableBits = 11;

    private readonly uint[] _maxCodes = new uint[MaxCodeSize + 1];
    private readonly int[] _valPtrs = new int[MaxCodeSize + 1];
    private ushort[] _sortedSymbolOrder = Array.Empty<ushort>();
    private uint[] _lookup = Array.Empty<uint>();

    private int _tableBits;
    private uint _tableMaxCode;
    private int _decodeStartCodeSize;

    public int TotalSymbols { get; private set; }

    // Builds the decode tables from the per-symbol code sizes, exactly as prefix_coding::decoder_tables.
    public static CrunchHuffmanTable? Build(byte[] codeSizes)
    {
        if (codeSizes.Length == 0)
            return null;

        var table = new CrunchHuffmanTable { TotalSymbols = codeSizes.Length };

        // Table lookups only pay off past a handful of symbols, which is also what the encoder assumed.
        int tableBits = codeSizes.Length > 16
            ? Math.Min(1 + CeilLog2(codeSizes.Length), MaxTableBits)
            : 0;

        var numCodes = new int[MaxCodeSize + 1];
        foreach (byte size in codeSizes)
        {
            if (size > MaxCodeSize)
                return null;
            if (size > 0)
                numCodes[size]++;
        }

        var minCodes = new uint[MaxCodeSize];
        var sortedPositions = new int[MaxCodeSize + 1];
        uint currentCode = 0;
        int totalUsed = 0, minSize = int.MaxValue, maxSize = 0;

        for (int size = 1; size <= MaxCodeSize; size++)
        {
            int n = numCodes[size];
            if (n == 0)
            {
                table._maxCodes[size - 1] = 0;
            }
            else
            {
                // More codes of this length than the code space holds means the sizes are not a prefix
                // code at all. The reference decoder only asserts on it; here it has to be rejected,
                // because the lookup fill below would run off the end of the table.
                if (currentCode + (uint)n > 1u << size)
                    return null;

                minSize = Math.Min(minSize, size);
                maxSize = Math.Max(maxSize, size);
                minCodes[size - 1] = currentCode;

                uint max = currentCode + (uint)n - 1;
                table._maxCodes[size - 1] = 1 + ((max << (16 - size)) | ((1u << (16 - size)) - 1));
                table._valPtrs[size - 1] = totalUsed;
                sortedPositions[size] = totalUsed;

                currentCode += (uint)n;
                totalUsed += n;
            }

            currentCode <<= 1;
        }

        if (totalUsed == 0)
            return null;

        table._sortedSymbolOrder = new ushort[totalUsed];
        for (int symbol = 0; symbol < codeSizes.Length; symbol++)
            if (codeSizes[symbol] > 0)
                table._sortedSymbolOrder[sortedPositions[codeSizes[symbol]]++] = (ushort)symbol;

        if (tableBits <= minSize)
            tableBits = 0;
        table._tableBits = tableBits;

        if (tableBits > 0)
        {
            table._lookup = new uint[1 << tableBits];
            Array.Fill(table._lookup, uint.MaxValue);

            for (int size = 1; size <= tableBits; size++)
            {
                if (numCodes[size] == 0)
                    continue;

                int fillSize = tableBits - size;
                int fillCount = 1 << fillSize;
                uint minCode = minCodes[size - 1];
                uint maxCode = UnshiftedMaxCode(table._maxCodes[size - 1], size);
                int valPtr = table._valPtrs[size - 1];

                for (uint code = minCode; code <= maxCode; code++)
                {
                    ushort symbol = table._sortedSymbolOrder[valPtr + (int)(code - minCode)];
                    for (int j = 0; j < fillCount; j++)
                        table._lookup[j + (int)(code << fillSize)] = symbol | ((uint)size << 16);
                }
            }
        }

        for (int i = 0; i < MaxCodeSize; i++)
            table._valPtrs[i] -= (int)minCodes[i];

        table._decodeStartCodeSize = minSize;
        if (tableBits > 0)
        {
            // The lookup covers every code up to tableBits, and there is always one to find: a table is
            // only built when tableBits is larger than the shortest code.
            for (int size = tableBits; size >= 1; size--)
                if (numCodes[size] > 0)
                {
                    table._tableMaxCode = table._maxCodes[size - 1];
                    break;
                }

            // Anything longer than the lookup is searched for linearly, starting at the shortest code the
            // lookup does not already answer.
            table._decodeStartCodeSize = tableBits + 1;
            for (int size = tableBits + 1; size <= maxSize; size++)
                if (numCodes[size] > 0)
                {
                    table._decodeStartCodeSize = size;
                    break;
                }
        }

        table._maxCodes[MaxCodeSize] = uint.MaxValue;
        table._valPtrs[MaxCodeSize] = 0xFFFFF;
        return table;
    }

    // The symbol for the code at the head of the bit buffer, plus how many bits it consumed. False when
    // no code matches, which happens whenever the encoder wrote nothing at all.
    public bool TryDecode(uint bitBuffer, out int symbol, out int length)
    {
        uint k = (bitBuffer >> 16) + 1;

        // Canonical codes are assigned from zero upwards, so the lookup is filled contiguously and
        // _tableMaxCode is the largest prefix it covers: a prefix that passes this test always lands on
        // an entry that was written. Build guarantees it by refusing sizes that overfill the code space.
        if (_tableBits > 0 && k <= _tableMaxCode)
        {
            uint entry = _lookup[bitBuffer >> (32 - _tableBits)];
            symbol = (int)(entry & 0xFFFF);
            length = (int)(entry >> 16);
            return true;
        }

        length = _decodeStartCodeSize;
        while (length <= MaxCodeSize && k > _maxCodes[length - 1])
            length++;

        if (length > MaxCodeSize)
        {
            symbol = 0;
            length = 0;
            return false;
        }

        // By the same contiguity, the search stopped at the first length whose codes reach this prefix,
        // so the index lands inside that length's stretch of the sorted symbols.
        symbol = _sortedSymbolOrder[_valPtrs[length - 1] + (int)(bitBuffer >> (32 - length))];
        return true;
    }

    private static uint UnshiftedMaxCode(uint packedMaxCode, int size) =>
        ((packedMaxCode - 1) & ~((1u << (16 - size)) - 1)) >> (16 - size);

    private static int CeilLog2(int value)
    {
        int bits = 0;
        while ((1 << bits) < value)
            bits++;
        return bits;
    }
}

// The bit reader and model reader (crunch's symbol_codec). Bits are consumed most-significant first out
// of a 32-bit window.
internal sealed class CrunchCodec
{
    private const int MaxCodelengthCodes = 21;
    private const int SmallZeroRunCode = 17;
    private const int LargeZeroRunCode = 18;
    private const int SmallRepeatCode = 19;
    private const int LargeRepeatCode = 20;

    // The order the encoder sends the code-length code sizes in.
    private static readonly byte[] MostProbableCodelengthCodes =
    {
        SmallZeroRunCode, LargeZeroRunCode, SmallRepeatCode, LargeRepeatCode,
        0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15, 16,
    };

    private readonly byte[] _data;
    private int _next;
    private int _end;
    private uint _bitBuffer;
    private int _bitCount;

    public CrunchCodec(byte[] data) => _data = data;

    public bool StartDecoding(int offset, int size)
    {
        if (size <= 0 || offset < 0 || offset + size > _data.Length)
            return false;

        _next = offset;
        _end = offset + size;
        _bitBuffer = 0;
        _bitCount = 0;
        return true;
    }

    public uint GetBits(int count)
    {
        if (count == 0)
            return 0;
        if (count > 16)
            return (GetBits(count - 16) << 16) | GetBits(16);

        while (_bitCount < count)
        {
            uint next = _next < _end ? _data[_next++] : 0u;
            _bitCount += 8;
            _bitBuffer |= next << (32 - _bitCount);
        }

        uint result = _bitBuffer >> (32 - count);
        _bitBuffer <<= count;
        _bitCount -= count;
        return result;
    }

    public int Decode(CrunchHuffmanTable table)
    {
        if (_bitCount < 24)
        {
            if (_bitCount < 16)
            {
                uint c0 = _next < _end ? _data[_next++] : 0u;
                uint c1 = _next < _end ? _data[_next++] : 0u;
                _bitCount += 16;
                _bitBuffer |= ((c0 << 8) | c1) << (32 - _bitCount);
            }
            else
            {
                uint c = _next < _end ? _data[_next++] : 0u;
                _bitCount += 8;
                _bitBuffer |= c << (32 - _bitCount);
            }
        }

        // A code longer than the table describes means the encoder wrote nothing here: the reference
        // decoder answers symbol 0 and consumes no bits, and the streams stay in step only if we do the
        // same. Single-entry palettes (a whole texture of one colour) hit this on every chunk.
        if (!table.TryDecode(_bitBuffer, out int symbol, out int length))
            return 0;

        _bitBuffer <<= length;
        _bitCount -= length;
        return symbol;
    }

    // Reads a Huffman model off the stream: the code sizes are themselves Huffman-coded, with run-length
    // escapes for repeated and zero sizes.
    public CrunchHuffmanTable? ReceiveModel()
    {
        int totalSymbols = (int)GetBits(14); // total_bits(cMaxSupportedSyms = 8192), which is 14, not 13
        if (totalSymbols == 0)
            return null;

        int codelengthCodes = (int)GetBits(5);
        if (codelengthCodes < 1 || codelengthCodes > MaxCodelengthCodes)
            return null;

        var codelengthSizes = new byte[MaxCodelengthCodes];
        for (int i = 0; i < codelengthCodes; i++)
            codelengthSizes[MostProbableCodelengthCodes[i]] = (byte)GetBits(3);

        CrunchHuffmanTable? codelengths = CrunchHuffmanTable.Build(codelengthSizes);
        if (codelengths == null)
            return null;

        var codeSizes = new byte[totalSymbols];
        int offset = 0;
        while (offset < totalSymbols)
        {
            int remaining = totalSymbols - offset;
            int code = Decode(codelengths);
            if (code <= 16)
            {
                codeSizes[offset++] = (byte)code;
                continue;
            }

            // The remaining codes are the run escapes; the code-length alphabet has no other values.
            if (code is SmallZeroRunCode or LargeZeroRunCode)
            {
                int zeros = code == SmallZeroRunCode ? (int)GetBits(3) + 3 : (int)GetBits(7) + 11;
                if (zeros > remaining)
                    return null;

                offset += zeros; // the sizes are already zero, so the run only has to be skipped
                continue;
            }

            int run = code == SmallRepeatCode ? (int)GetBits(2) + 3 : (int)GetBits(6) + 7;
            if (offset == 0 || run > remaining || codeSizes[offset - 1] == 0)
                return null;

            byte previous = codeSizes[offset - 1];
            for (int end = offset + run; offset < end; offset++)
                codeSizes[offset] = previous;
        }

        // Every run was clamped to the symbols left, so the sizes end exactly on the last symbol.
        return CrunchHuffmanTable.Build(codeSizes);
    }
}
