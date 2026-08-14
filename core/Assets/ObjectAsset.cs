using System;
using System.Diagnostics.CodeAnalysis;
using Godot;
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

    // Decal only: how wide and long the projection is, in metres (the .dat's Decal_X / Decal_Y). A decal
    // object ships no prefab at all — just a decal.png beside its .dat inside the bundle — so this and the
    // texture are the whole of it. Zero for everything else.
    public float DecalX { get; }
    public float DecalY { get; }

    // The bare "Decal_Alpha" flag: this decal's texture blends rather than clips, so its partly
    // transparent pixels have to survive instead of being scissored away at half alpha.
    public bool DecalBlends { get; }

    // --- What it takes to break it (ResourceAsset / ObjectAsset's rubble block) ---

    // Resource only: the tree or rock's own hit points, from the .dat's "Health". Every placed copy of
    // the asset starts here (ResourceSpawnpoint.health = asset.health) — 800 for a birch, 1400 for a
    // clay deposit — which is what makes a fist at 20 damage a forty-punch proposition.
    public ushort Health { get; }

    // ResourceAsset.vulnerableToFists, off "Vulnerable_To_Fists". Absent means false: the DEFAULT is
    // that bare hands do nothing to a resource, and the handful that opt in are the ones a survivor can
    // gather without a tool.
    public bool VulnerableToFists { get; }

    // ResourceAsset.bladeID: which family of cutting tool may harvest this. Zero is "any blade"; a tree
    // that names one can only be felled by a weapon whose own blade list contains it.
    public byte BladeId { get; }

    // Object only: the destructible-rubble block, written either as "Rubble_*" or, on an object whose
    // interactability IS rubble, as "Interactability_*". Both spellings mean the same fields and
    // Unturned parses whichever is present, so this does too.
    public bool HasRubble { get; }
    public ushort RubbleHealth { get; }
    public byte RubbleBladeId { get; }

    // rubbleIsVulnerable is the NEGATION of a bare flag: an object is destructible unless it says
    // "Rubble_Invulnerable". An object with no rubble block at all is not destructible either, which
    // HasRubble above answers separately.
    public bool RubbleIsVulnerable { get; }

    // --- Holidays (ObjectAsset.cs:1225, ResourceAsset.cs:508) ---

    // "Holiday_Restriction": the object does not exist outside this holiday. Both classes read the same
    // key and both fold it into areConditionsMet (LevelObject.cs:428, ResourceSpawnpoint.cs:539), which
    // gates the GameObject and the renderers together — so out of season the prop is neither drawn nor
    // collidable. NONE, the default, means no restriction at all.
    public ENPCHoliday HolidayRestriction { get; init; }

    // "Christmas_Redirect" / "Halloween_Redirect": the asset to load INSTEAD of this one while that
    // holiday runs — a wreath on the door, a snow-dusted pine. Unlike the restriction above these are
    // gated by the map's Allow_Holiday_Redirects, so the same asset is substituted on PEI and left alone
    // on a map that never opted in.
    public Guid ChristmasRedirect { get; init; }
    public Guid HalloweenRedirect { get; init; }

    // getHolidayRedirect(): the substitution for the holiday now running, or Guid.Empty when this asset
    // has none for it. Ported from both classes at once because they are the same switch twice over.
    public Guid GetHolidayRedirect(ENPCHoliday active) => active switch
    {
        ENPCHoliday.Christmas => ChristmasRedirect,
        ENPCHoliday.Halloween => HalloweenRedirect,
        _ => Guid.Empty,
    };

    // --- Where a resource actually stands (ResourceAsset) ---

    // "Vertical_Offset", default -0.75. The point in Trees.dat is where the trunk MEETS THE GROUND, and
    // the model is sunk by this much so the root ball ends up buried instead of hovering:
    // ResourceSpawnpoint.cs:484 instantiates at `point + Vector3.up * scale.y * verticalOffset`. Reading
    // the field rather than hardcoding the default is not pedantry — the two mushrooms override it to
    // +0.1, i.e. they are lifted, and a constant would push them 0.85 m into the dirt.
    public float VerticalOffset { get; init; } = DefaultVerticalOffset;

    // "RandomAngleDeviation_Min/_Max" (±5) and "RandomUniformScale_Min/_Max" (1.1 both): the range a
    // pre-v8 Trees.dat's trees are jittered across, since those files store no rotation or scale of
    // their own. See GetLegacyRotationAndScale.
    public float MinRandomAngleDeviation { get; init; } = DefaultMinRandomAngleDeviation;
    public float MaxRandomAngleDeviation { get; init; } = DefaultMaxRandomAngleDeviation;
    public float MinRandomUniformScale { get; init; } = DefaultRandomUniformScale;
    public float MaxRandomUniformScale { get; init; } = DefaultRandomUniformScale;

    public const float DefaultVerticalOffset = -0.75f;
    public const float DefaultMinRandomAngleDeviation = -5f;
    public const float DefaultMaxRandomAngleDeviation = 5f;
    public const float DefaultRandomUniformScale = 1.1f;

    private ObjectAsset(Guid guid, ushort id, EObjectType type, string rawType, string? explicitName,
        string? dataName, string? bundleOverridePath, Guid materialPaletteGuid, Guid redirectTargetGuid,
        float decalX = 0f, float decalY = 0f, bool decalBlends = false,
        ushort health = 0, bool vulnerableToFists = false, byte bladeId = 0,
        bool hasRubble = false, ushort rubbleHealth = 0, byte rubbleBladeId = 0,
        bool rubbleIsVulnerable = false)
    {
        DecalX = decalX;
        DecalY = decalY;
        DecalBlends = decalBlends;
        Health = health;
        VulnerableToFists = vulnerableToFists;
        BladeId = bladeId;
        HasRubble = hasRubble;
        RubbleHealth = rubbleHealth;
        RubbleBladeId = rubbleBladeId;
        RubbleIsVulnerable = rubbleIsVulnerable;
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

        data.TryGetSingle("Decal_X", out float decalX);
        data.TryGetSingle("Decal_Y", out float decalY);
        // A bare flag: present with no value at all, which is how Unturned writes its booleans-by-presence.
        bool decalBlends = data.TryGetString("Decal_Alpha", out _);

        // A resource's own hit points and what may take them off. Absent on everything that is not a
        // Bundles/Trees asset, and harmless there: Health 0 is a thing nothing can damage, which is
        // exactly what a decal or a bench is.
        data.TryGetUInt16("Health", out ushort health);
        data.TryGetByte("BladeID", out byte bladeId);
        bool vulnerableToFists = data.GetBool("Vulnerable_To_Fists");

        ReadRubble(data, out bool hasRubble, out ushort rubbleHealth, out byte rubbleBladeId,
            out bool rubbleIsVulnerable);

        ReadResourceTransform(data, out float verticalOffset, out float minAngle, out float maxAngle,
            out float minScale, out float maxScale);

        asset = new ObjectAsset(guid, id, ClassifyType(rawType), rawType, localizedName,
            data.GetString("Name"), overridePath, paletteGuid, redirectTarget, decalX, decalY, decalBlends,
            health, vulnerableToFists, bladeId, hasRubble, rubbleHealth, rubbleBladeId, rubbleIsVulnerable)
        {
            HolidayRestriction = ParseHoliday(data.GetString("Holiday_Restriction")),
            // readAssetReference accepts a GUID or a legacy id; every one of the 16 redirects the game
            // ships is written as a GUID, so the legacy spelling is left unported rather than guessed at.
            ChristmasRedirect = data.TryGetGuid("Christmas_Redirect", out Guid xmas) ? xmas : Guid.Empty,
            HalloweenRedirect = data.TryGetGuid("Halloween_Redirect", out Guid hw) ? hw : Guid.Empty,
            VerticalOffset = verticalOffset,
            MinRandomAngleDeviation = minAngle,
            MaxRandomAngleDeviation = maxAngle,
            MinRandomUniformScale = minScale,
            MaxRandomUniformScale = maxScale,
        };
        return true;
    }

    // "Holiday_Restriction" names an ENPCHoliday by its enum spelling ("CHRISTMAS"), case-insensitively.
    // Unturned uses Enum.Parse and reports an asset that names NONE as an error; here an unparseable or
    // absent value is simply no restriction, because the alternative — throwing out of the asset scan —
    // would take the whole map's object database down over one mod's typo.
    private static ENPCHoliday ParseHoliday(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && Enum.TryParse(value.Trim().Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true,
            out ENPCHoliday holiday)
            ? holiday
            : ENPCHoliday.None;

    // ResourceAsset's transform block. The scale range has two spellings and they are not additive: an
    // asset writing the legacy "Scale" gets 1.1 to 1.1 + 2*Scale, and only an asset without it reads the
    // modern pair. Nelson's own note records the in-game expression the legacy form reproduces,
    // `1.1 + scale + (seed * scale)` over a seed in [-1, 1], which is that range written out.
    private static void ReadResourceTransform(DatDictionary data, out float verticalOffset,
        out float minAngle, out float maxAngle, out float minScale, out float maxScale)
    {
        minAngle = data.TryGetSingle("RandomAngleDeviation_Min", out float minA)
            ? minA : DefaultMinRandomAngleDeviation;
        maxAngle = data.TryGetSingle("RandomAngleDeviation_Max", out float maxA)
            ? maxA : DefaultMaxRandomAngleDeviation;

        if (data.TryGetSingle("Scale", out float legacyScale))
        {
            minScale = DefaultRandomUniformScale;
            maxScale = DefaultRandomUniformScale + (legacyScale * 2f);
        }
        else
        {
            minScale = data.TryGetSingle("RandomUniformScale_Min", out float minS)
                ? minS : DefaultRandomUniformScale;
            maxScale = data.TryGetSingle("RandomUniformScale_Max", out float maxS)
                ? maxS : DefaultRandomUniformScale;
        }

        verticalOffset = data.TryGetSingle("Vertical_Offset", out float offset)
            ? offset : DefaultVerticalOffset;
    }

    // ResourceAsset.GetLegacyRotationAndScale, for the trees in a Trees.dat written before version 8
    // stored a rotation and a scale of its own. Not random despite the field names: the "seed" is a sine
    // of the tree's own X and Z, so the same tree lands the same way on every machine and every load,
    // which is what lets a server and a client agree about a forest neither of them stored.
    //
    // Rotation comes back as Euler degrees rather than as the quaternion Unturned builds, because that
    // is the representation the rest of this port carries a placement in — the same three floats a
    // modern file stores directly.
    public void GetLegacyRotationAndScale(Vector3 point, out Vector3 eulerDegrees, out Vector3 scale)
    {
        float seed = MathF.Sin(((point.X + 4096f) * 32f) + ((point.Z + 4096f) * 32f));
        // Nelson 2024-12-11: "It's like this for backwards compatibility. :P" — the sine is reused both
        // as the [0,1] lerp weight and, unscaled, as the yaw multiplier, so the tilt and the facing are
        // not independent of each other.
        float lerpWeight = (seed + 1f) * 0.5f;
        float angleDeviation = Lerp(MinRandomAngleDeviation, MaxRandomAngleDeviation, lerpWeight);
        eulerDegrees = new Vector3(angleDeviation, seed * 360f, 0f);
        float randomScale = Lerp(MinRandomUniformScale, MaxRandomUniformScale, lerpWeight);
        scale = new Vector3(randomScale, randomScale, randomScale);
    }

    // Mathf.Lerp, which clamps its weight. Godot's Mathf.Lerp does not, and while the weight above is a
    // sine mapped into [0,1] and so already in range, the clamp is what the ported expression says.
    private static float Lerp(float a, float b, float t) =>
        a + ((b - a) * Math.Clamp(t, 0f, 1f));

    // ObjectAsset's destructible block, under whichever of its two prefixes the file uses. An object
    // whose Interactability IS "Rubble" writes the fields as "Interactability_*" and everything else
    // writes them as "Rubble_*"; Unturned reads the interactability spelling first and falls back to the
    // other, so the same object cannot be described twice and mean two different things.
    private static void ReadRubble(DatDictionary data, out bool hasRubble, out ushort health,
        out byte bladeId, out bool isVulnerable)
    {
        string prefix =
            string.Equals(data.GetString("Interactability"), "Rubble", StringComparison.OrdinalIgnoreCase)
                ? "Interactability_"
                : "Rubble_";

        // "Rubble" itself is the EObjectRubble mode (DESTROY / DISAPPEAR): its presence is what says the
        // object has a rubble block at all, on the non-interactable path.
        hasRubble = prefix == "Interactability_" || data.ContainsKey("Rubble");
        data.TryGetUInt16(prefix + "Health", out health);
        data.TryGetByte(prefix + "Blade_ID", out bladeId);
        // The bare "…_Invulnerable" flag, negated: destructible unless it says otherwise.
        isVulnerable = hasRubble && !data.ContainsKey(prefix + "Invulnerable");
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
