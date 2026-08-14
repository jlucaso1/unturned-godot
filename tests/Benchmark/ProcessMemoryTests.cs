using System.Runtime.InteropServices;
using UnturnedGodot.Benchmark;
using Xunit;

namespace UnturnedGodot.Tests.Benchmark;

// Resident set size, which is the only memory number a benchmark or the debug overlay can compare across
// runs: the managed heap says nothing about the native side, and this is a game that decodes gigabytes
// through unmanaged buffers.
//
// It reads /proc first and falls back to the process's own working set, so it answers on every platform
// rather than being a Linux-only figure that silently reads zero elsewhere. Nothing here pins a VALUE —
// that is the allocator's business and would fail on a different runtime rather than on a regression.
public class ProcessMemoryTests
{
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
