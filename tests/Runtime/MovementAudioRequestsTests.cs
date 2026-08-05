using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// What each bundle owes the game's audio, in the one shape both consumers want it in.
//
// Two things ask for this list: the object streamer plans it into its cold-load bundle pass, and the
// player's own deferred extraction takes whatever that pass did not cover. Building it in one place is
// what lets the second find the cache already filled — two lists that disagreed would send the fallback
// back into a second full decode of the same 1.4 GB bundle for the difference.
//
// The clip groups are the awkward half. Zombie roars and groans and the bare-handed swing are RAW
// AudioClips the game plays directly, with no OneShotAudioDefinition of their own, so they are packaged
// as synthetic definitions carrying the envelope their caller uses.
public class MovementAudioRequestsTests : TestClass
{
    public MovementAudioRequestsTests(Node testScene) : base(testScene) { }

    // The zombie voices: 16 roars and 5 groans, exactly as ZombieManager's arrays hold them. A count that
    // drifted would leave a roar index the code can pick but the cache never extracted.
    [Test]
    public void TheZombieVoicesAreAllThere()
    {
        IReadOnlyList<AudioExtractor.RawClipGroup> groups = MovementAudioRequests.ZombieClipGroups;

        AudioExtractor.RawClipGroup roars = Find(groups, "ZombieRoars");
        AudioExtractor.RawClipGroup groans = Find(groups, "ZombieGroans");

        Assert.Equal(16, roars.ClipPaths.Count);
        Assert.Equal(5, groans.ClipPaths.Count);
        // Zombie.PlayOneShot's envelope for a normal zombie; megas override the pitch at play time.
        Assert.Equal(0.9f, roars.MinPitch);
        Assert.Equal(1.1f, roars.MaxPitch);
    }

    // Every roar path is distinct. Built in a loop, so a mistyped index would give two entries the same
    // file and silently halve the variety.
    [Test]
    public void EveryVoiceClipHasItsOwnPath()
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (AudioExtractor.RawClipGroup group in MovementAudioRequests.ZombieClipGroups)
            foreach (string path in group.ClipPaths)
                Assert.True(seen.Add(path), $"two clips share the path {path}");
    }

    // The punch is two clips at a fixed pitch — a swing does not vary the way a voice does.
    [Test]
    public void ThePunchIsAFixedPairOfClips()
    {
        AudioExtractor.RawClipGroup punch = MovementAudioRequests.PunchClipGroup;

        Assert.Equal(2, punch.ClipPaths.Count);
        Assert.Equal(1f, punch.MinPitch);
        Assert.Equal(1f, punch.MaxPitch);
    }

    // The core bundle owes the voices AND the punch. This list is built separately rather than by
    // appending to the zombie one, and the reason is written in the source: static initializers run in
    // declaration order, so a group declared below would still be null when the append ran.
    [Test]
    public void TheCoreBundleOwesEveryRawGroup()
    {
        IReadOnlyList<AudioExtractor.RawClipGroup> core = MovementAudioRequests.CoreClipGroups;

        Assert.Equal(MovementAudioRequests.ZombieClipGroups.Count + 1, core.Count);
        foreach (AudioExtractor.RawClipGroup group in MovementAudioRequests.ZombieClipGroups)
            Assert.Contains(core, g => g.Name == group.Name);
        Assert.Contains(core, g => g.Name == MovementAudioRequests.PunchClipGroup.Name);

        // Nothing came through null, which is exactly what the ordering hazard above would produce.
        foreach (AudioExtractor.RawClipGroup group in core)
        {
            Assert.NotNull(group);
            Assert.NotEmpty(group.ClipPaths);
        }
    }

    // --- against the real install ---------------------------------------------------------------------

    // Scanning the install's own physics materials. This is what names every definition the movement
    // audio will ask for, and a workshop map defines its own surfaces — scanning only the game's left
    // those silent.
    [Test]
    public void TheInstallsPhysicsMaterialsAreScanned()
    {
        if (!Sources(out IReadOnlyList<ContentSource> sources))
            return;

        PhysicsMaterialBank bank = MovementAudioRequests.ScanPhysicsMaterials(sources);

        Assert.True(bank.Count > 0, "the install carries no physics materials at all");
    }

    // The request list itself, built from real sources. Two consumers ask for it — the streamer's
    // cold-load pass and the player's deferred extraction — and they have to agree, or the second sends
    // the fallback into a full second decode of the same 1.4 GB bundle for the difference.
    [Test]
    public void TheRequestListIsBuiltFromTheRealSources()
    {
        if (!Sources(out IReadOnlyList<ContentSource> sources))
            return;

        string install = UnturnedInstall.Find()!;
        PhysicsMaterialBank bank = MovementAudioRequests.ScanPhysicsMaterials(sources);
        List<AudioExtractor.Request> requests =
            MovementAudioRequests.For(sources, bank, install, Path.GetTempPath());

        Assert.NotEmpty(requests);
        foreach (AudioExtractor.Request request in requests)
        {
            Assert.NotEmpty(request.BundlePath);
            Assert.Equal(Path.GetTempPath(), request.AudioCacheDir);
        }

        // Exactly one request carries the raw clip groups: they only exist in the game's own bundle, and
        // handing them to a workshop bundle would send it looking for roars it does not have.
        int withGroups = 0;
        foreach (AudioExtractor.Request request in requests)
            if (request.ClipGroups != null)
                withGroups++;
        Assert.True(withGroups <= 1, $"{withGroups} bundles were asked for the game's own raw clips");
    }

    // Asking the same thing twice produces the same list. It is read by two consumers at different
    // points in the load, and a list that varied between them is the disagreement this exists to prevent.
    [Test]
    public void TheListIsStableBetweenCalls()
    {
        if (!Sources(out IReadOnlyList<ContentSource> sources))
            return;

        string install = UnturnedInstall.Find()!;
        PhysicsMaterialBank bank = MovementAudioRequests.ScanPhysicsMaterials(sources);

        List<AudioExtractor.Request> first =
            MovementAudioRequests.For(sources, bank, install, Path.GetTempPath());
        List<AudioExtractor.Request> second =
            MovementAudioRequests.For(sources, bank, install, Path.GetTempPath());

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
            Assert.Equal(first[i].BundlePath, second[i].BundlePath);
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static bool Sources(out IReadOnlyList<ContentSource> sources)
    {
        sources = System.Array.Empty<ContentSource>();
        string? install = UnturnedInstall.Find();
        if (install == null)
        {
            if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
            {
                throw new IOException(
                    "UG_REQUIRE_REAL_DATA=1 but no Unturned install is present; this run exists to prove "
                    + "these tests execute");
            }

            Log.Print("[runtime-tests] skipping: no Unturned install "
                + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
            return false;
        }

        sources = ContentSource.Discover(install);
        return sources.Count > 0;
    }

    private static AudioExtractor.RawClipGroup Find(
        IReadOnlyList<AudioExtractor.RawClipGroup> groups, string name)
    {
        foreach (AudioExtractor.RawClipGroup group in groups)
            if (group.Name == name)
                return group;
        throw new System.InvalidOperationException($"no clip group named {name}");
    }
}
