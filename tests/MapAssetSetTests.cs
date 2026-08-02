using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class MapAssetSetTests
{
    // Level/Objects.dat at SAVEDATA_VERSION 12, every object in region (0,0).
    private static byte[] Objects(params Guid[] guids)
    {
        var w = new RiverBytes().Byte(12).UInt32(1);
        w.UInt16((ushort)guids.Length);
        foreach (Guid guid in guids)
        {
            w.Vector3(new Vector3(1, 2, 3)).Vector3(Vector3.Zero).Vector3(Vector3.One);
            w.UInt16(0);              // id
            w.Guid(guid);
            w.Byte(0);                // skin/state
            w.UInt32(42);             // instance id
            w.Guid(Guid.Empty).Int32(-1);
            w.Bool(true);
        }

        for (int i = 1; i < LevelObjects.WORLD_SIZE * LevelObjects.WORLD_SIZE; i++)
            w.UInt16(0);

        return w.ToArray();
    }

    // Terrain/Trees.dat at version 8.
    private static byte[] Trees(params Guid[] guids)
    {
        var w = new RiverBytes().Byte(8).Int32(guids.Length);
        foreach (Guid guid in guids)
        {
            w.Guid(guid).Vector3(new Vector3(4, 5, 6));
            w.Vector3(Vector3.Zero).Vector3(Vector3.One);
            w.Bool(false);
        }

        return w.ToArray();
    }

    // Foliage.blob v2: one tile with a single instance of the given asset.
    private static byte[] Foliage(Guid asset)
    {
        using var body = new MemoryStream();
        using (var bw = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(1);              // instanceCount
            bw.Write(0);              // assetIndex
            bw.Write(1);              // matrixCount
            foreach (float f in new[] { 1f, 0, 0, 0, 0, 1f, 0, 0, 0, 0, 1f, 0, 7f, 8f, 9f, 1f })
                bw.Write(f);
            bw.Write(false);          // clearWhenBaked
        }

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(2);               // version
            w.Write(1);               // tile count
            w.Write(0);               // tile x
            w.Write(0);               // tile y
            w.Write(0L);              // blob offset
            w.Write(1);               // asset count
            w.Write(asset.ToByteArray());
            w.Write(body.ToArray());
        }

        return ms.ToArray();
    }

    private static string FoliageAssetText(Guid guid) => $$"""
        Metadata
        {
            GUID {{guid:N}}
            Type SDG.Framework.Foliage.FoliageInstancedMeshInfoAsset
        }
        Asset
        {
            Mesh
            {
                Name core.masterbundle
                Path Terrain/Foliage/Grass/Grass_00_Mesh.fbx
            }
        }
        """;

    [Fact]
    public void Collect_TakesObjectsTreesAndResolvedFoliage_WithoutEmptyGuids()
    {
        using var dir = new TempDir();
        Guid objectGuid = Guid.NewGuid();
        Guid treeGuid = Guid.NewGuid();
        Guid foliageGuid = Guid.NewGuid();

        dir.Write(Path.Combine("map", "Level", "Objects.dat"), Objects(objectGuid, Guid.Empty));
        dir.Write(Path.Combine("map", "Terrain", "Trees.dat"), Trees(treeGuid));
        dir.Write(Path.Combine("map", "Foliage.blob"), Foliage(foliageGuid));
        dir.Write(Path.Combine("assets", "Grass", "Grass_00.asset"), FoliageAssetText(foliageGuid));

        HashSet<Guid> needed = MapAssetSet.Collect(Path.Combine(dir.Path, "map"),
            Path.Combine(dir.Path, "assets"));

        Assert.Equal(3, needed.Count);
        Assert.Contains(objectGuid, needed);
        Assert.Contains(treeGuid, needed);
        Assert.Contains(foliageGuid, needed);
        Assert.DoesNotContain(Guid.Empty, needed); // placeholder placements are not extractable
    }

    [Fact]
    public void Collect_UnresolvedFoliage_IsLeftOut()
    {
        // A foliage GUID with no asset behind it can never produce a mesh, so counting it would make the
        // cache look permanently incomplete.
        using var dir = new TempDir();
        dir.Write(Path.Combine("map", "Level", "Objects.dat"), Objects(Guid.NewGuid()));
        dir.Write(Path.Combine("map", "Foliage.blob"), Foliage(Guid.NewGuid()));

        HashSet<Guid> needed = MapAssetSet.Collect(Path.Combine(dir.Path, "map"),
            Path.Combine(dir.Path, "assets")); // the assets directory does not exist

        Assert.Single(needed);
    }

    [Fact]
    public void Collect_MapWithNoFiles_IsEmpty()
    {
        using var dir = new TempDir();
        Assert.Empty(MapAssetSet.Collect(Path.Combine(dir.Path, "nope"), dir.Path));
    }

    // Against the real game: PEI's needs must match what the map actually places.
    [RealDataFact(Map = "PEI")]
    public void Collect_RealPei_MatchesThePlacedObjects()
    {
        string install = GameData.Install!;
        string pei = GameData.Map("PEI")!;

        HashSet<Guid> needed = MapAssetSet.Collect(pei, Path.Combine(install, "Bundles", "Assets"));

        Assert.NotEmpty(needed);
        Assert.DoesNotContain(Guid.Empty, needed);

        var placed = new HashSet<Guid>();
        foreach (PlacedObject o in LevelObjects.Load(Path.Combine(pei, "Level", "Objects.dat")))
            placed.Add(o.Guid);
        foreach (PlacedTree t in LevelTrees.Load(Path.Combine(pei, "Terrain", "Trees.dat")))
            placed.Add(t.Guid);

        Assert.Subset(needed, placed); // every placed GUID (bar the empty one) is in the needed set
    }
}
