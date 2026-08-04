using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Loads a map's terrain layer textures (Terrain/Materials.unity3d) keyed by name: Dirt, Grass, Sand, Road,
// Stone, Gravel, Farm, Snow. The splatmap blends these per layer. Small (~50-180 KB), so read at build time
// rather than cached.
//
// Two container formats ship in the wild and both are read here: the legacy uncompressed UnityRaw (PEI,
// Russia, Germany) and UnityFS (Washington, Yukon, and workshop maps), whose pixels can live in a .resS
// entry inside the same bundle.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class TerrainTextures
{
    public static Dictionary<string, ImageTexture> Load(string terrainDir)
    {
        var result = new Dictionary<string, ImageTexture>(StringComparer.OrdinalIgnoreCase);
        string bundlePath = Path.Combine(terrainDir, "Materials.unity3d");
        if (!File.Exists(bundlePath))
            return result;

        try
        {
            ReadInto(result, File.ReadAllBytes(bundlePath));
        }
        catch (Exception e)
        {
            // A map whose bundle this reader does not decode still builds, with flat layer colors.
            Log.Print($"[unturned-godot] Terrain textures unavailable ({e.GetType().Name}: {e.Message}).");
        }

        return result;
    }

    private static void ReadInto(Dictionary<string, ImageTexture> result, byte[] data)
    {
        MapBundle? bundle = MapBundle.Read(data);
        if (bundle == null)
            return;

        foreach (SerializedObject o in bundle.Objects)
        {
            if (!bundle.TryReadTexture(o, out UnityTexture tex, out byte[] pixels) || tex.Name.Length == 0)
                continue;

            ImageTexture? image = ModelLibrary.BuildTexture(CachedTexture.From(tex, pixels));
            if (image != null)
                result[tex.Name] = image;
        }
    }
}
