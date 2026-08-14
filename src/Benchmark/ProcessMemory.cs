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
                int read = RandomAccess.Read(handle, Buffer, 0);
                if (TryReadVmRss(Buffer.AsSpan(0, read), out long bytes))
                    return bytes;
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

    // "VmRSS:\t   123456 kB" out of the whole file, without cutting it into lines or fields. False when
    // the key is not there or carries no number, which sends the caller to the portable fallback — the
    // same answer the line walk gave on a platform with no /proc.
    internal static bool TryReadVmRss(ReadOnlySpan<byte> status, out long bytes)
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
        // The kernel pads the number into a column, so the value is behind a tab and some spaces.
        while (rest.Length > 0 && (rest[0] == (byte)' ' || rest[0] == (byte)'\t'))
            rest = rest[1..];
        int digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit((char)rest[digits]))
            digits++;
        if (digits == 0 || !long.TryParse(rest[..digits], out long kib))
            return false;
        bytes = kib * 1024;
        return true;
    }
}
