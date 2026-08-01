using System;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class PhysicsMaterialAssetTests
{
    private const string FoliageDat = """
        Metadata
        {
            GUID 1c3a3c6664f14aa486d73528a5d9f92c
            Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp
        }
        Asset
        {
            UnityNames
            [
                Foliage
                Foliage_Static
            ]
            AudioDefs
            {
                BipedLand Effects/Physics/BipedLand/Grass/BipedLand_Grass.asset
                FootstepWalk Effects/Physics/Footstep/Grass_Walk/Footstep_Grass_Walk.asset
                FootstepRun Effects/Physics/Footstep/Grass_Run/Footstep_Grass_Run.asset
            }
        }
        """;

    private const string PeaksGrassDat = """
        Metadata
        {
            GUID 0a78583e84624e1ab275c89b55c2f688
            Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp
        }
        Asset
        {
            UnityNames
            [
                Peaks_Grass_Dry
            ]
            Fallback "1c3a3c6664f14aa486d73528a5d9f92c" // Foliage
        }
        """;

    private static PhysicsMaterialBank Bank()
    {
        var bank = new PhysicsMaterialBank();
        Assert.True(PhysicsMaterialAsset.TryParse(DatParser.Parse(FoliageDat), out PhysicsMaterialAsset foliage));
        Assert.True(PhysicsMaterialAsset.TryParse(DatParser.Parse(PeaksGrassDat), out PhysicsMaterialAsset peaks));
        bank.Add(foliage);
        bank.Add(peaks);
        return bank;
    }

    [Fact]
    public void Parse_ReadsNamesFallbackAndAudioDefs()
    {
        Assert.True(PhysicsMaterialAsset.TryParse(DatParser.Parse(PeaksGrassDat), out PhysicsMaterialAsset peaks));
        Assert.Equal(new[] { "Peaks_Grass_Dry" }, peaks.UnityNames);
        Assert.Equal(Guid.Parse("1c3a3c6664f14aa486d73528a5d9f92c"), peaks.Fallback);
        Assert.Empty(peaks.AudioDefs);
    }

    [Fact]
    public void FindAudioDefPath_DirectAndCaseInsensitive()
    {
        PhysicsMaterialBank bank = Bank();
        Assert.Equal("Effects/Physics/Footstep/Grass_Walk/Footstep_Grass_Walk.asset",
            bank.FindAudioDefPath("foliage_static", "footstepwalk"));
    }

    [Fact]
    public void FindAudioDefPath_WalksTheFallbackChain()
    {
        PhysicsMaterialBank bank = Bank();
        Assert.Equal("Effects/Physics/BipedLand/Grass/BipedLand_Grass.asset",
            bank.FindAudioDefPath("Peaks_Grass_Dry", "BipedLand"));
    }

    [Fact]
    public void FindAudioDefPath_UnknownNameOrKey_ReturnsNull()
    {
        PhysicsMaterialBank bank = Bank();
        Assert.Null(bank.FindAudioDefPath("Vibranium", "FootstepWalk"));
        Assert.Null(bank.FindAudioDefPath("Foliage", "Yodel"));
    }

    [Fact]
    public void FindAudioDefPath_SelfReferentialFallback_Terminates()
    {
        const string loop = """
            Metadata
            {
                GUID aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
                Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp
            }
            Asset
            {
                UnityNames
                [
                    Loop
                ]
                Fallback "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            }
            """;
        var bank = new PhysicsMaterialBank();
        Assert.True(PhysicsMaterialAsset.TryParse(DatParser.Parse(loop), out PhysicsMaterialAsset asset));
        bank.Add(asset);
        Assert.Null(bank.FindAudioDefPath("Loop", "FootstepWalk"));
    }

    [Fact]
    public void TryParse_RejectsOtherAssetTypes()
    {
        const string other = """
            Metadata
            {
                GUID bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
                Type SDG.Unturned.ItemAsset, Assembly-CSharp
            }
            Asset
            {
            }
            """;
        Assert.False(PhysicsMaterialAsset.TryParse(DatParser.Parse(other), out _));
        Assert.False(PhysicsMaterialAsset.TryParse(DatParser.Parse("Foo Bar"), out _));

        const string noGuid = """
            Metadata
            {
                Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp
            }
            Asset
            {
            }
            """;
        Assert.False(PhysicsMaterialAsset.TryParse(DatParser.Parse(noGuid), out _));

        const string noType = """
            Metadata
            {
                GUID cccccccccccccccccccccccccccccccc
            }
            Asset
            {
            }
            """;
        Assert.False(PhysicsMaterialAsset.TryParse(DatParser.Parse(noType), out _));
    }

    [Fact]
    public void TryParse_SkipsNonValueListEntriesAndNestedAudioDefs()
    {
        const string odd = """
            Metadata
            {
                GUID dddddddddddddddddddddddddddddddd
                Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp
            }
            Asset
            {
                UnityNames
                [
                    Odd
                    {
                        Junk 1
                    }
                ]
                AudioDefs
                {
                    FootstepWalk Effects/Walk.asset
                    Nested
                    {
                        Junk 1
                    }
                }
            }
            """;
        Assert.True(PhysicsMaterialAsset.TryParse(DatParser.Parse(odd), out PhysicsMaterialAsset asset));
        Assert.Equal(new[] { "Odd" }, asset.UnityNames);
        Assert.Single(asset.AudioDefs);
    }

    [Fact]
    public void FindAudioDefPath_DanglingFallback_ReturnsNull()
    {
        const string dangling = """
            Metadata
            {
                GUID eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee
                Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp
            }
            Asset
            {
                UnityNames
                [
                    Dangling
                ]
                Fallback "ffffffffffffffffffffffffffffffff"
            }
            """;
        var bank = new PhysicsMaterialBank();
        Assert.True(PhysicsMaterialAsset.TryParse(DatParser.Parse(dangling), out PhysicsMaterialAsset asset));
        bank.Add(asset);
        Assert.Null(bank.FindAudioDefPath("Dangling", "FootstepWalk"));
    }

    [Fact]
    public void ScanDirectory_LoadsAssetsRecursively()
    {
        string dir = Directory.CreateTempSubdirectory("physmat").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Foliage.asset"), FoliageDat);
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "sub", "Peaks.asset"), PeaksGrassDat);

            // Dangling symlink with a .asset name: enumerated, but ReadAllText throws (IOException subclass).
            File.CreateSymbolicLink(Path.Combine(dir, "broken.asset"), Path.Combine(dir, "void"));

            PhysicsMaterialBank bank = PhysicsMaterialBank.ScanDirectory(dir);
            Assert.Equal(2, bank.Count);
            Assert.NotNull(bank.FindAudioDefPath("Peaks_Grass_Dry", "FootstepRun"));

            Assert.Equal(0, PhysicsMaterialBank.ScanDirectory(Path.Combine(dir, "missing")).Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ScanDirectories_MergesEveryAssetTree()
    {
        // The game's assets plus a workshop mod's: a map placed by the mod resolves surfaces from both.
        using var game = new TempDir();
        using var mod = new TempDir();
        game.Write(Path.Combine("PhysicsMaterials", "Concrete.asset"), Asset("CONCRETE",
            "11111111111111111111111111111111"));
        mod.Write(Path.Combine("PhysicsMaterials", "CA_Sand.asset"), Asset("CA_SAND",
            "22222222222222222222222222222222"));

        PhysicsMaterialBank merged = PhysicsMaterialBank.ScanDirectories(new[]
        {
            Path.Combine(game.Path, "PhysicsMaterials"),
            Path.Combine(mod.Path, "PhysicsMaterials"),
            Path.Combine(mod.Path, "Missing"), // a source that ships none
        });

        Assert.Equal(2, merged.Count);
        Assert.NotNull(merged.FindAudioDefPath("CONCRETE", "FootstepWalk"));
        Assert.NotNull(merged.FindAudioDefPath("CA_SAND", "FootstepWalk"));
    }

    private static string Asset(string unityName, string guid) => $$"""
        Metadata
        {
            GUID {{guid}}
            Type SDG.Unturned.PhysicsMaterialAsset
        }
        Asset
        {
            UnityNames
            [
                {{unityName}}
            ]
            AudioDefs
            {
                FootstepWalk Sounds/Footsteps/{{unityName}}/Walk.asset
            }
        }
        """;

    [Fact]
    public void FindAudioDef_NamesTheAssetThatDefinedTheKey_AndWhereItCameFrom()
    {
        // The definition is packaged in the bundle of whichever source shipped THAT asset, so a surface
        // that falls back to a core material must report the core material, not itself: extracting the
        // audio from the mod's bundle would find nothing and leave the surface silent.
        using var dir = new TempDir();
        dir.Write("Core/Foliage.asset", """
            Metadata
            {
                GUID 11111111111111111111111111111111
                Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp
            }
            Asset
            {
                UnityNames
                [
                    FOLIAGE_STATIC
                ]
                AudioDefs
                {
                    FootstepWalk Sounds/Footsteps/Foliage_Walk.asset
                }
            }
            """);
        dir.Write("Mod/CaliforniaGrass.asset", """
            Metadata
            {
                GUID 22222222222222222222222222222222
                Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp
            }
            Asset
            {
                UnityNames
                [
                    CA_GRASS
                ]
                Fallback 11111111111111111111111111111111
                AudioDefs
                {
                    FootstepRun Sounds/CA/Grass_Run.asset
                }
            }
            """);

        PhysicsMaterialBank bank = PhysicsMaterialBank.ScanDirectory(dir.Path);

        // Defined by the mod material itself.
        (string Path, PhysicsMaterialAsset Owner)? run = bank.FindAudioDef("CA_GRASS", "FootstepRun");
        Assert.NotNull(run);
        Assert.Equal("Sounds/CA/Grass_Run.asset", run!.Value.Path);
        Assert.EndsWith("Mod", run.Value.Owner.Directory);

        // Reached through the fallback: the core asset is the owner.
        (string Path, PhysicsMaterialAsset Owner)? walk = bank.FindAudioDef("CA_GRASS", "FootstepWalk");
        Assert.NotNull(walk);
        Assert.Equal("Sounds/Footsteps/Foliage_Walk.asset", walk!.Value.Path);
        Assert.EndsWith("Core", walk.Value.Owner.Directory);

        // The old shape still answers the same paths.
        Assert.Equal("Sounds/CA/Grass_Run.asset", bank.FindAudioDefPath("CA_GRASS", "FootstepRun"));
        Assert.Null(bank.FindAudioDef("CA_GRASS", "BipedLand"));
        Assert.Null(bank.FindAudioDef("NOTHING", "FootstepWalk"));
    }
}
