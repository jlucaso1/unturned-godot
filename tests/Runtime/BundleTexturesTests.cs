using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Pulling textures out of a bundle, paying only for what is asked for.
//
// There are three ways in, and which one a caller takes decides what it pays. The SerializedFile names
// every texture and says where its pixels live; a texture kept INLINE costs nothing beyond that file,
// while a STREAMED one lives in a .resS node that only decodes forwards. So the streamed pass reads the
// stream exactly as far as the last node still owed and stops — and a bundle whose textures are all
// inline never opens its stream at all. Decoding the whole 110 MB blob is the fallback for bundles that
// are not a single LZMA block, not the normal road.
//
// Every path answers with what it could get rather than throwing, because the caller is mid-load and a
// level with one missing texture is a level; a level that failed to build is a menu.
public class BundleTexturesTests : TestClass
{
    public BundleTexturesTests(Node testScene) : base(testScene) { }

    // Asking for nothing reads nothing, from any of the three. This is the common case on most bundles
    // in a level, and it must not cost a decode.
    [Test]
    public void AskingForNothingReadsNothing()
    {
        var nothing = new List<string>();

        Assert.Empty(BundleTextures.ExtractStreamed("/nonexistent-bundle", nothing));
        Assert.Empty(BundleTextures.ExtractAll("/nonexistent-bundle", nothing));
    }

    // A bundle that is not there THROWS, and this pins that rather than asserting what one might expect.
    //
    // The streamed pass falls through to the whole-blob path for anything that is not a single LZMA
    // block, and that path reads the file unguarded. Its sibling — VertexStreamReader, which does the
    // same fallback for vertex buffers — catches IOException there and returns what it had, on the
    // stated grounds that a caller mid-level-build would rather lose one mesh than the level.
    //
    // Whether the asymmetry matters is NOT settled here: in production these paths come from a directory
    // scan, so a missing file would mean the bundle vanished mid-load. It is recorded because the two
    // readers disagree about the same failure, and a later change to either should be a decision rather
    // than a surprise.
    [Test]
    public void ABundleThatIsNotThereThrowsRatherThanYieldingNothing()
    {
        Assert.ThrowsAny<System.IO.IOException>(() => BundleTextures.ExtractStreamed(
            "/nonexistent-bundle", new[] { "assets/anything.png" }));
    }

    // The real bundle, asked for a container path it does not carry. A prefab naming a texture another
    // bundle owns takes this path, and the material is built without it rather than not at all.
    [Test]
    public void TheRealBundleYieldsNothingForAPathItDoesNotCarry()
    {
        if (!Available(out string bundle))
            return;

        Assert.Empty(BundleTextures.ExtractStreamed(bundle,
            new[] { "assets/nothing/like/this/at/all.png" }));
    }

    // Locating is not decoding. The SerializedFile names every texture in the bundle and says where its
    // pixels live, and that is what decides whether a caller pays for a stream pass at all — so the
    // lookup has to answer without touching pixels.
    [Test]
    public void LocatingNamesTexturesWithoutDecodingThem()
    {
        if (!Available(out string bundle))
            return;

        SerializedFile file = ModelExtractor.ReadMasterbundleFile(bundle);

        // Every container path the bundle knows, taken from the bundle itself rather than guessed.
        var paths = new List<string>();
        foreach ((string path, UnityTexture _) in BundleTextures.Locate(file, AllPaths(file)))
        {
            paths.Add(path);
            if (paths.Count == 4)
                break;
        }

        if (paths.Count == 0)
            return; // this platform's masterbundle names no textures in its first SerializedFile

        // And the located set is exactly what was asked for — no more, and no FEWER. A loop alone
        // passes when the lookup yields nothing at all, which is the shape of the regression that would
        // leave every caller deciding it needs no stream pass.
        int located = 0;
        foreach ((string path, UnityTexture texture) in BundleTextures.Locate(file, paths))
        {
            Assert.Contains(path, paths);
            Assert.NotNull(texture);
            located++;
        }

        Assert.Equal(paths.Count, located);
    }

    // Inline extraction over the real bundle: the textures whose pixels are already in the SerializedFile
    // come back decoded, and the streamed ones are simply absent — the caller then pays for a stream pass
    // only if it still wants them.
    [Test]
    public void InlineExtractionTakesOnlyWhatIsAlreadyThere()
    {
        if (!Available(out string bundle))
            return;

        SerializedFile file = ModelExtractor.ReadMasterbundleFile(bundle);
        var paths = new List<string>();
        foreach ((string path, UnityTexture _) in BundleTextures.Locate(file, AllPaths(file)))
        {
            paths.Add(path);
            if (paths.Count == 16)
                break;
        }

        if (paths.Count == 0)
            return;

        Dictionary<string, CachedTexture> inline = BundleTextures.ExtractInline(file, paths);

        // However many came back, each is a real image rather than an empty entry standing in for one.
        foreach (KeyValuePair<string, CachedTexture> entry in inline)
        {
            Assert.Contains(entry.Key, paths);
            Assert.True(entry.Value.Width > 0 && entry.Value.Height > 0,
                $"{entry.Key} decoded to a {entry.Value.Width}x{entry.Value.Height} image");
        }
    }

    // --- helpers -------------------------------------------------------------------------------------

    // Every container path the file's AssetBundle object names. Passed back into Locate, this is how a
    // test asks "whatever you have" without hardcoding an asset that a game update could rename.
    private static IReadOnlyCollection<string> AllPaths(SerializedFile file)
    {
        var everything = new HashSet<string>();
        foreach (SerializedObject o in file.Objects)
            if (o.ClassId == AssetBundleClassId)
            {
                foreach (KeyValuePair<string, object> entry in ContainerOf(o, file))
                    everything.Add(entry.Key);
                break;
            }

        return everything;
    }

    private const int AssetBundleClassId = 142;

    private static IEnumerable<KeyValuePair<string, object>> ContainerOf(SerializedObject o,
        SerializedFile file)
    {
        Dictionary<string, object> bundle = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
        if (bundle.TryGetValue("m_Container", out object? container)
            && container is List<object> pairs)
        {
            foreach (object pair in pairs)
                if (pair is Dictionary<string, object> entry
                    && entry.TryGetValue("first", out object? first) && first is string path)
                {
                    yield return new KeyValuePair<string, object>(path, entry);
                }
        }
    }

    private static bool Available(out string bundle)
    {
        string? install = UnturnedInstall.Find();
        bundle = (install == null ? null : UnturnedInstall.FindMasterBundle(install)) ?? "";
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
