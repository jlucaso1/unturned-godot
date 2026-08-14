using System;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace UnturnedGodot.Data;

// LevelInfoConfigData.Weather_Override (ELevelWeatherOverride): a map creator can pin the weather.
public enum ELevelWeatherOverride
{
    None = 0,
    Rain = 1,
    Snow = 2,
}

// Ports SDG.Unturned.LevelInfoConfigData — the map's own Config.json, which Unturned deserializes with
// Newtonsoft straight onto the class. Every default below is the one LevelInfoConfigData's constructor
// assigns, so a key the file omits reads exactly what the game would read for it.
//
// The important one is Use_Legacy_Ground. It is *the* authority on which terrain system a map uses:
// LevelGround.load returns early into loadTrees() when it is false, meaning the map's ground comes from
// Landscape tiles instead of the single legacy Terrain. This port used to answer that question by
// globbing Landscape/Heightmaps and calling a map with no tiles unsupported, which is an inference from
// a side-effect rather than the map's own declaration — a Landscape map whose tiles failed to list read
// as legacy, and a legacy map is not merely tile-less, it is a different loader.
public sealed class LevelConfigData
{
    // The LevelAsset this map names (Bundles/Assets/Levels/<Map>.asset). Guid.Empty when unset, which is
    // AssetReference<LevelAsset>.invalid — the game then falls back to LevelAsset.defaultLevel.
    public Guid Asset { get; init; }

    // Terrain system. True (the constructor default) is the pre-Landscape single-Terrain ground.
    public bool UseLegacyGround { get; init; } = true;

    // Water system: the legacy sea plane versus authored water volumes.
    public bool UseLegacyWater { get; init; } = true;
    public bool UseLegacyClipBorders { get; init; } = true;

    // "Should positions underground be clamped above ground? Underground volumes are used to whitelist
    // valid positions." PEI sets this, which is what makes its mineshafts legal places to stand.
    public bool UseUndergroundWhitelist { get; init; }

    // "If true, certain objects redirect to load others in-game" — the holiday variants an object asset
    // names through Bundle_Override_Path. A map that opts out keeps its authored objects year-round.
    public bool AllowHolidayRedirects { get; init; }

    // "If true, map creator has verified the clutter option works as-expected."
    public bool EnableClutterOption { get; init; }

    // "If true, map creator has verified that volumes are ONLY placed in the level editor."
    public bool EnableStaticVolumes { get; init; }

    // "LevelBatching is currently only enabled if map creator has verified it works properly." Zero means
    // the map has not opted into batching at all; PEI ships version 2.
    public int BatchingVersion { get; init; }

    // "Overrides maximum size of textures included in LevelBatching atlas."
    public int BatchingMaxTextureSize { get; init; } = 128;

    public bool TerrainSnowSparkle { get; init; }
    public bool AllowUnderwaterFeatures { get; init; }
    public bool HasGlobalElectricity { get; init; }
    public bool HasAtmosphere { get; init; } = true;
    public bool SnowAffectsTemperature { get; init; } = true;
    public bool IsAuroraBorealisVisible { get; init; }
    public ELevelWeatherOverride WeatherOverride { get; init; }

    public float Gravity { get; init; } = -9.81f;
    public float BlimpAltitude { get; init; } = 150f;

    // -1 is the constructor default and the sentinel LevelInfo turns into 59 degrees; kept raw here so
    // the resolved value stays one rule in PlayerConfig rather than two copies of the sentinel. That
    // rule is Player.PlayerConfig.ResolveMaxWalkableSlope, and it is what every consumer of this number
    // goes through — the number itself is only what the file said.
    public float MaxWalkableSlope { get; init; } = -1f;

    public float PreventBuildingNearSpawnpointRadius { get; init; } = 16f;

    public string? Category { get; init; }
    public string Version { get; init; } = "3.0.0.0";

    // What the game reads for a map with no Config.json at all: LevelInfoConfigData's own constructor.
    public static readonly LevelConfigData Default = new();

