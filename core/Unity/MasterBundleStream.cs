using System;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Compressors.LZMA;

namespace UnturnedGodot.Unity;

// Streams a UnityFS bundle's decompressed blob so a caller can pull the SerializedFile prefix, build the
// scene, then keep pulling the .resS texture stream from the SAME LZMA pass — no re-decompressing the
// ~171 MB SerializedFile, and textures can be extracted progressively as their bytes arrive. Only the
// masterbundle's shape (a single LZMA data block) is supported; Open returns null for anything else (and
// for any malformed input) so the caller can fall back to the whole-blob decode.
public sealed class MasterBundleStream : IDisposable
{
    public readonly struct Node
    {
        public readonly string Path;
        public readonly long Offset;
        public readonly long Size;
        public Node(string path, long offset, long size)
        {
            Path = path;
            Offset = offset;
            Size = size;
        }
    }

    private readonly Stream _decompressor;
    private readonly IDisposable _input;
    private long _cursor;

    public IReadOnlyList<Node> Nodes { get; }
    public long TotalSize { get; }
    public long Cursor => _cursor;

    private MasterBundleStream(Stream decompressor, IDisposable input, List<Node> nodes, long total)
    {
        _decompressor = decompressor;
        _input = input;
        Nodes = nodes;
        TotalSize = total;
    }

    // Parses the header/block table and opens the LZMA decoder over the single data block. Returns null if
    // the bundle is not a single LZMA block or is malformed (the caller then decodes the whole blob the
    // ordinary way).
    public static MasterBundleStream? Open(byte[] bundle)
    {
        try
        {
            return TryOpen(bundle);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static MasterBundleStream? TryOpen(byte[] bundle)
    {
        var r = new UnityBinaryReader(bundle, bigEndian: true);
        if (r.ReadCString() != "UnityFS")
            return null;

        int version = r.ReadInt32();
        r.ReadCString(); // unity min version
        r.ReadCString(); // unity revision
        r.ReadInt64();   // total bundle size
        int compressedBlocksInfoSize = r.ReadInt32();
        int uncompressedBlocksInfoSize = r.ReadInt32();
        int flags = r.ReadInt32();
        if (version >= 7)
            r.Align(16);

        int compressionType = flags & 0x3F;
        bool blockInfoAtEnd = (flags & 0x80) != 0;

        byte[] compressedBlocksInfo;
        if (blockInfoAtEnd)
        {
            int saved = r.Position;
            r.Position = bundle.Length - compressedBlocksInfoSize;
            compressedBlocksInfo = r.ReadBytes(compressedBlocksInfoSize);
            r.Position = saved;
        }
        else
        {
            compressedBlocksInfo = r.ReadBytes(compressedBlocksInfoSize);
        }

        // Reuse the (fully tested) bundle blocks-info decoder; unsupported compression throws and is
        // caught by Open, falling the caller back to the whole-blob path.
        byte[] blocksInfo = UnityBundle.Decompress(compressedBlocksInfo, compressionType, uncompressedBlocksInfoSize);

        if ((flags & 0x200) != 0)
            r.Align(16);

        var info = new UnityBinaryReader(blocksInfo, bigEndian: true);
        info.ReadBytes(16); // uncompressed data hash
        int blockCount = info.ReadInt32();
        if (blockCount != 1)
            return null; // only the single-block masterbundle is streamed

        uint blockUncompressed = info.ReadUInt32();
        uint blockCompressed = info.ReadUInt32();
        ushort blockFlags = info.ReadUInt16();
        if ((blockFlags & 0x3F) != 1)
            return null; // not LZMA

        int nodeCount = info.ReadInt32();
        var nodes = new List<Node>(nodeCount);
        for (int i = 0; i < nodeCount; i++)
        {
            long offset = info.ReadInt64();
            long size = info.ReadInt64();
            info.ReadUInt32(); // node flags
            nodes.Add(new Node(info.ReadCString(), offset, size));
        }

        // The compressed block sits at the reader's current position: 5 LZMA property bytes then the stream.
        byte[] compressed = r.ReadBytes((int)blockCompressed);
        var properties = new byte[5];
        Array.Copy(compressed, 0, properties, 0, 5);
        var input = new MemoryStream(compressed, 5, compressed.Length - 5);
        var lzma = new LzmaStream(properties, input, compressed.Length - 5, blockUncompressed);
        return new MasterBundleStream(lzma, input, nodes, blockUncompressed);
    }

    // Reads exactly count decompressed bytes (or fewer at end of stream) into a fresh array, advancing the
    // cursor. Must be called with the blob consumed strictly front-to-back.
    public byte[] Read(int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = _decompressor.Read(buffer, read, count - read);
            if (n <= 0)
                break;
            read += n;
        }
        _cursor += read;
        if (read != count)
            Array.Resize(ref buffer, read);
        return buffer;
    }

    public void Dispose()
    {
        _decompressor.Dispose();
        _input.Dispose();
    }
}
