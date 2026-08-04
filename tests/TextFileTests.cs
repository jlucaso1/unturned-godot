using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

// TextFile exists to return exactly what File.ReadAllText returns while allocating a small fraction of
// what it allocates, so every test here is one of three shapes: the two agree, the buffer is not shared
// between two readers, or the allocation stayed bounded.
public class TextFileTests
{
    // The corpus is the whole equivalence claim: everything StreamReader's byte-order-mark detection
    // branches on, plus the degenerate lengths around it. Each entry is the exact bytes on disk.
    public static TheoryData<string, byte[]> Corpus()
    {
        var data = new TheoryData<string, byte[]>();
        void Add(string name, byte[] bytes) => data.Add(name, bytes);

        Add("empty", Array.Empty<byte>());
        Add("ascii", Encoding.ASCII.GetBytes("Name Birch\nGUID 0123456789abcdef\n"));
        Add("crlf", Encoding.ASCII.GetBytes("a\r\nb\r\n"));
        Add("one byte", new byte[] { (byte)'x' });
        // A single 0xFF is not a mark (detection needs two bytes) and is not valid UTF-8 either, so both
        // readers have to reach the same replacement character rather than the same exception.
        Add("lone 0xFF", new byte[] { 0xFF });
        Add("truncated utf-8", new byte[] { (byte)'a', 0xC3 });
        Add("utf-8 no bom", Encoding.UTF8.GetBytes("Grüße — Ϊσλανδία 🧟"));
        Add("utf-8 bom", Prefix(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes("Grüße 🧟")));
        Add("utf-8 bom only", new byte[] { 0xEF, 0xBB, 0xBF });
        Add("utf-16 le bom", Prefix(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes("Grüße 🧟")));
        Add("utf-16 le bom only", new byte[] { 0xFF, 0xFE });
        Add("utf-16 be bom",
            Prefix(new byte[] { 0xFE, 0xFF }, Encoding.BigEndianUnicode.GetBytes("Grüße 🧟")));
        Add("utf-32 le bom",
            Prefix(new byte[] { 0xFF, 0xFE, 0x00, 0x00 },
                new UTF32Encoding(bigEndian: false, byteOrderMark: false).GetBytes("Grüße 🧟")));
        Add("utf-32 be bom",
            Prefix(new byte[] { 0x00, 0x00, 0xFE, 0xFF },
                new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetBytes("Grüße 🧟")));
        // 0x0000FEFF is only UTF-32 BE when all four bytes are there; two NULs alone are UTF-8 NULs.
        Add("two nulls", new byte[] { 0x00, 0x00 });
        // Large enough to be a large-object allocation rather than a small one, so the size class the
        // read allocates is nothing like every other case here.
        Add("4 MiB", Encoding.ASCII.GetBytes(new string('z', 4 * 1024 * 1024)));
        return data;
    }

    private static byte[] Prefix(byte[] head, byte[] tail) => head.Concat(tail).ToArray();

    [Theory]
    [MemberData(nameof(Corpus))]
    public void ReadAllText_MatchesTheFrameworkByteForByte(string name, byte[] bytes)
    {
        using var dir = new TempDir();
        string path = dir.Write("asset.dat", bytes);

        Assert.Equal(File.ReadAllText(path), TextFile.ReadAllText(path));
        Assert.NotNull(name);
    }

