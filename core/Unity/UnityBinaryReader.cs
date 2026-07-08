using System;
using System.Buffers.Binary;
using System.Text;

namespace UnturnedGodot.Unity;

// Cursor over a byte buffer with selectable endianness and 4-byte alignment, matching the
// primitives Unity's SerializedFile/AssetBundle formats are written with.
public sealed class UnityBinaryReader
{
    private readonly byte[] _data;
    public bool BigEndian { get; set; }
    public int Position { get; set; }

    public UnityBinaryReader(byte[] data, bool bigEndian = true)
    {
        _data = data;
        BigEndian = bigEndian;
    }

    public int Length => _data.Length;

    public byte ReadByte() => _data[Position++];

    public bool ReadBoolean() => ReadByte() != 0;

    public byte[] ReadBytes(int count)
    {
        var slice = new byte[count];
        Array.Copy(_data, Position, slice, 0, count);
        Position += count;
        return slice;
    }

    public ushort ReadUInt16()
    {
        var span = _data.AsSpan(Position, 2);
        Position += 2;
        return BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span)
                         : BinaryPrimitives.ReadUInt16LittleEndian(span);
    }

    public short ReadInt16() => (short)ReadUInt16();

    public uint ReadUInt32()
    {
        var span = _data.AsSpan(Position, 4);
        Position += 4;
        return BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(span)
                         : BinaryPrimitives.ReadUInt32LittleEndian(span);
    }

    public int ReadInt32() => (int)ReadUInt32();

    public ulong ReadUInt64()
    {
        var span = _data.AsSpan(Position, 8);
        Position += 8;
        return BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(span)
                         : BinaryPrimitives.ReadUInt64LittleEndian(span);
    }

    public long ReadInt64() => (long)ReadUInt64();

    // Null-terminated UTF-8 string (used by bundle directory and unity version fields).
    public string ReadCString()
    {
        int start = Position;
        while (_data[Position] != 0)
            Position++;
        string s = Encoding.UTF8.GetString(_data, start, Position - start);
        Position++; // skip the null terminator
        return s;
    }

    // Unity aligns most compound reads to a 4-byte boundary.
    public void Align4()
    {
        int rem = Position & 3;
        if (rem != 0)
            Position += 4 - rem;
    }

    public void Align(int alignment)
    {
        int rem = Position % alignment;
        if (rem != 0)
            Position += alignment - rem;
    }
}
