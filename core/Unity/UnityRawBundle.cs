using System;
using System.Collections.Generic;

namespace UnturnedGodot.Unity;

// Reads the legacy "UnityRaw" bundle format (Unity 5.x), which per-map bundles like a map's Roads.unity3d
// still ship in — unlike the modern "UnityFS" masterbundle (see UnityBundle). UnityRaw stores its files
// uncompressed after a header, so we just slice each entry out. Only the raw (uncompressed) variant is
// supported; the LZMA-compressed "UnityWeb" variant is rejected.
public sealed class UnityRawBundle
{
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    private UnityRawBundle(Dictionary<string, byte[]> files) => Files = files;

    // True when the bytes start with the "UnityRaw" signature (so callers can pick this reader).
    public static bool IsRaw(byte[] data) =>
        data.Length >= 8 && data[0] == 'U' && data[1] == 'n' && data[2] == 'i' && data[3] == 't' &&
        data[4] == 'y' && data[5] == 'R' && data[6] == 'a' && data[7] == 'w';

    public static UnityRawBundle Read(byte[] data)
    {
        var r = new UnityBinaryReader(data, bigEndian: true); // the header is big-endian

        string signature = r.ReadCString();
        if (signature != "UnityRaw")
            throw new NotSupportedException($"Not a UnityRaw bundle: '{signature}'");

        r.ReadUInt32();               // stream version
        r.ReadCString();              // unity version
        r.ReadCString();              // unity revision
        r.ReadUInt32();               // minimum streamed bytes
        uint headerSize = r.ReadUInt32();
        r.ReadUInt32();               // bytes to download before streaming
        int levelCount = (int)r.ReadUInt32();
        for (int i = 0; i < levelCount; i++)
        {
            r.ReadUInt32();           // compressed size
            r.ReadUInt32();           // uncompressed size
        }

        // The entry table (name, offset, size) lives at headerSize; entry data follows it, uncompressed.
        r.Position = (int)headerSize;
        int entryCount = (int)r.ReadUInt32();
        var files = new Dictionary<string, byte[]>(entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            string name = r.ReadCString();
            uint offset = r.ReadUInt32();
            uint size = r.ReadUInt32();
            var bytes = new byte[size];
            Array.Copy(data, headerSize + offset, bytes, 0, size);
            files[name] = bytes;
        }
        return new UnityRawBundle(files);
    }
}
