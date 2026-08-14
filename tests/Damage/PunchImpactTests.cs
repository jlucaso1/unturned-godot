using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Damage;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Damage;

// When a swing announces itself as having met a surface — DamageTool.ServerSpawnLegacyImpact, which the
// original spawns off `info.materialName` ABOVE the whole damage ladder.
//
// That placement is the behaviour under test here, and it is easy to get subtly wrong by hanging the
// effect off the damage instead: then a brick wall would be silent, a pine that shrugs off fists would be
// silent, and only the handful of things a bare hand can actually break would make any noise at all.
public class PunchImpactTests
{
    private static readonly Vector3 Spawn = new(0, 10f, 0);
    private const string Level = "PEI";

    private static bool FlatGround(float x, float z, out float y)
    {
        y = 10f;
        return true;
    }

    private sealed class Harness
    {
        public readonly LoopbackServerTransport ServerTransport = new();
        public readonly NetServer Server;
        public readonly NetClient Client;
        public readonly ZombieSystem Zombies;
        public readonly PunchDamageHost Punches;
        public readonly List<PunchImpact> Impacts = new();
        public double Now = 5000.0;

        public Harness(DamageableWorld? world = null, PunchTargeting.WorldRaycast? raycast = null)
        {
            Server = new NetServer(ServerTransport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn, Level);
            Zombies = new ZombieSystem(
                new[] { new ZombieTable { Name = "Civilian", Health = 100, Damage = 10 } },
                new List<NavBound>
                {
                    new() { Center = new Vector3(0, 140, 0), Size = new Vector3(400, 300, 400) },
                },
                FlatGround);
            Punches = new PunchDamageHost(Server, Zombies, zombieHost: null, world)
            {
                WorldRaycast = raycast,
            };
            Punches.Impacted += Impacts.Add;
            Client = new NetClient(ServerTransport.CreateClient(), "Puncher", Level);
        }

        public void PlantZombie(ushort id, Vector3 position, float yaw = 0f)
        {
            var state = new ZombieSystemState();
            state.Zombies.Add(new ZombieInstance
            {
                Id = id,
                Bound = 0,
                Position = position,
                Yaw = yaw,
                Health = 100,
                MaxHealth = 100,
            });
            Zombies.RestoreState(state);
        }

        public void Pump(int rounds = 1)
        {
            for (int i = 0; i < rounds; i++)
            {
                Now += ServerSimulation.TickRate;
                Server.Update(Now);
                Client.Update(Now);
            }
        }

        private uint _frame;
        private byte _swing;

        public void Swing(float yaw = 0f, float pitch = 0f)
        {
            Client.SendInput(new InputCommand(++_frame, 0, 0, jump: false, sprint: false,
                NetAngles.QuantizeYaw(yaw),
                NetAngles.QuantizePitch(pitch + PunchAim.WirePitchOffset),
                EPlayerStance.Stand, Spawn, grounded: true, hasSwing: true, swingSequence: ++_swing,
                swingFist: EPlayerPunch.Left));
        }
    }

    private static Harness Joined(DamageableWorld? world = null,
        PunchTargeting.WorldRaycast? raycast = null)
    {
        var h = new Harness(world, raycast);
        h.Pump(3);
        Assert.True(h.Client.Joined);
        return h;
    }

    // A wall a metre in front of the eyes, made of `surface`, belonging to `asset`.
    private static PunchTargeting.WorldRaycast Wall(string surface, Guid asset = default) =>
        (Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out Vector3 normal,
            out float distance, out Guid struck, out string material) =>
        {
            distance = 1f;
            point = origin + (direction.Normalized() * distance);
            normal = -direction.Normalized();
            struck = asset;
            material = surface;
            return true;
        };

    private static ObjectAsset GarbagePile()
    {
        Assert.True(ObjectAsset.TryParse(DatParser.Parse("""
            GUID a19b3ec55a2046668611c9d2775efd99
            Type Medium
            ID 60
            Rubble Destroy
            Rubble_Health 75
            """), null, out ObjectAsset? asset));
        return asset;
    }

    // The case the whole feature exists for, and the one an implementation that hangs the effect off the
    // damage would get wrong: concrete takes nothing from a fist and still cracks.
    [Fact]
    public void AWallThatTakesNoDamageStillMakesAnImpact()
    {
        Harness h = Joined(world: null, Wall("Concrete_Static"));

        h.Swing();
        h.Pump(2);

        PunchImpact impact = Assert.Single(h.Impacts);
        Assert.Equal("Concrete_Static", impact.Surface);
        // Named so a client can tell its own hits from everyone else's — the original's instigator.
        Assert.Equal(h.Client.PlayerId, impact.PlayerId);
    }

    // A collider with no PhysicMaterial: Unity's sharedMaterial is null, the original's
    // `!string.IsNullOrEmpty(info.materialName)` fails, and nothing is spawned at all.
    [Fact]
    public void ASurfacelessColliderMakesNoImpact()
    {
        Harness h = Joined(world: null, Wall(surface: ""));

        h.Swing();
        h.Pump(2);

        Assert.Empty(h.Impacts);
    }

    [Fact]
    public void ASwingThatReachesNothingMakesNoImpact()
    {
        Harness h = Joined(world: null, raycast: null);

        h.Swing();
        h.Pump(2);

        Assert.Empty(h.Impacts);
    }

