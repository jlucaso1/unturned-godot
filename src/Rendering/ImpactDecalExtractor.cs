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

    // True when THIS request has already been run to completion.
    //
    // Answered by a manifest rather than by looking at the textures, and both of the obvious shortcuts
    // are wrong. "Any wanted file exists" skips the bundle forever if extraction was interrupted after
    // writing one, or if a game update adds a decal path beside a still-current one. "Every wanted file
    // exists" never passes at all: most candidates are paths the bundle does not have — both folder
    // shapes are offered for every effect precisely because only the bundle knows which is real — so a
    // complete extraction still leaves most of them absent.
    //
    // Only the extraction knows the difference between "not there" and "not fetched yet", so it records
    // what it was asked for. A manifest naming a different set of paths is a request that has changed and
    // has to run again.
    public static string ManifestFor(string cacheDirectory, string bundleTag) =>
        Path.Combine(cacheDirectory, $"decals_{bundleTag}.manifest");

    public static bool IsSatisfied(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            string path = ManifestFor(request.CacheDirectory, request.BundleTag);
            if (!File.Exists(path))
                return false;
            var recorded = new HashSet<string>(File.ReadAllLines(path), StringComparer.Ordinal);
            recorded.Remove(string.Empty);
            if (recorded.Count != request.ContainerPaths.Count)
                return false;
            foreach (string wanted in request.ContainerPaths)
                if (!recorded.Contains(wanted))
                    return false;

            // The manifest says the bundle was read for exactly these. What it CANNOT vouch for is the
            // textures still being readable — a cache format change invalidates them without touching
            // this file — so anything the manifest claims produced something has to still be current.
            foreach (string wanted in request.ContainerPaths)
            {
                string texture = PathFor(request.CacheDirectory,
                    ImpactDecalPlan.CacheKey(request.BundleTag, wanted));
                if (File.Exists(texture) && !TextureCache.IsCurrent(texture))
                    return false;
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // unreadable manifest: extract again rather than skip forever
        }
    }

    // Files one decoded texture under the key both ends agree on. Public so the object streamer can hand
    // it what its own forward pass already read, instead of this reopening the same bundle afterwards.
    public static void WriteTexture(Request request, string containerPath, CachedTexture texture)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            Directory.CreateDirectory(request.CacheDirectory);
            string key = ImpactDecalPlan.CacheKey(request.BundleTag, containerPath);
            using FileStream stream = File.Create(PathFor(request.CacheDirectory, key));
            TextureCache.Write(stream, texture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Per texture: one unwritable file must not cost the rest of them.
        }
    }

    // Records that this exact set of paths has been looked for. See IsSatisfied for why the record is
    // what answers rather than the textures themselves.
    public static void WriteManifest(Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            Directory.CreateDirectory(request.CacheDirectory);
            File.WriteAllLines(ManifestFor(request.CacheDirectory, request.BundleTag),
                request.ContainerPaths);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // No manifest means this runs again next boot: slower, never wrong.
        }
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
            WriteTexture(request, containerPath, texture);
            written++;
        }

        // Written LAST, and only after a read that did not throw: a run that failed part-way leaves no
        // manifest and is simply retried.
        WriteManifest(request);
        return written;
    }
}
