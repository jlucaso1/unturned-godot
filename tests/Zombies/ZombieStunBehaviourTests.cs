using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

// What a stun does to a zombie in the simulation: it stops, it drops the swing it was in the middle of,
// and it comes back after a second.
//
// The dropped swing is the part worth testing. Zombie.stun sets isAttacking false, and a pending hit left
// counting down would land its damage a moment INTO the stagger — which is precisely the hit a player
// staggered the zombie to avoid.
public class ZombieStunBehaviourTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    private static ZombieSystem Brain()
    {
        var system = new ZombieSystem(
            new[] { new ZombieTable { Name = "Civilian", Health = 100, Damage = 10 } },
            new List<NavBound>
            {
                new() { Center = Vector3.Zero, Size = new Vector3(400, 300, 400) },
            },
            FlatGround);
        return system;
    }

    private static ZombieInstance Plant(ZombieSystem system, ushort id = 1,
        EZombieSpeciality speciality = EZombieSpeciality.Normal)
    {
        var state = new ZombieSystemState();
        state.Zombies.Add(new ZombieInstance
        {
            Id = id,
            Bound = 0,
            Position = Vector3.Zero,
            Health = 100,
            MaxHealth = 100,
            Speciality = speciality,
        });
        system.RestoreState(state);
        return system.Zombies[0];
    }

    private static readonly IReadOnlyList<ZombiePlayerView> NoPlayers = global::System.Array.Empty<ZombiePlayerView>();

    [Fact]
    public void AHardEnoughHitStunsAndRaisesTheEvent()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);
        var stuns = new List<(ushort Id, byte Clip)>();
        system.Stunned += (z, clip) => stuns.Add((z.Id, clip));

        system.Damage(zombie, 30, byte.MaxValue, NoPlayers);

        Assert.True(zombie.IsStunned);
        Assert.Equal(1, Assert.Single(stuns).Id);
        Assert.InRange(stuns[0].Clip, (byte)0, (byte)4);
    }

    [Fact]
    public void AHitBelowTheThresholdDoesNotStun()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);
        var stuns = new List<ushort>();
        system.Stunned += (z, _) => stuns.Add(z.Id);

        system.Damage(zombie, 20, byte.MaxValue, NoPlayers);

        Assert.False(zombie.IsStunned);
        Assert.Empty(stuns);
    }

    // A KILLING blow does not stagger. Zombie.askDamage only reaches the stun on the branch where the
    // zombie survived — the body is being replaced by a ragdoll, and a stagger reel on a corpse would
    // fight it.
    [Fact]
    public void AKillingBlowRaisesNoStun()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);
        var stuns = new List<ushort>();
        system.Stunned += (z, _) => stuns.Add(z.Id);

        Assert.True(system.Damage(zombie, 500, byte.MaxValue, NoPlayers));
        Assert.Empty(stuns);
    }

    // The whole point of the stagger: the swing already in flight never lands.
    [Fact]
    public void AStunCancelsTheSwingAlreadyInFlight()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);
        zombie.PendingHit = 0.2f;

        system.Stun(zombie);

        Assert.True(zombie.PendingHit < 0f, "the pending hit survived the stun");
    }

    // A second of standing still, and then the body is its own again. Ticked rather than asserted on the
    // field, because it is the TICK that has to stop running the behaviour.
    [Fact]
    public void AStunnedZombieDoesNothingForASecondAndThenRecovers()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);
        system.Stun(zombie);

        // Just short of a second: still down.
        for (int i = 0; i < 11; i++)
            system.Tick(NoPlayers, 0.08f);
        Assert.True(zombie.IsStunned);

        // Past it.
        for (int i = 0; i < 4; i++)
            system.Tick(NoPlayers, 0.08f);
        Assert.False(zombie.IsStunned);
    }

    // A stunned zombie does not move, whatever it was doing. Its swing clock does not advance either —
    // the stagger is not a pause the animation resumes from.
    [Fact]
    public void AStunnedZombieNeitherMovesNorAdvancesItsSwingClock()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);
        zombie.State = EZombieState.Chase;
        zombie.SinceSwing = 0f;
        Vector3 before = zombie.Position;

        system.Stun(zombie);
        for (int i = 0; i < 5; i++)
            system.Tick(NoPlayers, 0.08f);

        Assert.Equal(before, zombie.Position);
        Assert.Equal(0f, zombie.SinceSwing);
    }

    // The mode switches reach the simulation, not only the pure helper.
    [Fact]
    public void WithCanStunOffTheSimulationNeverStuns()
    {
        ZombieSystem system = Brain();
        system.CanStun = false;
        ZombieInstance zombie = Plant(system);

        system.Damage(zombie, 9999, byte.MaxValue, NoPlayers);

        Assert.False(zombie.IsStunned);
    }

    [Fact]
    public void AnAlwaysOverrideStunsThroughTheSimulation()
    {
        ZombieSystem system = Brain();
        system.OnlyCriticalStuns = true;
        ZombieInstance zombie = Plant(system);

        system.Damage(zombie, 1, byte.MaxValue, NoPlayers, EZombieStunOverride.Always);

        Assert.True(zombie.IsStunned);
    }

    // A crawler's reel has to come from the crawler set even when the roll goes through the simulation's
    // own RNG — the speciality is read off the instance, not passed in.
    [Fact]
    public void ACrawlerStunsWithACrawlerReel()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system, speciality: EZombieSpeciality.Crawler);
        var clips = new List<byte>();
        system.Stunned += (_, clip) => clips.Add(clip);

        for (int i = 0; i < 20; i++)
        {
            zombie.StunRemaining = 0f;
            system.Stun(zombie);
        }

        foreach (byte clip in clips)
            Assert.Contains(clip, ZombieStun.CrawlerClips);
    }

    [Fact]
    public void StunningADeadZombieDoesNothing()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);
        zombie.Health = 0;
        var stuns = new List<ushort>();
        system.Stunned += (z, _) => stuns.Add(z.Id);

        system.Stun(zombie);

        Assert.False(zombie.IsStunned);
        Assert.Empty(stuns);
    }
}
