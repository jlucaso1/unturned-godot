using System;
using System.IO;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests.Data;

public class LevelConfigDataTests
{
    // Every default here is LevelInfoConfigData's constructor, which is what the game reads for a key
    // the file leaves out — so an empty object and a missing file must land on the same values.
    [Fact]
    public void EmptyObject_TakesTheConstructorDefaults()
    {
        LevelConfigData config = LevelConfigData.Parse("{}");

        Assert.True(config.UseLegacyGround);
        Assert.True(config.UseLegacyWater);
        Assert.True(config.UseLegacyClipBorders);
        Assert.False(config.UseUndergroundWhitelist);
        Assert.False(config.AllowHolidayRedirects);
        Assert.False(config.EnableClutterOption);
        Assert.False(config.EnableStaticVolumes);
        Assert.Equal(0, config.BatchingVersion);
        Assert.Equal(128, config.BatchingMaxTextureSize);
        Assert.False(config.TerrainSnowSparkle);
        Assert.True(config.HasAtmosphere);
        Assert.True(config.SnowAffectsTemperature);
        Assert.False(config.IsAuroraBorealisVisible);
        Assert.False(config.AllowUnderwaterFeatures);
        Assert.False(config.HasGlobalElectricity);
        Assert.Equal(ELevelWeatherOverride.None, config.WeatherOverride);
        Assert.Equal(-9.81f, config.Gravity);
        Assert.Equal(150f, config.BlimpAltitude);
        Assert.Equal(-1f, config.MaxWalkableSlope);
        Assert.Equal(16f, config.PreventBuildingNearSpawnpointRadius);
        Assert.Null(config.Category);
        Assert.Equal("3.0.0.0", config.Version);
        Assert.Equal(Guid.Empty, config.Asset);
    }

    [Fact]
    public void EveryKeyIsRead()
    {
        LevelConfigData config = LevelConfigData.Parse("""
        {
            "Use_Legacy_Ground": false,
            "Use_Legacy_Water": false,
            "Use_Legacy_Clip_Borders": false,
            "Use_Underground_Whitelist": true,
            "Allow_Holiday_Redirects": true,
            "Enable_Clutter_Option": true,
            "Enable_Static_Volumes": true,
            "Batching_Version": 2,
            "Batching_Max_Texture_Size": 256,
            "Terrain_Snow_Sparkle": true,
            "Allow_Underwater_Features": true,
            "Has_Global_Electricity": true,
            "Has_Atmosphere": false,
            "Snow_Affects_Temperature": false,
            "Is_Aurora_Borealis_Visible": true,
            "Weather_Override": "SNOW",
            "Gravity": -12.5,
            "Blimp_Altitude": 200,
            "Max_Walkable_Slope": 45,
            "Prevent_Building_Near_Spawnpoint_Radius": 32,
            "Category": "Curated",
            "Version": "3.26.1.0",
            "Asset": { "GUID": "d258342682aa44f89b08de0b47797c4e" }
        }
        """);

        Assert.False(config.UseLegacyGround);
        Assert.False(config.UseLegacyWater);
        Assert.False(config.UseLegacyClipBorders);
        Assert.True(config.UseUndergroundWhitelist);
        Assert.True(config.AllowHolidayRedirects);
        Assert.True(config.EnableClutterOption);
        Assert.True(config.EnableStaticVolumes);
        Assert.Equal(2, config.BatchingVersion);
        Assert.Equal(256, config.BatchingMaxTextureSize);
        Assert.True(config.TerrainSnowSparkle);
        Assert.True(config.AllowUnderwaterFeatures);
        Assert.True(config.HasGlobalElectricity);
        Assert.False(config.HasAtmosphere);
        Assert.False(config.SnowAffectsTemperature);
        Assert.True(config.IsAuroraBorealisVisible);
        Assert.Equal(ELevelWeatherOverride.Snow, config.WeatherOverride);
        Assert.Equal(-12.5f, config.Gravity);
        Assert.Equal(200f, config.BlimpAltitude);
        Assert.Equal(45f, config.MaxWalkableSlope);
        Assert.Equal(32f, config.PreventBuildingNearSpawnpointRadius);
        Assert.Equal("Curated", config.Category);
        Assert.Equal("3.26.1.0", config.Version);
        Assert.Equal(Guid.Parse("d258342682aa44f89b08de0b47797c4e"), config.Asset);
    }

