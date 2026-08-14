using System;
using System.Collections.Generic;
using System.Threading;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// Reading the vertex buffers Unity moved out of a bundle's SerializedFile and into one of its .resS nodes.
//
// Which meshes this hits has nothing to do with what they are: a vehicle's Wheel_LOD0 is streamed while
// the Wheel_LOD1 beside it is inline. Before this reader existed a streamed buffer simply made the mesh
// unusable, and every shipped vehicle rendered with its wheels missing at close range and back again at
// distance — so what these hold is that a served range is the RIGHT bytes, and that an unservable one is
// absent rather than wrong.
public class VertexStreamReaderTests
{
    private const string StreamNode = "CAB-test.resS";

    private static byte[] Blob(int length, byte seed = 0)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = (byte)(seed + i);
        return bytes;
    }

    // A bundle carrying a SerializedFile-shaped node and one stream node with known contents. Nothing here
    // reads the first node — a request names its range directly — so its bytes only have to be skipped.
    private static string BundleWith(TempDir dir, byte[] stream, bool singleLzmaBlock = true)
    {
        var fs = new UnityFsBuilder { LzmaBlocks = singleLzmaBlock };
        fs.Add("CAB-test", Blob(512, 200));
        fs.Add(StreamNode, stream);
        return dir.Write("test.masterbundle", fs.Build());
    }

    private static VertexStreamReader.Request Request(long pathId, long offset, int size,
        string node = StreamNode) =>
        new(pathId, new UnityMesh.StreamRef("archive:/CAB-test/" + node, offset, size));

    [Fact]
    public void AskingForNothingNeverOpensTheBundle()
    {
        Assert.Empty(VertexStreamReader.Read("/nonexistent-bundle",
            Array.Empty<VertexStreamReader.Request>()));
    }

    [Fact]
    public void AnUnreadableBundleServesNothingRatherThanThrowing()
    {
        Assert.Empty(VertexStreamReader.Read("/nonexistent-bundle", new[] { Request(1, 0, 16) }));
    }

    [Fact]
    public void AStreamedRangeIsServedByteForByte()
    {
        using var dir = new TempDir();
        byte[] stream = Blob(256, 7);
        string bundle = BundleWith(dir, stream);

        Dictionary<long, byte[]> served =
            VertexStreamReader.Read(bundle, new[] { Request(42, 64, 32) });

        byte[] bytes = Assert.Contains(42L, served);
        Assert.Equal(new ArraySegment<byte>(stream, 64, 32).ToArray(), bytes);
    }

    // Several ranges out of one node, in one forward pass. They are handed back in stream order, so the
    // result has to be keyed by path id rather than by the order they were asked for.
    [Fact]
    public void SeveralRangesOutOfOneNodeAreAllServed()
    {
        using var dir = new TempDir();
        byte[] stream = Blob(512, 3);
        string bundle = BundleWith(dir, stream);

        Dictionary<long, byte[]> served = VertexStreamReader.Read(bundle, new[]
        {
            Request(1, 256, 16),
            Request(2, 0, 8),
            Request(3, 128, 32),
        });

        Assert.Equal(3, served.Count);
        Assert.Equal(new ArraySegment<byte>(stream, 0, 8).ToArray(), served[2]);
        Assert.Equal(new ArraySegment<byte>(stream, 128, 32).ToArray(), served[3]);
        Assert.Equal(new ArraySegment<byte>(stream, 256, 16).ToArray(), served[1]);
    }

    // A mesh that is not streamed at all has nothing to fetch. Most meshes are this, so a reader that
    // opened the bundle for them would pay a whole pass for nothing.
    [Fact]
    public void AnInlineMeshIsNotFetchedAndDoesNotOpenTheBundle()
    {
        var inline = new VertexStreamReader.Request(7, default);

        Assert.Empty(VertexStreamReader.Read("/nonexistent-bundle", new[] { inline }));
    }

    // A request naming a stream node the bundle does not carry is absent from the result. The mesh stays
    // unusable, which is what it was before this existed — the alternative is serving the wrong bytes.
    [Fact]
    public void ARangeInANodeTheBundleDoesNotCarryIsAbsent()
    {
        using var dir = new TempDir();
        string bundle = BundleWith(dir, Blob(128));

        Assert.Empty(VertexStreamReader.Read(bundle,
            new[] { Request(1, 0, 16, "CAB-nothing-like-this.resS") }));
    }

    // A range past the end of its node is absent for the same reason.
    [Fact]
    public void ARangePastTheEndOfItsNodeIsAbsent()
    {
        using var dir = new TempDir();
        string bundle = BundleWith(dir, Blob(64));

        Assert.Empty(VertexStreamReader.Read(bundle, new[] { Request(1, 32, 4096) }));
    }

    // A bundle shape the forward reader cannot open — multi-block, which workshop content ships — decodes
    // whole rather than dropping the geometry. The alternative is worse than slow: a prefab cached with
    // its streamed parts silently missing and stamped current, so nothing ever asks again.
    [Fact]
    public void AnotherBundleShapeIsDecodedWholeRatherThanGivenUpOn()
    {
        using var dir = new TempDir();
        byte[] stream = Blob(256, 11);
        string bundle = BundleWith(dir, stream, singleLzmaBlock: false);

        Dictionary<long, byte[]> served = VertexStreamReader.Read(bundle, new[] { Request(9, 96, 24) });

        Assert.Equal(new ArraySegment<byte>(stream, 96, 24).ToArray(), Assert.Contains(9L, served));
    }

    [Fact]
    public void TheWholeBundleFallbackAlsoDropsAnUnservableRange()
    {
        using var dir = new TempDir();
        string bundle = BundleWith(dir, Blob(64), singleLzmaBlock: false);

        Assert.Empty(VertexStreamReader.Read(bundle, new[] { Request(1, 0, 4096) }));
        Assert.Empty(VertexStreamReader.Read(bundle,
            new[] { Request(2, 0, 16, "CAB-nothing-like-this.resS") }));
    }

    // This runs on a worker during a load, and a quit has to be able to stop it: the pass is over a
    // multi-hundred-megabyte blob, and the alternative to checking is a teardown that waits for all of it.
    [Fact]
    public void ACancelledPassStopsRatherThanFinishing()
    {
        using var dir = new TempDir();
        string bundle = BundleWith(dir, Blob(256, 5));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Empty(VertexStreamReader.Read(bundle, new[] { Request(1, 0, 16) }, cancelled.Token));
        Assert.Empty(VertexStreamReader.Read(bundle, new[] { Request(1, 0, 16) }, cancelled.Token));
    }

    [Fact]
    public void ACancelledWholeBundlePassStopsToo()
    {
        using var dir = new TempDir();
        string bundle = BundleWith(dir, Blob(256, 5), singleLzmaBlock: false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Empty(VertexStreamReader.Read(bundle, new[] { Request(1, 0, 16) }, cancelled.Token));
    }

    // A stream node in a subfolder of the bundle. A mesh names its buffer by the path Unity wrote and the
    // node table keys it by its own, so the two only meet on the last segment.
    [Fact]
    public void AStreamNodeInASubfolderIsStillMatched()
    {
        using var dir = new TempDir();
        byte[] stream = Blob(256, 23);
        var fs = new UnityFsBuilder { LzmaBlocks = true };
        fs.Add("CAB-test", Blob(512, 200));
        fs.Add("sub/CAB-test.resS", stream);
        string bundle = dir.Write("test.masterbundle", fs.Build());

        Dictionary<long, byte[]> served = VertexStreamReader.Read(bundle, new[] { Request(5, 32, 16) });

        Assert.Equal(new ArraySegment<byte>(stream, 32, 16).ToArray(), Assert.Contains(5L, served));
    }

    // A request whose stream reference is present but empty is not streamed: Size 0 is what a mesh with
    // no external buffer carries, and asking for zero bytes would otherwise plan a pass for it.
    [Fact]
    public void AZeroSizedRangeCountsAsNotStreamed()
    {
        using var dir = new TempDir();
        string bundle = BundleWith(dir, Blob(64));

        Assert.Empty(VertexStreamReader.Read(bundle, new[] { Request(1, 0, 0) }));
    }
}
