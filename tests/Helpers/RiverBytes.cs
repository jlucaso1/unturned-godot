using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace UnturnedGodot.Tests.Helpers;

// Writes bytes exactly as Unturned's stream-backed River serializes them, so the reader can be
// exercised against the real on-disk layout (little-endian; GUID = uint16 length + 16 bytes).
public sealed class RiverBytes
{
    private readonly List<byte> _bytes = new();

    public byte[] ToArray() => _bytes.ToArray();

    public RiverBytes Byte(byte b) { _bytes.Add(b); return this; }
    public RiverBytes Bool(bool b) { _bytes.Add((byte)(b ? 1 : 0)); return this; }
    public RiverBytes UInt16(ushort v) { _bytes.AddRange(BitConverter.GetBytes(v)); return this; }
    public RiverBytes Int16(short v) { _bytes.AddRange(BitConverter.GetBytes(v)); return this; }
    public RiverBytes UInt32(uint v) { _bytes.AddRange(BitConverter.GetBytes(v)); return this; }
    public RiverBytes Int32(int v) { _bytes.AddRange(BitConverter.GetBytes(v)); return this; }
    public RiverBytes UInt64(ulong v) { _bytes.AddRange(BitConverter.GetBytes(v)); return this; }
    public RiverBytes Single(float v) { _bytes.AddRange(BitConverter.GetBytes(v)); return this; }

    public RiverBytes Str(string s)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(s);
        _bytes.Add((byte)utf8.Length);
        _bytes.AddRange(utf8);
        return this;
    }

    // River.readBytes: uint16 length prefix + payload.
    public RiverBytes Bytes(byte[] payload)
    {
        _bytes.AddRange(BitConverter.GetBytes((ushort)payload.Length));
        _bytes.AddRange(payload);
        return this;
    }

    public RiverBytes Guid(Guid guid) => Bytes(guid.ToByteArray());

    public RiverBytes Vector3(Vector3 v) => Single(v.X).Single(v.Y).Single(v.Z);
}
