using System;
using System.Collections.Generic;
using System.Text;

namespace UnturnedGodot.Tests.Helpers;

// Builds a map-local .unity3d the way the game ships them: a SerializedFile of Texture2D objects, wrapped
// in either container format, with the pixels inline (UnityRaw, and UnityFS before streaming) or in a
// .resS entry beside the SerializedFile.
public sealed class MapBundleBuilder
{
    public sealed record Texture(string Name, int Width, int Height, int Format, int MipCount, byte[] Pixels);

    private readonly List<Texture> _textures = new();

    public bool UnityFs { get; set; }
    public bool StreamedPixels { get; set; }   // pixels move to a .resS entry the textures point at
    public string StreamName { get; set; } = "CAB-map.resS";

    public MapBundleBuilder Add(Texture texture)
    {
        _textures.Add(texture);
        return this;
    }

    public MapBundleBuilder Add(string name, int width, int height, byte[] pixels) =>
        Add(new Texture(name, width, height, Format: 4, MipCount: 1, pixels));

    public byte[] Build()
    {
        var stream = new List<byte>();
        byte[] serialized = BuildSerializedFile(stream);

        if (!UnityFs)
            return RawBundleBytes.Wrap("CAB-map", serialized);

        var fs = new UnityFsBuilder().Add("CAB-map", serialized);
        if (StreamedPixels)
            fs.Add(StreamName, stream.ToArray());
        return fs.Build();
    }

    // A version-15 SerializedFile: one Texture2D type with its tree, then one object per texture.
    private byte[] BuildSerializedFile(List<byte> stream)
    {
        var meta = new List<byte>();
        WriteCString(meta, "5.x.x");
        WriteI32Le(meta, 0);              // target platform
        meta.Add(1);                      // enable type tree
        WriteI32Le(meta, 1);              // type count
        WriteI32Le(meta, 28);             // Texture2D
        meta.AddRange(new byte[16]);      // old type hash
        meta.AddRange(TextureTypeTree());

        var payloads = new List<byte[]>();
        foreach (Texture texture in _textures)
            payloads.Add(Payload(texture, stream));

        WriteI32Le(meta, payloads.Count);
        int byteStart = 0;
        for (int i = 0; i < payloads.Count; i++)
        {
            while (meta.Count % 4 != 0)
                meta.Add(0);
            WriteI64Le(meta, 100 + i);           // path id
            WriteU32Le(meta, (uint)byteStart);
            WriteU32Le(meta, (uint)payloads[i].Length);
            WriteI32Le(meta, 0);                 // type id
            WriteU16Le(meta, 28);                // class id
            WriteI16Le(meta, 0);                 // script type index
            meta.Add(0);                         // stripped
            byteStart += Aligned(payloads[i].Length);
        }

        var header = new List<byte>();
        WriteU32Be(header, 0);            // metadata size (legacy, unread)
        WriteU32Be(header, 0);            // file size (legacy, unread)
        WriteU32Be(header, 15);           // version
        int dataOffsetPos = header.Count;
        WriteU32Be(header, 0);            // data offset, patched below
        header.Add(0);                    // endianess: little
        header.AddRange(new byte[3]);     // reserved

        var all = new List<byte>(header);
        all.AddRange(meta);
        // Unity aligns the data section (its own files put it at 4096); the reader aligns object fields on
        // absolute positions, so an unaligned data offset would shift every string's padding.
        while (all.Count % 16 != 0)
            all.Add(0);
        uint dataOffset = (uint)all.Count;
        for (int i = 0; i < 4; i++)
            header[dataOffsetPos + i] = (byte)(dataOffset >> ((3 - i) * 8));
        for (int i = 0; i < 4; i++)
            all[dataOffsetPos + i] = header[dataOffsetPos + i];
        foreach (byte[] payload in payloads)
        {
            all.AddRange(payload);
            while ((all.Count - dataOffset) % 4 != 0)
                all.Add(0);
        }
        return all.ToArray();
    }

    private static int Aligned(int length) => (length + 3) & ~3;

