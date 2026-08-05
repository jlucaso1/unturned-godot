using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// What the movement and zombie audio owes each bundle, in the one shape both consumers want it in: the
// object streamer plans it into its cold-load bundle pass, and the player's own deferred extraction takes
// whatever the pass did not cover. Building it in one place is what lets the second of those find the
// cache already filled — two lists of definitions that disagreed would send the fallback back into a
// second full decode of the same 1.4 GB bundle for the difference.
public static class MovementAudioRequests
{
    // ZombieManager's raw clip arrays (played directly, not via OneShotAudioDefinitions): the 16 roars and
    // 5 groans, packaged as synthetic definitions with Zombie.PlayOneShot's envelope (pitch 0.9-1.1 for a
    // normal zombie; megas override at play time). They only exist in the game's own bundle.
    public static IReadOnlyList<AudioExtractor.RawClipGroup> ZombieClipGroups { get; } = BuildZombieGroups();

    // The bare-handed swing, the same way: two raw clips in the core bundle's Sounds root, with no
    // definition of their own. See PlayerGestures.PunchSound for where the envelope comes from.
    public static AudioExtractor.RawClipGroup PunchClipGroup { get; } = new(
        UnturnedGodot.Player.PlayerGestures.PunchSound,
        new[] { "Sounds/MeleeAttack_01.mp3", "Sounds/MeleeAttack_02.mp3" },
        Volume: 1f, MinPitch: 1f, MaxPitch: 1f);

    private static List<AudioExtractor.RawClipGroup> BuildZombieGroups()
    {
        var roarPaths = new string[16];
        for (int i = 0; i < roarPaths.Length; i++)
            roarPaths[i] = $"Sounds/Zombies/Roars/Roar_{i}.mp3";
        var groanPaths = new string[5];
        for (int i = 0; i < groanPaths.Length; i++)
            groanPaths[i] = $"Sounds/Zombies/Groans/Groan_{i}.mp3";
        return new List<AudioExtractor.RawClipGroup>
        {
            new("ZombieRoars", roarPaths, Volume: 1f, MinPitch: 0.9f, MaxPitch: 1.1f),
            new("ZombieGroans", groanPaths, Volume: 1f, MinPitch: 0.9f, MaxPitch: 1.1f),
        };
    }

    // Every raw clip group the game's own bundle owes, which is what a core-bundle request carries. Built
    // as its own list rather than by appending to ZombieClipGroups: static initializers run in
    // declaration order, so a group declared below that one would still be null when it ran.
    public static IReadOnlyList<AudioExtractor.RawClipGroup> CoreClipGroups { get; } =
        BuildCoreGroups();

    private static List<AudioExtractor.RawClipGroup> BuildCoreGroups()
    {
        var groups = new List<AudioExtractor.RawClipGroup>(ZombieClipGroups) { PunchClipGroup };
        return groups;
    }

    // The physics materials of every source, which is what names the definitions. A workshop map defines
    // its own surfaces, and scanning the game's alone left those silent.
    public static PhysicsMaterialBank ScanPhysicsMaterials(IReadOnlyList<ContentSource> sources)
    {
        var roots = new List<string>(sources.Count);
        foreach (ContentSource source in sources)
            roots.Add(Path.Combine(source.AssetsDir, "PhysicsMaterials"));
        return PhysicsMaterialBank.ScanDirectories(roots);
    }

    public static List<AudioExtractor.Request> For(IReadOnlyList<ContentSource> sources,
        PhysicsMaterialBank bank, string unturnedPath, string audioCacheDir)
    {
        string corePath = UnturnedInstall.MasterBundlePath(unturnedPath);
        var requests = new List<AudioExtractor.Request>();
        foreach ((string bundle, HashSet<string> paths) in
            MovementAudioPlan.DefPathsByBundle(sources, bank, corePath))
        {
            requests.Add(new AudioExtractor.Request(bundle, TagOf(sources, bundle), paths,
                bundle == corePath ? CoreClipGroups : null, audioCacheDir));
        }

        return requests;
    }

    // The discovered source's complete cache tag includes both the name from MasterBundle.dat and, for a
    // workshop source, its item identity. The FILE name only serves as a fallback because it carries a
    // platform suffix and would key the same content differently per platform.
    private static string TagOf(IReadOnlyList<ContentSource> sources, string bundlePath)
    {
        foreach (ContentSource source in sources)
            if (string.Equals(source.BundlePath, bundlePath, System.StringComparison.Ordinal))
                return source.CacheTag;

        return TextureKey.TagFor(Path.GetFileNameWithoutExtension(bundlePath));
    }
}
