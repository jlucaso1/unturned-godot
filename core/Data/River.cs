using System;
using System.IO;
using System.Text;
using Godot;

namespace UnturnedGodot.Data;

// Ports Unturned's stream-backed River reader (Unturned/Files/River.cs). Little-endian.
public sealed class River : IDisposable
{
    private readonly FileStream _stream;
    private readonly byte[] _buffer = new byte[16];

    public River(string path)
    {
        _stream = new FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
    }

    private void Fill(int count)
    {
        int read = 0;
        while (read < count)
        {
            int r = _stream.Read(_buffer, read, count - read);
            if (r <= 0) throw new EndOfStreamException();
            read += r;
        }
    }

    public byte ReadByte()
    {
        int b = _stream.ReadByte();
        return (byte)(b < 0 ? 0 : b);
    }

    public bool ReadBoolean() => ReadByte() != 0;

    public ushort ReadUInt16() { Fill(2); return BitConverter.ToUInt16(_buffer, 0); }
    public short ReadInt16() { Fill(2); return BitConverter.ToInt16(_buffer, 0); }
    public uint ReadUInt32() { Fill(4); return BitConverter.ToUInt32(_buffer, 0); }
    public int ReadInt32() { Fill(4); return BitConverter.ToInt32(_buffer, 0); }
    public ulong ReadUInt64() { Fill(8); return BitConverter.ToUInt64(_buffer, 0); }
    public float ReadSingle() { Fill(4); return BitConverter.ToSingle(_buffer, 0); }

    public string ReadString()
    {
        byte length = ReadByte();
        if (length == 0) return string.Empty;
        var bytes = new byte[length];
        int read = 0;
        while (read < length)
        {
            int r = _stream.Read(bytes, read, length - read);
            if (r <= 0) throw new EndOfStreamException();
            read += r;
        }
        return Encoding.UTF8.GetString(bytes);
    }

    // River's stream mode prefixes byte arrays with a uint16 length (Block uses a byte).
    public byte[] ReadBytes()
    {
        ushort count = ReadUInt16();
        var bytes = new byte[count];
        int read = 0;
        while (read < count)
        {
            int r = _stream.Read(bytes, read, count - read);
            if (r <= 0) throw new EndOfStreamException();
            read += r;
        }
        return bytes;
    }

    // On-disk GUID is the raw 16-byte MS mixed-endian layout, which new Guid(byte[16]) reproduces.
    public Guid ReadGuid()
    {
        byte[] bytes = ReadBytes();
        if (bytes.Length != 16)
            return Guid.Empty;
        return new Guid(bytes);
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

    public void Dispose() => _stream.Dispose();
}
