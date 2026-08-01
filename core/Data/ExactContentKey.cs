using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace UnturnedGodot.Data;

// Stable keys for byte-identical resources. This is deliberately exact rather than perceptual: sharing
// may reduce RAM/VRAM, but must never merge assets that only look similar.
public static class ExactContentKey
{
    public static string Bytes(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));

    // Hash large cached resources without materialising a second full byte[] just to identify aliases.
    public static string File(string path)
    {
        using FileStream input = System.IO.File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    public static string Image(int format, int width, int height, int mipCount, ReadOnlySpan<byte> pixels)
    {
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header, format);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], mipCount);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header);
        hash.AppendData(pixels);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
