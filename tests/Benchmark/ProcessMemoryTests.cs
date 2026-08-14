using System.Runtime.InteropServices;
using UnturnedGodot.Benchmark;
using Xunit;

namespace UnturnedGodot.Tests.Benchmark;

// Reading the process's resident size out of /proc/self/status.
//
// It is the F3 HUD's per-refresh cost and one of the benchmark tiers' samples, and it used to walk the
// file a line at a time — a string per line until VmRSS: turned up two thirds of the way down, then a
// string[] and a string per field of the line that matched. It reads the file into one reused buffer and
// scans it now, which changes what is allocated and must not change what is answered. So what is pinned
// here is the parse, against the shapes a real status file and a broken one take, plus the live figure
// end to end: /proc where there is one, WorkingSet64 where there is not.
public class ProcessMemoryTests
{
    // The line as the kernel writes it: a tab, then leading spaces that pad the number into a column.
    [Fact]
    public void TheKernelsOwnLineIsRead()
    {
        Assert.True(ProcessMemory.TryReadVmRss(
            "Name:\tgodot\nVmPeak:\t 2097152 kB\nVmRSS:\t  345052 kB\nThreads:\t28\n"u8, out long bytes));
        Assert.Equal(345052L * 1024, bytes);
    }

    // The key at the very start of the file, which the anchoring check has to admit rather than reject
    // for want of a newline in front of it.
    [Fact]
    public void TheKeyOnTheFirstLineIsRead()
    {
        Assert.True(ProcessMemory.TryReadVmRss("VmRSS:\t     4 kB\n"u8, out long bytes));
        Assert.Equal(4L * 1024, bytes);
    }

    // Only a line that STARTS with the key counts, which is what the old StartsWith gave. Nothing in the
    // file ends in "VmRSS:" today; the anchor is what stops a later field that contains it being read as
    // this one, and it is the one behavioural difference a plain substring search would have introduced.
    [Fact]
    public void AKeyInTheMiddleOfALineIsNotTheOne()
    {
        Assert.False(ProcessMemory.TryReadVmRss("HugetlbVmRSS:\t 99 kB\n"u8, out long bytes));
        Assert.Equal(0, bytes);
    }

    // Nothing to read: a status file without the field, one whose value is missing, and a buffer that
    // ran out mid-file. Each sends the caller to the portable fallback rather than to a wrong number.
    [Fact]
    public void AnUnreadableValueFallsThrough()
    {
        Assert.False(ProcessMemory.TryReadVmRss("Name:\tgodot\nThreads:\t28\n"u8, out _));
        Assert.False(ProcessMemory.TryReadVmRss("VmRSS:\t kB\n"u8, out _));
        Assert.False(ProcessMemory.TryReadVmRss(""u8, out _));
        Assert.False(ProcessMemory.TryReadVmRss("Name:\tgodot\nVmR"u8, out _));
    }

    // The last line of the file has no trailing newline to stop at.
    [Fact]
    public void TheLastLineNeedsNoTrailingNewline()
    {
        Assert.True(ProcessMemory.TryReadVmRss("Name:\tgodot\nVmRSS:\t 128 kB"u8, out long bytes));
        Assert.Equal(128L * 1024, bytes);
    }

    // The portable fallback, reached directly. On Linux the /proc read above always succeeds, so nothing
    // else in this suite ever runs it — and a fallback that only runs on the platforms CI does not cover
    // is one that breaks exactly when it is finally needed.
    [Fact]
    public void TheWorkingSetFallbackAnswersOnItsOwn()
    {
        Assert.InRange(ProcessMemory.WorkingSetBytes(), 1L << 20, 1L << 40);
    }

    [Fact]
    public void ReportsAPlausiblePositiveSize()
    {
        long bytes = ProcessMemory.RssBytes();

        // A running .NET process is never under a megabyte, and never over a terabyte. The bounds are
        // this loose on purpose: what would be a regression is 0 (the read broke) or a nonsense scale
        // (KiB reported as bytes), and both are outside them.
        Assert.InRange(bytes, 1L << 20, 1L << 40);
    }

    // Both readers have to agree about the unit. The /proc line is in KiB and the fallback is in bytes,
    // and a missing multiply there would read as a process using a thousandth of its real memory.
    [Fact]
    public void AgreesWithTheProcessesOwnWorkingSet()
    {
        long reported = ProcessMemory.RssBytes();
        using var self = System.Diagnostics.Process.GetCurrentProcess();

        // Same order of magnitude. They are two samples of a moving number, so this cannot be equality —
        // but a unit mistake would be off by 1024, which is well outside a factor of eight.
        Assert.InRange(reported, self.WorkingSet64 / 8, self.WorkingSet64 * 8);
    }

    [Fact]
    public void IsAnsweredOnEveryPlatformRatherThanLinuxAlone()
    {
        // On Linux this comes from /proc; everywhere else it comes from the fallback. Either way it is a
        // number, which is what distinguishes it from LoadMemory.ProcessRssMib — that one is a Linux
        // diagnostic and deliberately answers 0 elsewhere.
        Assert.True(ProcessMemory.RssBytes() > 0,
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? "/proc/self/status should have answered on Linux"
                : "the working-set fallback should have answered off Linux");
    }
}
