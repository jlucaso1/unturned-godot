using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Extracts the game's real skybox data from resources.assets: the Level/Skybox material's authored scalars
// (sun-disc thresholds, moon color and radius, cloud contrast params) plus the stars and clouds textures it
// references (pixels in resources.assets.resS). resources.assets ships with its type trees stripped, so the
// masterbundle's per-class trees decode it (same Unity build) — the same path CharacterModel takes.
public sealed class SkyboxAssets
{
    private const int ClassMaterial = 21;
    private const int ClassTexture2D = 28;

    public required float SunInnerThreshold { get; init; }
    public required float SunOuterThreshold { get; init; }
    public required float SqrMoonRadius { get; init; }
    public required Color MoonColor { get; init; }
    public required Color CloudParams { get; init; } // R = macro alpha cutoff, G = contrast saturation
    public ImageTexture? Stars { get; init; }
    public ImageTexture? Clouds { get; init; }

    public static SkyboxAssets? Load(string unturnedPath)
    {
        try
        {
            return LoadInternal(unturnedPath);
        }
        catch (Exception e)
        {
            Log.PrintErr($"[unturned-godot] Skybox: failed to read the game's sky assets " +
                $"({e.GetType().Name}: {e.Message}); the sky falls back to the plain gradient.");
            return null;
        }
    }

    private static SkyboxAssets? LoadInternal(string unturnedPath)
    {
        string? assetsPath = UnturnedInstall.FindDataFile(unturnedPath, "resources.assets");
        string? bundlePath = UnturnedInstall.FindMasterBundle(unturnedPath);
        if (assetsPath == null || bundlePath == null)
            return null;

        IReadOnlyDictionary<int, List<TypeTreeNode>> trees = ModelExtractor.ReadClassTypeTrees(bundlePath);
        SerializedFile file = SerializedFile.Read(File.ReadAllBytes(assetsPath), trees);

        // Find the Level/Skybox material: the one whose saved properties carry the sky-only _StarsCutoff.
        Dictionary<string, object>? material = null;
        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != ClassMaterial)
                continue;
            Dictionary<string, object> m = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
            if ((string)m["m_Name"] == "Skybox" && UnityMaterial.GetFloat(m, "_StarsCutoff") != null)
            {
                material = m;
                break;
            }
        }
        if (material == null)
            return null;

        (_, long starsId) = UnityMaterial.GetTexture(material, "_StarsTexture");
        (_, long cloudsId) = UnityMaterial.GetTexture(material, "_CloudsTexture");

        return new SkyboxAssets
        {
            SunInnerThreshold = UnityMaterial.GetFloat(material, "_SunInnerThreshold") ?? 0.995f,
            SunOuterThreshold = UnityMaterial.GetFloat(material, "_SunOuterThreshold") ?? 0.993f,
            SqrMoonRadius = UnityMaterial.GetFloat(material, "_SqrMoonRadius") ?? 0.01f,
            MoonColor = UnityMaterial.GetColor(material, "_MoonColor") ?? new Color(0.749f, 0.804f, 0.808f),
            CloudParams = UnityMaterial.GetColor(material, "_CloudParams") ?? new Color(0.6f, 10f, 0f, 0f),
            Stars = DecodeTexture(file, starsId, assetsPath),
            Clouds = DecodeTexture(file, cloudsId, assetsPath),
        };
    }

    private static ImageTexture? DecodeTexture(SerializedFile file, long pathId, string assetsPath)
    {
        if (pathId == 0)
            return null;
        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != ClassTexture2D || o.PathId != pathId)
                continue;
            UnityTexture tex = UnityTexture.Read(TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o)));
            // The player's stream file sits next to resources.assets (e.g. "resources.assets.resS").
            byte[]? pixels = tex.GetPixels(name =>
            {
                string path = Path.Combine(Path.GetDirectoryName(assetsPath)!, name);
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            });
            if (pixels == null || pixels.Length == 0)
                return null;
            return ModelLibrary.BuildTexture(CachedTexture.From(tex, pixels));
        }
        return null;
    }
}
