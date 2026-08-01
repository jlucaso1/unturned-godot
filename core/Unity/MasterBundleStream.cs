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

    // Same, straight off disk: only the header and the block table are read into memory and the decoder
    // pulls the compressed block through the file itself. Holding a whole masterbundle in memory for the
    // length of the pass cost a quarter of a gigabyte per bundle, and nothing ever read it twice.
    public static MasterBundleStream? OpenFile(string path)
    {
        FileStream? file = null;
        try
        {
            // Buffered and hinted sequential: the decoder pulls the block in small reads, and unbuffered
            // those became one syscall each — the pass took half again as long as reading the file whole.
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 20, FileOptions.SequentialScan);

            // The header and the (compressed) block table sit at the front; a header longer than this
            // would be a bundle with thousands of nodes, which is not the shape this reader handles.
            var head = new byte[(int)Math.Min(HeadProbeBytes, file.Length)];
            file.ReadExactly(head);

            Layout? layout = ReadLayout(head, size =>
            {
                var tail = new byte[size];
                file.Position = file.Length - size;
                file.ReadExactly(tail);
                return tail;
            });

            if (layout == null)
            {
                // Every unsupported bundle takes this exit, and the fallback decoder opens the file again
                // straight after: leaving the handle to a finaliser leaks a descriptor per attempt, and on
                // Windows the second open would contend with the first.
                file.Dispose();
                return null;
            }

            file.Position = layout.BlockStart + 5;
            var lzma = new LzmaStream(layout.Properties, file, layout.PayloadLength, layout.Uncompressed);
            return new MasterBundleStream(lzma, file, layout.Nodes, layout.Uncompressed);
        }
        catch (Exception)
        {
            file?.Dispose();
            return null;
        }
    }

    // How much of the file the header parse is allowed to need. The real masterbundles use a few hundred
    // bytes; anything past this falls back to the whole-blob path rather than guessing.
    private const int HeadProbeBytes = 1 << 20;

    // Everything the header says about the single data block, so both entry points parse it the same way.
    private sealed record Layout(List<Node> Nodes, int BlockStart, byte[] Properties, int PayloadLength,
        uint Uncompressed);

    private static MasterBundleStream? TryOpen(byte[] bundle)
    {
        Layout? layout = ReadLayout(bundle, size =>
        {
            var tail = new byte[size];
            Array.Copy(bundle, bundle.Length - size, tail, 0, size);
            return tail;
        });

        if (layout == null)
            return null;

        var input = new MemoryStream(bundle, layout.BlockStart + 5, layout.PayloadLength);
        var lzma = new LzmaStream(layout.Properties, input, layout.PayloadLength, layout.Uncompressed);
        return new MasterBundleStream(lzma, input, layout.Nodes, layout.Uncompressed);
    }

    // `readFromEnd` supplies the block table for bundles that store it at the end of the file, which is
    // the one thing the front of the file does not contain.
    private static Layout? ReadLayout(byte[] bundle, Func<int, byte[]> readFromEnd)
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

        byte[] compressedBlocksInfo = blockInfoAtEnd
            ? readFromEnd(compressedBlocksInfoSize)
            : r.ReadBytes(compressedBlocksInfoSize);

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
        int blockStart = r.Position;
        var properties = new byte[5];
        Array.Copy(bundle, blockStart, properties, 0, 5);
        return new Layout(nodes, blockStart, properties, (int)blockCompressed - 5, blockUncompressed);
    }

    // Reads exactly count decompressed bytes (or fewer at end of stream) into a fresh array, advancing the
    // cursor. Must be called with the blob consumed strictly front-to-back.
    public byte[] Read(int count)
    {
        var buffer = new byte[count];
        int read = Read(buffer, 0, count);
        if (read != count)
            Array.Resize(ref buffer, read);
        return buffer;
    }

    // Reads up to count decompressed bytes straight into dest at offset (fewer at end of stream), returning
    // how many were read — so a caller can accumulate into one buffer without an intermediate copy.
    public int Read(byte[] dest, int offset, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = _decompressor.Read(dest, offset + read, count - read);
            if (n <= 0)
                break;
            read += n;
        }
        _cursor += read;
        return read;
    }

    public void Dispose()
    {
        _decompressor.Dispose();
        _input.Dispose();
    }
}
