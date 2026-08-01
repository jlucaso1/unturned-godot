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
        IReadOnlyDictionary<string, byte[]> files = UnityRawBundle.IsRaw(data)
            ? UnityRawBundle.Read(data).Files
            : UnityBundle.Read(data).Files;

        byte[]? sfBytes = null;
        foreach (KeyValuePair<string, byte[]> f in files)
            if (!f.Key.EndsWith(".resS", StringComparison.Ordinal)
                && !f.Key.EndsWith(".resource", StringComparison.Ordinal))
            {
                sfBytes = f.Value;
            }

        if (sfBytes == null)
            return;

        SerializedFile file = SerializedFile.Read(sfBytes);
        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != 28) // Texture2D
                continue;

            UnityTexture tex = UnityTexture.Read(TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o)));
            byte[]? pixels = tex.GetPixels(name => ResolveStream(files, name));
            if (pixels == null || pixels.Length == 0 || tex.Name.Length == 0)
                continue;

            ImageTexture? image = ModelLibrary.BuildTexture(
                CachedTexture.From(tex, pixels));
            if (image != null)
                result[tex.Name] = image;
        }
    }

    // UnityFS textures point at a stream file by path; inside a map bundle that entry sits next to the
    // SerializedFile, so match on the file name alone.
    private static byte[]? ResolveStream(IReadOnlyDictionary<string, byte[]> files, string streamPath)
    {
        if (files.TryGetValue(streamPath, out byte[]? exact))
            return exact;

        string name = Path.GetFileName(streamPath);
        foreach (KeyValuePair<string, byte[]> f in files)
            if (Path.GetFileName(f.Key).Equals(name, StringComparison.OrdinalIgnoreCase))
                return f.Value;

        return null;
    }
}
