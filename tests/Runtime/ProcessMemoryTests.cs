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
//
// The whole point of the strictness below: a wrong answer here does not look like a failure, it looks
// like the process shed memory. Every rejection case is therefore its own test, because each one is a
// route to a plausible-but-false number rather than to the WorkingSet64 fallback.
public class ProcessMemoryTests : TestClass
{
    public ProcessMemoryTests(Node testScene) : base(testScene) { }

    // The line as the kernel writes it: a tab, then leading spaces that pad the number into a column.
    [Test]
    public void TheKernelsOwnLineIsRead()
    {
        Assert.True(ProcessMemory.TryReadVmRss(
            "Name:\tgodot\nVmPeak:\t 2097152 kB\nVmRSS:\t  345052 kB\nThreads:\t28\n"u8,
            reachedEnd: true, out long bytes));
        Assert.Equal(345052L * 1024, bytes);
    }

    // The key at the very start of the file, which the anchoring check has to admit rather than reject
    // for want of a newline in front of it.
    [Test]
    public void TheKeyOnTheFirstLineIsRead()
    {
        Assert.True(ProcessMemory.TryReadVmRss("VmRSS:\t     4 kB\n"u8, reachedEnd: true,
            out long bytes));
        Assert.Equal(4L * 1024, bytes);
    }

    // Only a line that STARTS with the key counts, which is what the old StartsWith gave. Nothing in the
    // file ends in "VmRSS:" today; the anchor is what stops a later field that contains it being read as
    // this one, and it is the one behavioural difference a plain substring search would have introduced.
    [Test]
    public void AKeyInTheMiddleOfALineIsNotTheOne()
    {
        Assert.False(ProcessMemory.TryReadVmRss("HugetlbVmRSS:\t 99 kB\n"u8, reachedEnd: true,
            out long bytes));
        Assert.Equal(0, bytes);
    }

    // Nothing to read: a status file without the field, one whose value is missing, an empty read, and a
    // buffer that ran out inside the key itself. Each sends the caller to the portable fallback.
    [Test]
    public void AnUnreadableValueFallsThrough()
    {
        Assert.False(ProcessMemory.TryReadVmRss("Name:\tgodot\nThreads:\t28\n"u8, true, out _));
        Assert.False(ProcessMemory.TryReadVmRss("VmRSS:\t kB\n"u8, true, out _));
        Assert.False(ProcessMemory.TryReadVmRss(""u8, true, out _));
        Assert.False(ProcessMemory.TryReadVmRss("Name:\tgodot\nVmR"u8, false, out _));
    }

    // The last line of the file has no trailing newline to stop at, and that is legitimate — but only
    // because the read reached the end of the file. See the next test for why the flag has to say so.
    [Test]
    public void TheLastLineNeedsNoTrailingNewline()
    {
        Assert.True(ProcessMemory.TryReadVmRss("Name:\tgodot\nVmRSS:\t 128 kB"u8, reachedEnd: true,
            out long bytes));
        Assert.Equal(128L * 1024, bytes);
    }

    // A read that stopped INSIDE the number must be refused, not rounded off.
    //
    // A status file longer than the buffer — a process with a long Groups: line pushing VmRSS: down the
    // file — can end the read part-way through the digits. Taking what is there would report 123 kB for
    // a real 123456 kB: three orders of magnitude low, silently, on the HUD and in every benchmark
    // report that samples it. The identical span WITH the end of the file behind it is the control, and
    // it parses — so what is being rejected is the truncation, not the shape of the line.
    [Test]
    public void DigitsCutOffByTheBufferAreRefused()
    {
        ReadOnlySpan<byte> cut = "Name:\tgodot\nGroups:\t4 24 27 30\nVmRSS:\t  123"u8;

        Assert.False(ProcessMemory.TryReadVmRss(cut, reachedEnd: false, out long bytes));
        Assert.Equal(0, bytes);

        Assert.True(ProcessMemory.TryReadVmRss(cut, reachedEnd: true, out long whole));
        Assert.Equal(123L * 1024, whole);
    }

    // A line the read DID get to the end of is complete whether or not the file was, so a mid-file
    // truncation elsewhere must not reject a value that has its own newline behind it.
    [Test]
    public void ATruncatedFileStillReadsAValueThatIsWhole()
    {
        Assert.True(ProcessMemory.TryReadVmRss("VmRSS:\t  345052 kB\nThreads:\t2"u8,
            reachedEnd: false, out long bytes));
        Assert.Equal(345052L * 1024, bytes);
    }

    // A number that parses as a long but cannot become bytes is refused before the multiply, which would
    // otherwise wrap to a negative or absurdly small figure rather than throw. No real VmRSS is within
    // exabytes of this — which is exactly why the wrap would never be spotted.
    [Test]
    public void AValueTooLargeToConvertIsRefused()
    {
        Assert.False(ProcessMemory.TryReadVmRss("VmRSS:\t 9223372036854775807 kB\n"u8, true, out long b));
        Assert.Equal(0, b);

        // One kB below the limit still converts, so the guard rejects the overflow and nothing else.
        long biggest = long.MaxValue / 1024;
        Assert.True(ProcessMemory.TryReadVmRss(
            System.Text.Encoding.ASCII.GetBytes($"VmRSS:\t {biggest} kB\n"), true, out long ok));
        Assert.Equal(biggest * 1024, ok);
    }

    // And end to end against the real process, on whatever platform this is running on: a live figure,
    // through /proc where there is one and through WorkingSet64 where there is not.
    [Test]
    public void TheLiveProcessReportsSomething()
    {
        Assert.True(ProcessMemory.RssBytes() > 0);
    }

    // The parser against THIS kernel's actual status file, rather than against a hand-written one.
    //
    // Every other case here is a shape someone typed out, so all of them together still prove nothing
    // about the file the HUD really reads — the layout is a kernel detail (field order, padding, which
    // fields exist at all) and the whole method is built around it. Skips where there is no /proc, which
    // is how the rest of the suite stays green on Windows and macOS.
    [Test]
    public void TheRealStatusFileParsesToWhatItSays()
    {
        if (!System.IO.File.Exists("/proc/self/status"))
            return;

        // ONE snapshot, parsed two ways. Reading the file twice is a race against the thing it reports:
        // the first draft of this test did, and failed by 12 KB — three pages the process happened to
        // touch in between — which is a real reading of a real number and no defect at all.
        byte[] status = System.IO.File.ReadAllBytes("/proc/self/status");
        Assert.True(ProcessMemory.TryReadVmRss(status, reachedEnd: true, out long bytes),
            "the parser did not find VmRSS in this kernel's own status file");

        // The same bytes, the dull way, and the same answer required.
        foreach (string line in System.Text.Encoding.ASCII.GetString(status).Split('\n'))
        {
            if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
                continue;
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(long.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture) * 1024,
                bytes);
            return;
        }

        Assert.Fail("this kernel's status file has no VmRSS line, so the assertion above proved nothing");
    }
}
