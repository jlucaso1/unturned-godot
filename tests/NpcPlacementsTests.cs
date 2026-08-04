using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class NpcPlacementsTests
{
    private static ObjectAsset Asset(Guid guid, ushort id, string type) =>
        ObjectAsset.TryParse(DatParser.Parse($"GUID {guid:N}\nID {id}\nType {type}\n"), null, out ObjectAsset? a)
            ? a
            : throw new InvalidOperationException("asset fixture did not parse");

    private static ObjectAssetDatabase DatabaseWith(params ObjectAsset[] assets)
    {
        var db = new ObjectAssetDatabase();
        foreach (ObjectAsset asset in assets)
            db.Add(asset);
        return db;
    }

    private static PlacedObject At(float x, Guid guid) =>
        new(new Vector3(x, 0, 0), Vector3.Zero, Vector3.One, 0, guid);

    [Fact]
    public void Partition_TakesTheNpcsAndKeepsEverythingElseInOrder()
    {
        var npc = Guid.NewGuid();
        var house = Guid.NewGuid();
        var placements = new List<PlacedObject> { At(1, house), At(2, npc), At(3, house), At(4, npc) };

        List<PlacedObject> npcs = NpcPlacements.Partition(placements,
            DatabaseWith(Asset(npc, 753, "NPC"), Asset(house, 12, "Large")));

        Assert.Equal(new[] { 2f, 4f }, npcs.ConvertAll(p => p.Position.X));
        Assert.Equal(new[] { 1f, 3f }, placements.ConvertAll(p => p.Position.X));
    }

    [Fact]
    public void Partition_LeavesAMapWithNoNpcsAlone()
    {
        var house = Guid.NewGuid();
        var placements = new List<PlacedObject> { At(1, house), At(2, house) };

        Assert.Empty(NpcPlacements.Partition(placements, DatabaseWith(Asset(house, 12, "Large"))));
        Assert.Equal(2, placements.Count);
    }

    [Fact]
    public void Partition_UnresolvedPlacementsStay()
    {
        // Nothing knows what these are, so they keep the placeholder box the object build gives them
        // rather than being quietly taken for characters.
        var placements = new List<PlacedObject> { At(1, Guid.NewGuid()) };

        Assert.Empty(NpcPlacements.Partition(placements, DatabaseWith()));
        Assert.Single(placements);
    }

    [Fact]
    public void VendorAndQuestRecordsAreNotScanned()
    {
        // Bundles/NPCs also holds Dialogues, Quests and Vendors — records with a GUID, an ID and a Type
        // that nothing places. The source points at the Characters subtree alone so they never reach the
        // database: parsed, they are Unknown-type assets that take a slot in the object id table, and the
        // game's own T.Rickster_Blackmarket vendor is ID 27.
        using var dir = new TempDir();
        dir.Write(Path.Combine("Bundles", "MasterBundle.dat"), "Asset_Bundle_Name core.masterbundle\n");
        dir.Write(Path.Combine("Bundles", "core_linux.masterbundle"), "x");
        dir.Write(Path.Combine("Bundles", "NPCs", "Characters", "Scout", "Asset.dat"),
            "GUID 3afa17f491c441588a03f1f908712f9b\nType NPC\nID 753\n");
        dir.Write(Path.Combine("Bundles", "NPCs", "Vendors", "Blackmarket", "Asset.dat"),
            "GUID fb7bcb201ef74a5ba5262e57f9b69b81\nType Vendor\nID 27\n");

        ObjectAssetDatabase db = ContentExtraction.ScanAssets(
            ContentSource.Discover(dir.Path, UnturnedInstall.Platform.Linux));

        Assert.NotNull(db.ResolveByGuid(new Guid("3afa17f491c441588a03f1f908712f9b")));
        Assert.Null(db.ResolveByGuid(new Guid("fb7bcb201ef74a5ba5262e57f9b69b81")));
        Assert.Null(db.ResolveById(27)); // the vendor did not claim an object id
    }

    [Fact]
    public void NpcLegacyIdsDoNotShadowObjects()
    {
        // EAssetType.NPC ids run straight through the object range — Russia's NPCs are 752..832 — so
        // scanning Bundles/NPCs into the shared table would have each one shadow an unrelated object.
        var npc = Guid.NewGuid();
        var house = Guid.NewGuid();
        ObjectAssetDatabase db = DatabaseWith(Asset(npc, 753, "NPC"), Asset(house, 753, "Large"));

        Assert.Equal(house, db.ResolveById(753)!.Guid);
        Assert.Equal(npc, db.ResolveByGuid(npc)!.Guid);
    }
}
