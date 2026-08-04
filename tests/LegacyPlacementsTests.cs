using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class LegacyPlacementsTests
{
    private static ObjectAsset Asset(Guid guid, ushort id, string type = "Large") =>
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

    private static PlacedObject Placement(ushort id, Guid guid) =>
        new(new Vector3(1, 2, 3), new Vector3(0, 90, 0), Vector3.One, id, guid);

    [Fact]
    public void ResolveGuids_FillsInTheGuidOfALegacyIdPlacement()
    {
        var guid = Guid.NewGuid();
        var placements = new List<PlacedObject> { Placement(id: 42, guid: Guid.Empty) };

        Assert.Equal(1, LegacyPlacements.ResolveGuids(placements, DatabaseWith(Asset(guid, 42))));

        Assert.Equal(guid, placements[0].Guid);
        Assert.Equal(42, placements[0].Id);          // the id it was placed with is kept
        Assert.Equal(new Vector3(1, 2, 3), placements[0].Position);
        Assert.Equal(new Vector3(0, 90, 0), placements[0].EulerDegrees);
    }

    [Fact]
    public void ResolveGuids_LeavesModernPlacementsAlone()
    {
        var placed = Guid.NewGuid();
        var other = Guid.NewGuid();
        // The id collides with a different asset: a placement that already has a GUID must not follow it.
        var placements = new List<PlacedObject> { Placement(id: 42, guid: placed) };

        Assert.Equal(0, LegacyPlacements.ResolveGuids(placements, DatabaseWith(Asset(other, 42))));
        Assert.Equal(placed, placements[0].Guid);
    }

    [Fact]
    public void ResolveGuids_UnknownIdStaysUnresolved()
    {
        var placements = new List<PlacedObject>
        {
            Placement(id: 7, guid: Guid.Empty),   // no asset carries this id
            Placement(id: 0, guid: Guid.Empty),   // no identity at all
        };

        Assert.Equal(0, LegacyPlacements.ResolveGuids(placements, DatabaseWith(Asset(Guid.NewGuid(), 42))));
        Assert.Equal(Guid.Empty, placements[0].Guid);
        Assert.Equal(Guid.Empty, placements[1].Guid);
    }

    [Fact]
    public void ResolveGuids_DoesNotFollowVehicleIds()
    {
        // Vehicles are indexed separately (a vehicle and an object may share a legacy id), and a tree or
        // object placement must never resolve into that table.
        var placements = new List<PlacedObject> { Placement(id: 5, guid: Guid.Empty) };

        Assert.Equal(0, LegacyPlacements.ResolveGuids(placements,
            DatabaseWith(Asset(Guid.NewGuid(), 5, "SDG.Unturned.VehicleAsset, Assembly-CSharp"))));
        Assert.Equal(Guid.Empty, placements[0].Guid);
    }
}
