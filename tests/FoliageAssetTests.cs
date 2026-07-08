using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using Xunit;

namespace UnturnedGodot.Tests;

public class FoliageAssetTests
{
    private const string GrassAsset = """
        Metadata
        {
            GUID c928fb99bae9434795563319a64f6461
            Type SDG.Framework.Foliage.FoliageInstancedMeshInfoAsset, Assembly-CSharp
        }
        Asset
        {
            Mesh
            {
                Name core.masterbundle
                Path Terrain/Foliage/Grass/Grass_00_Mesh.fbx
            }
            Material
            {
                Name core.masterbundle
                Path Terrain/Foliage/Grass/PEI/Grass_00_Material.mat
            }
            Cast_Shadows true
            Draw_Distance 64
        }
        """;

    [Fact]
    public void TryParse_ReadsMeshMaterialAndSettings()
    {
        Assert.True(FoliageAsset.TryParse(DatParser.Parse(GrassAsset), out FoliageAsset asset));

        Assert.Equal(new Guid("c928fb99bae9434795563319a64f6461"), asset.Guid);
        Assert.Equal("Terrain/Foliage/Grass/Grass_00_Mesh.fbx", asset.MeshPath);
        Assert.Equal("Terrain/Foliage/Grass/PEI/Grass_00_Material.mat", asset.MaterialPath);
        Assert.True(asset.CastShadows);
        Assert.Equal(64, asset.DrawDistance);
    }

    [Fact]
    public void TryParse_DefaultsShadowsAndDrawDistance()
    {
        const string minimal = """
            Metadata
            {
                GUID 703378589e9f404db3bf2d539b740593
                Type FoliageInstancedMeshInfoAsset
            }
            Asset
            {
                Mesh
                {
                    Path a/b.fbx
                }
            }
            """;
        Assert.True(FoliageAsset.TryParse(DatParser.Parse(minimal), out FoliageAsset asset));
        Assert.False(asset.CastShadows);      // absent -> false
        Assert.Equal(-1, asset.DrawDistance); // absent -> global (-1)
        Assert.Equal(string.Empty, asset.MaterialPath);
    }

    [Fact]
    public void TryParse_WrongType_Fails()
    {
        const string other = """
            Metadata
            {
                GUID c928fb99bae9434795563319a64f6461
                Type SomeOtherAsset
            }
            Asset
            {
                Mesh
                {
                    Path a.fbx
                }
            }
            """;
        Assert.False(FoliageAsset.TryParse(DatParser.Parse(other), out _));
    }

    [Fact]
    public void TryParse_MissingBlocksOrFields_Fails()
    {
        // No Metadata block (so no GUID).
        const string noMetadata = """
            Asset
            {
                Mesh
                {
                    Path a.fbx
                }
            }
            """;
        Assert.False(FoliageAsset.TryParse(DatParser.Parse(noMetadata), out _));

        // Metadata without a GUID.
        const string noGuid = """
            Metadata
            {
                Type FoliageInstancedMeshInfoAsset
            }
            Asset
            {
                Mesh { Path a.fbx }
            }
            """;
        Assert.False(FoliageAsset.TryParse(DatParser.Parse(noGuid), out _));

        // Metadata but no Asset block.
        const string noAsset = """
            Metadata
            {
                GUID c928fb99bae9434795563319a64f6461
                Type FoliageInstancedMeshInfoAsset
            }
            """;
        Assert.False(FoliageAsset.TryParse(DatParser.Parse(noAsset), out _));

        // Mesh block present but without a Path.
        const string noMeshPath = """
            Metadata
            {
                GUID c928fb99bae9434795563319a64f6461
                Type FoliageInstancedMeshInfoAsset
            }
            Asset
            {
                Mesh
                {
                    Name core.masterbundle
                }
            }
            """;
        Assert.False(FoliageAsset.TryParse(DatParser.Parse(noMeshPath), out _));

        // Asset block without a Mesh block at all.
        const string noMesh = """
            Metadata
            {
                GUID c928fb99bae9434795563319a64f6461
                Type FoliageInstancedMeshInfoAsset
            }
            Asset
            {
                Material { Path a.mat }
            }
            """;
        Assert.False(FoliageAsset.TryParse(DatParser.Parse(noMesh), out _));

        // No Type at all (so it can't be identified as foliage).
        const string noType = """
            Metadata
            {
                GUID c928fb99bae9434795563319a64f6461
            }
            Asset
            {
                Mesh { Path a.fbx }
            }
            """;
        Assert.False(FoliageAsset.TryParse(DatParser.Parse(noType), out _));
    }

    [Fact]
    public void TryParse_MaterialWithoutPath_LeavesMaterialEmpty()
    {
        const string noMatPath = """
            Metadata
            {
                GUID c928fb99bae9434795563319a64f6461
                Type FoliageInstancedMeshInfoAsset
            }
            Asset
            {
                Mesh
                {
                    Path a.fbx
                }
                Material
                {
                    Name core.masterbundle
                }
            }
            """;
        Assert.True(FoliageAsset.TryParse(DatParser.Parse(noMatPath), out FoliageAsset asset));
        Assert.Equal(string.Empty, asset.MaterialPath);
    }

    [Fact]
    public void ScanForGuids_KeepsOnlyNeeded()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"foliage-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "grass.asset"), GrassAsset);
            // A non-foliage .asset in the same tree must be parsed but rejected by TryParse.
            File.WriteAllText(Path.Combine(dir, "other.asset"), "Metadata\n{\n\tType SomethingElse\n}\n");
            var needed = new HashSet<Guid> { new("c928fb99bae9434795563319a64f6461") };

            Dictionary<Guid, FoliageAsset> found = FoliageAsset.ScanForGuids(dir, needed);
            Assert.Equal("Terrain/Foliage/Grass/Grass_00_Mesh.fbx", Assert.Single(found).Value.MeshPath);

            // A GUID the map doesn't use is dropped.
            Assert.Empty(FoliageAsset.ScanForGuids(dir, new HashSet<Guid> { Guid.NewGuid() }));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ScanForGuids_UnreadableFile_IsSkipped()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"foliage-broken-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "grass.asset"), GrassAsset);
            // Dangling symlink with a .asset name: enumerated, but ReadAllText throws (IOException subclass).
            File.CreateSymbolicLink(Path.Combine(dir, "broken.asset"),
                Path.Combine(dir, "does_not_exist.asset"));

            var needed = new HashSet<Guid> { new("c928fb99bae9434795563319a64f6461") };
            Assert.Single(FoliageAsset.ScanForGuids(dir, needed)); // the good one; the broken one skipped
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ScanForGuids_MissingDirectory_ReturnsEmpty() =>
        Assert.Empty(FoliageAsset.ScanForGuids(Path.Combine(Path.GetTempPath(), "no-such-dir-xyz"),
            new HashSet<Guid> { Guid.NewGuid() }));
}
