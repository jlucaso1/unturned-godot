using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// The loaded icons, held for the session. Small, four of them, and read once.
public sealed class HudIconSet
{
    private readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.Ordinal);
    private readonly string _cacheDirectory;
    private readonly string _bundleTag;
    private readonly string _assetPrefix;

    public HudIconSet(string cacheDirectory, string bundleTag, string assetPrefix)
    {
        _cacheDirectory = cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory));
        _bundleTag = bundleTag ?? throw new ArgumentNullException(nameof(bundleTag));
        _assetPrefix = assetPrefix ?? throw new ArgumentNullException(nameof(assetPrefix));
    }

    // One icon, or null when the extraction has not produced it yet. Null is drawn as nothing rather
    // than as a placeholder: a missing crosshair is better than a wrong one.
    //
    // A MISS is not cached, and that is the whole reason this is a method rather than a field read. The
    // icons are extracted on the deferred pass, several seconds after the HUD is built, so on a cold
    // cache every lookup during the load misses — caching those would leave the crosshair absent for the
    // rest of the session, with nothing to notice it. A hit is cached, and after four of them this never
    // touches the filesystem again.
    public Texture2D? Get(string file)
    {
        if (_textures.TryGetValue(file, out Texture2D? cached))
            return cached;

        Texture2D? texture = null;
        string key = ImpactDecalPlan.CacheKey(_bundleTag, HudIcons.ContainerPath(_assetPrefix, file));
        string path = ImpactDecalExtractor.PathFor(_cacheDirectory, key);
        if (File.Exists(path) && TextureCache.IsCurrent(path))
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                texture = ModelLibrary.BuildTexture(CachedTexture.Decoded(TextureCache.Read(stream)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or InvalidDataException)
            {
                texture = null;
            }
        }

        if (texture != null)
            _textures[file] = texture;
        return texture;
    }
}
