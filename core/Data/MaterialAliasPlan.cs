using System.Collections.Generic;

namespace UnturnedGodot.Data;

// Which surfaces of an already-built scene may share one material.
//
// A material is identified by its texture plus the rest of the Unity Standard state (colour, blend,
// metallic, smoothness, cull). The texture half of that is the exact content identity of the .tex file,
// not the cache key that names it, so two keys holding identical pixels build one material instead of two.
//
// On a cold load the identities do not exist yet — the materials are built between the mesh phase and the
// texture phase of the same decode pass — so every surface falls back to its raw key and the aliases stay
// separate. Re-running the grouping once the textures have settled is what folds them together, which is
// all this does: no file IO, no engine objects, just the grouping the warm path got for free.
public static class MaterialAliasPlan
{
    // Canonical[i] is the index of the surface whose material surface i should end up using: itself when it
    // is the first of its group. Order is the caller's, so the same inputs always elect the same surface.
    //
    // `identities` substitutes for the raw texture key. A key it does not carry (or carries empty) keeps
    // its own key as its identity, so an unresolved texture can never merge two surfaces — the fallback is
    // always "these are different", never "these are the same".
    public static int[] Canonical<TState>(IReadOnlyList<(string TextureKey, TState State)> surfaces,
        IReadOnlyDictionary<string, string> identities)
        where TState : notnull
    {
        var canonical = new int[surfaces.Count];
        var firstOfGroup = new Dictionary<(string Identity, TState State), int>();
        for (int i = 0; i < surfaces.Count; i++)
        {
            (string textureKey, TState state) = surfaces[i];
            string identity = identities.TryGetValue(textureKey, out string? found) && found.Length > 0
                ? found
                : textureKey;

            var group = (identity, state);
            if (firstOfGroup.TryGetValue(group, out int first))
            {
                canonical[i] = first;
                continue;
            }
            firstOfGroup[group] = i;
            canonical[i] = i;
        }
        return canonical;
    }

    // How many surfaces would change material, and how many distinct materials the plan leaves behind.
    public static (int Repointed, int Materials) Cost(IReadOnlyList<int> canonical)
    {
        int repointed = 0, materials = 0;
        for (int i = 0; i < canonical.Count; i++)
        {
            if (canonical[i] == i)
                materials++;
            else
                repointed++;
        }
        return (repointed, materials);
    }
}
