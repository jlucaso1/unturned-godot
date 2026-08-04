using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Damage;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests.Damage;

// The server's ledger of what can be broken, and the lookup that turns "the ray stopped here" back into
// "this is the tree it stopped on" — this port's answer to ResourceManager.tryGetRegion.
public class DamageableWorldTests
{
    private static ObjectAsset Asset(string text)
    {
        Assert.True(ObjectAsset.TryParse(DatParser.Parse(text), null, out ObjectAsset? asset));
        return asset;
    }

    // A pine off Bundles/Trees: 1200 health, no Vulnerable_To_Fists — a fist does nothing to it.
    private static ObjectAsset Pine() => Asset("""
        GUID 5db018b7fcf04727b072c281d256e06d
        Type Resource
        ID 30
        Health 1200
        """);

    private static ObjectAsset ForageBush() => Asset("""
        GUID 6db018b7fcf04727b072c281d256e06d
        Type Resource
        ID 31
        Health 100
        Vulnerable_To_Fists true
        """);

    // A Rubble/Garbage_1 pile as the shipped maps write it: 75 health, no blade, not invulnerable.
    private static ObjectAsset GarbagePile() => Asset("""
        GUID a19b3ec55a2046668611c9d2775efd99
        Type Medium
        ID 60
        Rubble Destroy
        Rubble_Reset 300
        Rubble_Health 75
        """);

    private static DamageableWorld WorldWith(params DamageableInstance[] instances)
    {
        var world = new DamageableWorld();
        foreach (DamageableInstance instance in instances)
            world.Add(instance);
        return world;
    }

    [Fact]
    public void AddReturnsTheIndexThatNamesIt()
    {
        var world = new DamageableWorld();
        Assert.Equal(0, world.Add(DamageableInstance.Resource(Vector3.Zero, Pine())));
        Assert.Equal(1, world.Add(DamageableInstance.Object(Vector3.One, GarbagePile())));
        Assert.Equal(2, world.Count);
    }

    [Fact]
    public void HealthStartsAtTheAssetsOwn()
    {
        DamageableWorld world = WorldWith(DamageableInstance.Resource(Vector3.Zero, Pine()));
        Assert.Equal(1200, world.HealthOf(0));
        Assert.False(world.IsDestroyed(0));
    }

    [Fact]
    public void RubbleHealthComesFromTheRubbleBlock()
    {
        DamageableWorld world = WorldWith(DamageableInstance.Object(Vector3.Zero, GarbagePile()));
        Assert.Equal(75, world.HealthOf(0));
    }

    // Punching a pine is the shipped game's own behaviour: the swing lands and nothing happens.
    [Fact]
    public void FistsDoNothingToAPine()
    {
        DamageableWorld world = WorldWith(DamageableInstance.Resource(Vector3.Zero, Pine()));
        Assert.Equal(0, world.PunchDamageTo(0));
        Assert.False(world.Damage(0, world.PunchDamageTo(0)));
        Assert.Equal(1200, world.HealthOf(0));
    }

    [Fact]
    public void FistsHarvestAResourceThatOptsIn()
    {
        DamageableWorld world = WorldWith(DamageableInstance.Resource(Vector3.Zero, ForageBush()));
        Assert.Equal(20, world.PunchDamageTo(0));
        Assert.False(world.Damage(0, 20));
        Assert.Equal(80, world.HealthOf(0));
    }

    [Fact]
    public void FistsBreakARubblePileInFifteenSwings()
    {
        DamageableWorld world = WorldWith(DamageableInstance.Object(Vector3.Zero, GarbagePile()));
        Assert.Equal(5, world.PunchDamageTo(0));
        for (int i = 0; i < 14; i++)
            Assert.False(world.Damage(0, world.PunchDamageTo(0)));
        Assert.True(world.Damage(0, world.PunchDamageTo(0)));
        Assert.True(world.IsDestroyed(0));
    }

    // The destruction is reported ONCE, so a caller announces it once however many more swings land.
    [Fact]
    public void FurtherSwingsOnSomethingBrokenReportNothing()
    {
        DamageableWorld world = WorldWith(DamageableInstance.Object(Vector3.Zero, GarbagePile()));
        world.Damage(0, 75);
        Assert.False(world.Damage(0, 5));
        Assert.Equal(0, world.HealthOf(0));
    }

    [Fact]
    public void ZeroDamageIsNotAHit()
    {
        DamageableWorld world = WorldWith(DamageableInstance.Object(Vector3.Zero, GarbagePile()));
        Assert.False(world.Damage(0, 0));
        Assert.Equal(75, world.HealthOf(0));
    }

