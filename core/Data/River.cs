using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Godot;

namespace UnturnedGodot.Data;

// Ports Unturned's stream-backed River reader (Unturned/Files/River.cs). Little-endian. Reads the whole
// (small) file into memory once and walks a byte cursor, so each primitive is a span read instead of a
// virtual FileStream.Read plus a scratch-buffer copy — the object/tree/node/road loaders pull hundreds of
// thousands of these per map.
public sealed class River : IDisposable
{
    private readonly byte[] _data;
    private int _pos;

    public River(string path) => _data = File.ReadAllBytes(path);

    private void EnsureAvailable(int count)
    {
        if (_pos + count > _data.Length)
            throw new EndOfStreamException();
    }

    // Lenient like the source: past end-of-file returns 0 without advancing (the Fill-based reads throw).
    public byte ReadByte() => _pos < _data.Length ? _data[_pos++] : (byte)0;

    public bool ReadBoolean() => ReadByte() != 0;

    public ushort ReadUInt16() { EnsureAvailable(2); ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos)); _pos += 2; return v; }
    public short ReadInt16() { EnsureAvailable(2); short v = BinaryPrimitives.ReadInt16LittleEndian(_data.AsSpan(_pos)); _pos += 2; return v; }
    public uint ReadUInt32() { EnsureAvailable(4); uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos)); _pos += 4; return v; }
    public int ReadInt32() { EnsureAvailable(4); int v = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_pos)); _pos += 4; return v; }
    public ulong ReadUInt64() { EnsureAvailable(8); ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(_pos)); _pos += 8; return v; }
    public float ReadSingle() { EnsureAvailable(4); float v = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(_pos)); _pos += 4; return v; }

    public string ReadString()
    {
        byte length = ReadByte();
        if (length == 0) return string.Empty;
        EnsureAvailable(length);
        string s = Encoding.UTF8.GetString(_data, _pos, length);
        _pos += length;
        return s;
    }

    // River's stream mode prefixes the GUID's byte array with a uint16 length. The on-disk GUID is the raw
    // 16-byte MS mixed-endian layout, which new Guid(16 bytes) reproduces; read the span directly.
    public Guid ReadGuid()
    {
        ushort count = ReadUInt16();
        EnsureAvailable(count);
        Guid g = count == 16 ? new Guid(_data.AsSpan(_pos, 16)) : Guid.Empty;
        _pos += count;
        return g;
    }

    public Vector3 ReadSingleVector3()
    {
        float x = ReadSingle();
        float y = ReadSingle();
        float z = ReadSingle();
        return new Vector3(x, y, z);
    }

    // Rotations are stored as Euler XYZ degrees, not a 4-component quaternion.
    public Vector3 ReadEulerDegrees()
    {
        float x = ReadSingle();
        float y = ReadSingle();
        float z = ReadSingle();
        return new Vector3(x, y, z);
    }

    public void Dispose() { } // the whole file is already in memory; nothing to release
}
