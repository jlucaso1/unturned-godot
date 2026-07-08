using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SharpCompress.Compressors.LZMA;

namespace UnturnedGodot.Tests.Helpers;

// Builds a minimal UnityFS bundle with uncompressed blocks, for hermetic UnityBundle tests.
public sealed class UnityFsBuilder
{
    private readonly List<(string name, byte[] data)> _files = new();
    public bool BlockInfoAtEnd { get; set; }
    public int BlockInfoCompression { get; set; } // 0 = none; other values exercise the throw path
    public int BlockCount { get; set; } = 1;      // split the blob into this many uncompressed blocks
    public int FormatVersion { get; set; } = 8;    // < 7 skips the 16-byte alignment
    public bool LzmaBlocks { get; set; }           // LZMA-compress the (single) data block
    public bool Lz4Blocks { get; set; }            // LZ4-encode the (single) data block
    public bool PaddingAtStart { get; set; }       // set flag 0x200 (align blocks to 16)

    public UnityFsBuilder Add(string name, byte[] data)
    {
        _files.Add((name, data));
        return this;
    }

    public byte[] Build()
    {
        // Blob = concatenation of the files; one uncompressed block covers it.
        var blob = new List<byte>();
        var nodes = new List<(long offset, long size, string path)>();
        foreach ((string name, byte[] data) in _files)
        {
            nodes.Add((blob.Count, data.Length, name));
            blob.AddRange(data);
        }

        var info = new List<byte>();
        info.AddRange(new byte[16]);               // uncompressed data hash

        byte[] blockData;
        if (LzmaBlocks)
        {
            blockData = CompressLzma(blob.ToArray());
            WriteI32Be(info, 1);
            WriteU32Be(info, (uint)blob.Count);    // uncompressed size
            WriteU32Be(info, (uint)blockData.Length);
            WriteU16Be(info, 1);                   // block flags: LZMA
        }
        else if (Lz4Blocks)
        {
            blockData = Lz4Literals(blob.ToArray());
            WriteI32Be(info, 1);
            WriteU32Be(info, (uint)blob.Count);    // uncompressed size
            WriteU32Be(info, (uint)blockData.Length);
            WriteU16Be(info, 2);                   // block flags: LZ4
        }
        else
        {
            // Split the blob into BlockCount uncompressed blocks (sizes need not match node boundaries).
            blockData = blob.ToArray();
            WriteI32Be(info, BlockCount);
            int per = (blob.Count + BlockCount - 1) / BlockCount;
            int remaining = blob.Count;
            for (int i = 0; i < BlockCount; i++)
            {
                int size = System.Math.Min(per, remaining);
                remaining -= size;
                WriteU32Be(info, (uint)size);      // uncompressed size
                WriteU32Be(info, (uint)size);      // compressed size (== uncompressed; block flag 0)
                WriteU16Be(info, 0);               // block flags: no compression
            }
        }

        WriteI32Be(info, nodes.Count);
        foreach ((long offset, long size, string path) in nodes)
        {
            WriteI64Be(info, offset);
            WriteI64Be(info, size);
            WriteU32Be(info, 0); // node flags
            info.AddRange(Encoding.UTF8.GetBytes(path));
            info.Add(0);
        }
        byte[] blocksInfo = info.ToArray();

        int flags = 0x40 | BlockInfoCompression;
        if (BlockInfoAtEnd)
            flags |= 0x80;
        if (PaddingAtStart)
            flags |= 0x200;

        var bytes = new List<byte>();
        bytes.AddRange(Encoding.UTF8.GetBytes("UnityFS"));
        bytes.Add(0);
        WriteI32Be(bytes, FormatVersion);          // format version
        bytes.AddRange(Encoding.UTF8.GetBytes("5.x.x"));
        bytes.Add(0);
        bytes.AddRange(Encoding.UTF8.GetBytes("2022.3.62f3"));
        bytes.Add(0);
        WriteI64Be(bytes, 0);                      // total size (unused by the reader)
        WriteU32Be(bytes, (uint)blocksInfo.Length); // compressed blocks info size
        WriteU32Be(bytes, (uint)blocksInfo.Length); // uncompressed blocks info size
        WriteU32Be(bytes, (uint)flags);

        if (FormatVersion >= 7)
            while (bytes.Count % 16 != 0)           // version >= 7 aligns to 16
                bytes.Add(0);

        if (BlockInfoAtEnd)
        {
            bytes.AddRange(blockData);
            bytes.AddRange(blocksInfo);
        }
        else
        {
            bytes.AddRange(blocksInfo);
            if (PaddingAtStart)
                while (bytes.Count % 16 != 0)
                    bytes.Add(0);
            bytes.AddRange(blockData);
        }
        return bytes.ToArray();
    }

    // Encodes data as a single LZ4 literal run (no matches) — the minimal valid LZ4 block.
    private static byte[] Lz4Literals(byte[] data)
    {
        var outp = new List<byte> { (byte)(Math.Min(data.Length, 15) << 4) };
        if (data.Length >= 15)
        {
            int rem = data.Length - 15;
            while (rem >= 255) { outp.Add(255); rem -= 255; }
            outp.Add((byte)rem);
        }
        outp.AddRange(data);
        return outp.ToArray();
    }

    private static byte[] CompressLzma(byte[] data)
    {
        using var output = new MemoryStream();
        var props = new LzmaEncoderProperties();
        byte[] header;
        using (var encoder = new LzmaStream(props, false, output))
        {
            header = encoder.Properties;
            encoder.Write(data, 0, data.Length);
        }
        return header.Concat(output.ToArray()).ToArray();
    }

    private static void WriteU16Be(List<byte> b, ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
    private static void WriteI32Be(List<byte> b, int v) => WriteU32Be(b, (uint)v);
    private static void WriteU32Be(List<byte> b, uint v)
    {
        b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v);
    }
    private static void WriteI64Be(List<byte> b, long v)
    {
        for (int i = 7; i >= 0; i--)
            b.Add((byte)(v >> (i * 8)));
    }
}
