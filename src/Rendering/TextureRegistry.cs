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
    private readonly Dictionary<string, List<StandardMaterial3D>> _pending = new();
    private readonly Dictionary<string, ImageTexture?> _loaded = new();

    public TextureRegistry(string textureCacheDir) => _textureCacheDir = textureCacheDir;

    public int PendingKeyCount => _pending.Count;

    public void Register(string textureKey, StandardMaterial3D material)
    {
        if (textureKey.Length == 0)
            return;
        if (!_pending.TryGetValue(textureKey, out List<StandardMaterial3D>? mats))
            _pending[textureKey] = mats = new List<StandardMaterial3D>();
        mats.Add(material);
    }

    // Applies the texture for a key to every material registered under it. Loads the .tex (creating a GPU
    // ImageTexture), so it MUST run on the main thread. Returns true if a texture was applied.
    public bool Apply(string textureKey)
    {
        if (!_pending.TryGetValue(textureKey, out List<StandardMaterial3D>? mats))
            return false;
        ImageTexture? tex = Load(textureKey);
        if (tex == null)
            return false;
        foreach (StandardMaterial3D material in mats)
            material.AlbedoTexture = tex;
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

    private ImageTexture? Load(string textureKey)
    {
        if (_loaded.TryGetValue(textureKey, out ImageTexture? cached))
            return cached;

        ImageTexture? tex = null;
        string path = Path.Combine(_textureCacheDir, textureKey + ".tex");
        if (File.Exists(path))
        {
            using FileStream stream = File.OpenRead(path);
            tex = ModelLibrary.BuildTexture(TextureCache.Read(stream));
        }
        _loaded[textureKey] = tex;
        return tex;
    }
}
