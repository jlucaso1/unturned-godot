using System;
using System.Collections.Generic;

namespace UnturnedGodot.Unity;

// Parses a UnityFS AssetBundle (the format of Unturned's *.masterbundle) into its contained
// files (the SerializedFile plus any .resS/.resource streams). Supports the compression Unturned
// ships: uncompressed and LZ4/LZ4HC blocks. Layout follows AssetStudio's BundleFile.
public sealed class UnityBundle
{
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    private UnityBundle(Dictionary<string, byte[]> files) => Files = files;

    private readonly struct BlockInfo
    {
        public readonly uint UncompressedSize;
        public readonly uint CompressedSize;
        public readonly ushort Flags;
        public BlockInfo(uint uncompressed, uint compressed, ushort flags)
        {
            UncompressedSize = uncompressed;
            CompressedSize = compressed;
            Flags = flags;
        }
    }

    // maxDecompressedBytes caps how much of the (single-stream) blob is inflated. The masterbundle
    // is one 1.4 GB LZMA block; passing a cap lets callers read just the SerializedFile prefix.
    public static UnityBundle Read(byte[] bundle, long maxDecompressedBytes = long.MaxValue)
    {
        var r = new UnityBinaryReader(bundle, bigEndian: true);

        string signature = r.ReadCString();
        if (signature != "UnityFS")
            throw new NotSupportedException($"Unsupported bundle signature '{signature}'");

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

        byte[] blocksInfo = Decompress(compressedBlocksInfo, compressionType, uncompressedBlocksInfoSize);

        if ((flags & 0x200) != 0)
            r.Align(16); // padding before the data blocks

        var info = new UnityBinaryReader(blocksInfo, bigEndian: true);
        info.ReadBytes(16); // uncompressed data hash

        int blockCount = info.ReadInt32();
        var blocks = new BlockInfo[blockCount];
        long totalUncompressed = 0;
        for (int i = 0; i < blockCount; i++)
        {
            uint u = info.ReadUInt32();
            uint c = info.ReadUInt32();
            ushort f = info.ReadUInt16();
            blocks[i] = new BlockInfo(u, c, f);
            totalUncompressed += u;
        }

        int nodeCount = info.ReadInt32();
        var nodeOffsets = new long[nodeCount];
        var nodeSizes = new long[nodeCount];
        var nodePaths = new string[nodeCount];
        for (int i = 0; i < nodeCount; i++)
        {
            nodeOffsets[i] = info.ReadInt64();
            nodeSizes[i] = info.ReadInt64();
            info.ReadUInt32(); // node flags
            nodePaths[i] = info.ReadCString();
        }

        long blobSize = Math.Min(totalUncompressed, maxDecompressedBytes);
        var blob = new byte[blobSize];
        int blobPos = 0;
        foreach (BlockInfo block in blocks)
        {
            if (blobPos >= blobSize)
                break; // reached the requested cap

            byte[] compressed = r.ReadBytes((int)block.CompressedSize);
            int want = (int)Math.Min(block.UncompressedSize, blobSize - blobPos);
            int blockCompression = block.Flags & 0x3F;

            if (blockCompression == 1) // LZMA: init with full size, but read only the prefix we need
                Array.Copy(Lzma.Decompress(compressed, (int)block.UncompressedSize, want), 0, blob, blobPos, want);
            else
                Array.Copy(Decompress(compressed, blockCompression, (int)block.UncompressedSize),
                    0, blob, blobPos, want);
            blobPos += want;
        }

        var files = new Dictionary<string, byte[]>();
        for (int i = 0; i < nodeCount; i++)
        {
            if (nodeOffsets[i] + nodeSizes[i] > blobSize)
                continue; // node lies beyond the decoded prefix

            var slice = new byte[nodeSizes[i]];
            Array.Copy(blob, nodeOffsets[i], slice, 0, nodeSizes[i]);
            files[nodePaths[i]] = slice;
        }

        return new UnityBundle(files);
    }

    private static byte[] Decompress(byte[] data, int compressionType, int uncompressedSize) => compressionType switch
    {
        0 => data,
        2 or 3 => Lz4.Decompress(data, uncompressedSize), // LZ4 / LZ4HC
        _ => throw new NotSupportedException($"Unsupported bundle compression {compressionType}"),
    };
}
