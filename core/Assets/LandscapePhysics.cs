using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Dat;

namespace UnturnedGodot.Assets;

// Maps each LandscapeMaterialAsset (a terrain splat layer) to its physic-material NAME, mirroring
// LandscapeMaterialAsset.physicsMaterialName: the .asset's Physics_Material field is either already a
// modern name or a legacy EPhysicsMaterial enum value that PhysicsTool.GetNameOfLegacyMaterial converts.
public sealed class LandscapePhysics
{
    private readonly Dictionary<Guid, string> _nameByGuid = new();

    public int Count => _nameByGuid.Count;

    public string? PhysicsNameOf(Guid materialGuid) =>
        _nameByGuid.TryGetValue(materialGuid, out string? name) ? name : null;

    public void Add(Guid guid, string physicsName) => _nameByGuid.TryAdd(guid, physicsName);

    // Every landscape material across several asset trees: the game's own plus each installed workshop
    // mod, since a workshop map's terrain layers are defined by its mod, not by the game.
    public static LandscapePhysics ScanDirectories(IEnumerable<string> roots)
        => ScanDirectories(roots, ScanDirectory);

    internal static LandscapePhysics ScanDirectories(IEnumerable<string> roots,
        Func<string, LandscapePhysics> scanDirectory)
    {
        var merged = new LandscapePhysics();
        foreach (string root in roots)
        {
            LandscapePhysics scanned;
            try { scanned = scanDirectory(root); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (KeyValuePair<Guid, string> entry in scanned._nameByGuid)
                merged._nameByGuid.TryAdd(entry.Key, entry.Value);
        }

        return merged;
    }

    public static LandscapePhysics ScanDirectory(string root)
    {
        var result = new LandscapePhysics();
        if (!Directory.Exists(root))
            return result;
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.asset", SearchOption.AllDirectories))
            {
                DatDictionary parsed;
                try { parsed = DatParser.Parse(File.ReadAllText(file)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

                if (!parsed.TryGetDictionary("Metadata", out var meta) ||
                    !parsed.TryGetDictionary("Asset", out var data) ||
                    !meta.TryGetGuid("GUID", out Guid guid))
                    continue;
                string type = meta.GetString("Type") ?? string.Empty;
                if (!type.Contains("LandscapeMaterialAsset", StringComparison.Ordinal))
                    continue;

                string? raw = data.GetString("Physics_Material");
                if (raw is not { Length: > 0 })
                    continue;
                string? name = ResolveName(raw);
                if (name != null)
                    result.Add(guid, name);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return result;
    }

    // PhysicsTool.GetNameOfLegacyMaterial's table; a value outside it is already the modern
    // physic-material name and passes through unchanged. NONE maps to null (no material).
    private static readonly Dictionary<string, string?> LegacyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NONE"] = null,
        ["CLOTH_DYNAMIC"] = "Cloth",
        ["CLOTH_STATIC"] = "Cloth",
        ["TILE_DYNAMIC"] = "Tile",
        ["TILE_STATIC"] = "Tile",
        ["CONCRETE_DYNAMIC"] = "Concrete",
        ["CONCRETE_STATIC"] = "Concrete",
        ["FLESH_DYNAMIC"] = "Flesh",
        ["GRAVEL_DYNAMIC"] = "Gravel",
        ["GRAVEL_STATIC"] = "Gravel",
        ["METAL_DYNAMIC"] = "Metal",
        ["METAL_STATIC"] = "Metal",
        ["METAL_SLIP"] = "Metal",
        ["WOOD_DYNAMIC"] = "Wood",
        ["WOOD_STATIC"] = "Wood",
        ["FOLIAGE_STATIC"] = "Foliage",
        ["FOLIAGE_DYNAMIC"] = "Foliage",
        ["SNOW_STATIC"] = "Snow",
        ["ICE_STATIC"] = "Ice",
        ["WATER_STATIC"] = "Water",
        ["ALIEN_DYNAMIC"] = "Alien",
        ["SAND_STATIC"] = "Sand",
    };

    public static string? ResolveName(string raw) =>
        LegacyNames.TryGetValue(raw, out string? mapped) ? mapped : raw;
}
