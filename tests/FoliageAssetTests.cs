using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Tests.Helpers;
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

    private const string ModGrassAsset = """
        Metadata
        {
            GUID 703378589e9f404db3bf2d539b740593
            Type SDG.Framework.Foliage.FoliageInstancedMeshInfoAsset, Assembly-CSharp
        }
        Asset
        {
            Mesh
            {
                Name california2.masterbundle
                Path Terrain/Foliage/Grass/CA_Grass_Mesh.fbx
            }
        }
        """;

    // A Steam library with the game and one workshop mod, each shipping its own foliage asset.
    private static string LibraryWithMod(TempDir dir)
    {
        const string core = "Asset_Bundle_Name core.masterbundle\nAsset_Prefix Assets/Core\n";
        const string mod = "Asset_Bundle_Name california2.masterbundle\nAsset_Prefix Assets/CA\n";
        string game = Path.Combine("steamapps", "common", "Unturned", "Bundles");
        dir.Write(Path.Combine(game, "MasterBundle.dat"), core);
        dir.Write(Path.Combine(game, "core_linux.masterbundle"), new byte[] { 1 });
        dir.Write(Path.Combine(game, "Assets", "Foliage", "grass.asset"), GrassAsset);
        Directory.CreateDirectory(Path.Combine(dir.Path, game, "Objects"));

        string item = Path.Combine("steamapps", "workshop", "content", "304930", "3711646503");
        dir.Write(Path.Combine(item, "MasterBundle.dat"), mod);
        dir.Write(Path.Combine(item, "california2_linux.masterbundle"), new byte[] { 1 });
        dir.Write(Path.Combine(item, "Objects", "CA_Sign", "CA_Sign.dat"),
            "GUID 0517b7a03b844929856fc4f72701fca9\nType Medium\n");
        dir.Write(Path.Combine(item, "Assets", "Foliage", "ca_grass.asset"), ModGrassAsset);

        return Path.Combine(dir.Path, "steamapps", "common", "Unturned");
    }

    // The workshop-foliage regression: a mod's own grass resolves, and to the mod's bundle. Scanning only
    // the game's Assets folder left that GUID unresolved, so its mesh was never extracted and the map
    // rendered with no foliage.
    [Fact]
    public void ScanSources_ResolvesModFoliageToItsOwnBundle()
    {
        using var dir = new TempDir();
        IReadOnlyList<ContentSource> sources =
            ContentSource.Discover(LibraryWithMod(dir), UnturnedInstall.Platform.Linux);

        var core = new Guid("c928fb99bae9434795563319a64f6461");
        var mod = new Guid("703378589e9f404db3bf2d539b740593");
        Dictionary<Guid, FoliageAsset.Owned> owned =
            FoliageAsset.ScanSources(sources, new HashSet<Guid> { core, mod });

        Assert.Equal(2, owned.Count);
        Assert.Equal(0, owned[core].SourceIndex);   // the game's
        Assert.Equal(1, owned[mod].SourceIndex);    // the mod's
        Assert.Equal("Terrain/Foliage/Grass/CA_Grass_Mesh.fbx", owned[mod].Asset.MeshPath);
    }

    [Fact]
    public void ScanSources_KeepsOnlyNeededGuids()
    {
        using var dir = new TempDir();
        IReadOnlyList<ContentSource> sources =
            ContentSource.Discover(LibraryWithMod(dir), UnturnedInstall.Platform.Linux);

        Assert.Empty(FoliageAsset.ScanSources(sources, new HashSet<Guid> { Guid.NewGuid() }));
    }

    // Unturned rejects an asset whose GUID is already registered, so the game's copy wins over a mod's.
    [Fact]
    public void ScanSources_FirstSourceWinsOnADuplicateGuid()
    {
        using var dir = new TempDir();
        string install = LibraryWithMod(dir);
        dir.Write(Path.Combine("steamapps", "workshop", "content", "304930", "3711646503", "Assets",
            "Foliage", "clash.asset"), GrassAsset); // same GUID as the game's grass
        IReadOnlyList<ContentSource> sources =
            ContentSource.Discover(install, UnturnedInstall.Platform.Linux);

        var core = new Guid("c928fb99bae9434795563319a64f6461");
        Dictionary<Guid, FoliageAsset.Owned> owned =
            FoliageAsset.ScanSources(sources, new HashSet<Guid> { core });

        Assert.Equal(0, owned[core].SourceIndex);
        Assert.Equal("Terrain/Foliage/Grass/Grass_00_Mesh.fbx", owned[core].Asset.MeshPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScanSources_FailedWorkshopTreeDoesNotDiscardOtherSources(bool unauthorized)
    {
        using var dir = new TempDir();
        IReadOnlyList<ContentSource> sources =
            ContentSource.Discover(LibraryWithMod(dir), UnturnedInstall.Platform.Linux);
        var core = new Guid("c928fb99bae9434795563319a64f6461");
        var mod = new Guid("703378589e9f404db3bf2d539b740593");

        Dictionary<Guid, FoliageAsset.Owned> owned = FoliageAsset.ScanSources(sources,
            new HashSet<Guid> { core, mod }, (root, needed) =>
            {
                if (root == sources[1].AssetsDir)
                    throw unauthorized
                        ? new UnauthorizedAccessException("workshop tree is locked")
                        : new IOException("workshop tree was removed");
                return FoliageAsset.ScanForGuids(root, needed);
            });

        Assert.True(owned.ContainsKey(core));
        Assert.False(owned.ContainsKey(mod));
    }
}
