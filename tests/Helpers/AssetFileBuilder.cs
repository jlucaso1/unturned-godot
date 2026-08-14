using System;
using System.Collections.Generic;
using System.Text;

namespace UnturnedGodot.Tests.Helpers;

// Builds a SerializedFile carrying an AssetBundle container plus the objects it names, so the readers that
// address assets BY CONTAINER PATH can be tested without a 1.4 GB masterbundle.
//
// MapBundleBuilder already writes Texture2D objects, and it is what the texture decoders are tested with.
// What it does not write is the AssetBundle object (class 142) that maps "assets/.../grass_00.png" to a
// path id — and that map is the entire subject of BundleTextures.Locate, ImpactDecalExtractor and
// AudioExtractor's catalog. Without it those three could only ever be tested against a real install, which
// means not in the coverage job, which is the one that has to be green before anything merges.
//
// Everything here is version 15: pre-16 files select an object's type tree by its class id rather than by
// an index into the type table, which keeps the writer honest — an object and its tree cannot drift apart
// without the class id saying so.
public sealed class AssetFileBuilder
{
    // The Unity class ids this writes. AudioClip and MonoBehaviour are here for the audio extraction; the
    // definition assets it reads are MonoBehaviours whose script this file never has to name, because
    // nothing on the reading side looks at the script — only at the fields.
    public const int AssetBundleClassId = 142;
    public const int Texture2DClassId = 28;
    public const int AudioClipClassId = 83;
    public const int MonoBehaviourClassId = 114;

    private readonly List<(int ClassId, byte[] Tree)> _types = new();
    private readonly Dictionary<int, int> _typeByClassId = new();
    private readonly List<(long PathId, int ClassId, byte[] Payload)> _objects = new();
    private readonly List<(string Path, long PathId)> _container = new();
    private long _nextPathId = 100;

    // Bytes appended to the bundle's .resource entry, which is where an AudioClip's FSB5 blob lives.
    private readonly List<byte> _resource = new();

    // ...and to its .resS entry, which is where a streamed texture's pixels live.
    private readonly List<byte> _stream = new();

    public string StreamName { get; set; } = "CAB-test.resS";

    public string ResourceName { get; set; } = "CAB-test.resource";

    public IReadOnlyList<byte> Resource => _resource;

