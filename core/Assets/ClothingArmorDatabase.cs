using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;

namespace UnturnedGodot.Assets;

// SDG.Unturned.EItemType, restricted to the clothing kinds. The names are the ones the .dat's `Type`
// key spells, and the four the armor path cares about are HAT, SHIRT, PANTS and VEST — "As of
// 2023-10-09, only hats, shirts, pants, and vests are eligible for player armor, but some items like
// masks have an armor value set" (ItemClothingAsset.cs:142).
public enum EClothingType
{
    Unknown = 0,
    Hat,
    Shirt,
    Pants,
    Vest,
    Backpack,
    Mask,
    Glasses,
}

// One clothing item, reduced to what the damage path reads.
public readonly record struct ClothingArmor(ushort Id, EClothingType Type, float Armor, float ExplosionArmor);

// A DELIBERATELY MINIMAL reader: it walks a content source's tree and keeps, for each clothing item,
// its legacy id, its type and its armor multipliers. Nothing else.
//
// The full item-asset family — meshes, blueprints, calibers, the whole ItemAsset tree — is a separate
// piece of work. This exists because the zombie damage path needs exactly one number that nothing in
// the port could answer: DamageTool.getZombieArmor resolves the clothing a zombie rolled into
// `ItemClothingAsset.armor`, and until this reader the port passed 1 for every limb. THIS SHOULD BE
// ABSORBED BY ItemAssetDatabase when that exists; the shape here is a stand-in, not a design.
public sealed class ClothingArmorDatabase
{
    private readonly Dictionary<ushort, ClothingArmor> _byId = new();

    public int Count => _byId.Count;

    public static readonly ClothingArmorDatabase Empty = new();

    public bool TryGet(ushort id, out ClothingArmor clothing) => _byId.TryGetValue(id, out clothing);

    // ItemClothingAsset.armor, "Multiplier to incoming damage. Defaults to 1.0." An id this database
    // does not know is bare, which is also what the original computes for a zombie whose slot rolled
    // empty — `Assets.find(...) as ItemClothingAsset` returning null falls through to `return 1f`.
    public float ArmorFor(ushort id) => _byId.TryGetValue(id, out ClothingArmor c) ? c.Armor : 1f;

    // Whether this id is a VEST, which is the one type test getZombieArmor's SPINE branch makes: the
    // gear slot only contributes armor when what it rolled is actually a vest.
    public bool IsVest(ushort id) => _byId.TryGetValue(id, out ClothingArmor c) && c.Type == EClothingType.Vest;

    internal void Add(ClothingArmor clothing) => _byId.TryAdd(clothing.Id, clothing);

    // The clothing of every content source. A workshop map ships its own items, and a zombie table on
    // that map names them by the same legacy ids.
    //
    // THE WHOLE SOURCE ROOT, not Root/Items. The game's own Bundles folder does keep clothing under
    // Items/ — in per-kind folders (Shirts, Pants, Hats, Vests, Masks, Glasses, Backpacks) plus
    // per-update folders that mix kinds, which is why a Type key rather than a folder name decides what
    // is clothing — but that layout is a convention of the game's own content, not a rule the loader
    // enforces. A subscribed item is handed to the asset worker as its own directory
    // (Assets.cs:2030-2033) and the worker recurses from there (AssetsWorker.cs:139-177), so a mod is
    // free to put clothing anywhere. Both installed mods here do: one keeps thirty clothing assets in
    // "Clothes/", and even the one that does use Items/ has two more under NPCs/.
    //
    // Measured on the same two sources, so the widening is the only variable: Root/Items found 1474
    // clothing in 141 ms, the whole root found 1476 in 270 ms. The scan runs once when a session comes
    // up, and 130 ms there does not buy a special case that silently loses whatever sits outside the
    // convention.
    public static ClothingArmorDatabase ScanContentSources(IEnumerable<ContentSource> sources)
    {
        var roots = new List<string>();
        foreach (ContentSource source in sources)
            roots.Add(source.Root);
        return ScanDirectories(roots);
    }

    // First claimant wins, roots arriving with the game's own first — the same precedence the object
    // database and the physics material bank use, so a workshop item cannot displace an official id.
    public static ClothingArmorDatabase ScanDirectories(IEnumerable<string> roots)
    {
        var merged = new ClothingArmorDatabase();
        foreach (string root in roots)
            foreach (ClothingArmor clothing in ScanDirectory(root)._byId.Values)
                merged.Add(clothing);
        return merged;
    }

    public static ClothingArmorDatabase ScanDirectory(string root)
    {
        var database = new ClothingArmorDatabase();
        if (!Directory.Exists(root))
            return database;
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.dat", SearchOption.AllDirectories))
            {
                DatDictionary parsed;
                try { parsed = DatParser.Parse(TextFile.ReadAllText(file)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (TryParse(parsed, out ClothingArmor clothing))
                    database.Add(clothing);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return database;
    }

    // ItemClothingAsset.PopulateAsset's armor block, and nothing else from it.
    public static bool TryParse(DatDictionary data, out ClothingArmor clothing)
    {
        clothing = default;

        EClothingType type = ParseType(data.GetString("Type"));
        if (type == EClothingType.Unknown)
            return false;

        // The legacy ushort id is how a zombie table names its clothing (slots hold item ids), so an
        // asset without one cannot be reached from a zombie and is skipped.
        if (!data.TryGetUInt16("ID", out ushort id))
            return false;

        // "if (isPro) { _armor = 1f; _explosionArmor = 1f; }" — a Steam economy item carries no armor
        // whatever its file says, and `isPro` is the bare presence of a `Pro` key (ItemAsset.cs:810).
        if (data.ContainsKey("Pro"))
        {
            clothing = new ClothingArmor(id, type, 1f, 1f);
            return true;
        }

        // "if (ContainsKey("Armor")) _armor = ParseFloat("Armor"); else _armor = 1.0f;" — the presence
        // of the key and the parse of its value are two separate questions in the original, and they
        // disagree for a key that is there but holds nothing: ParseFloat yields 0, while treating a
        // failed parse as "absent" would yield 1. An armor of 0 is total immunity and an armor of 1 is
        // bare, so the two are not a rounding apart.
        float armor = ReadOr(data, "Armor", 1f);
        // "Defaults to armor value if Armor_Explosion isn't specified."
        float explosion = ReadOr(data, "Armor_Explosion", armor);

        clothing = new ClothingArmor(id, type, armor, explosion);
        return true;
    }

    // ParseFloat behind a ContainsKey gate: an absent key takes `whenAbsent`, a present one takes what
    // it parses to — including the 0 that an unparseable value parses to.
    private static float ReadOr(DatDictionary data, string key, float whenAbsent)
    {
        if (!data.ContainsKey(key))
            return whenAbsent;
        return data.TryGetSingle(key, out float parsed) ? parsed : 0f;
    }

    private static EClothingType ParseType(string? type) => type switch
    {
        "Hat" => EClothingType.Hat,
        "Shirt" => EClothingType.Shirt,
        "Pants" => EClothingType.Pants,
        "Vest" => EClothingType.Vest,
        "Backpack" => EClothingType.Backpack,
        "Mask" => EClothingType.Mask,
        "Glasses" => EClothingType.Glasses,
        _ => EClothingType.Unknown,
    };
}
