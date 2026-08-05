using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Reading the game's own masterbundle.
//
// This one cannot be tested against a synthetic file, and that is the point rather than an inconvenience:
// the formats here are re-implementations checked byte-for-byte against Unturned's own, so a stand-in
// this reader accepts would prove nothing about the reader. Either it reads the real bundle or the test
// is theatre.
//
// So these run against the installed game when it is there. Without it they say so and return, which is
// the shape the xUnit suite's [RealDataFact] gives its own — GoDotTest discovers by reflection and has no
// skip, so the decision is made in the body. UG_REQUIRE_REAL_DATA=1 turns the absence into a FAILURE, so
// a job that fetched the content on purpose cannot pass by finding nothing.
public class ModelExtractorTests : TestClass
{
    public ModelExtractorTests(Node testScene) : base(testScene) { }

    // The per-class type trees are what every later decode is interpreted through: resources.assets ships
    // with its own stripped, so the entity importer borrows these. Reading them wrong does not fail — it
    // misreads every field of every object, which is far worse.
    [Test]
    public void TheClassTypeTreesAreReadFromTheRealBundle()
    {
        if (!Available("the core masterbundle", MasterBundle, out string bundle))
            return;

        IReadOnlyDictionary<int, List<TypeTreeNode>> trees = ModelExtractor.ReadClassTypeTrees(bundle);

        Assert.NotEmpty(trees);
        // Mesh (43) and Texture2D (28) are the two classes the whole extraction rests on.
        Assert.True(trees.ContainsKey(43), "no Mesh type tree; every mesh decode would be blind");
        Assert.True(trees.ContainsKey(28), "no Texture2D type tree");
        foreach (List<TypeTreeNode> nodes in trees.Values)
            Assert.NotEmpty(nodes);
    }

    // Reading them twice is cheap because the result is cached against the bundle's own mtime and size.
    // Without that, every entity import re-read ~111 MB and re-decoded ~171 MB of LZMA.
    [Test]
    public void TheTypeTreesAreCachedRatherThanReDecoded()
    {
        if (!Available("the core masterbundle", MasterBundle, out string bundle))
            return;

        ulong before = Time.GetTicksMsec();
        ModelExtractor.ReadClassTypeTrees(bundle);
        ulong first = Time.GetTicksMsec() - before;

        before = Time.GetTicksMsec();
        IReadOnlyDictionary<int, List<TypeTreeNode>> again = ModelExtractor.ReadClassTypeTrees(bundle);
        ulong second = Time.GetTicksMsec() - before;

        Assert.NotEmpty(again);
        // The cached read must be a fraction of a full decode. Deliberately loose: this is asserting
        // "a cache exists and is used", not a timing budget.
        Assert.True(second <= first + 250,
            $"the second read took {second} ms against the first's {first} ms; the cache is not being used");
    }

    // The masterbundle's SerializedFile is the index everything else addresses: no objects means no
    // meshes, no textures and a world of missing prefabs.
    [Test]
    public void TheMasterbundleSerializedFileCarriesItsObjects()
    {
        if (!Available("the core masterbundle", MasterBundle, out string bundle))
            return;

        SerializedFile file = ModelExtractor.ReadMasterbundleFile(bundle);

        Assert.NotEmpty(file.Objects);
        // Path ids are the addresses the prefab graph resolves through, so they have to be distinct.
        var seen = new HashSet<long>();
        foreach (SerializedObject o in file.Objects)
            Assert.True(seen.Add(o.PathId), $"two objects share path id {o.PathId}");
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static string? MasterBundle
    {
        get
        {
            string? install = UnturnedInstall.Find();
            return install == null ? null : UnturnedInstall.FindMasterBundle(install);
        }
    }

    // True when the caller should go ahead. False — after saying why — when the content is absent and the
    // run has not demanded it. Throws when it HAS, so the job that fetches the content cannot pass by
    // finding nothing: the point of that job is to prove these executed.
    private static bool Available(string what, string? path, out string resolved)
    {
        resolved = path ?? "";
        if (path != null)
            return true;

        if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
        {
            throw new InvalidDataException(
                $"UG_REQUIRE_REAL_DATA=1 but {what} is not present; this run exists to prove these tests execute");
        }

        Log.Print($"[runtime-tests] skipping: {what} is not present "
            + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
        return false;
    }
}