    // A kind that is neither a resource nor an object has no punch number of its own — the bodies are
    // damaged through the multiplier tables instead — so the ledger answers zero rather than guessing.
    [Fact]
    public void APlacementOfAnotherKindTakesNoPunchDamage()
    {
        DamageableWorld world = WorldWith(new DamageableInstance(EPunchTargetKind.Player, Vector3.Zero,
            Health: 100, VulnerableToFists: true, BladeId: 0, IsVulnerable: true, Guid.NewGuid()));
        Assert.Equal(0, world.PunchDamageTo(0));
    }

    [Fact]
    public void ResetPutsEverythingBack()
    {
        DamageableWorld world = WorldWith(DamageableInstance.Object(Vector3.Zero, GarbagePile()));
        world.Damage(0, 75);
        world.ResetAll();
        Assert.Equal(75, world.HealthOf(0));
        Assert.False(world.IsDestroyed(0));
    }

    // ---- The lookup -----------------------------------------------------------------------------

    private static readonly float Radius = PunchTargeting.PlacementMatchRadius;

    [Fact]
    public void FindReturnsTheNearestPlacementOfTheStruckAsset()
    {
        DamageableWorld world = WorldWith(
            DamageableInstance.Resource(new Vector3(0, 0, 0), Pine()),
            DamageableInstance.Resource(new Vector3(30f, 0, 0), Pine()),
            DamageableInstance.Resource(new Vector3(3f, 0, 0), Pine()));
        Assert.Equal(2, world.Find(new Vector3(3.5f, 0, 0), Radius, Pine().Guid));
    }

    [Fact]
    public void FindMissesBeyondItsRadius() =>
        Assert.Equal(-1, WorldWith(DamageableInstance.Resource(Vector3.Zero, Pine()))
            .Find(new Vector3(50f, 0, 0), Radius, Pine().Guid));

    // THE case the asset identity exists for: a punch that stops on a wall, terrain, or any body that
    // is not this placement's own must not take health off it, however close it stopped.
    [Fact]
    public void FindRefusesAHitOnSomethingElsesCollider()
    {
        DamageableWorld world = WorldWith(DamageableInstance.Object(Vector3.Zero, GarbagePile()));
        // The ray stopped half a metre away — but on the wall behind the pile, not on the pile.
        Assert.Equal(-1, world.Find(new Vector3(0.5f, 0.5f, 0), Radius, Pine().Guid));
        // And on terrain, or anything else the host could not name at all.
        Assert.Equal(-1, world.Find(new Vector3(0.5f, 0.5f, 0), Radius, Guid.Empty));
        // The pile's own collider does hit it.
        Assert.Equal(0, world.Find(new Vector3(0.5f, 0.5f, 0), Radius, GarbagePile().Guid));
    }

    // A tree is struck several metres up its trunk while its origin is at the base, so the match is
    // measured HORIZONTALLY inside a vertical window rather than as a 3D distance.
    [Fact]
    public void FindReachesFromATrunkHitDownToTheBase() =>
        Assert.Equal(0, WorldWith(DamageableInstance.Resource(Vector3.Zero, Pine()))
            .Find(new Vector3(0.5f, 6f, 0.5f), Radius, Pine().Guid));

    // Two pines side by side: a hit six metres up the near one is nearer in 3D to the FAR one's base,
    // which is exactly why the distance is horizontal.
    [Fact]
    public void FindPicksTheTrunkItStruckNotTheNearerBase()
    {
        DamageableWorld world = WorldWith(
            DamageableInstance.Resource(Vector3.Zero, Pine()),
            DamageableInstance.Resource(new Vector3(7f, 0, 0), Pine()));
        Assert.Equal(0, world.Find(new Vector3(0.4f, 6f, 0), Radius, Pine().Guid));
    }

    // The vertical window: a hit far above a bush is a hit on the building behind it, not on the bush.
    [Fact]
    public void FindRefusesAHitOutsideItsVerticalWindow()
    {
        DamageableWorld world = WorldWith(DamageableInstance.Resource(Vector3.Zero, Pine()));
        Assert.Equal(-1, world.Find(new Vector3(0, DamageableWorld.MatchAbove + 1f, 0), Radius,
            Pine().Guid));
        Assert.Equal(-1, world.Find(new Vector3(0, -DamageableWorld.MatchBelow - 1f, 0), Radius,
            Pine().Guid));
    }

