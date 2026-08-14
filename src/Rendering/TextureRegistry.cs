using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Maps each texture key to the materials that want it, so textures can be applied to an already-rendered
// scene as they arrive (cold-load streaming): meshes show flat palette colors first, then textures fill
// in. Building ImageTextures and assigning them is a main-thread operation, so the registry is only ever
// touched from the main thread — background extraction just signals which keys became available.
public sealed class TextureRegistry
{
    private static readonly bool DeduplicateGpu = EnvFlag.IsOn(System.Environment.GetEnvironmentVariable("UG_DEDUP_GPU"), whenUnset: true);
    private readonly string _textureCacheDir;
    private readonly Dictionary<string, List<Material>> _pending = new();
    private readonly Dictionary<string, (ImageTexture? tex, int filterMode)> _loaded = new();
    private readonly Dictionary<string, ImageTexture> _imagesByContent = new();
    private readonly Dictionary<string, string> _materialIdentity = new();
    // Keys whose identity is still the raw key because the .tex was not on disk when it was asked for.
    // A cold load asks for every one of them before the texture phase has written anything, so this is
    // where the identities that a warm load resolves immediately are remembered to be re-resolved later.
    private readonly HashSet<string> _provisionalIdentity = new(System.StringComparer.Ordinal);
    private static readonly bool DeduplicateMaterials =
        EnvFlag.IsOn(System.Environment.GetEnvironmentVariable("UG_DEDUP_MATERIAL_CONTENT"), whenUnset: true);

    public TextureRegistry(string textureCacheDir) => _textureCacheDir = textureCacheDir;

    public string TextureCacheDir => _textureCacheDir;
    public int PendingKeyCount => _pending.Count;
    public int MaterialAliasCount => _materialIdentity.Count
        - new HashSet<string>(_materialIdentity.Values, System.StringComparer.Ordinal).Count;

    // The identity every material was built on, for the re-deduplication pass to group by.
    public IReadOnlyDictionary<string, string> MaterialIdentities => _materialIdentity;

    // The keys whose identity is still a guess. Empty after a warm load; on a cold one it starts as
    // "nearly all of them" and empties as ResolvedIdentities lands.
    public IReadOnlyCollection<string> ProvisionalIdentityKeys => _provisionalIdentity;

    // A material may use a different cache key for byte-identical texture data. The complete .tex file
    // includes format, dimensions, mip count, filter mode and pixels, so its exact hash is a safe material
    // discriminator and never merges two textures whose sampling or visual result differs.
    public string MaterialIdentity(string textureKey)
    {
        if (!DeduplicateMaterials || textureKey.Length == 0)
            return textureKey;
        if (_materialIdentity.TryGetValue(textureKey, out string? identity))
            return identity;

        // Cold load: the .tex for this key is usually still somewhere in the .resS tail being decoded, so
        // the key stands in for its own identity and nothing merges. Recorded as provisional rather than
        // settled, because that answer stops being true the moment the texture lands.
        identity = TextureIdentity.Resolve(_textureCacheDir, textureKey);
        if (identity.Length == 0)
        {
            identity = textureKey;
            _provisionalIdentity.Add(textureKey);
        }
        _materialIdentity[textureKey] = identity;
        return identity;
    }

    // Takes identities resolved off the main thread (TextureIdentity.ResolveAll over the provisional keys)
    // and settles them here. Only provisional keys are updated: an identity a material was already built
    // on must not change underneath it. Returns how many keys now know their real identity.
    public int ResolvedIdentities(IReadOnlyDictionary<string, string> resolved)
    {
        int settled = 0;
        foreach ((string key, string identity) in resolved)
        {
            if (identity.Length == 0 || !_provisionalIdentity.Remove(key))
                continue;
            _materialIdentity[key] = identity;
            settled++;
        }
        return settled;
    }

    public void Register(string textureKey, Material material)
    {
        if (textureKey.Length == 0)
            return;
        if (!_pending.TryGetValue(textureKey, out List<Material>? mats))
            _pending[textureKey] = mats = new List<Material>();
        if (!mats.Contains(material))
            mats.Add(material);
    }

    // Adds a material to a key that is still waiting for its texture, used when re-deduplication points a
    // surface at another key's material. Deliberately not Register: a key whose texture has already been
    // applied is done with, and re-registering it would resurrect a pending entry nothing will ever drain.
    public void RegisterAlias(string textureKey, Material material)
    {
        if (_pending.TryGetValue(textureKey, out List<Material>? mats) && !mats.Contains(material))
            mats.Add(material);
    }

