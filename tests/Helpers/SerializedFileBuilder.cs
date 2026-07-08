using System;
using System.Collections.Generic;
using System.Text;

namespace UnturnedGodot.Tests.Helpers;

// Builds a minimal version-22 SerializedFile with one typed object, for hermetic SerializedFile tests.
// The header is big-endian; the metadata is little-endian (endianess byte = 0).
public sealed class SerializedFileBuilder
{
    public int ClassId { get; set; } = 43;
    public int DependencyCount { get; set; } = 1;
    public bool EnableTypeTree { get; set; } = true;

    public byte[] Build()
    {
        var meta = new List<byte>();
        WriteCString(meta, "2022.3.62f3");
        WriteI32Le(meta, 0);                 // target platform
        meta.Add((byte)(EnableTypeTree ? 1 : 0));

        WriteI32Le(meta, 1);          // type count
        WriteI32Le(meta, ClassId);
        meta.Add(0);                  // is stripped
        WriteI16Le(meta, 0);          // script type index
        if (ClassId == 114)
            meta.AddRange(new byte[16]); // script id
        meta.AddRange(new byte[16]);  // old type hash

        if (EnableTypeTree)
        {
            // Type tree blob: single "Base" node using a local string buffer.
            byte[] stringBuffer = Encoding.ASCII.GetBytes("Base\0");
            WriteI32Le(meta, 1);                 // node count
            WriteI32Le(meta, stringBuffer.Length);
            WriteU16Le(meta, 1);                 // node version
            meta.Add(0);                         // level
            meta.Add(0);                         // type flags
            WriteU32Le(meta, 0);                 // type string offset -> "Base"
            WriteU32Le(meta, 0);                 // name string offset -> "Base"
            WriteI32Le(meta, -1);                // byte size
            WriteI32Le(meta, 0);                 // index
            WriteI32Le(meta, 0);                 // meta flag
            WriteU64Le(meta, 0);                 // ref type hash (version >= 19)
            meta.AddRange(stringBuffer);

            WriteI32Le(meta, DependencyCount);   // type dependencies (version >= 21)
            for (int i = 0; i < DependencyCount; i++)
                WriteI32Le(meta, 0);
        }

        WriteI32Le(meta, 1);          // object count
        AlignLe(meta);
        WriteI64Le(meta, 100);        // path id
        WriteI64Le(meta, 0);          // byte start (relative to data offset)
        WriteU32Le(meta, 4);          // byte size
        WriteI32Le(meta, 0);          // type id

        // Header (big-endian). dataOffset points right after the metadata region.
        long dataOffset = 0; // filled once header size is known
        var header = new List<byte>();
        WriteU32Be(header, 0);        // legacy metadata size
        WriteU32Be(header, 0);        // legacy file size
        WriteU32Be(header, 22);       // version
        WriteU32Be(header, 0);        // legacy data offset
        header.Add(0);                // endianess (little)
        header.AddRange(new byte[3]); // reserved
        WriteU32Be(header, 0);        // metadata size
        WriteI64Be(header, 0);        // file size
        int dataOffsetPos = header.Count;
        WriteI64Be(header, 0);        // data offset (patched below)
        WriteI64Be(header, 0);        // unknown

        var all = new List<byte>();
        all.AddRange(header);
        all.AddRange(meta);
        dataOffset = all.Count;
        // patch data offset
        for (int i = 0; i < 8; i++)
            all[dataOffsetPos + i] = (byte)(dataOffset >> ((7 - i) * 8));
        all.AddRange(new byte[] { 1, 2, 3, 4 }); // object data
        return all.ToArray();
    }

    private static void WriteCString(List<byte> b, string s) { b.AddRange(Encoding.ASCII.GetBytes(s)); b.Add(0); }
    private static void AlignLe(List<byte> b) { while (b.Count % 4 != 0) b.Add(0); }

    private static void WriteU16Le(List<byte> b, ushort v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
    private static void WriteI16Le(List<byte> b, short v) => WriteU16Le(b, (ushort)v);
    private static void WriteI32Le(List<byte> b, int v) => WriteU32Le(b, (uint)v);
    private static void WriteU32Le(List<byte> b, uint v)
    {
        for (int i = 0; i < 4; i++) b.Add((byte)(v >> (i * 8)));
    }
    private static void WriteI64Le(List<byte> b, long v) { for (int i = 0; i < 8; i++) b.Add((byte)(v >> (i * 8))); }
    private static void WriteU64Le(List<byte> b, ulong v) { for (int i = 0; i < 8; i++) b.Add((byte)(v >> (i * 8))); }

    private static void WriteU32Be(List<byte> b, uint v) { for (int i = 3; i >= 0; i--) b.Add((byte)(v >> (i * 8))); }
    private static void WriteI64Be(List<byte> b, long v) { for (int i = 7; i >= 0; i--) b.Add((byte)(v >> (i * 8))); }
}
