using System;
using System.IO;
using System.Text;

namespace UnturnedGodot.Data;

// File.ReadAllText, without the buffers it grows on the way to the string.
//
// The framework's version streams the file through a FileStream buffer and a StreamReader into a
// StringBuilder that allocates another block whenever it fills, then copies the lot into the string it
// returns. None of that intermediate storage has to exist: the content is on disk and its length is
// known before the first byte is read, so one right-sized array and one decode produce the same string.
//
// It matters because of how often this runs, not because any one file is large. A PEI load reads 2,327
// asset definitions, a tile-material list per terrain tile, and every foliage, spawn-table, landscape
// and physics-material directory in the install — and it is the fixed per-file overhead that dominates,
// roughly 11 KB of transient against definitions that average about 2 KB.
//
// The array belongs to one read and to nothing else: it is allocated inside this method, filled,
// decoded into a fresh string, and garbage the moment that string exists. Nothing is shared, pooled or
// reused, so there is no buffer whose owner has to be named and no reader that can be handed another's
// bytes. Only the span the read actually filled is decoded, so the one uninitialized byte past the end
// is never looked at.
//
// ArrayPool was measured here and is deliberately not used. Over one load's worth of reads it allocates
// 13.3 MB against this form's 18.0 MB — but ArrayPool.Shared keeps the buckets it grows for the rest of
// the process, which measured as 1.2 MB still held after a full collection against this form's 19 KB,
// and as +2 MB of runtime.managedLiveBytes across a Tier 3 run. Buying load-time allocation with
// steady-state residency is a trade; this is not.
public static class TextFile
{
    // Beyond this a file goes back to the framework's streaming read: the length has to fit the int the
    // array and the span are sized by, with the one extra byte the read asks for still inside it.
    public const long MaxWholeFileBytes = int.MaxValue - 1;

    // Whether a file of this length is read whole. Public because it is the rule the read follows, and
    // the only way to check the ends of it without putting a 2 GB file on disk.
    public static bool CanReadWhole(long length) => length > 0 && length <= MaxWholeFileBytes;

    // Byte-for-byte what File.ReadAllText(path) returns, including which byte-order marks decide the
    // encoding and which exceptions a missing or unreadable file throws.
    public static string ReadAllText(string path)
    {
        // bufferSize 1 asks for the unbuffered strategy: the whole file is read in one call, so the
        // framework's staging buffer would be allocated and copied through for nothing.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.SequentialScan);

        long length = stream.CanSeek ? stream.Length : 0;
        if (!CanReadWhole(length))
            return File.ReadAllText(path); // empty, or with no length to size a buffer from

        // One byte more than the file claims to be: reading fewer than we asked for is then the proof
        // that the end really was where Length said, and reading all of them means it grew under us.
        byte[] buffer = GC.AllocateUninitializedArray<byte>((int)length + 1);
        int filled = stream.ReadAtLeast(buffer, (int)length + 1, throwOnEndOfStream: false);
        return filled == length
            ? Decode(buffer.AsSpan(0, filled))
            : File.ReadAllText(path); // the file changed size while we were reading it
    }

    // The byte-order marks StreamReader detects, in the order it checks them, since File.ReadAllText
    // asks it to (detectEncodingFromByteOrderMarks: true) and leaves UTF-8 as the fallback. The mark
    // itself is consumed rather than returned, which is what the reader's own position does.
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return bytes.Length >= 4 && bytes[2] == 0x00 && bytes[3] == 0x00
                ? new UTF32Encoding(bigEndian: false, byteOrderMark: true).GetString(bytes[4..])
                : Encoding.Unicode.GetString(bytes[2..]);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE
            && bytes[3] == 0xFF)
        {
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true).GetString(bytes[4..]);
        }
        return Encoding.UTF8.GetString(bytes);
    }
}
