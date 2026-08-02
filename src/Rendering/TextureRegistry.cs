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
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class TextureRegistry
{
    private static readonly bool DeduplicateGpu = EnvFlag.IsOn(System.Environment.GetEnvironmentVariable("UG_DEDUP_GPU"), whenUnset: true);
    private readonly string _textureCacheDir;
    private readonly Dictionary<string, List<Material>> _pending = new();
    private readonly Dictionary<string, (ImageTexture? tex, int filterMode)> _loaded = new();
    private readonly Dictionary<string, ImageTexture> _imagesByContent = new();
    private readonly Dictionary<string, string> _materialIdentity = new();
    private static readonly bool DeduplicateMaterials =
        EnvFlag.IsOn(System.Environment.GetEnvironmentVariable("UG_DEDUP_MATERIAL_CONTENT"), whenUnset: true);

    public TextureRegistry(string textureCacheDir) => _textureCacheDir = textureCacheDir;

    public int PendingKeyCount => _pending.Count;
    public int MaterialAliasCount => _materialIdentity.Count
        - new HashSet<string>(_materialIdentity.Values, System.StringComparer.Ordinal).Count;

    // A material may use a different cache key for byte-identical texture data. The complete .tex file
    // includes format, dimensions, mip count, filter mode and pixels, so its exact hash is a safe material
    // discriminator and never merges two textures whose sampling or visual result differs.
    public string MaterialIdentity(string textureKey)
    {
        if (!DeduplicateMaterials || textureKey.Length == 0)
            return textureKey;
        if (_materialIdentity.TryGetValue(textureKey, out string? identity))
            return identity;
        identity = textureKey;
        string path = Path.Combine(_textureCacheDir, textureKey + ".tex");
        try
        {
            if (File.Exists(path) && TextureCache.IsCurrent(path))
                identity = ExactContentKey.File(path);
        }
        catch (IOException) { }
        _materialIdentity[textureKey] = identity;
        return identity;
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
                    cutout.SetShaderParameter("has_texture", true);
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
        return entries;
    }

    private (ImageTexture? tex, int filterMode) Load(string textureKey)
    {
        if (_loaded.TryGetValue(textureKey, out (ImageTexture? tex, int filterMode) cached))
            return cached;

        (ImageTexture? tex, int filterMode) loaded = (null, 1);
        string path = Path.Combine(_textureCacheDir, textureKey + ".tex");
        if (File.Exists(path) && TextureCache.IsCurrent(path)) // stale formats re-extract on the next pass
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
        // A miss during cold streaming is temporary: the mesh phase runs before most of the .resS tail
        // has been written. Caching null here made the later ready notification reuse the miss forever.
        // Successful GPU resources are stable and remain worth caching; absent/invalid files are retried.
        if (loaded.tex != null)
            _loaded[textureKey] = loaded;
        return loaded;
    }
}