    // Newtonsoft's StringEnumConverter takes the name case-insensitively or the raw number, and falls
    // back rather than throwing on something it does not know.
    [Theory]
    [InlineData("\"RAIN\"", ELevelWeatherOverride.Rain)]
    [InlineData("\"rain\"", ELevelWeatherOverride.Rain)]
    [InlineData("2", ELevelWeatherOverride.Snow)]
    [InlineData("0", ELevelWeatherOverride.None)]
    [InlineData("9", ELevelWeatherOverride.None)]      // out of range
    [InlineData("\"Blizzard\"", ELevelWeatherOverride.None)] // unknown name
    [InlineData("true", ELevelWeatherOverride.None)]   // wrong kind entirely
    public void WeatherOverride_ParsesNameOrNumber(string raw, ELevelWeatherOverride expected) =>
        Assert.Equal(expected, LevelConfigData.Parse($"{{ \"Weather_Override\": {raw} }}").WeatherOverride);

    // AssetReference deserializes from the object form the shipped configs use, and from a bare string.
    [Theory]
    [InlineData("{ \"GUID\": \"d258342682aa44f89b08de0b47797c4e\" }")]
    [InlineData("\"d258342682aa44f89b08de0b47797c4e\"")]
    public void Asset_TakesEitherSpelling(string raw) =>
        Assert.Equal(Guid.Parse("d258342682aa44f89b08de0b47797c4e"),
            LevelConfigData.Parse($"{{ \"Asset\": {raw} }}").Asset);

    [Theory]
    [InlineData("{ \"Asset\": { \"NotAGuid\": \"x\" } }")]
    [InlineData("{ \"Asset\": { \"GUID\": \"not-a-guid\" } }")]
    [InlineData("{ \"Asset\": 7 }")]
    [InlineData("{ }")]
    public void Asset_UnresolvableIsEmpty(string json) =>
        Assert.Equal(Guid.Empty, LevelConfigData.Parse(json).Asset);

    // A value of the wrong JSON kind keeps the default rather than throwing: the game's deserializer
    // leaves the constructed field alone when it cannot convert.
    [Fact]
    public void WrongTypedValues_KeepTheirDefaults()
    {
        LevelConfigData config = LevelConfigData.Parse("""
        {
            "Use_Legacy_Ground": "yes",
            "Batching_Version": "two",
            "Gravity": "heavy",
            "Category": 3
        }
        """);

        Assert.True(config.UseLegacyGround);
        Assert.Equal(0, config.BatchingVersion);
        Assert.Equal(-9.81f, config.Gravity);
        Assert.Null(config.Category);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]   // an array is not a config
    [InlineData("\"scalar\"")]
    [InlineData("")]
    public void MalformedConfig_YieldsTheDefaults(string json) =>
        Assert.Equal(LevelConfigData.Default.UseLegacyGround,
            LevelConfigData.Parse(json).UseLegacyGround);

    // Hand-edited workshop configs carry both, and the game's reader accepts them.
    [Fact]
    public void CommentsAndTrailingCommasAreAccepted()
    {
        LevelConfigData config = LevelConfigData.Parse("""
        {
            // the map is on Landscape tiles
            "Use_Legacy_Ground": false,
        }
        """);
        Assert.False(config.UseLegacyGround);
    }

    // An unpaired surrogate throws out of GetString rather than out of Parse. Losing the whole config to
    // it would silently demote a Landscape map to legacy terrain — i.e. mark a loadable map unsupported
    // over a bad character in an unrelated field.
    [Fact]
    public void UnpairedSurrogate_LosesOnlyThatString()
    {
        LevelConfigData config =
            LevelConfigData.Parse("{ \"Category\": \"\\uD800\", \"Use_Legacy_Ground\": false }");

        Assert.Null(config.Category);
        Assert.False(config.UseLegacyGround);
    }

