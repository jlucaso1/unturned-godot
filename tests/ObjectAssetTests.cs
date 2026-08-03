using System;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
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
}
