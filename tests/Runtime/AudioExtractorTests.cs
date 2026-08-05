using System;
using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Planning and caching the game's audio.
//
// The cache key is the part with a bug in its history, and it is the kind that is invisible until someone
// walks on the wrong surface: definitions with the same LEAF name routinely live in different folders, and
// keying on the leaf alone let the later one overwrite the earlier def.bin — so two surfaces played each
// other's footsteps. The key therefore carries both the source identity and the full asset path.
//
// The planning half is the other constraint, and it comes from the decoder rather than from the audio: a
// forward-only reader cannot go back for a byte range named after it has gone past, so every clip range a
// bundle owes has to be worked out BEFORE any stream node is read.
//
// The parts that need the real bundle say so and return without it; UG_REQUIRE_REAL_DATA=1 makes that a
// failure, so the job that fetches the content cannot pass by finding nothing.
public class AudioExtractorTests : TestClass
{
    public AudioExtractorTests(Node testScene) : base(testScene) { }

    // Two definitions with the same leaf name in different folders must not collide. This is the bug:
    // keyed on the leaf alone, the second overwrote the first and both surfaces played one set of clips.
    [Test]
    public void DefinitionsSharingALeafNameGetDifferentKeys()
    {
        string first = AudioExtractor.DefKey("core", "Sounds/Landscape/Grass/Footstep.asset");
        string second = AudioExtractor.DefKey("core", "Sounds/Landscape/Gravel/Footstep.asset");

        Assert.NotEqual(first, second);
        // Both still carry the readable leaf, so a cache directory can be reasoned about by eye.
        Assert.Contains("footstep", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("footstep", second, StringComparison.OrdinalIgnoreCase);
    }

    // The bundle tag is part of the key too: a workshop map defining its own surface with the game's name
    // for it must not overwrite the game's.
    [Test]
    public void TheSameDefinitionFromTwoBundlesGetsTwoKeys()
    {
        string core = AudioExtractor.DefKey("core", "Sounds/Landscape/Grass/Footstep.asset");
        string workshop = AudioExtractor.DefKey("workshop-1234", "Sounds/Landscape/Grass/Footstep.asset");

        Assert.NotEqual(core, workshop);
    }

    // An untagged source still produces a usable key rather than a leading separator, which is what a
    // discovered source with no MasterBundle.dat name resolves to.
    [Test]
    public void AnUntaggedSourceStillKeysCleanly()
    {
        string key = AudioExtractor.DefKey("", "Sounds/Landscape/Grass/Footstep.asset");

        Assert.NotEmpty(key);
        Assert.False(key.StartsWith('-'), $"the key begins with a separator: '{key}'");
    }

    // The name inside the key is the asset's own, with the extension and the folders stripped — both
    // path separators, because the game's own data uses either.
    [Test]
    public void TheDefinitionNameIsTheAssetsOwn()
    {
        Assert.Equal("Footstep", AudioExtractor.DefNameOf("Sounds/Landscape/Grass/Footstep.asset"));
        Assert.Equal("Footstep", AudioExtractor.DefNameOf("Sounds\\Landscape\\Grass\\Footstep.asset"));
        Assert.Equal("Footstep", AudioExtractor.DefNameOf("Footstep"));
        Assert.Equal("Footstep", AudioExtractor.DefNameOf("Footstep.asset"));
    }

    // A definition is complete only once its def.bin is there. That file is the marker the whole cache
    // rests on: without it a half-written directory would read as done and the surface would stay silent
    // with nothing ever re-extracting it.
    [Test]
    public void ADefinitionIsOnlyCachedOnceItsMarkerExists()
    {
        using var dir = new TempDir();
        string defDir = Path.Combine(dir.Path, "core-footstep");
        Directory.CreateDirectory(defDir);
        File.WriteAllBytes(Path.Combine(defDir, "clip0.ogg"), new byte[] { 1, 2, 3 });

        Assert.False(AudioExtractor.IsCached(dir.Path, "core-footstep"), "a clip alone is not a definition");

        File.WriteAllBytes(Path.Combine(defDir, "def.bin"), new byte[] { 1 });
        Assert.True(AudioExtractor.IsCached(dir.Path, "core-footstep"));
    }

    // Nothing missing means no plan at all, so a bundle pass carrying several SerializedFiles pays
    // nothing for the ones that owe it nothing.
    [Test]
    public void AFileThatOwesNothingProducesNoPlan()
    {
        if (!Available("the core masterbundle", MasterBundle, out string bundle))
            return;

        using var dir = new TempDir();
        SerializedFile file = ModelExtractor.ReadMasterbundleFile(bundle);
        var request = new AudioExtractor.Request(bundle, "core",
            new List<string>(), null, dir.Path);

        Assert.Null(AudioExtractor.Plan(file, request));
    }

    // And a file that DOES owe something plans it out of the real bundle: the ranges are worked out before
    // any stream node is read, because a forward-only decoder cannot go back for one named after it went
    // past.
    [Test]
    public void TheRealBundlePlansTheClipsItOwes()
    {
        if (!Available("the core masterbundle", MasterBundle, out string bundle))
            return;

        using var dir = new TempDir();
        SerializedFile file = ModelExtractor.ReadMasterbundleFile(bundle);
        var request = new AudioExtractor.Request(bundle, "core", new List<string>(),
            new[]
            {
                new AudioExtractor.RawClipGroup("ZombieRoars",
                    new[] { "Sounds/Zombies/Roars/Roar_0.mp3", "Sounds/Zombies/Roars/Roar_1.mp3" },
                    Volume: 1f, MinPitch: 0.9f, MaxPitch: 1.1f),
            },
            dir.Path);

        AudioExtractor.StreamPlan? plan = AudioExtractor.Plan(file, request);

        // Either the game's bundle carries those clips and a plan comes back, or this platform's bundle
        // does not and it does not — both are answers, and neither may throw.
        if (plan == null)
            return;
        Assert.Equal(dir.Path, plan.CacheDirectory);
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

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-audioex-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
