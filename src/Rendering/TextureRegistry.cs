using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Maps each texture key to the materials that want it, so textures can be applied to an already-rendered
// scene as they arrive (cold-load streaming): meshes show flat palette colors first, then textures fill
// in. Building ImageTextures and assigning them is a main-thread operation, so the registry is only ever
// touched from the main thread — background extraction just signals which keys became available.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class TextureRegistry
{
    private readonly string _textureCacheDir;
    private readonly Dictionary<string, List<Material>> _pending = new();
    private readonly Dictionary<string, (ImageTexture? tex, int filterMode)> _loaded = new();

    public TextureRegistry(string textureCacheDir) => _textureCacheDir = textureCacheDir;

    public int PendingKeyCount => _pending.Count;

    public void Register(string textureKey, Material material)
    {
        if (textureKey.Length == 0)
            return;
        if (!_pending.TryGetValue(textureKey, out List<Material>? mats))
            _pending[textureKey] = mats = new List<Material>();
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

    private (ImageTexture? tex, int filterMode) Load(string textureKey)
    {
        if (_loaded.TryGetValue(textureKey, out (ImageTexture? tex, int filterMode) cached))
            return cached;

        (ImageTexture? tex, int filterMode) loaded = (null, 1);
        string path = Path.Combine(_textureCacheDir, textureKey + ".tex");
        if (File.Exists(path) && TextureCache.IsCurrent(path)) // stale formats re-extract on the next pass
        {
            using FileStream stream = File.OpenRead(path);
            CachedTexture ct = TextureCache.Read(stream);
            loaded = (ModelLibrary.BuildTexture(ct), ct.FilterMode);
        }
        _loaded[textureKey] = loaded;
        return loaded;
    }
}