    // A breakable target makes the same impact a wall does — the effect is not a damage notification, so
    // it fires alongside the damage rather than instead of it.
    [Fact]
    public void ABreakableTargetMakesAnImpactAsWellAsTakingDamage()
    {
        var world = new DamageableWorld();
        world.Add(DamageableInstance.Object(new Vector3(0, 10f, -1f), GarbagePile()));
        Harness h = Joined(world, Wall("Wood_Static", GarbagePile().Guid));

        h.Swing(yaw: 0f); // straight down -Z, at the pile
        h.Pump(2);

        Assert.Equal("Wood_Static", Assert.Single(h.Impacts).Surface);
        Assert.True(world.HealthOf(0) < 75, "the pile should have taken the punch as well");
    }

    // A zombie is not in the physics world here, so its surface cannot be read off a collider. It answers
    // Flesh, which is what every zombie prefab's bone colliders carry.
    [Fact]
    public void AZombieHitReportsFlesh()
    {
        Harness h = Joined(world: null, raycast: null);
        h.PlantZombie(1, new Vector3(0, 10f, -1.2f));

        h.Swing();
        h.Pump(2);

        Assert.Equal(PunchTargeting.ZombieSurface, Assert.Single(h.Impacts).Surface);
    }

    // The zombie is nearer than the wall, so it is what the swing met — and the impact has to follow the
    // thing actually struck rather than the last surface the ray reported.
    [Fact]
    public void AZombieInFrontOfAWallTakesTheImpactFromTheZombie()
    {
        Harness h = Joined(world: null, Wall("Concrete_Static"));
        h.PlantZombie(1, new Vector3(0, 10f, -0.5f));

        h.Swing();
        h.Pump(2);

        Assert.Equal(PunchTargeting.ZombieSurface, Assert.Single(h.Impacts).Surface);
    }

    // The point and the normal are what orient the mark, and they have to describe the SURFACE rather
    // than the swing: a mark placed at the eyes, or facing the wrong way, is worse than none.
    [Fact]
    public void TheImpactCarriesWhereItLandedAndWhichWayTheSurfaceFaces()
    {
        Harness h = Joined(world: null, Wall("Metal_Static"));

        h.Swing(yaw: 0f);
        h.Pump(2);

        PunchImpact impact = Assert.Single(h.Impacts);
        // A metre down -Z from the eyes, with the wall facing back up +Z at the puncher.
        Assert.Equal(-1f, impact.Point.Z, 3);
        Assert.True(impact.Normal.Z > 0.9f, $"the wall should face the swing, got {impact.Normal}");
        Assert.True(impact.Point.Y > Spawn.Y, "the swing leaves the eyes, not the feet");
    }

    // A host with a raycast that reports a hit but names no surface at all — a null rather than an empty
    // string, which is what a host returning `default` for the out parameter produces. It has to read as
    // "no surface" rather than throwing in the middle of a tick.
    [Fact]
    public void ANullSurfaceIsTreatedAsNoSurface()
    {
        Harness h = Joined(world: null,
            (Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out Vector3 normal,
                out float distance, out Guid struck, out string material) =>
            {
                distance = 1f;
                point = origin + (direction.Normalized() * distance);
                normal = -direction.Normalized();
                struck = Guid.Empty;
                material = null!;
                return true;
            });

        h.Swing();
        h.Pump(2);

        Assert.Empty(h.Impacts);
    }

    // A zombie killed by the swing hands its killing blow's shove to the host, so the corpse is thrown
    // in the direction it was hit. A kill that reported no shove would drop every body straight down.
    [Fact]
    public void AKillingBlowReportsTheShoveThatDeliveredIt()
    {
        Harness h = Joined(world: null, raycast: null);
        h.PlantZombie(1, new Vector3(0, 10f, -1.2f));
        // Weak enough not to kill on the first swing, so this also covers the surviving branch.
        h.Zombies.Zombies[0].Health = 1;

        h.Swing();
        h.Pump(2);

        // The zombie is gone, and the impact still fired for the hit that killed it.
        Assert.Empty(h.Zombies.Zombies);
        Assert.Equal(PunchTargeting.ZombieSurface, Assert.Single(h.Impacts).Surface);
    }

    // A hit that names a zombie the brain no longer holds — killed by something else between the swing
    // being accepted and this tick resolving it. The impact still fires (the fist met flesh) and no
    // damage is worked out.
    [Fact]
    public void AHitOnAZombieThatIsAlreadyGoneDamagesNothing()
    {
        Harness h = Joined(world: null, raycast: null);
        h.PlantZombie(1, new Vector3(0, 10f, -1.2f));
        var results = new List<PunchResult>();
        h.Punches.Resolved += results.Add;

        // Removed after the ray would have found it: the host looks the id up again by design.
        h.Punches.Resolved += _ => { };
        h.Zombies.Remove(h.Zombies.Zombies[0]);

        h.Swing();
        h.Pump(2);

        Assert.All(results, r => Assert.Equal(0, r.Amount));
    }

    // The cooldown is one swing every six frames, and the impact rides the swing — so a burst of input
    // produces one impact, not one per frame.
    [Fact]
    public void OneSwingMakesOneImpact()
    {
        Harness h = Joined(world: null, Wall("Wood_Static"));

        for (int i = 0; i < 4; i++)
        {
            h.Swing();
            h.Pump();
        }

        Assert.Single(h.Impacts);
    }
}
