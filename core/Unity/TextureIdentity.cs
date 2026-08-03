using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnturnedGodot.Data;

namespace UnturnedGodot.Unity;

// The exact identity of the pixels behind a texture cache key.
//
// Two keys can name byte-identical texture data: the same artwork lives under a different PathID in
// another bundle, or a material points at a second copy of one palette. The complete .tex file carries
// format, dimensions, mip count, filter mode and pixels, so its hash is a safe discriminator for anything
// that wants to treat those keys as one — it never merges two textures whose sampling or visual result
// differs.
//
// An identity only exists once the texture has been extracted. During a cold load the materials are built
// while the .resS tail is still being written, so most keys resolve to nothing; that is reported as an
// empty string rather than guessed at, and the caller retries when the textures have settled.
public static class TextureIdentity
{
    // The identity of one key, or "" when its .tex is absent, stale or unreadable.
    public static string Resolve(string textureCacheDir, string textureKey)
    {
        if (textureKey.Length == 0)
            return "";
        string path = Path.Combine(textureCacheDir, textureKey + ".tex");
        try
        {
            if (File.Exists(path) && TextureCache.IsCurrent(path))
                return ExactContentKey.File(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return "";
    }

    // Resolves many keys at once, hashing in parallel. Only keys that resolved appear in the result, so an
    // absent texture leaves the caller's existing (raw-key) identity in place. Pure IO and CPU: safe to run
    // on a worker while the main thread keeps rendering, which is the point — hashing a map's whole texture
    // cache is far too much work to drop into a frame.
    public static Dictionary<string, string> ResolveAll(string textureCacheDir,
        IReadOnlyCollection<string> textureKeys, CancellationToken cancellationToken = default)
    {
        var resolved = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var options = new ParallelOptions { CancellationToken = cancellationToken };
        Parallel.ForEach(textureKeys, options, key =>
        {
            string identity = Resolve(textureCacheDir, key);
            if (identity.Length > 0)
                resolved[key] = identity;
        });
        return new Dictionary<string, string>(resolved, StringComparer.Ordinal);
    }
}
