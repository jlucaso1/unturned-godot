using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;

namespace UnturnedGodot.Assets;

// Ports RoadAsset (Unturned/Bundles/RoadAsset.cs), the modern replacement for a numbered entry in a map's
// Environment/Roads.dat.
//
// From Paths.dat version 6 a road may name one of these by GUID instead of indexing the legacy table, and
// the two describe the same road with different numbers — so a road that names an asset and is drawn from
// the table instead comes out the wrong width, at the wrong height, tiled wrong. The game ships 16 of
// them under Bundles/Assets/Roads.
//
// The field names are a trap worth naming once, because getting it wrong is a silent factor of two.
// Roads.dat's "width" and "depth" are ALREADY halved — Road.cs:774 assigns them straight to halfWidth and
// halfVerticalSize — whereas RoadAsset's Width and Depth are the full sizes and Road.cs:758 halves them
// on the way in. ToMaterialConfig does that halving, which is what lets both kinds of road go through the
// one RoadMesh port unchanged.
public sealed class RoadAsset
{
    public Guid Guid { get; }

    // Full width across the road, in metres, before it tapers into the terrain.
    public float Width { get; }

    // Full size along the "up" axis.
    public float Depth { get; }

    // How far each vertex moves along the terrain surface normal.
    public float OffsetAlongNormal { get; }

    // Multiplier for how far along the road before the texture repeats. Defaults to 1; 10 of the 16
    // shipped assets set it, most of them to 4.
    public float RepeatDistanceScale { get; }

    // "TexturePath", e.g. "Roads/PEI_Trail.png" — a path inside the asset's master bundle rather than a
    // file beside the .asset, which is why nothing here loads it.
    public string? TexturePath { get; }

    // The Resources path Unturned loads the road's PhysicMaterial from, e.g. "Gravel_Static".
    public string? VanillaPhysicsMaterial { get; }

    public const float DefaultRepeatDistanceScale = 1f;

    // The legacy table's one-bit surface toggle, reconstructed from the physics material so a road that
    // names an asset can still pick the paved-or-dirt fallback shader. Strictly a narrowing: the asset
    // names a whole PhysicMaterial and the flag is a boolean, so this only answers the question the
    // fallback asks. All 16 shipped assets name Concrete_Static (10) or Gravel_Static (6).
    public bool IsConcrete =>
        VanillaPhysicsMaterial?.StartsWith("Concrete", StringComparison.OrdinalIgnoreCase) ?? true;

    private RoadAsset(Guid guid, float width, float depth, float offsetAlongNormal,
        float repeatDistanceScale, string? texturePath, string? vanillaPhysicsMaterial)
    {
        Guid = guid;
        Width = width;
        Depth = depth;
        OffsetAlongNormal = offsetAlongNormal;
        RepeatDistanceScale = repeatDistanceScale;
        TexturePath = texturePath;
        VanillaPhysicsMaterial = vanillaPhysicsMaterial;
    }

    public static bool TryParse(DatDictionary root, [MaybeNullWhen(false)] out RoadAsset asset)
    {
        DatDictionary guidSource = root.TryGetDictionary("Metadata", out DatDictionary? md) ? md : root;
        DatDictionary data = root.TryGetDictionary("Asset", out DatDictionary? a) ? a : root;

        // Only a "Road" asset, and only one carrying a GUID: the same folder tree holds landscapes,
        // material palettes and weather, and a road placement resolving onto one of those would hand a
        // width and a depth to something that has neither.
        if (!guidSource.TryGetGuid("GUID", out Guid guid)
            || !string.Equals(data.GetString("Type") ?? guidSource.GetString("Type"), "Road",
                StringComparison.OrdinalIgnoreCase))
        {
            asset = null;
            return false;
        }

        data.TryGetSingle("Width", out float width);
        data.TryGetSingle("Depth", out float depth);
        data.TryGetSingle("OffsetAlongNormal", out float offset);
        float repeat = data.TryGetSingle("RepeatDistanceScale", out float scale)
            ? scale
            : DefaultRepeatDistanceScale;

        asset = new RoadAsset(guid, width, depth, offset, repeat, data.GetString("TexturePath"),
            data.GetString("VanillaPhysicsMaterial"));
        return true;
    }

    // The asset expressed in the legacy table's own units, so both kinds of road reach RoadMesh through
    // one path. Width and Depth are halved here for the reason in the type comment; Height is left at
    // zero because it only ever fed the legacy texture-repeat divisor, and an asset computes its repeat
    // by its own formula below instead.
    public RoadMaterialConfig ToMaterialConfig() =>
        new(Width * 0.5f, height: 0f, Depth * 0.5f, OffsetAlongNormal, IsConcrete);

    // Road.cs:802 — `Width * (texture.height / texture.width) * RepeatDistanceScale`, inverted. The v
    // coordinate advances by the return value per metre of road, so the texture's own aspect decides how
    // often it tiles rather than a distance authored in the file. A texture with no size to ask (none
    // loaded) falls back to 1, exactly as the source does.
    public float InverseTextureRepeatDistance(int textureWidth, int textureHeight)
    {
        if (textureWidth <= 0 || textureHeight <= 0)
            return 1f;

        float distance = Width * ((float)textureHeight / textureWidth) * RepeatDistanceScale;
        return distance > 0f ? 1f / distance : 1f;
    }
}

// The road assets an install carries, by GUID. Tiny next to the object database — the game ships 16 —
// so it is scanned per load rather than cached.
public sealed class RoadAssetDatabase
{
    private readonly Dictionary<Guid, RoadAsset> _byGuid = new();

    public int Count => _byGuid.Count;

    public RoadAsset? ResolveByGuid(Guid guid) =>
        guid != Guid.Empty && _byGuid.TryGetValue(guid, out RoadAsset? a) ? a : null;

    public void Add(RoadAsset asset) => _byGuid[asset.Guid] = asset;

    // Every road asset reachable from an install, its own and any workshop source's: a workshop map that
    // ships custom roads keeps them beside its own bundle.
    public static RoadAssetDatabase ScanSources(IReadOnlyList<ContentSource> sources)
    {
        var db = new RoadAssetDatabase();
        foreach (ContentSource source in sources)
            db.ScanDirectory(source.AssetsDir);
        return db;
    }

    // Road assets are written as ".asset" rather than ".dat", like the rest of Bundles/Assets.
    public void ScanDirectory(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return;

        foreach (string path in Directory.EnumerateFiles(root, "*.asset", SearchOption.AllDirectories))
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (IOException)
            {
                continue; // a file that cannot be read costs its road's shape, not the whole map
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (RoadAsset.TryParse(DatParser.Parse(text), out RoadAsset? asset))
                Add(asset);
        }
    }
}