    [Fact]
    public void CompleteSurrogatePair_Survives() =>
        Assert.Equal("\U00010000",
            LevelConfigData.Parse("{ \"Category\": \"\\uD800\\uDC00\" }").Category);

    [Fact]
    public void MissingFile_YieldsTheDefaults()
    {
        using var dir = new TempDir();
        LevelConfigData config = LevelConfigData.Load(Path.Combine(dir.Path, "nothing-here"));
        Assert.True(config.UseLegacyGround);
    }

    [Fact]
    public void Load_ReadsTheFile()
    {
        using var dir = new TempDir();
        dir.Write("Config.json", "{ \"Use_Legacy_Ground\": false, \"Batching_Version\": 2 }");

        LevelConfigData config = LevelConfigData.Load(dir.Path);

        Assert.False(config.UseLegacyGround);
        Assert.Equal(2, config.BatchingVersion);
    }

    // "Version string packed into integer", one byte per component.
    [Theory]
    [InlineData("3.26.1.0", 0x031A0100u)]
    [InlineData("0.0.0.0", 0u)]
    [InlineData("3.0.0", 0u)]          // wrong component count
    [InlineData("3.26.1.x", 0u)]       // non-numeric component
    [InlineData("3.999.1.0", 0u)]      // component past a byte
    [InlineData("", 0u)]
    [InlineData(null, 0u)]
    public void PackedVersion_PacksOneByteEach(string? version, uint expected) =>
        Assert.Equal(expected, LevelConfigData.PackVersion(version));

    [Fact]
    public void PackedVersion_ReadsTheParsedVersion() =>
        Assert.Equal(0x031A0100u, LevelConfigData.Parse("{ \"Version\": \"3.26.1.0\" }").PackedVersion);

    // ---- against the real file -------------------------------------------------------------------

    // PEI's shipped Config.json. The point of this whole change is that these values exist on disk, so
    // the numbers are asserted against the file rather than against a fixture that restates them.
    [RealDataFact(Map = "PEI")]
    public void RealPeiConfig_IsReadInFull()
    {
        LevelConfigData config = LevelConfigData.Load(GameData.Map("PEI")!);

        Assert.False(config.UseLegacyGround);   // PEI is a Landscape map and says so
        Assert.False(config.UseLegacyWater);
        Assert.True(config.AllowHolidayRedirects);
        Assert.True(config.TerrainSnowSparkle);
        Assert.True(config.UseUndergroundWhitelist);
        Assert.True(config.EnableClutterOption);
        Assert.True(config.EnableStaticVolumes);
        Assert.Equal(2, config.BatchingVersion);
        Assert.Equal("Official", config.Category);
        Assert.Equal(Guid.Parse("d258342682aa44f89b08de0b47797c4e"), config.Asset);
    }

    // And the GUID it names really is the map's LevelAsset, not just a well-formed GUID.
    [RealDataFact(Map = "PEI")]
    public void RealPeiConfig_AssetPointsAtTheLevelAsset()
    {
        LevelConfigData config = LevelConfigData.Load(GameData.Map("PEI")!);
        string assetPath = Path.Combine(GameData.Install!, "Bundles", "Assets", "Levels", "PEI.asset");
        Assert.True(File.Exists(assetPath), $"expected the level asset at {assetPath}");

        string text = File.ReadAllText(assetPath);
        Assert.Contains(config.Asset.ToString("N"), text.Replace("-", "", StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase);
    }

    [RealDataFact(Map = "PEI")]
    public void RealPeiLevelInfo_ReportsLandscapeTerrain()
    {
        var level = new LevelInfo(GameData.Map("PEI")!);
        Assert.True(level.UsesLandscapeTerrain);
        Assert.NotEmpty(level.EnumerateTiles());
    }
}
