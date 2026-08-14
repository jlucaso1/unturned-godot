using System;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Benchmark;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Reading the process's resident size out of /proc/self/status.
//
// It is the F3 HUD's per-refresh cost and one of the benchmark tiers' samples, and it used to walk the
// file a line at a time — a string per line until VmRSS: turned up two thirds of the way down, then a
// string[] and a string per field of the line that matched. It reads the file into one reused buffer and
// scans it now, which changes what is allocated and must not change what is answered. So what is pinned
// here is the parse, against the shapes a real status file and a broken one take.
public class ProcessMemoryTests : TestClass
{
    public ProcessMemoryTests(Node testScene) : base(testScene) { }

    // The line as the kernel writes it: a tab, then leading spaces that pad the number into a column.
    [Test]
    public void TheKernelsOwnLineIsRead()
    {
        Assert.True(ProcessMemory.TryReadVmRss(
            "Name:\tgodot\nVmPeak:\t 2097152 kB\nVmRSS:\t  345052 kB\nThreads:\t28\n"u8, out long bytes));
        Assert.Equal(345052L * 1024, bytes);
    }

    // The key at the very start of the file, which the anchoring check has to admit rather than reject
    // for want of a newline in front of it.
    [Test]
    public void TheKeyOnTheFirstLineIsRead()
    {
        Assert.True(ProcessMemory.TryReadVmRss("VmRSS:\t     4 kB\n"u8, out long bytes));
        Assert.Equal(4L * 1024, bytes);
    }

    // Only a line that STARTS with the key counts, which is what the old StartsWith gave. Nothing in the
    // file ends in "VmRSS:" today; the anchor is what stops a later field that contains it being read as
    // this one, and it is the one behavioural difference a plain substring search would have introduced.
    [Test]
    public void AKeyInTheMiddleOfALineIsNotTheOne()
    {
        Assert.False(ProcessMemory.TryReadVmRss("HugetlbVmRSS:\t 99 kB\n"u8, out long bytes));
        Assert.Equal(0, bytes);
    }

    // Nothing to read: a status file without the field, one whose value is missing, and a buffer that
    // ran out mid-file. Each sends the caller to the portable fallback rather than to a wrong number.
    [Test]
    public void AnUnreadableValueFallsThrough()
    {
        Assert.False(ProcessMemory.TryReadVmRss("Name:\tgodot\nThreads:\t28\n"u8, out _));
        Assert.False(ProcessMemory.TryReadVmRss("VmRSS:\t kB\n"u8, out _));
        Assert.False(ProcessMemory.TryReadVmRss(""u8, out _));
        Assert.False(ProcessMemory.TryReadVmRss("Name:\tgodot\nVmR"u8, out _));
    }

    // The last line of the file has no trailing newline to stop at.
    [Test]
    public void TheLastLineNeedsNoTrailingNewline()
    {
        Assert.True(ProcessMemory.TryReadVmRss("Name:\tgodot\nVmRSS:\t 128 kB"u8, out long bytes));
        Assert.Equal(128L * 1024, bytes);
    }

    // And end to end against the real process, on whatever platform this is running on: a live figure,
    // through /proc where there is one and through WorkingSet64 where there is not.
    [Test]
    public void TheLiveProcessReportsSomething()
    {
        Assert.True(ProcessMemory.RssBytes() > 0);
    }
}
