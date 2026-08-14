using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Damage;
using UnturnedGodot.Dat;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Damage;

// DamageTool.raycast, split across two worlds: the zombies the brain owns and the geometry the physics
// server owns. What the tests here are really about is the SEAM — a single cast in the original returns
// one answer, so two casts here have to agree on which of them was first.
public class PunchTargetingTests
{
    private static ZombieInstance Zombie(ushort id, Vector3 position, float yaw = 0f) => new()
    {
        Id = id,
        Position = position,
        Yaw = yaw,
        Health = 100,
        MaxHealth = 100,
    };

    private static ObjectAsset Asset(string text)
    {
        Assert.True(ObjectAsset.TryParse(DatParser.Parse(text), null, out ObjectAsset? asset));
        return asset;
    }

    private static ObjectAsset GarbagePile() => Asset("""
        GUID a19b3ec55a2046668611c9d2775efd99
        Type Medium
        ID 60
        Rubble Destroy
        Rubble_Health 75
        """);

    // A stand-in for the physics world: the ray stops dead at `distance` along it, on a body belonging
    // to `asset` — Guid.Empty standing for terrain or anything else the host cannot name — made of
    // `surface`, which is empty for a collider carrying no PhysicMaterial.
    private static PunchTargeting.WorldRaycast WallAt(float distance, Guid asset = default,
        string surface = "") =>
        (Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out Vector3 normal,
            out float hit, out Guid struck, out string material) =>
        {
            point = origin + (direction.Normalized() * distance);
            normal = -direction.Normalized(); // a wall square on to the swing
            hit = distance;
            struck = asset;
            material = surface;
            return distance <= maxDistance;
        };

    private static readonly Vector3 Eyes = new(0, 1.75f, 0);
    private static readonly Vector3 Forward = new(0, 0, -1);

