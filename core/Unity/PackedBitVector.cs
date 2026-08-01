using System;
using System.Collections.Generic;

namespace UnturnedGodot.Unity;

// Unity's PackedBitVector: an array of values quantized to a fixed number of bits, stored back to back
// with no byte alignment. Everything needed to read it travels with the data — how many items, how many
// bits each takes, and (for floats) the range they were quantized into — so nothing about the layout is
// assumed here.
//
// Floats are stored as an integer of BitSize bits, mapped onto [Start, Start + Range]:
//   value = Start + Range * bits / (2^BitSize - 1)
public readonly struct PackedBitVector
{
    public int NumItems { get; }
    public float Range { get; }
    public float Start { get; }
    public byte[] Data { get; }
    public int BitSize { get; }

    public PackedBitVector(int numItems, float range, float start, byte[] data, int bitSize)
    {
        NumItems = numItems;
        Range = range;
        Start = start;
        Data = data;
        BitSize = bitSize;
    }

    public bool IsEmpty => NumItems <= 0 || BitSize <= 0 || Data.Length == 0;

    // Reads the vector out of a TypeTree-decoded node. Integer vectors carry no range/start, so those
    // fields are simply absent there; every field is optional and defaults to "nothing to unpack".
    public static PackedBitVector Read(object? node)
    {
        if (node is not Dictionary<string, object> fields)
            return default;

        return new PackedBitVector(
            Int(fields, "m_NumItems"),
            Float(fields, "m_Range"),
            Float(fields, "m_Start"),
            fields.TryGetValue("m_Data", out object? data) && data is byte[] bytes ? bytes : Array.Empty<byte>(),
            Int(fields, "m_BitSize"));
    }

    // `count` values starting at item `start`, or every item when count is negative.
    public uint[] UnpackUInts(int count = -1, int start = 0)
    {
        if (IsEmpty)
            return Array.Empty<uint>();

        int total = count < 0 ? NumItems - start : count;
        if (total <= 0 || start < 0 || start + total > NumItems)
            return Array.Empty<uint>();

        // Reading past the end would mean the file lied about NumItems or truncated m_Data.
        long neededBits = (long)(start + total) * BitSize;
        if (neededBits > (long)Data.Length * 8)
            return Array.Empty<uint>();

        var values = new uint[total];
        long bitPos = (long)start * BitSize;
        for (int i = 0; i < total; i++)
        {
            uint value = 0;
            for (int bit = 0; bit < BitSize; bit++, bitPos++)
            {
                uint set = (uint)((Data[(int)(bitPos >> 3)] >> (int)(bitPos & 7)) & 1);
                value |= set << bit;
            }

            values[i] = value;
        }

        return values;
    }

    // The same values mapped back onto [Start, Start + Range].
    public float[] UnpackFloats(int count = -1, int start = 0)
    {
        uint[] raw = UnpackUInts(count, start);
        if (raw.Length == 0)
            return Array.Empty<float>();

        // UnpackUInts only returns values for a positive BitSize, so the denominator is never zero.
        float maxValue = BitSize >= 32 ? uint.MaxValue : (1u << BitSize) - 1u;
        float scale = Range / maxValue;

        var values = new float[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            values[i] = Start + (raw[i] * scale);

        return values;
    }

    private static int Int(Dictionary<string, object> fields, string key) =>
        fields.TryGetValue(key, out object? value) ? Convert.ToInt32(value) : 0;

    private static float Float(Dictionary<string, object> fields, string key) =>
        fields.TryGetValue(key, out object? value) ? Convert.ToSingle(value) : 0f;
}
