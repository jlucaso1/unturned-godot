using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class LandscapeMaterialAssetTests
{
    private const string PeiGrass = """
        Metadata
        {
            GUID 3d7717c2bc074401853b2fdacd9db1ba
            Type SDG.Unturned.LandscapeMaterialAsset, Assembly-CSharp
        }
        Asset
        {
            ID 0
            Texture
            {
                Name core.masterbundle
                Path Terrain/Materials/PEI/Grass_00.png
            }
            Mask
            {
                Name
                Path
            }
            Physics_Material FOLIAGE_STATIC
        }
        """;

    [Fact]
    public void TryParse_ReadsTheBundleReferenceAndSurface()
    {
        Assert.True(LandscapeMaterialAsset.TryParse(DatParser.Parse(PeiGrass),
            out LandscapeMaterialAsset asset));

        Assert.Equal(Guid.Parse("3d7717c2bc074401853b2fdacd9db1ba"), asset.Guid);
        Assert.Equal("core.masterbundle", asset.TextureBundle);
        Assert.Equal("Terrain/Materials/PEI/Grass_00.png", asset.TexturePath);
        Assert.Equal("FOLIAGE_STATIC", asset.PhysicsMaterial);
        Assert.Equal("", asset.MaskPath); // present but empty in the file
    }

    [Fact]
    public void TryParse_RejectsOtherAssetTypesAndBrokenIdentity()
    {
        // A foliage asset lives in the same tree and must not be taken for a landscape material.
        Assert.False(LandscapeMaterialAsset.TryParse(DatParser.Parse(PeiGrass.Replace(
            "SDG.Unturned.LandscapeMaterialAsset", "SDG.Framework.Foliage.FoliageInstancedMeshInfoAsset")),
            out _));

        // No Metadata block at all, and Metadata without the GUID that identifies the material.
        Assert.False(LandscapeMaterialAsset.TryParse(DatParser.Parse("Asset\n{\nID 0\n}\n"), out _));
        Assert.False(LandscapeMaterialAsset.TryParse(DatParser.Parse(
            PeiGrass.Replace("GUID 3d7717c2bc074401853b2fdacd9db1ba", "")), out _));

        // Metadata with a GUID but no Type: not identifiable as a landscape material either.
        Assert.False(LandscapeMaterialAsset.TryParse(DatParser.Parse(PeiGrass.Replace(
            "Type SDG.Unturned.LandscapeMaterialAsset, Assembly-CSharp", "")), out _));
    }

    [Fact]
    public void TryParse_WithoutATextureBlock_StillYieldsTheMaterial()
    {
        Assert.True(LandscapeMaterialAsset.TryParse(DatParser.Parse("""
            Metadata
            {
                GUID 11111111111111111111111111111111
                Type SDG.Unturned.LandscapeMaterialAsset
            }
            Asset
            {
                ID 0
            }
            """), out LandscapeMaterialAsset asset));

        Assert.Equal("", asset.TextureBundle);
        Assert.Equal("", asset.TexturePath);
        Assert.Equal("", asset.MaskBundle);
        Assert.Equal("", asset.PhysicsMaterial);
    }

    [Fact]
    public void TryParse_TextureBlockMissingItsKeys_ReadsAsEmpty()
    {
        // A half-written reference (the block exists, the keys do not) must not throw.
        Assert.True(LandscapeMaterialAsset.TryParse(DatParser.Parse("""
            Metadata
            {
                GUID 11111111111111111111111111111111
                Type SDG.Unturned.LandscapeMaterialAsset
            }
            Asset
            {
                Texture
                {
                    ID 0
                }
            }
            """), out LandscapeMaterialAsset asset));

        Assert.Equal("", asset.TextureBundle);
        Assert.Equal("", asset.TexturePath);
    }

    [Fact]
    public void ScanDirectory_IndexesEveryMaterialByGuid()
    {
        using var dir = new TempDir();
        dir.Write(Path.Combine("PEI", "Grass.asset"), PeiGrass);
        dir.Write(Path.Combine("Yukon", "Snow.asset"), PeiGrass
            .Replace("3d7717c2bc074401853b2fdacd9db1ba", "22222222222222222222222222222222")
            .Replace("Terrain/Materials/PEI/Grass_00.png", "Terrain/Materials/Yukon/Snow_00.png"));
        dir.Write(Path.Combine("PEI", "English.dat"), "Name Grass");   // not an .asset: ignored
        dir.Write(Path.Combine("PEI", "Broken.asset"), "Metadata { }"); // parses, not a material

        Dictionary<Guid, LandscapeMaterialAsset> materials = LandscapeMaterialAsset.ScanDirectory(dir.Path);

        Assert.Equal(2, materials.Count);
        Assert.Equal("Terrain/Materials/Yukon/Snow_00.png",
            materials[Guid.Parse("22222222222222222222222222222222")].TexturePath);
    }

    [Fact]
    public void ScanDirectory_SkipsAnUnreadableAsset()
    {
        using var dir = new TempDir();
        dir.Write(Path.Combine("PEI", "Grass.asset"), PeiGrass);
        string locked = dir.Write(Path.Combine("PEI", "Locked.asset"), PeiGrass.Replace(
            "3d7717c2bc074401853b2fdacd9db1ba", "33333333333333333333333333333333"));

        using var handle = new FileStream(locked, FileMode.Open, System.IO.FileAccess.Read, FileShare.None);
        Dictionary<Guid, LandscapeMaterialAsset> materials = LandscapeMaterialAsset.ScanDirectory(dir.Path);

        Assert.Single(materials);
        Assert.True(materials.ContainsKey(Guid.Parse("3d7717c2bc074401853b2fdacd9db1ba")));
    }

    [Fact]
    public void ScanDirectory_InaccessibleNestedTree_DoesNotEscapeTheSource()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX permissions only

        using var dir = new TempDir();
        dir.Write("Grass.asset", PeiGrass);
        string locked = Path.Combine(dir.Path, "Locked");
        dir.Write(Path.Combine("Locked", "Snow.asset"), PeiGrass.Replace(
            "3d7717c2bc074401853b2fdacd9db1ba", "33333333333333333333333333333333"));
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            try
            {
                Directory.EnumerateFiles(locked).GetEnumerator().MoveNext();
                return; // running as root: modes do not apply
            }
            catch (UnauthorizedAccessException)
            {
                // expected
            }

            Dictionary<Guid, LandscapeMaterialAsset> materials =
                LandscapeMaterialAsset.ScanDirectory(dir.Path);
            Assert.DoesNotContain(Guid.Parse("33333333333333333333333333333333"), materials.Keys);
        }
        finally
        {
            File.SetUnixFileMode(locked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void ScanDirectory_MissingRoot_IsEmpty()
    {
        using var dir = new TempDir();
        Assert.Empty(LandscapeMaterialAsset.ScanDirectory(Path.Combine(dir.Path, "nope")));
    }

    [Fact]
    public void MergeFirstClaimants_PreservesTheEarlierSource()
    {
        using var first = new TempDir();
        using var second = new TempDir();
        const string guid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        first.Write("Grass.asset", PeiGrass.Replace("3d7717c2bc074401853b2fdacd9db1ba", guid));
        second.Write("Grass.asset", PeiGrass.Replace("3d7717c2bc074401853b2fdacd9db1ba", guid)
            .Replace("Terrain/Materials/PEI/Grass_00.png", "Terrain/Materials/Mod/Grass_00.png"));

        var merged = new Dictionary<Guid, LandscapeMaterialAsset>();
        LandscapeMaterialAsset.MergeFirstClaimants(merged, LandscapeMaterialAsset.ScanDirectory(first.Path));
        LandscapeMaterialAsset.MergeFirstClaimants(merged, LandscapeMaterialAsset.ScanDirectory(second.Path));

        Assert.Equal("Terrain/Materials/PEI/Grass_00.png", merged[Guid.Parse(guid)].TexturePath);
    }

    // Against the real game: every layer GUID PEI's tiles reference must resolve to a material whose
    // texture lives in the core master bundle.
    [RealDataFact(Map = "PEI")]
    public void RealPei_EveryTileLayerResolvesToACoreBundleTexture()
    {
        string install = GameData.Install!;
        string pei = GameData.Map("PEI")!;

        Dictionary<Guid, LandscapeMaterialAsset> materials = LandscapeMaterialAsset.ScanDirectory(
            Path.Combine(install, "Bundles", "Assets", "Landscapes"));
        Assert.NotEmpty(materials);

        Dictionary<(int x, int y), Guid[]> tiles = UnturnedGodot.Data.LevelHierarchy.ReadTileMaterials(
            Path.Combine(pei, "Level.hierarchy"));
        Assert.NotEmpty(tiles);

        foreach (Guid[] layers in tiles.Values)
            foreach (Guid guid in layers)
            {
                Assert.True(materials.TryGetValue(guid, out LandscapeMaterialAsset? material),
                    $"unresolved landscape material {guid:N}");
                Assert.Equal("core.masterbundle", material!.TextureBundle);
                Assert.StartsWith("Terrain/Materials/", material.TexturePath, StringComparison.Ordinal);
            }
    }
}