    // Applies the texture for a key to every material registered under it. Loads the .tex (creating a GPU
    // ImageTexture), so it MUST run on the main thread. Returns true if a texture was applied.
    public bool Apply(string textureKey)
    {
        if (!_pending.TryGetValue(textureKey, out List<Material>? mats))
            return false;
        (ImageTexture? tex, int filterMode) = Load(textureKey);
        if (tex == null)
            return false;
        foreach (Material material in mats)
        {
            // Unturned's tiny palette textures are point-filtered (FilterMode.Point): the mesh UVs park on
            // solid texels, and bilinear/aniso sampling bleeds neighbouring palette colors into smears
            // (a stop sign's face mottling). Nearest+mips reproduces Unity's Point mode.
            switch (material)
            {
                case StandardMaterial3D standard:
                    standard.AlbedoTexture = tex;
                    if (filterMode == 0)
                        standard.TextureFilter = BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps;
                    break;
                case ShaderMaterial cutout: // the foliage-family cutout shader (ModelLibrary.CutoutShader)
                    if (filterMode == 0)
                        cutout.Shader = ModelLibrary.CutoutAsNearest(cutout.Shader);
                    cutout.SetShaderParameter("albedo_texture", tex);
                    break;
            }
        }
        _pending.Remove(textureKey);
        return true;
    }

    // Applies every pending key whose .tex is already on disk (warm load, or catch-up). Returns the count.
    public int ApplyAllAvailable()
    {
        int applied = 0;
        foreach (string key in new List<string>(_pending.Keys))
            if (Apply(key))
                applied++;
        return applied;
    }

    // Once loading/streaming has finished, materials own the ImageTextures they use. These lookup tables
    // are only acceleration/index state and otherwise retain every material list and texture for the whole
    // play session. Returns the number of entries released for profiling diagnostics.
    public int ReleaseLoadingIndexes()
    {
        int entries = _pending.Count + _loaded.Count + _imagesByContent.Count + _materialIdentity.Count;
        _pending.Clear();
        _loaded.Clear();
        _imagesByContent.Clear();
        _materialIdentity.Clear();
        _provisionalIdentity.Clear();
        return entries;
    }

    private (ImageTexture? tex, int filterMode) Load(string textureKey)
    {
        if (_loaded.TryGetValue(textureKey, out (ImageTexture? tex, int filterMode) cached))
            return cached;

        (ImageTexture? tex, int filterMode) loaded = (null, 1);
        string path = Path.Combine(_textureCacheDir, textureKey + ".tex");
        if (IsCommitted(path)) // stale formats and half-written files re-extract/retry on the next pass
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                CachedTexture ct = CachedTexture.Decoded(TextureCache.Read(stream));
                string content = DeduplicateGpu
                    ? ExactContentKey.Image(ct.Format, ct.Width, ct.Height, ct.MipCount, ct.Pixels)
                    : textureKey;
                if (!_imagesByContent.TryGetValue(content, out ImageTexture? image))
                {
                    image = ModelLibrary.BuildTexture(ct);
                    if (image != null)
                        _imagesByContent[content] = image;
                }
                loaded = (image, ct.FilterMode);
            }
            // The writer holds its .tex with FileShare.None, so on Windows the file can become
            // unopenable between the check above and the open here. Nothing about this key is decided
            // by that: it stays pending and the next frame's Apply tries again.
            catch (System.Exception e) when (e is IOException or System.UnauthorizedAccessException
                or InvalidDataException)
            {
                loaded = (null, 1);
            }
        }
        // A miss during cold streaming is temporary: the mesh phase runs before most of the .resS tail
        // has been written. Caching null here made the later ready notification reuse the miss forever.
        // Successful GPU resources are stable and remain worth caching; absent/invalid files are retried.
        if (loaded.tex != null)
            _loaded[textureKey] = loaded;
        return loaded;
    }

    // Whether a cached texture is finished being written, not merely started.
    //
    // The magic alone cannot answer that: it is the FIRST four bytes the writer emits, so a .tex the
    // extraction worker is part-way through passes TextureCache.IsCurrent while the payload behind the
    // header is short. TextureCache.Read then hands back a byte[] shorter than its declared length —
    // ReadBytes does not throw on a short read — and Image.CreateFromData rejects it, which on a cold
    // load turns into engine error spam and a wasted decode for every key ApplyAllAvailable happens to
    // sweep mid-write. (ApplyAllAvailable sweeps every pending key, not just the ones the worker has
    // signalled, and OnMeshesExtractedAsync calls it while the texture tail is still streaming.)
    //
    // The extraction already built the proof: TextureCache.RecordSource writes a `.source` sidecar
    // atomically AFTER the payload, precisely so a reader can tell a committed entry from one in flight
    // (see ModelExtractor.AlreadyCached). Both texture-cache writers record it, and
    // TextureDependencyIndex.MissingTextureIds treats a .tex without a current sidecar as owed — so the
    // set this admits is exactly the set the extraction plan considers already present.
    private static bool IsCommitted(string texturePath) =>
        File.Exists(TextureCache.SourcePath(texturePath)) && TextureCache.IsCurrent(texturePath);
}
