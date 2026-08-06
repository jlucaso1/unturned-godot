using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Pulls the impact decal textures out of a master bundle and files them where ImpactDecals looks.
//
// A handful of textures — the shipped surfaces name a dozen effects between them — so this asks for every
// candidate path at once and keeps what comes back. A path that is not in the bundle costs nothing: both
// shapes of decal folder are offered for every effect precisely because only the bundle knows which of
// them exists.
public static class ImpactDecalExtractor
{
    // What one bundle owes. Mirrors AudioExtractor.Request so the two can be planned and run side by side.
    public sealed record Request(string BundlePath, string BundleTag,
        IReadOnlyCollection<string> ContainerPaths, string CacheDirectory);

    public static string PathFor(string cacheDirectory, string cacheKey) =>
        Path.Combine(cacheDirectory, cacheKey + ".tex");

    // True when every texture this request could produce is already cached — which, because most requests
    // ask for paths the bundle does not have, is not the same as "every path is on disk".
    //
    // The test is deliberately weak: a bundle is only opened when NOTHING it was asked for is present.
    // Asking whether a specific candidate exists would mean opening the bundle to find out, which is the
    // whole cost this is here to avoid, and a decal that never arrives leaves a surface unmarked rather
    // than breaking anything.
    public static bool IsSatisfied(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (string path in request.ContainerPaths)
            if (File.Exists(PathFor(request.CacheDirectory, ImpactDecalPlan.CacheKey(request.BundleTag, path))))
                return true;
        return false;
    }

    // Extracts what the bundle actually holds, and returns how many textures were written. Best-effort:
    // a bundle that cannot be read leaves those surfaces unmarked, which is the same outcome as a surface
    // that names no effect.
    public static int Extract(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BundlePath.Length == 0 || request.ContainerPaths.Count == 0)
            return 0;

        Dictionary<string, CachedTexture> textures;
        try
        {
            textures = BundleTextures.ExtractStreamed(request.BundlePath, request.ContainerPaths);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return 0;
        }

        int written = 0;
        foreach ((string containerPath, CachedTexture texture) in textures)
        {
            string key = ImpactDecalPlan.CacheKey(request.BundleTag, containerPath);
            try
            {
                Directory.CreateDirectory(request.CacheDirectory);
                using FileStream stream = File.Create(PathFor(request.CacheDirectory, key));
                TextureCache.Write(stream, texture);
                written++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Per texture: one unwritable file must not cost the rest of them.
            }
        }

        return written;
    }
}
