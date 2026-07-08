using System;

namespace UnturnedGodot.Unity;

// LZ4 block decompressor. UnityFS uses LZ4/LZ4HC block compression; both decode with this single
// algorithm (LZ4HC only differs in how it encodes). No dependency, so it stays under test.
public static class Lz4
{
    public static byte[] Decompress(ReadOnlySpan<byte> src, int uncompressedSize)
    {
        var dst = new byte[uncompressedSize];
        int sp = 0;
        int dp = 0;

        while (sp < src.Length)
        {
            byte token = src[sp++];

            int literalLen = token >> 4;
            if (literalLen == 15)
                literalLen += ReadLength(src, ref sp);

            for (int i = 0; i < literalLen; i++)
                dst[dp++] = src[sp++];

            // The final sequence in a block is literals only; input is exhausted here.
            if (sp >= src.Length)
                break;

            int offset = src[sp] | (src[sp + 1] << 8); // little-endian match offset
            sp += 2;

            int matchLen = token & 0x0F;
            if (matchLen == 15)
                matchLen += ReadLength(src, ref sp);
            matchLen += 4; // minimum match length

            int matchPos = dp - offset;
            for (int i = 0; i < matchLen; i++) // byte-by-byte for overlapping matches
                dst[dp++] = dst[matchPos++];
        }

        return dst;
    }

    // Extended length encoding: sum bytes until one is not 255.
    private static int ReadLength(ReadOnlySpan<byte> src, ref int sp)
    {
        int extra = 0;
        byte b;
        do
        {
            b = src[sp++];
            extra += b;
        } while (b == 255);
        return extra;
    }
}