    // Reads <map>/Config.json. Returns Default for a missing, unreadable or malformed file, which is
    // what Unturned does too — Newtonsoft leaves the constructed instance alone on a parse failure, and
    // a map with no config is loaded rather than skipped.
    public static LevelConfigData Load(string mapDirectory)
    {
        string path = Path.Combine(mapDirectory, "Config.json");
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Default;
        }
        return Parse(text);
    }

    public static LevelConfigData Parse(string json)
    {
        JsonDocument document;
        try
        {
            // Trailing commas and comments appear in hand-edited workshop configs; the game's Newtonsoft
            // settings accept both, so refusing them here would reject maps the game loads.
            document = JsonDocument.Parse(json,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return Default;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Default;

            return Read(root);
        }
    }

    private static LevelConfigData Read(JsonElement root) =>
        new()
        {
            Asset = ReadAssetGuid(root),
            UseLegacyGround = Bool(root, "Use_Legacy_Ground", true),
            UseLegacyWater = Bool(root, "Use_Legacy_Water", true),
            UseLegacyClipBorders = Bool(root, "Use_Legacy_Clip_Borders", true),
            UseUndergroundWhitelist = Bool(root, "Use_Underground_Whitelist", false),
            AllowHolidayRedirects = Bool(root, "Allow_Holiday_Redirects", false),
            EnableClutterOption = Bool(root, "Enable_Clutter_Option", false),
            EnableStaticVolumes = Bool(root, "Enable_Static_Volumes", false),
            BatchingVersion = Int(root, "Batching_Version", 0),
            BatchingMaxTextureSize = Int(root, "Batching_Max_Texture_Size", 128),
            TerrainSnowSparkle = Bool(root, "Terrain_Snow_Sparkle", false),
            AllowUnderwaterFeatures = Bool(root, "Allow_Underwater_Features", false),
            HasGlobalElectricity = Bool(root, "Has_Global_Electricity", false),
            HasAtmosphere = Bool(root, "Has_Atmosphere", true),
            SnowAffectsTemperature = Bool(root, "Snow_Affects_Temperature", true),
            IsAuroraBorealisVisible = Bool(root, "Is_Aurora_Borealis_Visible", false),
            WeatherOverride = Weather(root),
            Gravity = Float(root, "Gravity", -9.81f),
            BlimpAltitude = Float(root, "Blimp_Altitude", 150f),
            MaxWalkableSlope = Float(root, "Max_Walkable_Slope", -1f),
            PreventBuildingNearSpawnpointRadius =
                Float(root, "Prevent_Building_Near_Spawnpoint_Radius", 16f),
            Category = String(root, "Category"),
            Version = String(root, "Version") ?? "3.0.0.0",
        };

    // "Asset": { "GUID": "..." } — Newtonsoft's shape for AssetReference<LevelAsset>. A bare string is
    // accepted too because that is the other spelling the game's converter takes.
    private static Guid ReadAssetGuid(JsonElement root)
    {
        if (!Property(root, "Asset", out JsonElement asset))
            return Guid.Empty;
        JsonElement value = asset;
        if (asset.ValueKind == JsonValueKind.Object)
        {
            if (!Property(asset, "GUID", out value))
                return Guid.Empty;
        }
        return Guid.TryParse(Text(value), out Guid guid) ? guid : Guid.Empty;
    }

    // One property of the config object, matched the way Newtonsoft matches one.
    //
    // The game hands Config.json to JsonConvert.DeserializeObject<LevelInfoConfigData> (Level.cs:944),
    // and Newtonsoft resolves each JSON property against the class through
    // JsonPropertyCollection.GetClosestMatchProperty: an ORDINAL match first, and an
    // OrdinalIgnoreCase one when that finds nothing. So a workshop config spelling
    // `use_legacy_ground`, or `Max_Walkable_Slope` as `MAX_WALKABLE_SLOPE`, loads in Unturned exactly
    // as the canonical spelling does. JsonElement.TryGetProperty is case-sensitive and read those as
    // absent — which for Use_Legacy_Ground meant falling back to true and marking a perfectly loadable
    // Landscape map unsupported.
    //
    // The LAST match wins, whatever its case, because Newtonsoft assigns the member once per JSON
    // property it reads: a document naming the same field twice ends up holding the second value. That
    // also settles duplicates of one spelling, where TryGetProperty returned the first.
    //
    // Deliberately not TryGetProperty for the exact spelling first: that would return the earlier of a
    // pair like {"X": 1, "x": 2} and the game keeps 2.
    private static bool Property(JsonElement element, string key, out JsonElement value)
    {
        value = default;
        bool found = false;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                continue;
            value = property.Value;
            found = true;
        }
        return found;
    }

    // A string value, or null when it is not a string or cannot be decoded.
    //
    // GetString() throws InvalidOperationException — not JsonException — when the value holds an
    // unpaired surrogate ("Cannot read incomplete UTF-16 JSON text as string"). Parse accepts the
    // escape, so the throw lands out here, and one hand-edited workshop config carrying such an escape
    // once took the whole map list down with it.
    //
    // Caught per STRING rather than around the whole read, which matters more now than it did when only
    // the category came through here: a map whose Category is unreadable still has a perfectly good
    // Use_Legacy_Ground, and failing the config wholesale would quietly demote it to legacy terrain —
    // marking a loadable map unsupported over a bad character in an unrelated field.
    private static string? Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
            return null;
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool Bool(JsonElement root, string key, bool fallback) =>
        Property(root, key, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            }
            : fallback;

    private static int Int(JsonElement root, string key, int fallback) =>
        Property(root, key, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int parsed)
            ? parsed
            : fallback;

    private static float Float(JsonElement root, string key, float fallback) =>
        Property(root, key, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetSingle(out float parsed)
            ? parsed
            : fallback;

    // GetString() throws InvalidOperationException — not JsonException — when the value holds an
    // unpaired surrogate ("Cannot read incomplete UTF-16 JSON text as string"). Parse accepts the
    // escape, so the throw lands out here, and one hand-edited workshop config with such an escape once
    // took the whole map list down with it.
    //
    // Caught per STRING rather than around the whole read: a map whose Category is unreadable still has
    // a perfectly good Use_Legacy_Ground, and failing the config wholesale would silently demote it to
    // legacy terrain — i.e. mark a loadable map unsupported over a bad character in an unrelated field.
    private static string? String(JsonElement root, string key) =>
        Property(root, key, out JsonElement value) ? Text(value) : null;

    // Newtonsoft's StringEnumConverter is case-insensitive and also accepts the numeric value.
    private static ELevelWeatherOverride Weather(JsonElement root)
    {
        if (!Property(root, "Weather_Override", out JsonElement value))
            return ELevelWeatherOverride.None;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numeric))
            return Enum.IsDefined(typeof(ELevelWeatherOverride), numeric)
                ? (ELevelWeatherOverride)numeric
                : ELevelWeatherOverride.None;
        return Enum.TryParse(Text(value), ignoreCase: true, out ELevelWeatherOverride parsed)
            ? parsed
            : ELevelWeatherOverride.None;
    }

    // LevelInfoConfigData.PackedVersion: "a.b.c.d" packed one byte per component, for the version gates
    // the game applies to older maps. Zero when the string is not in that form.
    public uint PackedVersion => PackVersion(Version);

    internal static uint PackVersion(string? version)
    {
        if (string.IsNullOrEmpty(version))
            return 0;
        string[] parts = version.Split('.');
        if (parts.Length != 4)
            return 0;
        uint packed = 0;
        foreach (string part in parts)
        {
            if (!byte.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte component))
                return 0;
            packed = (packed << 8) | component;
        }
        return packed;
    }
}
