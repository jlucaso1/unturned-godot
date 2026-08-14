using System;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class ObjectAssetTests
{
    [Fact]
    public void ParsesV1_RootGuid_WithDataNameFallback()
    {
        DatDictionary root = DatParser.Parse("GUID 2e698a7b85e94c019b3f91ec8796a961\nType Small\nID 57\nName FromData\n");
        Assert.True(ObjectAsset.TryParse(root, localizedName: null, out var asset));

        Assert.Equal(57, asset.Id);
        Assert.Equal(EObjectType.Small, asset.Type);
        Assert.Equal("Small", asset.RawType);
        Assert.Equal("FromData", asset.Name); // falls back to data "Name" when no localization
    }

    [Fact]
    public void LocalizedNameTakesPriority()
    {
        DatDictionary root = DatParser.Parse("GUID 2e698a7b85e94c019b3f91ec8796a961\nType Small\nName FromData\n");
        Assert.True(ObjectAsset.TryParse(root, "FromLocalization", out var asset));
        Assert.Equal("FromLocalization", asset.Name);
    }

    [Fact]
    public void ParsesV2_MetadataGuid_AndAssetSubDictionary()
    {
        string text =
            "Metadata\n{\nGUID 2e698a7b85e94c019b3f91ec8796a961\nType SDG.Unturned.ObjectAsset\n}\n" +
            "Asset\n{\nType Large\nID 99\n}\n";
        DatDictionary root = DatParser.Parse(text);
        Assert.True(ObjectAsset.TryParse(root, null, out var asset));

        Assert.Equal(99, asset.Id);
        Assert.Equal(EObjectType.Large, asset.Type);
    }

    [Fact]
    public void ParsesBundleOverridePath()
    {
        DatDictionary root = DatParser.Parse(
            "GUID 2e698a7b85e94c019b3f91ec8796a961\nType Medium\nID 1\nBundle_Override_Path /Objects/Medium/Furniture/Grave_0\n");
        Assert.True(ObjectAsset.TryParse(root, null, out var asset));
        Assert.Equal("/Objects/Medium/Furniture/Grave_0", asset.BundleOverridePath);
    }

    [Theory]
    [InlineData("/Bundles/Objects/Medium/Fences/Fence_Wood_0", true)]
    [InlineData("C:\\Game\\Bundles\\Objects\\Medium\\Fences\\Fence_Metal_0", true)]
    [InlineData("/Bundles/Objects/Medium/Furniture/Grave_0", false)]
    [InlineData("/Workshop/Objects/Medium/Benches/Fences_Are_Nearby", false)]
    public void MediumFenceDirectories_DecideWhetherZombiesCollide(string directory, bool blocks)
    {
        DatDictionary root = DatParser.Parse(
            "GUID 40921a1a3cd742f69cc25cc25b856572\nType Medium\nID 2\n");
        Assert.True(ObjectAsset.TryParse(root, null, out ObjectAsset? asset));
        asset.Directory = directory;

        uint layer = ObjectCollisionPolicy.PhysicsLayer(asset);

        Assert.Equal(blocks, (layer & CollisionLayers.World) != 0);
        Assert.NotEqual(0u, layer & CollisionLayers.MediumFurniture);
        Assert.NotEqual(0u, layer & CollisionLayers.VisionBlocker);
    }

    [Fact]
    public void MediumFenceBundleOverridesAlsoBlockZombies()
    {
        DatDictionary root = DatParser.Parse(
            "GUID 40921a1a3cd742f69cc25cc25b856572\nType Medium\nID 2\n" +
            "Bundle_Override_Path /Objects/Medium/Fences/Fence_Wood_0\n");
        Assert.True(ObjectAsset.TryParse(root, null, out ObjectAsset? asset));
        asset.Directory = "/Workshop/Objects/Medium/Holiday/Fence_Wood_Snow";

        uint layer = ObjectCollisionPolicy.PhysicsLayer(asset);

        Assert.NotEqual(0u, layer & CollisionLayers.World);
    }

    [Fact]
    public void NonFenceBundleOverridesRemainPassableToZombies()
    {
        DatDictionary root = DatParser.Parse(
            "GUID 2e698a7b85e94c019b3f91ec8796a961\nType Medium\nID 1\n" +
            "Bundle_Override_Path /Objects/Medium/Furniture/Grave_0\n");
        Assert.True(ObjectAsset.TryParse(root, null, out ObjectAsset? asset));
        asset.Directory = "/Workshop/Objects/Medium/Holiday/Grave_Snow";

        Assert.Equal(CollisionLayers.MediumFurniture | CollisionLayers.VisionBlocker,
            ObjectCollisionPolicy.PhysicsLayer(asset));
    }

    [Theory]
    [InlineData("Resource", CollisionLayers.World)]
    [InlineData("Large", CollisionLayers.World | CollisionLayers.VisionBlocker)]
    [InlineData("Small", 0u)]
    public void NonMediumObjectTypesKeepTheirExistingCollisionPolicy(string type, uint expectedLayer)
    {
        DatDictionary root = DatParser.Parse(
            $"GUID 2e698a7b85e94c019b3f91ec8796a961\nType {type}\nID 1\n");
        Assert.True(ObjectAsset.TryParse(root, null, out ObjectAsset? asset));

        Assert.Equal(expectedLayer, ObjectCollisionPolicy.PhysicsLayer(asset));
    }

    [Fact]
    public void CollisionPolicyRejectsANullAsset()
    {
        Assert.Throws<ArgumentNullException>(() => ObjectCollisionPolicy.PhysicsLayer(null!));
    }

    [RealDataFact]
    public void PeiWoodFenceAsset_IsPublishedAsAWorldBarrier()
    {
        string directory = Path.Combine(GameData.Install!, "Bundles", "Objects", "Medium",
            "Fences", "Fence_Wood_0");
        ObjectAssetDatabase db = ObjectAssetDatabase.ScanDirectory(directory);

        ObjectAsset? fence = db.Resolve(new Guid("40921a1a-3cd7-42f6-9cc2-5cc25b856572"), 2);

        Assert.NotNull(fence);
        Assert.Equal(EObjectType.Medium, fence!.Type);
        Assert.NotEqual(0u, ObjectCollisionPolicy.PhysicsLayer(fence) & CollisionLayers.World);
    }

    [Fact]
    public void WithoutOverridePath_IsNull()
    {
        DatDictionary root = DatParser.Parse("GUID 2e698a7b85e94c019b3f91ec8796a961\nType Small\nID 1\n");
        Assert.True(ObjectAsset.TryParse(root, null, out var asset));
        Assert.Null(asset.BundleOverridePath);
        Assert.Equal(System.Guid.Empty, asset.MaterialPaletteGuid);
    }

    [Fact]
    public void ParsesMaterialPaletteGuid()
    {
        DatDictionary root = DatParser.Parse(
            "GUID 2e698a7b85e94c019b3f91ec8796a961\nType Small\nID 1\nMaterial_Palette 3fcc42609bfb4154abb9dd39e7542ed8\n");
        Assert.True(ObjectAsset.TryParse(root, null, out var asset));
        Assert.NotEqual(System.Guid.Empty, asset.MaterialPaletteGuid);
    }

    [Fact]
    public void MissingGuid_ReturnsFalse()
    {
        DatDictionary root = DatParser.Parse("Type Small\nID 5\n"); // localization-style file
        Assert.False(ObjectAsset.TryParse(root, null, out _));
    }

    [Fact]
    public void MissingType_DefaultsToEmptyRawTypeAndUnknown()
    {
        DatDictionary root = DatParser.Parse("GUID 2e698a7b85e94c019b3f91ec8796a961\nID 5\n");
        Assert.True(ObjectAsset.TryParse(root, null, out var asset));
        Assert.Equal("", asset.RawType);
        Assert.Equal(EObjectType.Unknown, asset.Type);
    }

    [Theory]
    [InlineData("Small", EObjectType.Small)]
    [InlineData("MEDIUM", EObjectType.Medium)]
    [InlineData("large", EObjectType.Large)]
    [InlineData("NPC", EObjectType.Npc)]
    [InlineData("Decal", EObjectType.Decal)]
    [InlineData("Resource", EObjectType.Resource)]
    [InlineData("Structure", EObjectType.Unknown)]
    [InlineData("Vehicle", EObjectType.Vehicle)]
    // Vehicles and their redirectors also name the runtime class in full, which is how a mod writes them.
    [InlineData("SDG.Unturned.VehicleAsset, Assembly-CSharp, Version=0.0.0.0", EObjectType.Vehicle)]
    [InlineData("SDG.Unturned.VehicleRedirectorAsset, Assembly-CSharp, Version=0.0.0.0",
        EObjectType.VehicleRedirector)]
    public void ClassifyType(string raw, EObjectType expected)
    {
        Assert.Equal(expected, ObjectAsset.ClassifyType(raw));
    }

    [Fact]
    public void TryParse_ReadsARedirectorsTargetVehicle()
    {
        DatDictionary root = DatParser.Parse("GUID a2f98d9b28ec40df9268d7d6c822cc14\n"
            + "Type SDG.Unturned.VehicleRedirectorAsset, Assembly-CSharp\nID 57\n"
            + "TargetVehicle 6f56dc58382349b79793f9ba8839774e\n");

        Assert.True(ObjectAsset.TryParse(root, null, out ObjectAsset? asset));
        Assert.Equal(EObjectType.VehicleRedirector, asset.Type);
        Assert.Equal(Guid.Parse("6f56dc58382349b79793f9ba8839774e"), asset.RedirectTargetGuid);
    }

    [Fact]
    public void TryParse_ReadsTheTypeFromTheMetadataBlockOfAV2File()
    {
        DatDictionary root = DatParser.Parse("""
            Metadata
            {
                GUID 2e698a7b85e94c019b3f91ec8796a961
                Type SDG.Unturned.VehicleAsset, Assembly-CSharp
            }
            Asset
            {
                ID 58
            }
            """);

        Assert.True(ObjectAsset.TryParse(root, null, out ObjectAsset? asset));
        Assert.Equal(EObjectType.Vehicle, asset.Type);
        Assert.Equal((ushort)58, asset.Id);
    }

    [Fact]
    public void ParsesDecalSize()
    {
        // A Decal ships no prefab: its .dat's Decal_X/Decal_Y are the metres its texture covers, and
        // without them there is nothing to draw the decal.png on.
        Assert.True(ObjectAsset.TryParse(
            DatParser.Parse("GUID 982a81b1d5bc4c179a689b3b08caa15a\nType Decal\nID 729\n"
                + "Decal_X 3\nDecal_Y 4.5\n"), null, out ObjectAsset? decal));

        Assert.Equal(EObjectType.Decal, decal.Type);
        Assert.Equal(3f, decal.DecalX);
        Assert.Equal(4.5f, decal.DecalY);
    }

    [Fact]
    public void ParsesTheBareDecalAlphaFlag()
    {
        // Test_Auto_Decal_Alpha declares it with no value at all, which is how Unturned writes a boolean
        // it reads by presence. Its texture fades rather than clips.
        Assert.True(ObjectAsset.TryParse(
            DatParser.Parse("GUID f0a76ecfb0684da48bba7c5f5e5a0830\nType Decal\n"
                + "Decal_X 6\nDecal_Y 3\nDecal_Alpha\n"), null, out ObjectAsset? blended));
        Assert.True(blended.DecalBlends);

        Assert.True(ObjectAsset.TryParse(
            DatParser.Parse("GUID 982a81b1d5bc4c179a689b3b08caa15a\nType Decal\nDecal_X 3\nDecal_Y 3\n"),
            null, out ObjectAsset? clipped));
        Assert.False(clipped.DecalBlends);
    }

    [Fact]
    public void DecalSize_IsZeroForEverythingElse()
    {
        Assert.True(ObjectAsset.TryParse(
            DatParser.Parse($"GUID {System.Guid.NewGuid():N}\nType Large\n"), null, out ObjectAsset? large));

        Assert.Equal(0f, large.DecalX);
        Assert.Equal(0f, large.DecalY);
    }

    // --- Holidays (ObjectAsset.cs:1225 / :1238, ResourceAsset.cs:508 / :521) ---

    private static ObjectAsset Parse(string body) =>
        ObjectAsset.TryParse(DatParser.Parse($"GUID {Guid.NewGuid():N}\nType Large\n{body}"), null,
            out ObjectAsset? a)
            ? a
            : throw new InvalidOperationException("asset fixture did not parse");

    [Theory]
    // The three spellings that actually appear across every .dat the game ships: 106 CHRISTMAS,
    // 7 HALLOWEEN, 1 PRIDE_MONTH.
    [InlineData("CHRISTMAS", (int)ENPCHoliday.Christmas)]
    [InlineData("HALLOWEEN", (int)ENPCHoliday.Halloween)]
    [InlineData("PRIDE_MONTH", (int)ENPCHoliday.PrideMonth)]
    [InlineData("christmas", (int)ENPCHoliday.Christmas)]
    public void HolidayRestriction_ParsesTheEnumSpelling(string value, int expected) =>
        Assert.Equal((ENPCHoliday)expected, Parse($"Holiday_Restriction {value}\n").HolidayRestriction);

    [Fact]
    public void HolidayRestriction_DefaultsToNoneAndSurvivesAMalformedValue()
    {
        // Unturned's Enum.Parse would throw here; taking the whole asset scan down over one mod's typo
        // would cost the map its entire object database, so an unreadable value is simply no restriction.
        Assert.Equal(ENPCHoliday.None, Parse("").HolidayRestriction);
        Assert.Equal(ENPCHoliday.None, Parse("Holiday_Restriction Easter\n").HolidayRestriction);
        Assert.Equal(ENPCHoliday.None, Parse("Holiday_Restriction NONE\n").HolidayRestriction);
    }

    [Fact]
    public void GetHolidayRedirect_AnswersOnlyForTheHolidayItNames()
    {
        var xmas = Guid.NewGuid();
        var hw = Guid.NewGuid();
        ObjectAsset asset = Parse($"Christmas_Redirect {xmas:N}\nHalloween_Redirect {hw:N}\n");

        Assert.Equal(xmas, asset.GetHolidayRedirect(ENPCHoliday.Christmas));
        Assert.Equal(hw, asset.GetHolidayRedirect(ENPCHoliday.Halloween));
        // Every other holiday returns AssetReference.invalid, i.e. no substitution at all.
        Assert.Equal(Guid.Empty, asset.GetHolidayRedirect(ENPCHoliday.PrideMonth));
        Assert.Equal(Guid.Empty, asset.GetHolidayRedirect(ENPCHoliday.None));
        Assert.Equal(Guid.Empty, Parse("").GetHolidayRedirect(ENPCHoliday.Christmas));
    }

    // --- Where a resource stands and how it is jittered (ResourceAsset.cs:424 onwards) ---

    [Fact]
    public void VerticalOffset_DefaultsToMinusThreeQuarters()
    {
        Assert.Equal(-0.75f, Parse("").VerticalOffset);
        // The two mushrooms, which are the reason the field is read rather than assumed.
        Assert.Equal(0.1f, Parse("Vertical_Offset 0.1\n").VerticalOffset);
    }

    [Fact]
    public void RandomUniformScale_LegacyScaleWinsOverTheModernPair()
    {
        // "The old in-game transform scale was 1.1f + asset.scale + (seed * asset.scale)" over a seed in
        // [-1, 1], which is the range 1.1 .. 1.1 + 2*Scale written out. 37 tree assets still use it, and
        // an asset writing both must read the legacy one -- the game never falls through to the pair.
        ObjectAsset legacy = Parse("Scale 0.1\nRandomUniformScale_Min 5\nRandomUniformScale_Max 9\n");
        Assert.Equal(1.1f, legacy.MinRandomUniformScale, 5);
        Assert.Equal(1.3f, legacy.MaxRandomUniformScale, 5);

        ObjectAsset modern = Parse("RandomUniformScale_Min 1\nRandomUniformScale_Max 1.55\n");
        Assert.Equal(1f, modern.MinRandomUniformScale);
        Assert.Equal(1.55f, modern.MaxRandomUniformScale);

        // Both default to 1.1, so an asset naming neither is not scaled to nothing.
        Assert.Equal(1.1f, Parse("").MinRandomUniformScale);
        Assert.Equal(1.1f, Parse("").MaxRandomUniformScale);
    }

    [Fact]
    public void RandomAngleDeviation_DefaultsToFiveDegreesEitherWay()
    {
        Assert.Equal(-5f, Parse("").MinRandomAngleDeviation);
        Assert.Equal(5f, Parse("").MaxRandomAngleDeviation);

        ObjectAsset tilted = Parse("RandomAngleDeviation_Min -20\nRandomAngleDeviation_Max 3\n");
        Assert.Equal(-20f, tilted.MinRandomAngleDeviation);
        Assert.Equal(3f, tilted.MaxRandomAngleDeviation);
    }

    [Fact]
    public void GetLegacyRotationAndScale_IsTheSineOfTheTreesOwnXAndZ()
    {
        // Not random despite the field names: a server and a client that never exchange a tree's
        // transform still have to draw the same forest, so the "seed" is a function of the position.
        ObjectAsset asset = Parse("RandomUniformScale_Min 1\nRandomUniformScale_Max 3\n");
        var point = new Godot.Vector3(13f, 999f, 27f);

        asset.GetLegacyRotationAndScale(point, out Godot.Vector3 euler, out Godot.Vector3 scale);

        float seed = MathF.Sin(((13f + 4096f) * 32f) + ((27f + 4096f) * 32f));
        float weight = (seed + 1f) * 0.5f;
        Assert.Equal(seed * 360f, euler.Y, 3);
        Assert.Equal(-5f + (10f * weight), euler.X, 5);
        Assert.Equal(0f, euler.Z);
        Assert.Equal(1f + (2f * weight), scale.X, 5);
        Assert.Equal(scale.X, scale.Y);
        Assert.Equal(scale.X, scale.Z);

        // Y is not part of the seed, so two trees on the same spot at different heights agree.
        asset.GetLegacyRotationAndScale(new Godot.Vector3(13f, -50f, 27f), out Godot.Vector3 e2, out _);
        Assert.Equal(euler, e2);
    }
}
