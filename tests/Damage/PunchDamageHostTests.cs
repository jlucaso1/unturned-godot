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

// The whole path, over the real transport: a client presses the attack button, the input reaches the
// server, the simulation accepts the swing, and something loses health for it.
//
// This is the test that would catch a break anywhere between the two ends — a pitch convention off by
// ninety degrees, an eye height read from the wrong stance, a gesture list read on the wrong tick — none
// of which the unit tests around it can see, because each of them is right about its own half.
public class PunchDamageHostTests
{
    private const string Level = "PEI";
    private static readonly Vector3 Spawn = new(0, 10f, 0);

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
        public readonly List<PunchResult> Results = new();
        public double Now = 5000.0;

        public Harness(DamageableWorld? world = null, PunchTargeting.WorldRaycast? raycast = null,
            bool withZombies = true)
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
            Punches = new PunchDamageHost(Server, withZombies ? Zombies : null, zombieHost: null, world)
            {
                WorldRaycast = raycast,
            };
            Punches.Resolved += Results.Add;
            Client = new NetClient(ServerTransport.CreateClient(), "Puncher", Level);
        }

        // A zombie planted directly in front of the spawn, facing away — so a punch from the spawn is a
        // backstab unless a test turns it round.
        public ZombieInstance PlantZombie(ushort id, Vector3 position, float yaw = 0f)
        {
            var zombie = new ZombieInstance
            {
                Id = id,
                Bound = 0,
                Position = position,
                Yaw = yaw,
                Health = 100,
                MaxHealth = 100,
            };
            var state = new ZombieSystemState();
            state.Zombies.Add(zombie);
            Zombies.RestoreState(state);
            return zombie;
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

        // One input frame announcing a fresh swing, aimed by yaw and (Godot) pitch.
        public void Swing(float yaw = 0f, float pitch = 0f,
            EPlayerStance stance = EPlayerStance.Stand)
        {
            Client.SendInput(new InputCommand(++_frame, 0, 0, jump: false, sprint: false,
                NetAngles.QuantizeYaw(yaw),
                NetAngles.QuantizePitch(pitch + PunchAim.WirePitchOffset),
                stance, Spawn, grounded: true, hasSwing: true, swingSequence: ++_swing,
                swingFist: EPlayerPunch.Left));
        }

        // A plain movement frame at some other view angle: no swing, just somewhere else to be looking.
        public void Look(float yaw = 0f, float pitch = 0f)
        {
            Client.SendInput(new InputCommand(++_frame, 0, 0, jump: false, sprint: false,
                NetAngles.QuantizeYaw(yaw),
                NetAngles.QuantizePitch(pitch + PunchAim.WirePitchOffset),
                EPlayerStance.Stand, Spawn));
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

    // The whole thing: press the button, the zombie in front loses health.
    [Fact]
    public void ASwingDamagesTheZombieInFrontOfIt()
    {
        Harness h = Joined();
        ZombieInstance zombie = h.PlantZombie(1, new Vector3(0, 10f, -1.2f), yaw: 180f);

        h.Swing();
        h.Pump(2);

        Assert.True(zombie.Health < zombie.MaxHealth);
        PunchResult result = Assert.Single(h.Results);
        Assert.Equal(EPunchTargetKind.Zombie, result.Hit.Kind);
        Assert.Equal(1, result.Hit.Id);
        Assert.False(result.Destroyed);
    }

    // Looking the other way is a miss, which is the sharpest check that the aim really is the player's.
    [Fact]
    public void ASwingWithTheZombieBehindYouHitsNothing()
    {
        Harness h = Joined();
        ZombieInstance zombie = h.PlantZombie(1, new Vector3(0, 10f, -1.2f));

        h.Swing(yaw: 180f);
        h.Pump(2);

        Assert.Equal(zombie.MaxHealth, zombie.Health);
        Assert.False(Assert.Single(h.Results).Hit.Exists);
    }

    // And so is looking at the sky.
    [Fact]
    public void ASwingAimedUpwardHitsNothing()
    {
        Harness h = Joined();
        ZombieInstance zombie = h.PlantZombie(1, new Vector3(0, 10f, -1.2f));

        h.Swing(pitch: 80f);
        h.Pump(2);

        Assert.Equal(zombie.MaxHealth, zombie.Health);
    }

    [Fact]
    public void AZombieOutOfReachIsNotHit()
    {
        Harness h = Joined();
        ZombieInstance zombie = h.PlantZombie(1, new Vector3(0, 10f, -5f));

        h.Swing();
        h.Pump(2);

        Assert.Equal(zombie.MaxHealth, zombie.Health);
    }

    // Being punched turns a zombie on its attacker, and the attacker's id is the one the server knows
    // the player by — not something the client supplied.
    [Fact]
    public void BeingPunchedAggroesTheZombie()
    {
        Harness h = Joined();
        ZombieInstance zombie = h.PlantZombie(1, new Vector3(0, 10f, -1.2f), yaw: 180f);

        h.Swing();
        h.Pump(2);

        Assert.Equal(h.Client.PlayerId, zombie.TargetPlayer);
        Assert.Equal(EZombieState.Chase, zombie.State);
    }

    // Enough swings and it dies, and the population no longer holds it.
    [Fact]
    public void EnoughSwingsKillIt()
    {
        Harness h = Joined();
        h.PlantZombie(1, new Vector3(0, 10f, -1.2f), yaw: 180f);

        // The simulation's own cooldown is six ticks, so each swing needs room around it.
        for (int i = 0; i < 20 && h.Zombies.Zombies.Count > 0; i++)
        {
            h.Swing();
            h.Pump(8);
        }

        Assert.Empty(h.Zombies.Zombies);
        Assert.Contains(h.Results, r => r.Destroyed);
    }

    // Damage is hung off the swing the SIMULATION accepted, so every gate it already enforces is a gate
    // on damage too — and the one a modified client runs into is the rate limit. A flurry of swings in
    // one instant spends the whole allowance (two) and nothing more, however many are sent.
    [Fact]
    public void AFlurryOfSwingsOnlyDamagesWhatTheAllowancePaysFor()
    {
        Harness h = Joined();
        ZombieInstance zombie = h.PlantZombie(1, new Vector3(0, 10f, -1.2f), yaw: 180f);

        for (int i = 0; i < 20; i++)
            h.Swing();
        h.Pump(6);

        int landed = h.Results.Count;
        Assert.InRange(landed, 1, (int)ServerSimulation.MaxSwingAllowance);
        // Standing eyes are above a 2 m zombie's shoulders, so a level punch lands on its head: 16 a
        // swing, and health comes off once per swing the allowance actually paid for.
        Assert.All(h.Results, r => Assert.Equal(16, r.Amount));
        Assert.Equal(zombie.MaxHealth - (landed * 16), zombie.Health);
    }

    // A prone player throws no punch at all — but that gate is the CLIENT's, and deliberately so: the
    // stance the server holds is the client's own word (ApplyTrustedPosition takes it verbatim), so
    // re-running the gate here would judge a swing in whatever stance the repeat happened to land in.
    // Damage follows the swing, so it inherits exactly that division.
    [Fact]
    public void TheProneGateIsTheClientsOwn()
    {
        var equipment = new PlayerEquipment();
        Assert.Equal(EPlayerGesture.None, equipment.Simulate(100, EAttackInputFlags.Start,
            EAttackInputFlags.None, new HandState { Stance = EPlayerStance.Prone }));
        Assert.Equal(EPlayerGesture.PunchLeft, equipment.Simulate(100, EAttackInputFlags.Start,
            EAttackInputFlags.None, new HandState { Stance = EPlayerStance.Stand }));
    }

    // An idle tick is not a swing.
    [Fact]
    public void NoSwingNoDamage()
    {
        Harness h = Joined();
        ZombieInstance zombie = h.PlantZombie(1, new Vector3(0, 10f, -1.2f), yaw: 180f);

        h.Client.SendInput(new InputCommand(1, 0, 0, false, false, 0,
            NetAngles.QuantizePitch(PunchAim.WirePitchOffset), EPlayerStance.Stand, Spawn));
        h.Pump(2);

        Assert.Equal(zombie.MaxHealth, zombie.Health);
        Assert.Empty(h.Results);
    }

    // ---- The world half --------------------------------------------------------------------------

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

    // A rubble prop standing in front of the player, reported by a stand-in for the physics world.
    [Fact]
    public void ASwingBreaksARubblePropInFifteenHits()
    {
        var world = new DamageableWorld();
        world.Add(DamageableInstance.Object(new Vector3(0, 10f, -1f), GarbagePile()));
        Harness h = Joined(world,
            (Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out float distance,
                out Guid asset) =>
            {
                point = new Vector3(0, 10.5f, -1f);
                distance = 1f;
                asset = GarbagePile().Guid;
                return direction.Z < -0.5f; // only when actually looking that way
            });

        for (int i = 0; i < 15; i++)
        {
            h.Swing();
            h.Pump(8);
        }

        Assert.True(world.IsDestroyed(0));
        Assert.Contains(h.Results, r => r.Destroyed && r.Hit.Kind == EPunchTargetKind.Object);
    }

    // The original's own ceiling on where a hit may have landed, applied to whatever the raycast
    // produced. A cast this host makes itself cannot exceed it — the reach is 1.75 m — so the only way
    // to reach the guard is a raycast that reports a point somewhere other than along its own ray,
    // which is exactly the shape of the thing the rule exists to refuse.
    [Fact]
    public void AHitReportedBeyondSixMetresIsDiscarded()
    {
        var world = new DamageableWorld();
        world.Add(DamageableInstance.Object(new Vector3(0, 10f, -40f), GarbagePile()));
        Harness h = Joined(world,
            (Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out float distance,
                out Guid asset) =>
            {
                point = new Vector3(0, 10f, -40f); // forty metres away, claimed as one
                distance = 1f;
                asset = GarbagePile().Guid;
                return true;
            });

        h.Swing();
        h.Pump(2);

        Assert.False(Assert.Single(h.Results).Hit.Exists);
        Assert.Equal(75, world.HealthOf(0));
    }

    // A host with no ledger of its own: the ray still stops on the world, and the swing finds nothing.
    [Fact]
    public void AWorldHitWithNoLedgerDamagesNothing()
    {
        Harness h = Joined(world: null,
            (Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out float distance,
                out Guid asset) =>
            {
                point = origin + (direction * 0.5f);
                distance = 0.5f;
                asset = Guid.Empty; // terrain
                return true;
            });
        h.PlantZombie(1, new Vector3(0, 10f, -1.2f), yaw: 180f);

        h.Swing();
        h.Pump(2);

        // The wall is nearer than the zombie, so it takes the reach and nothing is damaged.
        Assert.False(Assert.Single(h.Results).Hit.Exists);
    }

    // A level with no zombie data still has rubble on it and a player who can punch it, so the host is
    // built without a brain rather than not built at all.
    [Fact]
    public void AHostWithNoZombieSystemStillBreaksTheWorld()
    {
        var world = new DamageableWorld();
        world.Add(DamageableInstance.Object(new Vector3(0, 10f, -1f), GarbagePile()));
        var h = new Harness(world,
            (Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point, out float distance,
                out Guid asset) =>
            {
                point = new Vector3(0, 10.5f, -1f);
                distance = 1f;
                asset = GarbagePile().Guid;
                return direction.Z < -0.5f;
            },
            withZombies: false);
        h.Pump(3);
        Assert.True(h.Client.Joined);

        h.Swing();
        h.Pump(2);

        Assert.Equal(70, world.HealthOf(0));
        Assert.Equal(EPunchTargetKind.Object, Assert.Single(h.Results).Hit.Kind);
    }

    // The swing is answered when its datagram lands, but the loop plays one queued movement frame per
    // tick — so after a burst the tick that emits the gesture can be replaying a frame from a third of a
    // second earlier. The punch has to be aimed by the frame that THREW it: here the player swings while
    // facing the zombie and then sends three frames looking the other way, all of which arrive first.
    [Fact]
    public void ASwingIsAimedByTheFrameThatThrewIt()
    {
        Harness h = Joined();
        ZombieInstance zombie = h.PlantZombie(1, new Vector3(0, 10f, -1.2f), yaw: 180f);

        h.Swing();                       // facing the zombie
        for (int i = 0; i < 3; i++)
            h.Look(yaw: 180f);           // then spun round, and these play first
        h.Pump(4);

        Assert.True(zombie.Health < zombie.MaxHealth,
            "the punch was resolved from a later frame's aim, not the swing's own");
    }

    [Fact]
    public void HostRejectsANullServer() =>
        Assert.Throws<ArgumentNullException>(() => new PunchDamageHost(null!, new Harness().Zombies));
}
