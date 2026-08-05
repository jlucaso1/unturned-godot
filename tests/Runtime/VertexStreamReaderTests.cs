using System.Collections.Generic;
using System.Threading;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Fetching the vertex buffers Unity moved out of a bundle's SerializedFile and into a .resS node.
//
// Most meshes keep their buffer inline and never come here. Which ones Unity streams out has nothing to
// do with what they are: a vehicle's Wheel_LOD0 is streamed while the Wheel_LOD1 beside it is inline, and
// the body of the same vehicle is inline too. Before this existed a streamed buffer simply made the mesh
// unusable, so every shipped vehicle rendered with its wheels missing at close range and back again at
// distance.
//
// Every failure here is therefore PARTIAL by design. A request the bundle cannot serve is absent from the
// result rather than an error, because absent leaves the caller exactly where it was before any of this
// existed — one mesh short — while a throw would take down the whole level build for a wheel.
public class VertexStreamReaderTests : TestClass
{
    public VertexStreamReaderTests(Node testScene) : base(testScene) { }

    // Nothing asked for means nothing read. The bundle is never opened, which matters because this runs
    // on every bundle in the level and almost none of them owe anything.
    [Test]
    public void AskingForNothingOpensNothing()
    {
        Assert.Empty(VertexStreamReader.Read("/nonexistent-bundle", new List<VertexStreamReader.Request>()));
    }

    // A bundle that is not there serves nothing, and does not throw. The caller is mid-level-build, and
    // the meshes that wanted those bytes stay unusable — which is what they were before this existed.
    [Test]
    public void ABundleThatIsNotThereServesNothing()
    {
        Assert.Empty(VertexStreamReader.Read("/nonexistent-bundle", new[] { Request(1, "CAB-x.resS") }));
    }

    // A mesh that was never streamed is not a request at all. The caller passes whatever a mesh's stream
    // reference says, and an inline mesh's is a default instance whose Path is null — a reader that read
    // .Length off it would fault on the common case rather than the rare one.
    [Test]
    public void AMeshThatWasNeverStreamedIsSkipped()
    {
        var inline = new VertexStreamReader.Request(7, default);

        Assert.False(inline.Stream.IsStreamed);
        Assert.Empty(VertexStreamReader.Read(MasterBundle ?? "/nonexistent-bundle", new[] { inline }));
    }

    // The real bundle, asked for a node it does not carry. This is the path a mesh takes when its stream
    // reference names another bundle's .resS — the request is dropped and the rest of the pass carries on.
    [Test]
    public void TheRealBundleDropsARequestItCannotServe()
    {
        if (!Available(out string bundle))
            return;

        Assert.Empty(VertexStreamReader.Read(bundle, new[] { Request(1, "CAB-nothing-like-this.resS") }));
    }

    // A range past the end of a node it DOES carry is dropped too, rather than read as whatever happens
    // to follow it. A mesh built from the wrong bytes is worse than a mesh that is missing: it renders,
    // as a spray of triangles across the map.
    [Test]
    public void ARangePastTheEndOfANodeIsDropped()
    {
        if (!Available(out string bundle))
            return;

        var absurd = new VertexStreamReader.Request(2,
            new UnityMesh.StreamRef("archive:/CAB-x/CAB-x.resS", long.MaxValue / 2, 4096));

        Assert.Empty(VertexStreamReader.Read(bundle, new[] { absurd }));
    }

    // A cancelled pass returns what it had rather than finishing the bundle. This runs on a worker during
    // the cold load, and a player who backs out to the menu must not wait for a 110 MB decode to end.
    [Test]
    public void ACancelledPassStopsRatherThanFinishingTheBundle()
    {
        if (!Available(out string bundle))
            return;

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Empty(VertexStreamReader.Read(bundle, new[] { Request(1, "CAB-x.resS") }, cancelled.Token));
    }

    // The whole point, over the game's own bundle: the buffers its streamed meshes actually point at come
    // back, at the size they asked for.
    //
    // This is the wheels. A streamed buffer used to make its mesh unusable, so the part was dropped from
    // the level being built, and every shipped vehicle rendered with its wheels missing at close range and
    // present again at distance — because the LOD1 beside them happened to be inline. Which meshes Unity
    // streams out has nothing to do with what they are.
    [Test]
    public void TheRealBundleServesItsOwnStreamedMeshes()
    {
        if (!Available(out string bundle))
            return;

        var requests = new List<VertexStreamReader.Request>();
        SerializedFile file = ModelExtractor.ReadMasterbundleFile(bundle);
        foreach (SerializedObject obj in file.Objects)
        {
            if (obj.ClassId != MeshClassId)
                continue;
            UnityMesh.StreamRef stream =
                UnityMesh.ReadStreamRef(TypeTreeReader.Read(obj.TypeTree, file.ReaderFor(obj)));
            if (stream.IsStreamed)
                requests.Add(new VertexStreamReader.Request(obj.PathId, stream));
            if (requests.Count == 8)
                break; // a handful is the point; the pass is one forward walk either way
        }

        if (requests.Count == 0)
            return; // this platform's bundle streams nothing out, which is Unity's choice to make

        Dictionary<long, byte[]> served = VertexStreamReader.Read(bundle, requests);

        // EVERY request, not "at least one". A partial drop of streamed buffers is exactly the
        // production failure this file exists for — the wheels came back at distance and vanished up
        // close — and a loop that only checked what happened to be present could not see it.
        Assert.Equal(requests.Count, served.Count);
        foreach (VertexStreamReader.Request request in requests)
        {
            Assert.True(served.TryGetValue(request.PathId, out byte[]? bytes),
                $"the bundle owed mesh {request.PathId} its vertex buffer and did not serve it");
            Assert.Equal(request.Stream.Size, bytes!.Length);
        }
    }

    // --- helpers -------------------------------------------------------------------------------------

    private const int MeshClassId = 43;

    private static VertexStreamReader.Request Request(long pathId, string node) =>
        new(pathId, new UnityMesh.StreamRef($"archive:/CAB-x/{node}", 0, 4096));

    private static string? MasterBundle
    {
        get
        {
            string? install = Assets.UnturnedInstall.Find();
            return install == null ? null : Assets.UnturnedInstall.FindMasterBundle(install);
        }
    }

    private static bool Available(out string bundle)
    {
        bundle = MasterBundle ?? "";
        if (bundle.Length > 0)
            return true;
        if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
        {
            throw new System.IO.IOException(
                "UG_REQUIRE_REAL_DATA=1 but the core masterbundle is not present; this run exists to prove "
                + "these tests execute");
        }

        Log.Print("[runtime-tests] skipping: the core masterbundle is not present "
            + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
        return false;
    }
}