    // m_Name, m_Width, m_Height, m_TextureFormat, m_MipCount, "image data", m_StreamData — the fields
    // UnityTexture reads, in the order a real Texture2D writes them.
    private byte[] TextureTypeTree()
    {
        var strings = new StringBuffer();
        var nodes = new List<byte>();
        var blob = new List<byte>();

        void Node(int level, string type, string name, int byteSize, bool array = false, bool align = false)
        {
            WriteU16Le(nodes, 1);                      // node version
            nodes.Add((byte)level);
            nodes.Add((byte)(array ? 1 : 0));
            WriteU32Le(nodes, strings.Offset(type));
            WriteU32Le(nodes, strings.Offset(name));
            WriteI32Le(nodes, byteSize);
            WriteI32Le(nodes, 0);                      // index
            WriteI32Le(nodes, align ? 0x4000 : 0);
        }

        int count = 0;
        void Emit(int level, string type, string name, int byteSize, bool array = false, bool align = false)
        {
            Node(level, type, name, byteSize, array, align);
            count++;
        }

        Emit(0, "Texture2D", "Base", -1);
        Emit(1, "string", "m_Name", -1, align: true);
        Emit(2, "Array", "Array", -1, array: true, align: true);
        Emit(3, "int", "size", 4);
        Emit(3, "char", "data", 1);
        Emit(1, "int", "m_Width", 4);
        Emit(1, "int", "m_Height", 4);
        Emit(1, "int", "m_TextureFormat", 4);
        Emit(1, "int", "m_MipCount", 4);
        Emit(1, "TypelessData", "image data", -1);
        if (StreamedPixels)
        {
            Emit(1, "StreamingInfo", "m_StreamData", -1);
            Emit(2, "unsigned int", "offset", 4);
            Emit(2, "unsigned int", "size", 4);
            Emit(2, "string", "path", -1, align: true);
            Emit(3, "Array", "Array", -1, array: true, align: true);
            Emit(4, "int", "size", 4);
            Emit(4, "char", "data", 1);
        }

        byte[] stringBuffer = strings.ToArray();
        WriteI32Le(blob, count);
        WriteI32Le(blob, stringBuffer.Length);
        blob.AddRange(nodes);
        blob.AddRange(stringBuffer);
        return blob.ToArray();
    }

    private byte[] Payload(Texture texture, List<byte> stream)
    {
        var body = new List<byte>();
        byte[] name = Encoding.UTF8.GetBytes(texture.Name);
        WriteI32Le(body, name.Length);
        body.AddRange(name);
        while (body.Count % 4 != 0)
            body.Add(0);
        WriteI32Le(body, texture.Width);
        WriteI32Le(body, texture.Height);
        WriteI32Le(body, texture.Format);
        WriteI32Le(body, texture.MipCount);

        if (!StreamedPixels)
        {
            WriteI32Le(body, texture.Pixels.Length);
            body.AddRange(texture.Pixels);
            return body.ToArray();
        }

        WriteI32Le(body, 0);                       // empty inline data; the pixels are in the stream
        WriteU32Le(body, (uint)stream.Count);      // m_StreamData.offset
        WriteU32Le(body, (uint)texture.Pixels.Length);
        byte[] path = Encoding.UTF8.GetBytes("archive:/CAB-map/" + StreamName);
        WriteI32Le(body, path.Length);
        body.AddRange(path);
        while (body.Count % 4 != 0)
            body.Add(0);
        stream.AddRange(texture.Pixels);
        return body.ToArray();
    }

    // The type tree's string buffer: every distinct name written once, addressed by its offset.
    private sealed class StringBuffer
    {
        private readonly List<byte> _bytes = new();
        private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal);

        public uint Offset(string value)
        {
            if (_offsets.TryGetValue(value, out uint offset))
                return offset;

            offset = (uint)_bytes.Count;
            _bytes.AddRange(Encoding.ASCII.GetBytes(value));
            _bytes.Add(0);
            _offsets[value] = offset;
            return offset;
        }

        public byte[] ToArray() => _bytes.ToArray();
    }

    private static void WriteCString(List<byte> b, string s) { b.AddRange(Encoding.ASCII.GetBytes(s)); b.Add(0); }
    private static void WriteU16Le(List<byte> b, ushort v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
    private static void WriteI16Le(List<byte> b, short v) => WriteU16Le(b, (ushort)v);
    private static void WriteI32Le(List<byte> b, int v) => WriteU32Le(b, (uint)v);
    private static void WriteU32Le(List<byte> b, uint v) { for (int i = 0; i < 4; i++) b.Add((byte)(v >> (i * 8))); }
    private static void WriteI64Le(List<byte> b, long v) { for (int i = 0; i < 8; i++) b.Add((byte)(v >> (i * 8))); }
    private static void WriteU32Be(List<byte> b, uint v) { for (int i = 3; i >= 0; i--) b.Add((byte)(v >> (i * 8))); }
}