    [Fact]
    public void FindsAZombieWithinReach()
    {
        var zombies = new List<ZombieInstance> { Zombie(7, new Vector3(0, 0, -1.5f)) };
        PunchHit hit = PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach, zombies);
        Assert.Equal(EPunchTargetKind.Zombie, hit.Kind);
        Assert.Equal(7, hit.Id);
    }

    [Fact]
    public void MissesAZombieBeyondReach()
    {
        var zombies = new List<ZombieInstance> { Zombie(7, new Vector3(0, 0, -4f)) };
        Assert.False(PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach, zombies).Exists);
    }

    [Fact]
    public void PicksTheNearestOfSeveralZombies()
    {
        var zombies = new List<ZombieInstance>
        {
            Zombie(1, new Vector3(0, 0, -1.6f)),
            Zombie(2, new Vector3(0, 0, -0.9f)),
        };
        Assert.Equal(2, PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach, zombies).Id);
    }

    // A dead zombie is not a target. Nothing removes it mid-tick, so the guard has to be here too.
    [Fact]
    public void SkipsADeadZombie()
    {
        ZombieInstance dead = Zombie(1, new Vector3(0, 0, -1f));
        dead.Health = 0;
        Assert.False(PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach,
            new List<ZombieInstance> { dead }).Exists);
    }

    // The whole point of casting against the world as well: a wall in between shortens the punch, so a
    // zombie standing behind it is out of reach rather than merely further away.
    [Fact]
    public void AWallShortensTheReach()
    {
        var zombies = new List<ZombieInstance> { Zombie(7, new Vector3(0, 0, -1.5f)) };
        Assert.False(PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach, zombies,
            world: null, WallAt(0.5f)).Exists);
    }

    [Fact]
    public void AZombieInFrontOfTheWallIsStillHit()
    {
        var zombies = new List<ZombieInstance> { Zombie(7, new Vector3(0, 0, -0.6f)) };
        PunchHit hit = PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach, zombies,
            world: null, WallAt(1.5f));
        Assert.Equal(EPunchTargetKind.Zombie, hit.Kind);
    }

    // A world hit that lands on something breakable IS the target.
    [Fact]
    public void AWorldHitOnAPlacementBecomesTheTarget()
    {
        var world = new DamageableWorld();
        world.Add(DamageableInstance.Object(new Vector3(0, 0, -1f), GarbagePile()));
        PunchHit hit = PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach,
            Array.Empty<ZombieInstance>(), world, WallAt(1f, GarbagePile().Guid));
        Assert.Equal(EPunchTargetKind.Object, hit.Kind);
        Assert.Equal(0, hit.Id);
    }

    // The same geometry, struck on somebody else's collider: a wall a handspan from a rubble pile is a
    // wall, and the pile keeps its health.
    [Fact]
    public void AWorldHitOnAnotherAssetsColliderIsAMiss()
    {
        var world = new DamageableWorld();
        world.Add(DamageableInstance.Object(new Vector3(0, 0, -1f), GarbagePile()));
        Assert.False(PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach,
            Array.Empty<ZombieInstance>(), world, WallAt(1f, Guid.NewGuid())).Exists);
        // And terrain, which carries no asset identity at all.
        Assert.False(PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach,
            Array.Empty<ZombieInstance>(), world, WallAt(1f)).Exists);
    }

    // And one that lands on bare geometry is a wall: nothing is hit, but the reach is spent anyway.
    [Fact]
    public void AWorldHitOnNothingBreakableIsAMiss()
    {
        PunchHit hit = PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach,
            Array.Empty<ZombieInstance>(), new DamageableWorld(), WallAt(1f));
        Assert.False(hit.Exists);
    }

    // A zombie standing in front of the tree wins, because a single cast would have met it first.
    [Fact]
    public void ANearerZombieBeatsAFurtherPlacement()
    {
        var world = new DamageableWorld();
        world.Add(DamageableInstance.Object(new Vector3(0, 0, -1.6f), GarbagePile()));
        var zombies = new List<ZombieInstance> { Zombie(3, new Vector3(0, 0, -1f)) };
        PunchHit hit = PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach, zombies, world,
            WallAt(1.6f, GarbagePile().Guid));
        Assert.Equal(EPunchTargetKind.Zombie, hit.Kind);
        Assert.Equal(3, hit.Id);
    }

    // A host that can cast against the world but holds no ledger yet — the interactive session while
    // the map is still streaming in. The ray still stops on the geometry; there is simply nothing there
    // to damage.
    [Fact]
    public void AWorldHitWithNoLedgerIsAMiss()
    {
        var zombies = new List<ZombieInstance> { Zombie(7, new Vector3(0, 0, -1.5f)) };
        Assert.False(PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach, zombies,
            world: null, WallAt(0.5f)).Exists);
    }

    // The blocking predicate is the fallback for a host with no cast of its own.
    [Fact]
    public void LineBlockedRefusesAZombie()
    {
        var zombies = new List<ZombieInstance> { Zombie(7, new Vector3(0, 0, -1.5f)) };
        Assert.False(PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach, zombies,
            world: null, worldRaycast: null, blocked: (_, _) => true).Exists);
    }

    [Fact]
    public void DegenerateAimHitsNothing()
    {
        var zombies = new List<ZombieInstance> { Zombie(7, new Vector3(0, 0, -1f)) };
        Assert.False(PunchTargeting.Resolve(Eyes, Vector3.Zero, PunchDamageData.Reach, zombies).Exists);
        Assert.False(PunchTargeting.Resolve(Eyes, Forward, 0f, zombies).Exists);
    }

    [Fact]
    public void ResolveRejectsANullPopulation() =>
        Assert.Throws<ArgumentNullException>(() =>
            PunchTargeting.Resolve(Eyes, Forward, 1f, null!));

    // The six-metre ceiling the original applies to whatever its raycast produced.
    [Fact]
    public void ServerRangeCeiling()
    {
        Assert.True(PunchTargeting.IsWithinServerRange(Vector3.Zero, new Vector3(0, 0, -6f)));
        Assert.False(PunchTargeting.IsWithinServerRange(Vector3.Zero, new Vector3(0, 0, -6.1f)));
    }

    // The hit records where it landed, which is what the ceiling above is measured against. Standing
    // eyes are 1.75 m up and the capsule's cylinder ends at 1.6, so this level ray meets the SHOULDER
    // cap rather than the chest — hence 1.129 rather than the 1.1 a cylinder alone would give.
    [Fact]
    public void HitCarriesItsPointAndDistance()
    {
        var zombies = new List<ZombieInstance> { Zombie(7, new Vector3(0, 0, -1.5f)) };
        PunchHit hit = PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach, zombies);
        Assert.Equal(1.129f, hit.Distance, 3);
        Assert.Equal(-1.129f, hit.Point.Z, 3);
        Assert.Equal(Eyes.Y, hit.Point.Y, 3);
        Assert.True(PunchTargeting.IsWithinServerRange(Eyes, hit.Point));
    }

    // The surface a zombie hit reports, which is what picks the impact effect and the decal. Read off
    // the zombie's own speciality rather than fixed at Flesh: "isRadioactive ? AlienDynamicRef :
    // FleshDynamicRef" (Zombie.cs:255). An ACID zombie is the case that actually occurs on PEI, and it
    // spent this whole port cracking like meat.
    [Theory]
    [InlineData(EZombieSpeciality.Normal, PunchTargeting.ZombieSurface)]
    [InlineData(EZombieSpeciality.Crawler, PunchTargeting.ZombieSurface)]
    [InlineData(EZombieSpeciality.Acid, PunchTargeting.RadioactiveZombieSurface)]
    [InlineData(EZombieSpeciality.BossNuclear, PunchTargeting.RadioactiveZombieSurface)]
    [InlineData(EZombieSpeciality.BossAll, PunchTargeting.RadioactiveZombieSurface)]
    [InlineData(EZombieSpeciality.BossBuakFinal, PunchTargeting.RadioactiveZombieSurface)]
    public void AZombieHitReportsItsOwnSurface(EZombieSpeciality speciality, string expected)
    {
        ZombieInstance zombie = Zombie(7, new Vector3(0, 0, -1.5f));
        zombie.Speciality = speciality;
        var zombies = new List<ZombieInstance> { zombie };

        // Both entry points: the bare cast, and the whole resolve a swing goes through.
        Assert.True(PunchTargeting.RaycastZombies(zombies, Eyes, Forward, PunchDamageData.Reach,
            out PunchHit direct));
        Assert.Equal(expected, direct.Surface);
        Assert.Equal(expected,
            PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach, zombies).Surface);
    }

    // ...including when the ray met a wall first and the zombie is what actually stopped it: the surface
    // has to follow the thing struck rather than the last one the world reported.
    [Fact]
    public void ARadioactiveZombieInFrontOfAWallStillReportsAlien()
    {
        ZombieInstance zombie = Zombie(7, new Vector3(0, 0, -0.6f));
        zombie.Speciality = EZombieSpeciality.Acid;

        PunchHit hit = PunchTargeting.Resolve(Eyes, Forward, PunchDamageData.Reach,
            new List<ZombieInstance> { zombie }, world: null, WallAt(1.5f, default, "Concrete_Static"));

        Assert.Equal(EPunchTargetKind.Zombie, hit.Kind);
        Assert.Equal(PunchTargeting.RadioactiveZombieSurface, hit.Surface);
    }

    [Fact]
    public void NoneIsNotAHit()
    {
        Assert.False(PunchHit.None.Exists);
        Assert.Equal(EPunchTargetKind.None, PunchHit.None.Kind);
    }
}
