using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Loads a map's terrain layer textures (Terrain/Materials.unity3d — the same legacy UnityRaw bundle format
// as Roads.unity3d) keyed by name: Dirt, Grass, Sand, Road, Stone, Gravel, Farm, Snow. The splatmap blends
// these per layer. Small (~180 KB, uncompressed), so read at build time rather than cached.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class TerrainTextures
{
    public static Dictionary<string, ImageTexture> Load(string terrainDir)
    {
        var result = new Dictionary<string, ImageTexture>(StringComparer.OrdinalIgnoreCase);
        string bundlePath = Path.Combine(terrainDir, "Materials.unity3d");
        if (!File.Exists(bundlePath))
            return result;

        byte[] data = File.ReadAllBytes(bundlePath);
        if (!UnityRawBundle.IsRaw(data))
            return result;

        UnityRawBundle raw = UnityRawBundle.Read(data);
        byte[]? sfBytes = null;
        foreach (KeyValuePair<string, byte[]> f in raw.Files)
            sfBytes = f.Value; // one CAB entry: the SerializedFile
        if (sfBytes == null)
            return result;

        SerializedFile file = SerializedFile.Read(sfBytes);
        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != 28) // Texture2D
                continue;
            UnityTexture tex = UnityTexture.Read(TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o)));
            byte[]? pixels = tex.GetPixels(_ => null); // inline data (UnityRaw has no .resS stream)
            if (pixels == null || pixels.Length == 0 || tex.Name.Length == 0)
                continue;
            ImageTexture? image = ModelLibrary.BuildTexture(
                new CachedTexture(tex.Format, tex.Width, tex.Height, tex.MipCount, pixels));
            if (image != null)
                result[tex.Name] = image;
        }
        return result;
    }
}
