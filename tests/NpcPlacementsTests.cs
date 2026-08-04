using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
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
