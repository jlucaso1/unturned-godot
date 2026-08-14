using System;
using System.IO;

namespace UnturnedGodot.Unity;

// LZ4 block decompressor. UnityFS uses LZ4/LZ4HC block compression; both decode with this single
// algorithm (LZ4HC only differs in how it encodes). No dependency, so it stays under test.
//
// Every read and every write is bounds-checked against the two buffers, and a block that does not fit
// throws InvalidDataException. That is not defensive habit: this is the codec UnityFS uses for its
// *blocks-info* header (UnityBundle.Read, MasterBundleStream.ReadLayout), which is decoded before a
// single field of the bundle has been validated — the literal lengths, match offsets and match lengths
// steering the loop below all come straight off disk. Unchecked, a malformed block reads past the end of
// the source (a match offset read at src.Length - 1), writes past the end of the destination (a literal
// run longer than the declared uncompressed size), or copies from a negative index (a match offset larger
// than what has been produced so far). Those faulted with whatever exception the runtime happened to
// raise from the bad access; naming the failure makes it one the callers already contain, and one the
// suite can actually assert on.
public static class Lz4
{
    public static byte[] Decompress(ReadOnlySpan<byte> src, int uncompressedSize)
    {
        if (uncompressedSize < 0)
            throw new InvalidDataException($"Negative LZ4 output size {uncompressedSize}");

        var dst = new byte[uncompressedSize];
        int sp = 0;
        int dp = 0;

        while (sp < src.Length)
        {
            byte token = src[sp++];

            int literalLen = token >> 4;
            if (literalLen == 15)
                literalLen += ReadLength(src, ref sp);

            // The slice and the copy each need their own room: a run can be longer than the input holds
            // (truncated block) or longer than the declared output holds (a size that does not match the
            // stream). Span would throw on both, but from inside the BCL and without naming the block.
            if (literalLen > src.Length - sp)
                throw new InvalidDataException($"LZ4 literal run of {literalLen} runs past the input");
            if (literalLen > dst.Length - dp)
                throw new InvalidDataException($"LZ4 literal run of {literalLen} runs past the output");

            // Literals never overlap the output, so the whole run bulk-copies (-65% decode time measured
            // on long-literal blocks vs the old per-byte loop). Matches below must stay per-byte.
            src.Slice(sp, literalLen).CopyTo(dst.AsSpan(dp));
            sp += literalLen;
            dp += literalLen;

            // The final sequence in a block is literals only; input is exhausted here.
            if (sp >= src.Length)
                break;

            // The offset is two bytes, so one byte left is a truncated sequence rather than a complete
            // one — reading it unchecked took the byte past the end of the block.
            if (sp + 2 > src.Length)
                throw new InvalidDataException("LZ4 match offset is truncated");

            int offset = src[sp] | (src[sp + 1] << 8); // little-endian match offset
            sp += 2;

            int matchLen = token & 0x0F;
            if (matchLen == 15)
                matchLen += ReadLength(src, ref sp);
            matchLen += 4; // minimum match length

            // A match copies from output already produced, so the offset has to name a byte inside it:
            // zero is no distance at all, and anything beyond dp points before the buffer.
            if (offset <= 0 || offset > dp)
                throw new InvalidDataException($"LZ4 match offset {offset} is outside the output produced so far");
            if (matchLen > dst.Length - dp)
                throw new InvalidDataException($"LZ4 match of {matchLen} runs past the output");

            int matchPos = dp - offset;
            for (int i = 0; i < matchLen; i++) // byte-by-byte for overlapping matches
                dst[dp++] = dst[matchPos++];
        }

        return dst;
    }

    // Extended length encoding: sum bytes until one is not 255. Both bounds matter — the run has no
    // terminator of its own, so a block ending in 0xFF bytes walks off the end, and a long enough run of
    // them overflows the sum into a negative length that then passes a naive "too long" test.
    private static int ReadLength(ReadOnlySpan<byte> src, ref int sp)
    {
        long extra = 0;
        byte b;
        do
        {
            if (sp >= src.Length)
                throw new InvalidDataException("LZ4 extended length runs past the input");
            b = src[sp++];
            extra += b;
            if (extra > int.MaxValue)
                throw new InvalidDataException("LZ4 extended length overflows");
        } while (b == 255);
        return (int)extra;
    }
}