    [Fact]
    public void Decode_TakesTheSameEncodingStreamReaderWould()
    {
        // Decode is the half of the read that has no file behind it, so its branches are checked here
        // directly rather than only through the corpus above.
        Assert.Equal("hi", TextFile.Decode(Encoding.ASCII.GetBytes("hi")));
        Assert.Equal("hi", TextFile.Decode(Prefix(new byte[] { 0xEF, 0xBB, 0xBF },
            Encoding.UTF8.GetBytes("hi"))));
        Assert.Equal("hi", TextFile.Decode(Prefix(new byte[] { 0xFF, 0xFE },
            Encoding.Unicode.GetBytes("hi"))));
        Assert.Equal("hi", TextFile.Decode(Prefix(new byte[] { 0xFE, 0xFF },
            Encoding.BigEndianUnicode.GetBytes("hi"))));
        Assert.Equal("hi", TextFile.Decode(Prefix(new byte[] { 0xFF, 0xFE, 0x00, 0x00 },
            new UTF32Encoding(bigEndian: false, byteOrderMark: false).GetBytes("hi"))));
        Assert.Equal("hi", TextFile.Decode(Prefix(new byte[] { 0x00, 0x00, 0xFE, 0xFF },
            new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetBytes("hi"))));
        Assert.Equal("", TextFile.Decode(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void CanReadWhole_CoversOnlyLengthsOneArrayCanHold()
    {
        Assert.False(TextFile.CanReadWhole(0));      // nothing to size a buffer from
        Assert.False(TextFile.CanReadWhole(-1));     // a stream that cannot report its length
        Assert.True(TextFile.CanReadWhole(1));
        Assert.True(TextFile.CanReadWhole(TextFile.MaxWholeFileBytes));
        Assert.False(TextFile.CanReadWhole(TextFile.MaxWholeFileBytes + 1));
        Assert.False(TextFile.CanReadWhole(long.MaxValue));
    }

    [Fact]
    public void ReadAllText_ThrowsWhatTheFrameworkThrowsForAMissingFile()
    {
        using var dir = new TempDir();
        string missing = Path.Combine(dir.Path, "not-here.dat");

        Assert.Throws<FileNotFoundException>(() => File.ReadAllText(missing));
        Assert.Throws<FileNotFoundException>(() => TextFile.ReadAllText(missing));
    }

    // The reuse-safety test. Nothing is pooled today, so this cannot fail as written — it is here for
    // the change that would make it able to. The moment the array is rented rather than allocated it
    // comes back at least as long as asked for and still carrying the last reader's bytes, and a short
    // read that decoded the whole array instead of the span it filled would return the previous file's
    // tail glued to its own contents. Interleaving a long file and a short one is what makes that
    // visible: the short read is the one that would get the long read's buffer back.
    [Fact]
    public void ReadAllText_NeverServesTheBufferAPreviousReadLeftBehind()
    {
        using var dir = new TempDir();
        string longPath = dir.Write("long.dat", new string('L', 200_000));
        string shortPath = dir.Write("short.dat", "short");

        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(200_000, TextFile.ReadAllText(longPath).Length);
            string small = TextFile.ReadAllText(shortPath);
            Assert.Equal("short", small);
        }
    }

    // The same hazard with the readers genuinely overlapping rather than merely alternating. Not
    // hypothetical: ObjectAssetDatabase.ScanFiles calls this from inside a Parallel.For, so whatever
    // buffer scheme the read uses has to hold with several threads inside it at once.
    [Fact]
    public void ReadAllText_IsSafeWhenTwoReadersOverlap()
    {
        using var dir = new TempDir();
        var paths = new string[8];
        for (int i = 0; i < paths.Length; i++)
            paths[i] = dir.Write($"f{i}.dat", new string((char)('a' + i), 1 << (10 + i)));

        var failures = new List<string>();
        System.Threading.Tasks.Parallel.For(0, 400, i =>
        {
            int which = i % paths.Length;
            string text = TextFile.ReadAllText(paths[which]);
            string expected = new((char)('a' + which), 1 << (10 + which));
            if (text != expected)
                lock (failures) failures.Add($"read {which} returned {text.Length} chars");
        });

        Assert.Empty(failures);
    }

    // The allocation-bound test: this change IS the allocation, so a bound is the only thing that stops
    // the framework's growing buffers coming back. Asserted as a fraction of what File.ReadAllText costs
    // for the same reads rather than as an absolute number, so it stays stable across runtimes.
    [Fact]
    public void ReadAllText_AllocatesFarLessThanTheFrameworkForTheSameFiles()
    {
        using var dir = new TempDir();
        // The shape a scan actually meets: many small definitions, not one big file.
        var paths = new string[64];
        for (int i = 0; i < paths.Length; i++)
            paths[i] = dir.Write($"asset{i}.dat", new string('x', 2048));

        Warm(paths);

        long frameworkBefore = GC.GetAllocatedBytesForCurrentThread();
        foreach (string path in paths)
            _ = File.ReadAllText(path);
        long framework = GC.GetAllocatedBytesForCurrentThread() - frameworkBefore;

        long wholeFileBefore = GC.GetAllocatedBytesForCurrentThread();
        foreach (string path in paths)
            _ = TextFile.ReadAllText(path);
        long wholeFile = GC.GetAllocatedBytesForCurrentThread() - wholeFileBefore;

        // Each read still allocates its string (2048 chars = 4 KB) and, on this side, the file's own
        // bytes; that pair is the floor. Everything above it is the buffers only the framework's path
        // grows. Two thirds is a loose bound around a measured ratio of 0.40, so this fails when those
        // buffers come back rather than when the runtime happens to allocate something differently.
        Assert.True(wholeFile * 3 < framework * 2,
            $"whole-file read allocated {wholeFile} bytes against the framework's {framework}");
    }

    // The other half of the claim, and the reason this does not rent from ArrayPool: a read must leave
    // nothing behind once its string exists, and a pool would hold the buckets it grew for the rest of
    // the process — steady-state residency bought with load-time allocation, which is a trade rather
    // than a saving and not this change's to make.
    //
    // Asserted as "the bytes were allocated" rather than as "nothing was retained", because retention is
    // a property of the whole heap and this suite runs its collections in parallel: a GetTotalMemory
    // delta around these reads also counts whatever every other test happens to be holding at the time.
    // Written that way it passed alone and failed inside the suite on a 24-core machine, off 6 MB of
    // other tests' live objects. GetAllocatedBytesForCurrentThread is per-thread, so this form measures
    // only these reads, and it pins the same decision from the other side: an implementation that rented
    // its buffer would stop allocating the file's bytes, which is exactly what would let it retain them.
    [Fact]
    public void ReadAllText_AllocatesTheFileSBytesRatherThanRentingThem()
    {
        const int length = 256 * 1024;
        using var dir = new TempDir();
        string path = dir.Write("big.dat", new string('x', length));
        _ = TextFile.ReadAllText(path); // one-time statics and encoder

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = TextFile.ReadAllText(path);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // An ASCII file of this length costs 2 * length for the string it decodes into, plus length for
        // the bytes it is read through. A version that rented the buffer would allocate only the string,
        // so the threshold sits between the two rather than at either.
        Assert.True(allocated > 2.5 * length,
            $"a {length} byte file allocated {allocated} bytes — the buffer looks rented, not allocated");
    }

    // First touch of either path allocates its statics and its encoder; that is a one-off, and counting
    // it would measure whichever side ran first.
    private static void Warm(string[] paths)
    {
        for (int i = 0; i < 2; i++)
            foreach (string path in paths)
            {
                _ = File.ReadAllText(path);
                _ = TextFile.ReadAllText(path);
            }
    }
}
