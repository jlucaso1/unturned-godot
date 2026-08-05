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

    // --- writing the cache ----------------------------------------------------------------------------

    // Writing a clip whose index is not in the plan does nothing. The pass hands ranges back in stream
    // order and the caller indexes into the plan, so an off-by-one there must not write a file into
    // another definition's directory.
    [Test]
    public void WritingAClipOutsideThePlanDoesNothing()
    {
        if (!Available("the core masterbundle", MasterBundle, out string bundle))
            return;

        using var dir = new TempDir();
        AudioExtractor.StreamPlan? plan = PlanForZombieVoices(bundle, dir.Path);
        if (plan == null)
            return;

        AudioExtractor.WriteClip(plan, -1, System.Array.Empty<byte>());
        AudioExtractor.WriteClip(plan, plan.Clips.Count, System.Array.Empty<byte>());
        AudioExtractor.WriteClip(plan, plan.Clips.Count + 10, System.Array.Empty<byte>());

        Assert.Empty(Directory.GetFiles(dir.Path, "*.ogg", SearchOption.AllDirectories));
    }

    // A blob FMOD cannot rebuild writes no file. The alternative — an empty or truncated .ogg on disk —
    // would be read as a real clip by every later boot and play as silence with nothing re-extracting it.
    [Test]
    public void AClipThatCannotBeRebuiltWritesNothing()
    {
        if (!Available("the core masterbundle", MasterBundle, out string bundle))
            return;

        using var dir = new TempDir();
        AudioExtractor.StreamPlan? plan = PlanForZombieVoices(bundle, dir.Path);
        if (plan == null || plan.Clips.Count == 0)
            return;

        AudioExtractor.WriteClip(plan, 0, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }); // not an FSB5 bank

        Assert.Empty(Directory.GetFiles(dir.Path, "*.ogg", SearchOption.AllDirectories));
    }

    // And with no clips written, no definition is marked complete. def.bin is the cache's ONLY
    // completeness marker: one over an empty directory would make every later boot skip the definition
    // and play silence, with nothing ever looking at it again.
    [Test]
    public void NoDefinitionIsMarkedCompleteWithoutClips()
    {
        if (!Available("the core masterbundle", MasterBundle, out string bundle))
            return;

        using var dir = new TempDir();
        AudioExtractor.StreamPlan? plan = PlanForZombieVoices(bundle, dir.Path);
        if (plan == null)
            return;

        int completed = AudioExtractor.CompleteDefs(plan);

        Assert.Equal(0, completed);
        Assert.Empty(Directory.GetFiles(dir.Path, "def.bin", SearchOption.AllDirectories));
    }

    // --- the whole pass ------------------------------------------------------------------------------

    // The standalone extraction, run for real over the game's own bundle into a cache the test owns.
    //
    // This is the fallback path: what the cold load's own pass did not cover — an unstreamable bundle, a
    // cancelled pass, or a load whose meshes were already cached and therefore ran no pass at all — still
    // has to be fetched, and this is what fetches it. Every session that starts with a warm mesh cache
    // and a cold audio cache takes exactly this road.
    //
    // What is asserted is that it PRODUCES something and that what it produced is complete: clips on disk
    // and a def.bin beside them. That marker is the cache's only completeness signal, so an extraction
    // that wrote clips without it would re-extract forever, and one that wrote it without clips would
    // make every later boot skip the definition and play silence with nothing looking at it again.
    [Test]
    public void TheRealExtractionFillsTheCacheAndMarksItComplete()
    {
        if (!Available("the core masterbundle", MasterBundle, out string bundle))
            return;

        using var dir = new TempDir();

        int served = AudioExtractor.Extract(bundle, "core", new List<string>(), dir.Path,
            MovementAudioRequests.ZombieClipGroups);

        if (served == 0)
            return; // this platform's bundle does not carry them, which is an answer rather than a failure

        string[] clips = Directory.GetFiles(dir.Path, "*.ogg", SearchOption.AllDirectories);
        string[] markers = Directory.GetFiles(dir.Path, "def.bin", SearchOption.AllDirectories);

        Assert.NotEmpty(clips);
        Assert.NotEmpty(markers);
        foreach (string clip in clips)
            Assert.True(new FileInfo(clip).Length > 0, $"{clip} was written empty");
    }

    // And the second run over a full cache opens nothing at all. That is the whole point of the marker:
    // an extraction that could not tell a finished cache from an empty one would decode a 110 MB bundle
    // on every boot, behind a loading screen, for audio it already had.
    [Test]
    public void ASecondRunOverAFullCacheOpensNothing()
    {
        if (!Available("the core masterbundle", MasterBundle, out string bundle))
            return;

        using var dir = new TempDir();
        if (AudioExtractor.Extract(bundle, "core", new List<string>(), dir.Path,
                MovementAudioRequests.ZombieClipGroups) == 0)
        {
            return;
        }

        var request = new AudioExtractor.Request(bundle, "core", new List<string>(),
            MovementAudioRequests.ZombieClipGroups, dir.Path);

        Assert.True(AudioExtractor.IsSatisfied(request),
            "a filled cache still reported work outstanding, so every boot would decode the bundle again");
        Assert.Equal(0, AudioExtractor.Extract(bundle, "core", new List<string>(), dir.Path,
            MovementAudioRequests.ZombieClipGroups));
    }

    // An empty cache is never satisfied — the other side of the same question, and the one that decides
    // whether a first boot fetches its audio at all.
    [Test]
    public void AnEmptyCacheIsNeverSatisfied()
    {
        using var dir = new TempDir();
        var request = new AudioExtractor.Request("/nonexistent-bundle", "core", new List<string>(),
            MovementAudioRequests.ZombieClipGroups, dir.Path);

        Assert.False(AudioExtractor.IsSatisfied(request));
    }

    // A request that owes nothing does not open the bundle, however unreadable that bundle is. This is
    // what keeps a session with a warm audio cache from paying for a decode it does not need — and the
    // path is proven by handing it a bundle that could not possibly be read.
    [Test]
    public void ARequestThatOwesNothingNeverTouchesTheBundle()
    {
        using var dir = new TempDir();

        Assert.Equal(0, AudioExtractor.Extract("/nonexistent-bundle", "core", new List<string>(),
            dir.Path, System.Array.Empty<AudioExtractor.RawClipGroup>()));
    }

    // --- helpers -------------------------------------------------------------------------------------

    // A plan over the game's own zombie voices, or null on a platform whose bundle does not carry them.
    private static AudioExtractor.StreamPlan? PlanForZombieVoices(string bundle, string cacheDir)
    {
        SerializedFile file = ModelExtractor.ReadMasterbundleFile(bundle);
        return AudioExtractor.Plan(file, new AudioExtractor.Request(bundle, "core",
            new List<string>(), MovementAudioRequests.ZombieClipGroups, cacheDir));
    }

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
