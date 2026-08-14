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

    // ---- the difficulty asset's own thresholds ---------------------------------------------------

    private static readonly global::System.Guid DifficultyGuid =
        global::System.Guid.Parse("646b4cdcc9c547b0a24528f8acccc8e8");

    private static UnturnedGodot.Assets.ZombieDifficultyBank Bank(string body)
    {
        var bank = new UnturnedGodot.Assets.ZombieDifficultyBank();
        Assert.True(UnturnedGodot.Assets.ZombieDifficultyAsset.TryParse(
            UnturnedGodot.Dat.DatParser.Parse(
                "Metadata\n{\n\tGUID " + DifficultyGuid.ToString("N")
                + "\n\tType SDG.Unturned.ZombieDifficultyAsset\n}\nAsset\n{\n" + body + "}\n"),
            out UnturnedGodot.Assets.ZombieDifficultyAsset? asset));
        bank.AddIfAbsent(asset);
        return bank;
    }

    // The asset hangs off the navigation bound by default, and off the zombie TABLE when the bound is
    // told to name none — the two halves of GetDifficultyInBoundForTable.
    private static ZombieSystem BrainWithDifficulty(string body, bool onBound = true)
    {
        var system = new ZombieSystem(
            new[]
            {
                new ZombieTable
                {
                    Name = "Civilian",
                    Health = 100,
                    Damage = 10,
                    DifficultyGuid = onBound ? default : DifficultyGuid,
                },
            },
            new List<NavBound>
            {
                new()
                {
                    Center = Vector3.Zero,
                    Size = new Vector3(400, 300, 400),
                    DifficultyGuid = onBound ? DifficultyGuid : default,
                },
            },
            FlatGround);
        system.Difficulties = Bank(body);
        return system;
    }

    // getStunDamageThreshold reads Normal_Stun_Threshold / Mega_Stun_Threshold off the zombie's cached
    // difficulty asset. Both were parsed and never consumed, so the hard-coded 20/150 always won and any
    // map customising the stagger was simply ignored.
    [Fact]
    public void ADifficultyAssetRaisesTheNormalStunThreshold()
    {
        ZombieSystem system = BrainWithDifficulty("\tNormal_Stun_Threshold 50\n");
        ZombieInstance zombie = Plant(system);

        system.Damage(zombie, 30, byte.MaxValue, NoPlayers);
        Assert.False(zombie.IsStunned);

        system.Damage(zombie, 51, byte.MaxValue, NoPlayers);
        Assert.True(zombie.IsStunned);
    }

    // And lowering it makes an otherwise harmless hit stagger.
    [Fact]
    public void ADifficultyAssetLowersTheNormalStunThreshold()
    {
        ZombieSystem system = BrainWithDifficulty("\tNormal_Stun_Threshold 5\n");
        ZombieInstance zombie = Plant(system);

        system.Damage(zombie, 6, byte.MaxValue, NoPlayers);
        Assert.True(zombie.IsStunned);
    }

    // "if (threshold < 1) threshold = -1" — an asset that sets neither leaves the built-ins alone.
    [Fact]
    public void AnAssetThatSetsNoThresholdKeepsTheBuiltIns()
    {
        ZombieSystem system = BrainWithDifficulty("\tCrawler_Chance 0.2\n");
        ZombieInstance zombie = Plant(system);

        system.Damage(zombie, 20, byte.MaxValue, NoPlayers);
        Assert.False(zombie.IsStunned); // strictly greater than 20
        system.Damage(zombie, 21, byte.MaxValue, NoPlayers);
        Assert.True(zombie.IsStunned);
    }

    // The mega branch, and the fact that a BOSS takes it: Zombie.isMega counts the bosses, so a boss
    // shrugs off the mega threshold rather than the normal one.
    [Fact]
    public void TheMegaThresholdCoversTheBossesToo()
    {
        ZombieSystem plain = Brain();
        ZombieInstance boss = Plant(plain, speciality: EZombieSpeciality.BossFire);
        boss.Health = boss.MaxHealth = 12000; // a boss's own health, so 200 is not a killing blow
        plain.Damage(boss, 100, byte.MaxValue, NoPlayers); // over 20, under 150
        Assert.False(boss.IsStunned);
        plain.Damage(boss, 200, byte.MaxValue, NoPlayers);
        Assert.True(boss.IsStunned);

        ZombieSystem custom = BrainWithDifficulty("\tMega_Stun_Threshold 40\n");
        ZombieInstance customBoss = Plant(custom, speciality: EZombieSpeciality.BossFire);
        custom.Damage(customBoss, 41, byte.MaxValue, NoPlayers);
        Assert.True(customBoss.IsStunned);
    }

    // The lookup is Zombie.updateDifficulty's, which passes forSpawnOverrides FALSE — so a table asset
    // that declines to override the spawn chance still sets this zombie's stagger.
    [Fact]
    public void ATableAssetDecliningToOverrideSpawnChanceStillSetsTheThreshold()
    {
        ZombieSystem system = BrainWithDifficulty(
            "\tOverrides_Spawn_Chance False\n\tNormal_Stun_Threshold 5\n", onBound: false);
        ZombieInstance zombie = Plant(system);

        system.Damage(zombie, 6, byte.MaxValue, NoPlayers);
        Assert.True(zombie.IsStunned);
    }

    // With no bank at all the built-ins stand, which is every shipped map — and the lookup must not
    // reach for a table index the zombie's Type does not have.
    [Fact]
    public void WithNoDifficultyBankTheBuiltInsStand()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);
        zombie.Type = 9; // past the end of the tables list

        system.Damage(zombie, 21, byte.MaxValue, NoPlayers);
        Assert.True(zombie.IsStunned);
    }

    [Fact]
    public void AZombieWhoseTypeIsPastTheTablesKeepsTheBuiltIns()
    {
        ZombieSystem system = BrainWithDifficulty("\tNormal_Stun_Threshold 500\n");
        ZombieInstance zombie = Plant(system);
        zombie.Type = 9;

        system.Damage(zombie, 21, byte.MaxValue, NoPlayers);
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

    // ---- The three races a stun used to lose -------------------------------------------------------

    // A zombie staggered mid-attack must leave the Attack state. The state is REPLICATED, and a zombie
    // left in Attack for the second it spends staggered has every client re-triggering its swing
    // animation off that state — the body plays the stagger and then snaps into a swing it is not
    // making. This is `isAttacking = false`, seen from the wire.
    [Fact]
    public void AStunTakesTheZombieOutOfTheAttackState()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);
        zombie.State = EZombieState.Attack;

        system.Stun(zombie);

        Assert.Equal(EZombieState.Chase, zombie.State);
    }

    // And the swing clock goes back to zero. Without it, a zombie interrupted late in its cooldown swings
    // the instant the stagger ends — the stun becomes a delay rather than a reprieve, which is exactly
    // what a player staggering it was trying to buy.
    [Fact]
    public void AStunResetsTheSwingClockRatherThanFreezingIt()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);
        zombie.SinceSwing = 10f; // long overdue for a swing

        system.Stun(zombie);

        Assert.Equal(0f, zombie.SinceSwing);
    }

    // The aggro race: being damaged both stuns and alerts, and the alert runs afterwards. It may retarget
    // the zombie and put it back into Chase — what it must NOT do is undo the stagger.
    [Fact]
    public void BeingAlertedByTheSameHitDoesNotUndoTheStun()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);
        zombie.State = EZombieState.Attack;
        var players = new[]
        {
            new ZombiePlayerView(7, new Vector3(1f, 0f, 0f), UnturnedGodot.Player.EPlayerStance.Stand, moving: false),
        };

        system.Damage(zombie, 30, 7, players);

        Assert.True(zombie.IsStunned);
        Assert.Equal(7, zombie.TargetPlayer);           // the alert landed
        Assert.True(zombie.PendingHit < 0f);            // and did not restore the swing
        Assert.Equal(0f, zombie.SinceSwing);
    }

    // A zombie alerted WHILE staggered — a second player shooting it, or the detect scan noticing someone
    // — keeps staggering. The alert changes who it is angry at, not whether it can move.
    [Fact]
    public void BeingAlertedDuringAStunKeepsTheZombieDown()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);
        var players = new[]
        {
            new ZombiePlayerView(7, new Vector3(1f, 0f, 0f), UnturnedGodot.Player.EPlayerStance.Stand, moving: false),
        };
        system.Stun(zombie);
        Vector3 before = zombie.Position;

        // A second, weaker hit from another player: alerts, does not stun.
        system.Damage(zombie, 5, 7, players);
        for (int i = 0; i < 5; i++)
            system.Tick(players, 0.08f);

        Assert.True(zombie.IsStunned);
        Assert.Equal(before, zombie.Position);
    }

    // Two stuns in a row do not stack into two seconds; the second restarts the clock, as a fresh
    // askStun does. A player landing three heavy hits should not freeze a zombie for three seconds.
    [Fact]
    public void ASecondStunRestartsTheClockRatherThanStacking()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);

        system.Stun(zombie);
        system.Tick(NoPlayers, 0.5f);
        system.Stun(zombie);

        Assert.Equal(ZombieStun.DurationSeconds, zombie.StunRemaining, 3);
    }

    // A zombie whose stun expires goes back to being an ordinary zombie: it moves again on the very next
    // tick rather than needing another event to release it.
    [Fact]
    public void AfterTheStunTheZombieHuntsAgain()
    {
        ZombieSystem system = Brain();
        ZombieInstance zombie = Plant(system);
        var players = new[]
        {
            new ZombiePlayerView(7, new Vector3(6f, 0f, 0f), UnturnedGodot.Player.EPlayerStance.Stand, moving: false),
        };
        system.Damage(zombie, 30, 7, players);
        Assert.True(zombie.IsStunned);

        for (int i = 0; i < 40; i++)
            system.Tick(players, 0.08f);

        Assert.False(zombie.IsStunned);
        Assert.True(zombie.SinceSwing > 0f, "the swing clock never restarted after the stagger");
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