    // Something already broken is not what the ray hit: its collider is gone in the game, and here it
    // simply stops being a candidate.
    [Fact]
    public void FindSkipsWhatIsAlreadyDestroyed()
    {
        DamageableWorld world = WorldWith(
            DamageableInstance.Object(Vector3.Zero, GarbagePile()),
            DamageableInstance.Object(new Vector3(3f, 0, 0), GarbagePile()));
        world.Damage(0, 75);
        Assert.Equal(1, world.Find(Vector3.Zero, radius: 12f, GarbagePile().Guid));
    }

    // The grid is only an index: a lookup near a cell boundary must find a placement in the next cell.
    [Fact]
    public void FindCrossesCellBoundaries()
    {
        float cell = DamageableWorld.CellSize;
        DamageableWorld world = WorldWith(
            DamageableInstance.Resource(new Vector3(cell + 0.5f, 0, 0), Pine()));
        Assert.Equal(0, world.Find(new Vector3(cell - 0.5f, 0, 0), radius: 4f, Pine().Guid));
    }

    [Fact]
    public void FindHandlesNegativeCoordinates()
    {
        DamageableWorld world = WorldWith(
            DamageableInstance.Resource(new Vector3(-100f, 0, -250f), Pine()));
        Assert.Equal(0, world.Find(new Vector3(-101f, 0, -251f), radius: 5f, Pine().Guid));
    }

    // ---- Building it from the level's placements ------------------------------------------------

    [Fact]
    public void BuilderKeepsOnlyWhatCanBreak()
    {
        var database = new ObjectAssetDatabase();
        ObjectAsset pine = Pine();
        ObjectAsset garbage = GarbagePile();
        ObjectAsset wall = Asset("""
            GUID 7db018b7fcf04727b072c281d256e06d
            Type Large
            ID 99
            """);
        database.Add(pine);
        database.Add(garbage);
        database.Add(wall);

        var placements = new List<PlacedObject>
        {
            new(new Vector3(1f, 0, 2f), Vector3.Zero, Vector3.One, 0, pine.Guid),
            new(new Vector3(4f, 0, 5f), Vector3.Zero, Vector3.One, 0, garbage.Guid),
            new(new Vector3(7f, 0, 8f), Vector3.Zero, Vector3.One, 0, wall.Guid),
            new(new Vector3(9f, 0, 9f), Vector3.Zero, Vector3.One, 0, Guid.NewGuid()), // unknown asset
        };

        DamageableWorld world = DamageableWorldBuilder.Build(placements, database);
        Assert.Equal(2, world.Count);
        Assert.Equal(EPunchTargetKind.Resource, world[0].Kind);
        Assert.Equal(pine.Guid, world[0].Asset);
        Assert.Equal(EPunchTargetKind.Object, world[1].Kind);
        Assert.Equal(garbage.Guid, world[1].Asset);
    }

    // The ledger has to agree with the collision world about where things stand, so it goes through the
    // same Unity->Godot transform the bodies do — which mirrors Z.
    [Fact]
    public void BuilderPlacesThingsInGodotSpace()
    {
        var placement = new PlacedObject(new Vector3(10f, 3f, 20f), Vector3.Zero, Vector3.One, 0,
            Pine().Guid);
        Assert.True(DamageableWorldBuilder.TryDescribe(placement, Pine(),
            out DamageableInstance instance));
        Assert.Equal(ObjectPlacement.ComputeTransform(placement).Origin, instance.Position);
        Assert.Equal(-20f, instance.Position.Z, 3);
    }

    [Fact]
    public void BuilderRejectsAnObjectWithNoRubbleBlock()
    {
        ObjectAsset wall = Asset("""
            GUID 7db018b7fcf04727b072c281d256e06d
            Type Large
            ID 99
            """);
        Assert.False(DamageableWorldBuilder.TryDescribe(
            new PlacedObject(Vector3.Zero, Vector3.Zero, Vector3.One, 0, wall.Guid), wall, out _));
    }

    [Fact]
    public void BuilderRejectsAHealthlessResource()
    {
        ObjectAsset stump = Asset("""
            GUID 8db018b7fcf04727b072c281d256e06d
            Type Resource
            ID 98
            """);
        Assert.False(DamageableWorldBuilder.TryDescribe(
            new PlacedObject(Vector3.Zero, Vector3.Zero, Vector3.One, 0, stump.Guid), stump, out _));
    }

    [Fact]
    public void BuilderRejectsNulls()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DamageableWorldBuilder.Build(null!, new ObjectAssetDatabase()));
        Assert.Throws<ArgumentNullException>(() =>
            DamageableWorldBuilder.Build(new List<PlacedObject>(), null!));
    }
}
