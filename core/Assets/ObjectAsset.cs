using System;
using System.Diagnostics.CodeAnalysis;
using UnturnedGodot.Dat;

namespace UnturnedGodot.Assets;

public enum EObjectType { Small, Medium, Large, Npc, Decal, Resource, Vehicle, VehicleRedirector, Unknown }

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

    // VehicleRedirector only: the vehicle this entry stands in for. Redirectors are what a map's vehicle
    // tables and the spawn tables actually reference — nearly every legacy vehicle id today belongs to one
    // — and they carry no prefab of their own, only a paint colour and the vehicle to spawn instead.
    public Guid RedirectTargetGuid { get; }

    // Folder holding the .dat, used to match the object to its prefab path in the bundle.
    public string Directory { get; set; } = string.Empty;

    private ObjectAsset(Guid guid, ushort id, EObjectType type, string rawType, string? explicitName,
        string? dataName, string? bundleOverridePath, Guid materialPaletteGuid, Guid redirectTargetGuid)
    {
        Guid = guid;
        Id = id;
        Type = type;
        RawType = rawType;
        _explicitName = explicitName;
        _dataName = dataName;
        BundleOverridePath = bundleOverridePath;
        MaterialPaletteGuid = materialPaletteGuid;
        RedirectTargetGuid = redirectTargetGuid;
    }

    // A v2 file wraps identity in a "Metadata" block; v1 keeps it at the root (optionally under "Asset").
    public static bool TryParse(DatDictionary root, string? localizedName,
        [MaybeNullWhen(false)] out ObjectAsset asset)
    {
        DatDictionary guidSource = root.TryGetDictionary("Metadata", out var md) ? md : root;
        DatDictionary data = root.TryGetDictionary("Asset", out var a) ? a : root;

        if (!guidSource.TryGetGuid("GUID", out Guid guid))
        {
            asset = null;
            return false;
        }

        data.TryGetUInt16("ID", out ushort id);
        // A v2 file carries two types: the editor category in the data block ("Large") and the runtime
        // class in the metadata ("SDG.Unturned.ObjectAsset"). The category wins where there is one, and
        // the class is the fallback — which is how a vehicle or a redirector, neither of which has a
        // category, is recognised.
        string rawType = data.GetString("Type") ?? guidSource.GetString("Type") ?? string.Empty;
        string? overridePath = data.GetString("Bundle_Override_Path");
        data.TryGetGuid("Material_Palette", out Guid paletteGuid);
        data.TryGetGuid("TargetVehicle", out Guid redirectTarget);

        asset = new ObjectAsset(guid, id, ClassifyType(rawType), rawType, localizedName,
            data.GetString("Name"), overridePath, paletteGuid, redirectTarget);
        return true;
    }

    public static EObjectType ClassifyType(string rawType)
    {
        // Vehicles and their redirectors name their runtime class in full ("SDG.Unturned.VehicleRedirector
        // Asset, Assembly-CSharp, ..."), so they are matched on substring rather than by the short editor
        // names below. The game's own vehicles use the short "Vehicle" and fall through to the switch.
        if (rawType.Contains("VehicleRedirectorAsset", StringComparison.Ordinal))
            return EObjectType.VehicleRedirector;
        if (rawType.Contains("VehicleAsset", StringComparison.Ordinal))
            return EObjectType.Vehicle;
        return ClassifyEditorType(rawType);
    }

    private static EObjectType ClassifyEditorType(string rawType) => rawType.ToLowerInvariant() switch
    {
        "small" => EObjectType.Small,
        "medium" => EObjectType.Medium,
        "large" => EObjectType.Large,
        "npc" => EObjectType.Npc,
        "decal" => EObjectType.Decal,
        // Bundles/Trees assets (trees, rocks, bushes) — Unturned routes "Resource" .dats to ResourceAsset,
        // a separate class from ObjectAsset; this minimal port keeps them in the same table, tagged.
        "resource" => EObjectType.Resource,
        // Bundles/Vehicles, same compromise: VehicleAsset is its own class in Unturned, but everything
        // downstream here — the bundle plan, the extraction, the mesh cache — is keyed on the GUID, which
        // is global. Only the legacy 16-bit id is per-category, and ObjectAssetDatabase indexes that
        // separately so a vehicle cannot answer a placed object's id.
        "vehicle" => EObjectType.Vehicle,
        _ => EObjectType.Unknown,
    };
}
