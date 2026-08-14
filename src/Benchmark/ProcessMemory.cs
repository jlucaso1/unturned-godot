using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace UnturnedGodot.Benchmark;

public static class ProcessMemory
{
    // The buffer /proc/self/status is read into, reused across calls.
    //
    // The line walk this replaces allocated a string per line until VmRSS: turned up — which sits about
    // two thirds of the way down a file of some fifty lines — plus a string[] and a string per field of
    // the line that matched: 9,112 B per call, measured. That is the F3 HUD's per-refresh cost, five
    // times a second while it is up, and the benchmark tiers sample it too. The file is ~1.3 KiB on
    // every kernel that has it, so one read covers it; a status file somehow longer than the buffer
    // falls through to WorkingSet64, which on Linux is the same RSS read through a different file.
    //
    // Read as raw bytes through a file handle rather than through a StreamReader, because the reader
    // brings its own byte and char buffers and allocating those per call was most of what remained
    // (9,112 B down to 7,632, against 208 for this). The file is ASCII, so the bytes ARE the characters.
    //
    // Deliberately not thread-safe: every caller is the HUD or a benchmark sample, both on the main
    // thread, and nothing hands the buffer out.
    private static readonly byte[] Buffer = new byte[4096];

    public static long RssBytes()
    {
        try
        {
            using (SafeFileHandle handle = File.OpenHandle("/proc/self/status"))
            {
                // Read in a loop rather than once. RandomAccess.Read may return a short read even for a
                // regular file, and a short read is indistinguishable from end-of-file to the parser
                // below — which is the difference between "this is the whole file" and "the value line
                // may be cut in half". Filling the buffer here means reachedEnd below is a fact about
                // the FILE rather than about one syscall's mood.
                int read = 0;
                int got;
                while (read < Buffer.Length
                    && (got = RandomAccess.Read(handle, Buffer.AsSpan(read), read)) > 0)
                {
                    read += got;
                }

                if (TryReadVmRss(Buffer.AsSpan(0, read), reachedEnd: read < Buffer.Length,
                        out long bytes))
                {
                    return bytes;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
            return process.WorkingSet64;
        }
        catch { return 0; }
    }

    // "VmRSS:\t   123456 kB" out of the whole file, without cutting it into lines or fields.
    //
    // The contract is the strict one, and it is the whole point of the method: it returns true ONLY for
    // a value it has proved is complete and representable. Anything else — no key, no digits, a line the
    // read stopped in the middle of, a number too large to turn into bytes — is false, and false sends
    // RssBytes to WorkingSet64, which on Linux is the same RSS through a different file.
    //
    // Being strict is not pedantry here. A wrong answer from this does not look like a failure: it looks
    // like the process shed memory. Reporting 123 MB for a 123,456 kB reading would show up on the HUD
    // and in a benchmark report as a drop, and this repo measures before it asserts — a silently wrong
    // measurement is worse than a missing one.
    //
    // `reachedEnd` says whether `status` is the whole file or merely as much of it as fit in a buffer.
    // Without it the two are indistinguishable, and a file that ran past the buffer mid-number would
    // have its digits accepted as if they were the value.
    internal static bool TryReadVmRss(ReadOnlySpan<byte> status, bool reachedEnd, out long bytes)
    {
        bytes = 0;
        ReadOnlySpan<byte> key = "VmRSS:"u8;
        int at = status.IndexOf(key);
        // Only a line that STARTS with the key counts, which is what StartsWith gave the walk. Nothing
        // in the file ends in "VmRSS:" today; anchoring it is what stops a later field that contains it
        // from being read as this one.
        if (at < 0 || (at > 0 && status[at - 1] != (byte)'\n'))
            return false;

        ReadOnlySpan<byte> rest = status[(at + key.Length)..];
        int end = rest.IndexOf((byte)'\n');
        if (end >= 0)
            rest = rest[..end];
        else if (!reachedEnd)
            return false; // the read stopped inside this line: its digits may be a prefix of the value

        // The kernel pads the number into a column, so the value is behind a tab and some spaces.
        while (rest.Length > 0 && (rest[0] == (byte)' ' || rest[0] == (byte)'\t'))
            rest = rest[1..];
        int digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit((char)rest[digits]))
            digits++;
        if (digits == 0 || !long.TryParse(rest[..digits], out long kib))
            return false;
        // TryParse only proved the digits fit a long; kB to bytes is another factor of 1024, and an
        // overflow there wraps to a negative or absurdly small figure rather than throwing. No real
        // VmRSS is within exabytes of this, which is exactly why it would never be noticed.
        if (kib > long.MaxValue / 1024)
            return false;
        bytes = kib * 1024;
        return true;
    }
}