    // A texture whose pixels sit inline in the SerializedFile.
    public long AddInlineTexture(string containerPath, string name, int width, int height, byte[] pixels,
        int format = 4, int mipCount = 1)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        var body = new List<byte>();
        WriteString(body, name);
        WriteI32(body, width);
        WriteI32(body, height);
        WriteI32(body, format);
        WriteI32(body, mipCount);
        WriteI32(body, pixels.Length);
        body.AddRange(pixels);
        Align(body);
        // An empty stream path is how a real inline texture says "the pixels are right here". Every
        // Texture2D in one file shares a type tree — version 15 selects it by class id — so the field is
        // always written, and it is its CONTENT that says where the pixels are.
        WriteU32(body, 0);
        WriteU32(body, 0);
        WriteString(body, string.Empty);
        return Add(containerPath, Texture2DClassId, TextureTree(), body);
    }

    // A texture whose pixels sit in the bundle's .resS entry, which is how every large one ships.
    public long AddStreamedTexture(string containerPath, string name, int width, int height, byte[] pixels,
        int format = 4, int mipCount = 1)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        int offset = _stream.Count;
        _stream.AddRange(pixels);

        var body = new List<byte>();
        WriteString(body, name);
        WriteI32(body, width);
        WriteI32(body, height);
        WriteI32(body, format);
        WriteI32(body, mipCount);
        WriteI32(body, 0); // no inline pixels
        WriteU32(body, (uint)offset);
        WriteU32(body, (uint)pixels.Length);
        WriteString(body, "archive:/CAB-test/" + StreamName);
        return Add(containerPath, Texture2DClassId, TextureTree(), body);
    }

    // An AudioClip pointing at a byte range of the bundle's .resource entry. `blob` is what lands there —
    // for a rebuild test that is an FSB5 bank, and for a "this is not a bank" test it is anything else.
    public long AddAudioClip(string containerPath, string name, byte[] blob, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(blob);
        int offset = _resource.Count;
        _resource.AddRange(blob);

        var body = new List<byte>();
        WriteString(body, name);
        WriteString(body, source ?? ResourceName);
        WriteU64(body, (ulong)offset);
        WriteU64(body, (ulong)blob.Length);
        return Add(containerPath, AudioClipClassId, AudioClipTree(), body);
    }

    // A OneShotAudioDefinition: the envelope, and the clips it plays in the author's order.
    public long AddAudioDefinition(string containerPath, float volume, float minPitch, float maxPitch,
        IReadOnlyList<long> clipPathIds)
    {
        ArgumentNullException.ThrowIfNull(clipPathIds);
        var body = new List<byte>();
        WriteFloat(body, volume);
        WriteFloat(body, minPitch);
        WriteFloat(body, maxPitch);
        WriteI32(body, clipPathIds.Count);
        foreach (long id in clipPathIds)
        {
            WriteI32(body, 0); // m_FileID: same file
            WriteI64(body, id);
        }

        Align(body);
        return Add(containerPath, MonoBehaviourClassId, AudioDefinitionTree(), body);
    }

    // Names an already-added object under a SECOND container path. Real bundles do this — a holiday
    // variant reuses one texture under two names — and it is what makes "the same asset, asked for twice"
    // reachable in a test.
    public void Alias(string containerPath, long pathId) => _container.Add((containerPath, pathId));

    private long Add(string containerPath, int classId, byte[] tree, List<byte> payload)
    {
        if (!_typeByClassId.ContainsKey(classId))
        {
            _typeByClassId[classId] = _types.Count;
            _types.Add((classId, tree));
        }

        long pathId = _nextPathId++;
        _objects.Add((pathId, classId, payload.ToArray()));
        if (containerPath.Length > 0)
            _container.Add((containerPath, pathId));
        return pathId;
    }

    // The SerializedFile alone, which is what a caller that already holds one is tested against.
    public byte[] BuildSerializedFile()
    {
        var objects = new List<(long PathId, int ClassId, byte[] Payload)>(_objects)
        {
            (_nextPathId++, AssetBundleClassId, ContainerPayload()),
        };
        if (!_typeByClassId.ContainsKey(AssetBundleClassId))
        {
            _typeByClassId[AssetBundleClassId] = _types.Count;
            _types.Add((AssetBundleClassId, AssetBundleTree()));
        }

        var meta = new List<byte>();
        WriteCString(meta, "5.x.x");
        WriteI32(meta, 0);           // target platform
        meta.Add(1);                 // enable type tree
        WriteI32(meta, _types.Count);
        foreach ((int classId, byte[] tree) in _types)
        {
            WriteI32(meta, classId);
            meta.AddRange(new byte[16]); // old type hash
            meta.AddRange(tree);
        }

        WriteI32(meta, objects.Count);
        int byteStart = 0;
        foreach ((long pathId, int classId, byte[] payload) in objects)
        {
            while (meta.Count % 4 != 0)
                meta.Add(0);
            WriteI64(meta, pathId);
            WriteU32(meta, (uint)byteStart);
            WriteU32(meta, (uint)payload.Length);
            WriteI32(meta, 0);              // type id: unread at version 15, the class id below selects
            WriteU16(meta, (ushort)classId);
            WriteI16(meta, 0);              // script type index
            meta.Add(0);                    // stripped
            byteStart += (payload.Length + 3) & ~3;
        }

        var header = new List<byte>();
        WriteU32Be(header, 0);       // metadata size (legacy, unread)
        WriteU32Be(header, 0);       // file size (legacy, unread)
        WriteU32Be(header, 15);      // version
        int dataOffsetPos = header.Count;
        WriteU32Be(header, 0);       // data offset, patched below
        header.Add(0);               // endianess: little
        header.AddRange(new byte[3]);

        var all = new List<byte>(header);
        all.AddRange(meta);
        // The reader aligns object fields on absolute positions, so the data section starts aligned or
        // every string's padding shifts under it.
        while (all.Count % 16 != 0)
            all.Add(0);
        uint dataOffset = (uint)all.Count;
        for (int i = 0; i < 4; i++)
            all[dataOffsetPos + i] = (byte)(dataOffset >> ((3 - i) * 8));
        foreach ((long _, int _, byte[] payload) in objects)
        {
            all.AddRange(payload);
            while ((all.Count - dataOffset) % 4 != 0)
                all.Add(0);
        }

        return all.ToArray();
    }

    // The whole bundle: the SerializedFile, then the stream entries the objects point into. LZMA-compressed
    // as a single block, which is the shape the streaming readers understand and the game's own bundle has.
    public byte[] BuildBundle(bool singleLzmaBlock = true)
    {
        var fs = new UnityFsBuilder { LzmaBlocks = singleLzmaBlock };
        fs.Add("CAB-test", BuildSerializedFile());
        if (_stream.Count > 0)
            fs.Add(StreamName, _stream.ToArray());
        if (_resource.Count > 0)
            fs.Add(ResourceName, _resource.ToArray());
        return fs.Build();
    }

    private byte[] ContainerPayload()
    {
        var body = new List<byte>();
        WriteString(body, "cab-test");
        WriteI32(body, _container.Count);
        foreach ((string path, long pathId) in _container)
        {
            WriteString(body, path);
            WriteU32(body, 0); // preload index
            WriteU32(body, 0); // preload size
            WriteI32(body, 0); // m_FileID
            WriteI64(body, pathId);
        }

        Align(body);
        return body.ToArray();
    }

    // ---- type trees -------------------------------------------------------------------------------
    //
    // Each of these has to match its payload writer field for field: TypeTreeReader walks the tree and
    // consumes bytes as it goes, so a tree with one field the payload does not write reads the next
    // field's bytes as that one and everything after it is garbage. They are kept adjacent for that
    // reason.

    private static byte[] AssetBundleTree()
    {
        var t = new TreeWriter();
        t.Node(0, "AssetBundle", "Base", -1);
        t.String(1, "m_Name");
        t.Node(1, "map", "m_Container", -1);
        t.Node(2, "Array", "Array", -1, array: true, align: true);
        t.Node(3, "int", "size", 4);
        t.Node(3, "pair", "data", -1);
        t.String(4, "first");
        t.Node(4, "AssetInfo", "second", -1);
        t.Node(5, "unsigned int", "preloadIndex", 4);
        t.Node(5, "unsigned int", "preloadSize", 4);
        t.Node(5, "PPtr<Object>", "asset", -1);
        t.Node(6, "int", "m_FileID", 4);
        t.Node(6, "SInt64", "m_PathID", 8);
        return t.Build();
    }

    // One tree for every Texture2D in the file, because version 15 addresses a type by class id: two
    // Texture2D trees in one file cannot be told apart, and the second object would be read with the
    // first's shape. Real bundles have the same constraint and resolve it the same way — m_StreamData is
    // always present, and an empty path is what marks the pixels as inline.
    private static byte[] TextureTree()
    {
        var t = new TreeWriter();
        t.Node(0, "Texture2D", "Base", -1);
        t.String(1, "m_Name");
        t.Node(1, "int", "m_Width", 4);
        t.Node(1, "int", "m_Height", 4);
        t.Node(1, "int", "m_TextureFormat", 4);
        t.Node(1, "int", "m_MipCount", 4);
        t.Node(1, "TypelessData", "image data", -1);
        t.Node(1, "StreamingInfo", "m_StreamData", -1);
        t.Node(2, "unsigned int", "offset", 4);
        t.Node(2, "unsigned int", "size", 4);
        t.String(2, "path");
        return t.Build();
    }

    private static byte[] AudioClipTree()
    {
        var t = new TreeWriter();
        t.Node(0, "AudioClip", "Base", -1);
        t.String(1, "m_Name");
        t.Node(1, "StreamedResource", "m_Resource", -1);
        t.String(2, "m_Source");
        t.Node(2, "UInt64", "m_Offset", 8);
        t.Node(2, "UInt64", "m_Size", 8);
        return t.Build();
    }

    private static byte[] AudioDefinitionTree()
    {
        var t = new TreeWriter();
        t.Node(0, "MonoBehaviour", "Base", -1);
        t.Node(1, "float", "volumeMultiplier", 4);
        t.Node(1, "float", "minPitch", 4);
        t.Node(1, "float", "maxPitch", 4);
        t.Node(1, "vector", "clips", -1);
        t.Node(2, "Array", "Array", -1, array: true, align: true);
        t.Node(3, "int", "size", 4);
        t.Node(3, "PPtr<AudioClip>", "data", -1);
        t.Node(4, "int", "m_FileID", 4);
        t.Node(4, "SInt64", "m_PathID", 8);
        return t.Build();
    }

    // Emits the version-12+ type tree blob: fixed-size nodes addressing a shared string buffer.
    private sealed class TreeWriter
    {
        private readonly List<byte> _nodes = new();
        private readonly List<byte> _strings = new();
        private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal);
        private int _count;

        internal void Node(int level, string type, string name, int byteSize, bool array = false,
            bool align = false)
        {
            WriteU16(_nodes, 1);                    // node version
            _nodes.Add((byte)level);
            _nodes.Add((byte)(array ? 1 : 0));
            WriteU32(_nodes, Offset(type));
            WriteU32(_nodes, Offset(name));
            WriteI32(_nodes, byteSize);
            WriteI32(_nodes, 0);                    // index
            WriteI32(_nodes, align ? 0x4000 : 0);
            _count++;
        }

        // A string field, which is always the same four nodes: Unity models it as an aligned char array.
        internal void String(int level, string name)
        {
            Node(level, "string", name, -1, align: true);
            Node(level + 1, "Array", "Array", -1, array: true, align: true);
            Node(level + 2, "int", "size", 4);
            Node(level + 2, "char", "data", 1);
        }

        internal byte[] Build()
        {
            var blob = new List<byte>();
            WriteI32(blob, _count);
            WriteI32(blob, _strings.Count);
            blob.AddRange(_nodes);
            blob.AddRange(_strings);
            return blob.ToArray();
        }

        private uint Offset(string value)
        {
            if (_offsets.TryGetValue(value, out uint offset))
                return offset;

            offset = (uint)_strings.Count;
            _strings.AddRange(Encoding.ASCII.GetBytes(value));
            _strings.Add(0);
            _offsets[value] = offset;
            return offset;
        }
    }

    // ---- little-endian writers --------------------------------------------------------------------

    private static void Align(List<byte> b)
    {
        while (b.Count % 4 != 0)
            b.Add(0);
    }

    // A Unity string: length, bytes, then padding to the next 4-byte boundary.
    private static void WriteString(List<byte> b, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteI32(b, bytes.Length);
        b.AddRange(bytes);
        Align(b);
    }

    private static void WriteCString(List<byte> b, string s)
    {
        b.AddRange(Encoding.ASCII.GetBytes(s));
        b.Add(0);
    }

    private static void WriteFloat(List<byte> b, float value) =>
        WriteU32(b, (uint)BitConverter.SingleToInt32Bits(value));

    private static void WriteU16(List<byte> b, ushort v)
    {
        b.Add((byte)v);
        b.Add((byte)(v >> 8));
    }

    private static void WriteI16(List<byte> b, short v) => WriteU16(b, (ushort)v);

    private static void WriteI32(List<byte> b, int v) => WriteU32(b, (uint)v);

    private static void WriteU32(List<byte> b, uint v)
    {
        for (int i = 0; i < 4; i++)
            b.Add((byte)(v >> (i * 8)));
    }

    private static void WriteI64(List<byte> b, long v) => WriteU64(b, (ulong)v);

    private static void WriteU64(List<byte> b, ulong v)
    {
        for (int i = 0; i < 8; i++)
            b.Add((byte)(v >> (i * 8)));
    }

    private static void WriteU32Be(List<byte> b, uint v)
    {
        for (int i = 3; i >= 0; i--)
            b.Add((byte)(v >> (i * 8)));
    }
}
