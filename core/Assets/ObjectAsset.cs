using System;
using UnturnedGodot.Dat;

namespace UnturnedGodot.Assets;

public enum EObjectType { Small, Medium, Large, Npc, Decal, Resource, Unknown }

// Mirrors how Unturned's Assets loader pulls GUID/ID/Type off an object .dat (Assets.cs, ObjectAsset.cs).
public sealed class ObjectAsset
{
    public Guid Guid { get; }
    public ushort Id { get; }
    public EObjectType Type { get; }
    public string RawType { get; }

    private readonly string? _explicitName; // localized name passed at parse time, if any
    private readonly string? _dataName;     // the .dat's own "Name" field

    // No production code reads Name, so the localized name is resolved lazily: the folder's English.dat is
    // read only if Name is actually accessed (tests), not eagerly for all ~2500 assets during a scan.
    public string? Name =>
        _explicitName
        ?? (Directory.Length > 0 ? ObjectAssetDatabase.ReadLocalizedName(Directory) : null)
        ?? _dataName;

    // Holiday/variant objects reuse a base object's mesh via this path (e.g. "/Objects/.../Grave_0").
    public string? BundleOverridePath { get; }

    // Palette of materials (by GUID) whose textures the object's submeshes use.
    public Guid MaterialPaletteGuid { get; }

    // Folder holding the .dat, used to match the object to its prefab path in the bundle.
    public string Directory { get; set; } = string.Empty;

    private ObjectAsset(Guid guid, ushort id, EObjectType type, string rawType, string? explicitName,
        string? dataName, string? bundleOverridePath, Guid materialPaletteGuid)
    {
        Guid = guid;
        Id = id;
        Type = type;
        RawType = rawType;
        _explicitName = explicitName;
        _dataName = dataName;
        BundleOverridePath = bundleOverridePath;
        MaterialPaletteGuid = materialPaletteGuid;
    }

    // A v2 file wraps identity in a "Metadata" block; v1 keeps it at the root (optionally under "Asset").
    public static bool TryParse(DatDictionary root, string? localizedName, out ObjectAsset asset)
    {
        DatDictionary guidSource = root.TryGetDictionary("Metadata", out DatDictionary md) ? md : root;
        DatDictionary data = root.TryGetDictionary("Asset", out DatDictionary a) ? a : root;

        if (!guidSource.TryGetGuid("GUID", out Guid guid))
        {
            asset = null!;
            return false;
        }

        data.TryGetUInt16("ID", out ushort id);
        string rawType = data.GetString("Type") ?? string.Empty;
        string? overridePath = data.GetString("Bundle_Override_Path");
        data.TryGetGuid("Material_Palette", out Guid paletteGuid);

        asset = new ObjectAsset(guid, id, ClassifyType(rawType), rawType, localizedName,
            data.GetString("Name"), overridePath, paletteGuid);
        return true;
    }

    public static EObjectType ClassifyType(string rawType) => rawType.ToLowerInvariant() switch
    {
        "small" => EObjectType.Small,
        "medium" => EObjectType.Medium,
        "large" => EObjectType.Large,
        "npc" => EObjectType.Npc,
        "decal" => EObjectType.Decal,
        // Bundles/Trees assets (trees, rocks, bushes) — Unturned routes "Resource" .dats to ResourceAsset,
        // a separate class from ObjectAsset; this minimal port keeps them in the same table, tagged.
        "resource" => EObjectType.Resource,
        _ => EObjectType.Unknown,
    };
}
