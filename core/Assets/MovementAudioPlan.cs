using System;
using System.Collections.Generic;

namespace UnturnedGodot.Assets;

// Which OneShotAudioDefinition assets the movement audio needs, grouped by the bundle that packages them.
//
// The one-time audio extraction and the cold-load bundle pass both have to know this, and they run in
// different places: the extraction from the player's own spawn, the pass from the object streamer's
// preparation, several seconds earlier. Deriving it in one place from the physics-material bank keeps
// them naming the same definitions — a pass that planned a smaller set than the extraction expects just
// leaves the difference for the extraction to fetch, which is the slow path this exists to avoid.
public static class MovementAudioPlan
{
    // The events a footstep/landing resolves through. Not every surface defines all three; a material
    // without one falls back through the chain the bank walks.
    public static readonly string[] EventKeys = { "FootstepWalk", "FootstepRun", "BipedLand" };

    // What a fist landing on that same surface resolves through. A different moment entirely, planned in
    // the same pass because it is the same question asked of the same assets: a bundle opened once should
    // give up everything its surfaces owe, and a definition discovered later costs a second whole-bundle
    // decode. See ImpactAudio for which of the two keys wins at play time.
    public static readonly string[] ImpactEventKeys = { "MeleeImpact", "LegacyImpact" };

    // Every surface event the extraction plans for, in one sequence.
    public static IEnumerable<string> AllEventKeys
    {
        get
        {
            foreach (string key in EventKeys)
                yield return key;
            foreach (string key in ImpactEventKeys)
                yield return key;
        }
    }

    // Every name the bank knows, not the game's ten base surfaces: a workshop landscape can name its own
    // material, and one the extraction never visited was resolvable at runtime but absent from the audio
    // cache, so that ground was silent. Definitions are shared between materials, so the set of paths this
    // produces is barely larger than the built-in one.
    //
    // A definition is looked for in the bundle of the source whose assets folder defines it: a workshop
    // map's own surfaces are packaged in its mod's bundle, and asking the game's bundle for those left the
    // new surfaces silent. A material that falls back to a core one still resolves to the core bundle,
    // because the fallback asset is the one that defines the event.
    public static Dictionary<string, HashSet<string>> DefPathsByBundle(
        IReadOnlyList<ContentSource> sources, PhysicsMaterialBank bank, string fallbackBundlePath)
    {
        var byBundle = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string key in AllEventKeys)
            foreach (string name in bank.Names)
            {
                if (bank.FindAudioDef(name, key) is not { } def)
                    continue;

                string owner = SourceForAssetDirectory(sources, def.Owner.Directory)?.BundlePath
                    ?? fallbackBundlePath;
                if (owner.Length == 0)
                    continue;

                if (!byBundle.TryGetValue(owner, out HashSet<string>? paths))
                    byBundle[owner] = paths = new HashSet<string>(StringComparer.Ordinal);
                paths.Add(def.Path);
            }

        // The game's own bundle always gets an entry, even an empty one: the zombie clip groups ride along
        // with its pass and would otherwise never be extracted on a map whose surfaces are all modded.
        if (fallbackBundlePath.Length > 0 && !byBundle.ContainsKey(fallbackBundlePath))
            byBundle[fallbackBundlePath] = new HashSet<string>(StringComparer.Ordinal);
        return byBundle;
    }

    public static ContentSource? SourceForAssetDirectory(IReadOnlyList<ContentSource> sources,
        string directory)
    {
        if (directory.Length == 0)
            return null;

        foreach (ContentSource source in sources)
            if (source.Owns(directory))
                return source;

        return null;
    }
}
