using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class ExtractionIndexTests
{
    private const long Stamp = 1234567890L;

    [Fact]
    public void SaveThenLoad_RoundTripsTheMisses()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "nested", "extraction.index");
        Guid[] misses = { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };

        ExtractionIndex.Save(path, Stamp, misses);

        Assert.Equal(new HashSet<Guid>(misses), ExtractionIndex.Load(path, Stamp));
    }

    [Fact]
    public void Load_ForADifferentBundle_ForgetsEverything()
    {
        // The stamp is the bundle's identity: after a game update every GUID must be tried again.
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "extraction.index");
        ExtractionIndex.Save(path, Stamp, new[] { Guid.NewGuid() });

        Assert.Empty(ExtractionIndex.Load(path, Stamp + 1));
    }

    [Fact]
    public void Load_MissingShortOrForeignFile_IsEmpty()
    {
        using var dir = new TempDir();
        Assert.Empty(ExtractionIndex.Load(Path.Combine(dir.Path, "absent.index"), Stamp));

        string tooShort = dir.Write("short.index", new byte[] { 1, 2, 3, 4 });
        Assert.Empty(ExtractionIndex.Load(tooShort, Stamp));

        var wrongMagic = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(wrongMagic, 0xDEADBEEF);
        Assert.Empty(ExtractionIndex.Load(dir.Write("magic.index", wrongMagic), Stamp));
    }

    // A miss says "this reader could not make a mesh out of it", so it has to be forgotten whenever the
    // reader gains ground — not only when the bundle changes. Prefabs whose geometry lived in a .resS
    // produced nothing and were recorded as misses; without dropping those entries they would stay
    // placeholder boxes against an unchanged bundle stamp however capable the reader became.
    [Fact]
    public void Load_AnIndexFromAnEarlierReader_ForgetsItsMisses()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "reader.index");
        ExtractionIndex.Save(path, Stamp, new[] { Guid.NewGuid() });

        byte[] current = File.ReadAllBytes(path);
        Assert.NotEmpty(ExtractionIndex.Load(path, Stamp));

        foreach (uint older in new uint[] { 0x31584755 }) // UGX1: before streamed vertex buffers
        {
            byte[] stale = (byte[])current.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(stale, older);
            Assert.Empty(ExtractionIndex.Load(dir.Write("older.index", stale), Stamp));
        }
    }

    [Fact]
    public void Load_TruncatedOrNegativeCount_IsEmpty()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "extraction.index");
        ExtractionIndex.Save(path, Stamp, new[] { Guid.NewGuid(), Guid.NewGuid() });
        byte[] full = File.ReadAllBytes(path);

        // Header says two GUIDs, only one is present.
        Assert.Empty(ExtractionIndex.Load(dir.Write("cut.index", full[..^16]), Stamp));

        // A negative count must not be trusted either (it would index outside the buffer).
        byte[] negative = (byte[])full.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(negative.AsSpan(12), -3);
        Assert.Empty(ExtractionIndex.Load(dir.Write("negative.index", negative), Stamp));
    }

    [Fact]
    public void Load_UnreadableFile_IsEmpty()
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "extraction.index");
        ExtractionIndex.Save(path, Stamp, new[] { Guid.NewGuid() });

        using var handle = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.None);
        Assert.Empty(ExtractionIndex.Load(path, Stamp));
    }

    [Fact]
    public void Save_ToAnUnwritablePath_IsSilent()
    {
        // Best effort by design: a failed write only means the next boot re-tries those GUIDs.
        using var dir = new TempDir();
        string path = dir.Write("taken.index", Array.Empty<byte>());

        using var handle = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.None);
        ExtractionIndex.Save(path, Stamp, new[] { Guid.NewGuid() }); // must not throw
    }

    [Fact]
    public void MissingMeshes_SkipsCachedMeshesEmptyGuidsAndKnownMisses()
    {
        using var dir = new TempDir();
        Guid cached = Guid.NewGuid();
        Guid known = Guid.NewGuid();
        Guid wanted = Guid.NewGuid();
        Guid stale = Guid.NewGuid();

        WriteMesh(dir, cached, current: true);
        WriteMesh(dir, stale, current: false); // a cache entry from an older format version

        HashSet<Guid> missing = ExtractionIndex.MissingMeshes(dir.Path,
            new[] { cached, known, wanted, stale, Guid.Empty },
            new HashSet<Guid> { known });

        Assert.Equal(new HashSet<Guid> { wanted, stale }, missing);
    }

    [Fact]
    public void StampFor_ChangesWithTheFile_AndIsZeroWhenAbsent()
    {
        using var dir = new TempDir();
        string bundle = dir.Write("core.masterbundle", new byte[] { 1, 2, 3 });

        long first = ExtractionIndex.StampFor(bundle);
        Assert.NotEqual(0, first);

        File.WriteAllBytes(bundle, new byte[] { 1, 2, 3, 4 }); // size (and mtime) change
        Assert.NotEqual(first, ExtractionIndex.StampFor(bundle));

        Assert.Equal(0, ExtractionIndex.StampFor(Path.Combine(dir.Path, "absent.masterbundle")));
    }

    // A <guid>.mesh whose header either matches the current cache format or does not.
    private static void WriteMesh(TempDir dir, Guid guid, bool current)
    {
        using var ms = new MemoryStream();
        if (current)
        {
            MeshCache.Write(ms, Array.Empty<Godot.Vector3>(), Array.Empty<Godot.Vector3>(),
                Array.Empty<Godot.Vector2>(), Array.Empty<CachedSubmesh>());
        }
        else
        {
            ms.Write(new byte[] { 9, 9, 9, 9 });
        }

        dir.Write(guid.ToString("N") + ".mesh", ms.ToArray());
    }
}
