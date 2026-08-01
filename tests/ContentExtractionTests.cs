using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests;

public class ContentExtractionTests
{
    private static readonly Guid CoreGuid = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid ModGuid = Guid.Parse("22222222222222222222222222222222");
    private static readonly Guid FoliageGuid = Guid.Parse("33333333333333333333333333333333");
    private static readonly Guid NoBundleGuid = Guid.Parse("44444444444444444444444444444444");

    [Fact]
    public void ScanAssets_MergesObjectsAndTreesWithCoreTakingPrecedence()
    {
        using var dir = new TempDir();
        IReadOnlyList<ContentSource> sources = BuildSources(dir);

        ObjectAssetDatabase db = ContentExtraction.ScanAssets(sources);

        Assert.Equal(4, db.Count);
        Assert.True(sources[0].Owns(db.ResolveByGuid(CoreGuid)!.Directory));
        Assert.NotNull(db.ResolveByGuid(ModGuid));
        Assert.NotNull(db.ResolveByGuid(FoliageGuid));
        Assert.NotNull(db.ResolveByGuid(NoBundleGuid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScanAssets_UnreadableTreeDoesNotDiscardOtherContent(bool unauthorized)
    {
        using var dir = new TempDir();
        IReadOnlyList<ContentSource> sources = BuildSources(dir);
        ContentSource mod = Assert.Single(sources, source => source.Name == "california2.masterbundle");

        ObjectAssetDatabase db = ContentExtraction.ScanAssets(sources, path =>
        {
            if (path == mod.ObjectsDir)
                throw unauthorized
                    ? new UnauthorizedAccessException("test tree is unreadable")
                    : new IOException("test tree disappeared");
            return ObjectAssetDatabase.ScanDirectory(path);
        });

        Assert.NotNull(db.ResolveByGuid(CoreGuid));
        Assert.Null(db.ResolveByGuid(ModGuid));
        // The mod's independent Resources tree and later sources are still scanned.
        Assert.NotNull(db.ResolveByGuid(FoliageGuid));
        Assert.NotNull(db.ResolveByGuid(NoBundleGuid));
    }

    [Fact]
    public void Plan_RoutesEachAssetAndFoliageToItsBundleAndChecksBothCaches()
    {
        using var dir = new TempDir();
        IReadOnlyList<ContentSource> sources = BuildSources(dir);
        ObjectAssetDatabase db = ContentExtraction.ScanAssets(sources);
        ContentSource core = Assert.Single(sources, source => source.IsCore);
        ContentSource mod = Assert.Single(sources, source => source.Name == "california2.masterbundle");
        ContentSource layers = Assert.Single(sources, source => source.Name == "layers.masterbundle");
        int modIndex = IndexOf(sources, mod);

        string meshes = Directory.CreateDirectory(Path.Combine(dir.Path, "mesh-cache")).FullName;
        string textures = Directory.CreateDirectory(Path.Combine(dir.Path, "texture-cache")).FullName;
        WriteMesh(meshes, CoreGuid, TextureKey.For(core.CacheTag, 17));
        ExtractionIndex.RecordMeshOwner(meshes, CoreGuid, core.BundlePath,
            ExtractionIndex.StampFor(core.BundlePath));

        Guid unknown = Guid.Parse("55555555555555555555555555555555");
        ExtractionIndex.Save(Path.Combine(meshes, ExtractionIndex.FileNameFor(core.BundlePath)),
            ExtractionIndex.StampFor(core.BundlePath), new[] { unknown });

        FoliageAsset foliage = ParseFoliage();
        var ownedFoliage = new Dictionary<Guid, FoliageAsset.Owned>
        {
            [foliage.Guid] = new(foliage, modIndex),
        };
        var layerBundles = new HashSet<string>(StringComparer.Ordinal) { layers.BundlePath };

        List<ContentExtraction.BundlePlan> plans = ContentExtraction.Plan(sources, meshes, textures,
            new[] { Guid.Empty, CoreGuid, ModGuid, NoBundleGuid, unknown }, db, ownedFoliage,
            layerBundles);

        Assert.Equal(3, plans.Count);

        ContentExtraction.BundlePlan corePlan = Assert.Single(plans, plan => plan.Source == core);
        Assert.Equal(new HashSet<Guid> { CoreGuid, unknown }, corePlan.Needed);
        Assert.Empty(corePlan.Missing);
        Assert.Equal(new HashSet<long> { 17 }, corePlan.MissingTextures);

        ContentExtraction.BundlePlan modPlan = Assert.Single(plans, plan => plan.Source == mod);
        Assert.Equal(new HashSet<Guid> { ModGuid, FoliageGuid }, modPlan.Needed);
        Assert.Equal(modPlan.Needed, modPlan.Missing);
        Assert.Same(foliage, Assert.Single(modPlan.Foliage));

        ContentExtraction.BundlePlan layerPlan = Assert.Single(plans, plan => plan.Source == layers);
        Assert.Empty(layerPlan.Needed);
        Assert.False(layerPlan.NeedsExtraction);

        Assert.Equal(3, ContentExtraction.TotalMissing(plans));
        Assert.Equal(new[]
        {
            "[extract] core.masterbundle: 0/2 meshes and 1 textures not cached",
            "[extract] california2.masterbundle: 2/2 meshes and 0 textures not cached",
        }, ContentExtraction.PendingReports(plans));
        Assert.DoesNotContain(plans, plan => plan.Needed.Contains(NoBundleGuid));
    }

    [Fact]
    public void Plan_WithNothingNeededProducesNoWork()
    {
        using var dir = new TempDir();
        IReadOnlyList<ContentSource> sources = BuildSources(dir);

        List<ContentExtraction.BundlePlan> plans = ContentExtraction.Plan(sources, dir.Path, dir.Path,
            Array.Empty<Guid>(), new ObjectAssetDatabase(),
            new Dictionary<Guid, FoliageAsset.Owned>());

        Assert.Empty(plans);
        Assert.Equal(0, ContentExtraction.TotalMissing(plans));
        Assert.Empty(ContentExtraction.PendingReports(plans));
    }

    [Fact]
    public void ExtractionIndexFileNameIsStableAndNamespacedBySourcePath()
    {
        string first = Path.Combine(Path.GetTempPath(), "workshop", "100", "shared_linux.masterbundle");
        string second = Path.Combine(Path.GetTempPath(), "workshop", "200", "shared_linux.masterbundle");

        string firstName = ExtractionIndex.FileNameFor(first);
        Assert.Equal(firstName, ExtractionIndex.FileNameFor(first));
        Assert.NotEqual(firstName, ExtractionIndex.FileNameFor(second));
        Assert.StartsWith("extraction_shared-linux-", firstName, StringComparison.Ordinal);
        Assert.EndsWith(".index", firstName, StringComparison.Ordinal);
    }

    private static IReadOnlyList<ContentSource> BuildSources(TempDir dir)
    {
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        string bundles = Path.Combine("steamapps", "common", "Unturned", "Bundles");
        dir.Write(Path.Combine(bundles, "MasterBundle.dat"),
            "Asset_Bundle_Name core.masterbundle\nAsset_Prefix Assets/Core\n");
        dir.Write(Path.Combine(bundles, "core_linux.masterbundle"), new byte[] { 1 });
        WriteObject(dir, Path.Combine(bundles, "Objects", "Core", "Core.dat"), CoreGuid);
        WriteObject(dir, Path.Combine(bundles, "Trees", "CoreTree", "CoreTree.dat"), FoliageGuid);

        string workshop = Path.Combine("steamapps", "workshop", "content", "304930");
        string mod = Path.Combine(workshop, "100");
        dir.Write(Path.Combine(mod, "MasterBundle.dat"),
            "Asset_Bundle_Name california2.masterbundle\nAsset_Prefix Assets/California\n");
        dir.Write(Path.Combine(mod, "california2_linux.masterbundle"), new byte[] { 2 });
        WriteObject(dir, Path.Combine(mod, "Objects", "Mod", "Mod.dat"), ModGuid);
        // Duplicate core GUID: ScanAssets must keep the official declaration registered first.
        WriteObject(dir, Path.Combine(mod, "Objects", "Duplicate", "Duplicate.dat"), CoreGuid);
        WriteObject(dir, Path.Combine(mod, "Resources", "Grass", "Grass.dat"), FoliageGuid);

        string layerOnly = Path.Combine(workshop, "200");
        dir.Write(Path.Combine(layerOnly, "MasterBundle.dat"),
            "Asset_Bundle_Name layers.masterbundle\nAsset_Prefix Assets/Layers\n");
        dir.Write(Path.Combine(layerOnly, "layers_linux.masterbundle"), new byte[] { 3 });
        dir.Write(Path.Combine(layerOnly, "Assets", "Landscapes", "Dirt", "Dirt.asset"), "Metadata {}\n");

        string noBundle = Path.Combine(workshop, "300");
        dir.Write(Path.Combine(noBundle, "MasterBundle.dat"),
            "Asset_Bundle_Name absent.masterbundle\nAsset_Prefix Assets/Absent\n");
        WriteObject(dir, Path.Combine(noBundle, "Objects", "Absent", "Absent.dat"), NoBundleGuid);

        return ContentSource.Discover(install, UnturnedInstall.Platform.Linux);
    }

    private static void WriteObject(TempDir dir, string path, Guid guid) =>
        dir.Write(path, $"GUID {guid:N}\nType Small\n");

    private static FoliageAsset ParseFoliage()
    {
        DatDictionary data = DatParser.Parse($$"""
            Metadata
            {
                GUID {{FoliageGuid:N}}
                Type SDG.Framework.Foliage.FoliageInstancedMeshInfoAsset
            }
            Asset
            {
                Mesh
                {
                    Path Terrain/Foliage/Grass.fbx
                }
                Material
                {
                    Path Terrain/Foliage/Grass.mat
                }
            }
            """);
        Assert.True(FoliageAsset.TryParse(data, out FoliageAsset foliage));
        return foliage;
    }

    private static int IndexOf(IReadOnlyList<ContentSource> sources, ContentSource wanted)
    {
        for (int i = 0; i < sources.Count; i++)
            if (sources[i] == wanted)
                return i;
        return -1;
    }

    private static void WriteMesh(string directory, Guid guid, string textureKey)
    {
        using FileStream stream = File.Create(Path.Combine(directory, guid.ToString("N") + ".mesh"));
        MeshCache.Write(stream, Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector2>(),
            new[] { new CachedSubmesh(Array.Empty<int>(), Colors.White, textureKey,
                UnityMaterial.Blend.Cutout) });
    }
}
