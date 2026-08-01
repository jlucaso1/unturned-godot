using System;
using System.Globalization;

namespace UnturnedGodot.Unity;

// Names a texture in the shared cache: the bundle that owns it, then its Unity path id in hex.
//
// A PathID is only unique WITHIN its own SerializedFile. The cache, on the other hand, is shared by every
// map and every bundle — the game's own and each workshop mod's. Keyed by the id alone, a workshop
// texture and a core texture that happen to share an id claim the same <key>.tex: whichever extraction
// ran first wins, the second sees a current file and skips writing, and from then on one of the two
// materials is painted with the other's pixels. Naming the bundle in the key is what keeps those apart.
//
// The separator is the LAST underscore. TagFor never emits one, but splitting from the right means the
// hex id is still recovered intact if a tag ever carries one.
public static class TextureKey
{
    public static string For(string bundleTag, long pathId) =>
        bundleTag + "_" + pathId.ToString("x", CultureInfo.InvariantCulture);

    // Splits a key back into its bundle and path id. False for anything that is not a key this class
    // produced — including the bare-hex keys older cache formats used.
    public static bool TryParse(string key, out string bundleTag, out long pathId)
    {
        bundleTag = string.Empty;
        pathId = 0;
        if (string.IsNullOrEmpty(key))
            return false;

        int split = key.LastIndexOf('_');
        if (split < 0 || split == key.Length - 1)
            return false;

        if (!long.TryParse(key.AsSpan(split + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out pathId))
        {
            return false;
        }

        bundleTag = key[..split];
        return true;
    }

    // The path id a key carries, but only when the key belongs to `bundleTag`. This is what keeps a
    // texture pass from trying to resolve another bundle's ids against its own SerializedFile.
    public static bool TryParseFor(string key, string bundleTag, out long pathId) =>
        TryParse(key, out string owner, out pathId)
        && string.Equals(owner, bundleTag, StringComparison.Ordinal);

    // A filename-safe discriminator for a bundle, derived from the name assets reference it by
    // ("core.masterbundle" -> "core"). The bundle FILE name is not usable: it carries a platform suffix
    // (california2_linux.masterbundle), which would give the same content a different key per platform.
    public static string TagFor(string bundleName)
    {
        string name = bundleName;
        if (name.EndsWith(".masterbundle", StringComparison.OrdinalIgnoreCase))
            name = name[..^".masterbundle".Length];

        Span<char> safe = name.Length <= 128 ? stackalloc char[name.Length] : new char[name.Length];
        bool needsDiscriminator = false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = char.ToLowerInvariant(name[i]);
            safe[i] = char.IsAsciiLetterOrDigit(c) ? c : '-';
            needsDiscriminator |= !char.IsAsciiLetterOrDigit(c);
        }
        string tag = new string(safe);
        return needsDiscriminator ? tag + "-" + Discriminator(name) : tag;
    }

    // Punctuation is normalized above for filenames, so retain a stable discriminator for names such as
    // "some mod" and "some-mod" that would otherwise share one namespace. FNV-1a is tiny, deterministic,
    // and runs only while content sources are discovered, never while texture keys are streamed.
    private static string Discriminator(string name)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (char c in name)
        {
            hash ^= char.ToLowerInvariant(c);
            hash *= prime;
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
