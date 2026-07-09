using System;
using System.IO;
using UnturnedGodot.Assets;
using Xunit;

namespace UnturnedGodot.Tests;

public class LandscapePhysicsTests
{
    [Theory]
    [InlineData("FOLIAGE_STATIC", "Foliage")]
    [InlineData("foliage_dynamic", "Foliage")]
    [InlineData("CONCRETE_STATIC", "Concrete")]
    [InlineData("GRAVEL_STATIC", "Gravel")]
    [InlineData("SAND_STATIC", "Sand")]
    [InlineData("METAL_SLIP", "Metal")]
    [InlineData("WOOD_DYNAMIC", "Wood")]
    [InlineData("CLOTH_STATIC", "Cloth")]
    [InlineData("TILE_DYNAMIC", "Tile")]
    [InlineData("SNOW_STATIC", "Snow")]
    [InlineData("ICE_STATIC", "Ice")]
    [InlineData("WATER_STATIC", "Water")]
    [InlineData("ALIEN_DYNAMIC", "Alien")]
    [InlineData("FLESH_DYNAMIC", "Flesh")]
    public void ResolveName_MapsLegacyEnumValues(string raw, string expected)
    {
        Assert.Equal(expected, LandscapePhysics.ResolveName(raw));
    }

    [Fact]
    public void ResolveName_NoneIsNull_ModernNamesPassThrough()
    {
        Assert.Null(LandscapePhysics.ResolveName("NONE"));
        Assert.Equal("Peaks_Grass_Dry", LandscapePhysics.ResolveName("Peaks_Grass_Dry"));
    }

    [Fact]
    public void ScanDirectory_ReadsLandscapeMaterialAssets()
    {
        const string grass = """
            Metadata
            {
                GUID 3d7717c2bc074401853b2fdacd9db1ba
                Type SDG.Unturned.LandscapeMaterialAsset, Assembly-CSharp
            }
            Asset
            {
                Physics_Material FOLIAGE_STATIC
            }
            """;
        const string noPhysics = """
            Metadata
            {
                GUID 22b77c4c51514b0fbb66765eedf1a7f4
                Type SDG.Unturned.LandscapeMaterialAsset, Assembly-CSharp
            }
            Asset
            {
            }
            """;
        const string otherType = """
            Metadata
            {
                GUID 92cb5a3afd534054a64eb320b50c48de
                Type SDG.Unturned.ObjectAsset, Assembly-CSharp
            }
            Asset
            {
                Physics_Material CONCRETE_STATIC
            }
            """;

        string dir = Directory.CreateTempSubdirectory("landmat").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Grass.asset"), grass);
            File.WriteAllText(Path.Combine(dir, "Bare.asset"), noPhysics);
            File.WriteAllText(Path.Combine(dir, "Other.asset"), otherType);
            File.WriteAllText(Path.Combine(dir, "Flat.asset"), "Foo Bar"); // no Metadata block at all
            File.WriteAllText(Path.Combine(dir, "NoAsset.asset"), """
                Metadata
                {
                    GUID 44444444444444444444444444444444
                    Type SDG.Unturned.LandscapeMaterialAsset, Assembly-CSharp
                }
                """);
            File.WriteAllText(Path.Combine(dir, "NoType.asset"), """
                Metadata
                {
                    GUID 55555555555555555555555555555555
                }
                Asset
                {
                    Physics_Material SAND_STATIC
                }
                """);
            File.WriteAllText(Path.Combine(dir, "NoGuid.asset"), """
                Metadata
                {
                    Type SDG.Unturned.LandscapeMaterialAsset, Assembly-CSharp
                }
                Asset
                {
                    Physics_Material SAND_STATIC
                }
                """);
            // Dangling symlink with a .asset name: enumerated, but ReadAllText throws (IOException subclass).
            File.CreateSymbolicLink(Path.Combine(dir, "broken.asset"), Path.Combine(dir, "void"));

            LandscapePhysics physics = LandscapePhysics.ScanDirectory(dir);
            Assert.Equal(1, physics.Count);
            Assert.Equal("Foliage", physics.PhysicsNameOf(Guid.Parse("3d7717c2bc074401853b2fdacd9db1ba")));
            Assert.Null(physics.PhysicsNameOf(Guid.Parse("22b77c4c51514b0fbb66765eedf1a7f4")));

            Assert.Equal(0, LandscapePhysics.ScanDirectory(Path.Combine(dir, "missing")).Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
