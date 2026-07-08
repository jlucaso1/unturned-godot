using System;
using UnturnedGodot.Dat;

namespace UnturnedGodot.Assets;

public enum EObjectType { Small, Medium, Large, Npc, Decal, Unknown }

// Mirrors how Unturned's Assets loader pulls GUID/ID/Type off an object .dat (Assets.cs, ObjectAsset.cs).
public sealed class ObjectAsset
{
    public Guid Guid { get; }
    public ushort Id { get; }
    public EObjectType Type { get; }
    public string RawType { get; }
    public string? Name { get; }

    // Folder holding the .dat, used to match the object to its prefab path in the bundle.
    public string Directory { get; set; } = string.Empty;

    private ObjectAsset(Guid guid, ushort id, EObjectType type, string rawType, string? name)
    {
        Guid = guid;
        Id = id;
        Type = type;
        RawType = rawType;
        Name = name;
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
        string? name = localizedName ?? data.GetString("Name");

        asset = new ObjectAsset(guid, id, ClassifyType(rawType), rawType, name);
        return true;
    }

    public static EObjectType ClassifyType(string rawType) => rawType.ToLowerInvariant() switch
    {
        "small" => EObjectType.Small,
        "medium" => EObjectType.Medium,
        "large" => EObjectType.Large,
        "npc" => EObjectType.Npc,
        "decal" => EObjectType.Decal,
        _ => EObjectType.Unknown,
    };
}
