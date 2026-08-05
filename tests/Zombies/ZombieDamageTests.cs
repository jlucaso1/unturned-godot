using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Damage;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

// Zombie.askDamage and the alert DamageTool.damageZombie asks for afterwards. The health itself comes
// from the MAP — LevelZombies' own table — which is what makes a Police zombie tougher than a civilian
// without a line of code knowing either word.
public class ZombieDamageTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 5f;
        return true;
    }

    private static ZombieTable Table(bool mega = false, ushort health = 100) => new()
    {
        Name = mega ? "Special" : "Civilian",
        IsMega = mega,
        Health = health,
        Damage = 10,
    };

    private static List<NavBound> OneBound() => new()
    {
        new NavBound { Center = new Vector3(0, 140, 0), Size = new Vector3(200, 300, 200) },
    };

    // The speciality roll is random at spawn and it SCALES the health, so it is pinned here (and the
    // health re-derived) to keep every number below a statement about the punch rather than about the
    // seed. The roll itself is covered by SpecialityAdjustsHealth.
    private static ZombieSystem SpawnOne(out ZombieInstance zombie, ZombieTable? table = null)
    {
        ZombieTable resolved = table ?? Table();
        var system = new ZombieSystem(new[] { resolved }, OneBound(), FlatGround);
        system.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(0, 5f, 0)) }, new Random(1));
        zombie = Assert.Single(system.Zombies);
        zombie.Speciality = EZombieSpeciality.Normal;
        zombie.MaxHealth = ZombieInstance.HealthFor(resolved, EZombieSpeciality.Normal);
        zombie.Health = zombie.MaxHealth;
        return system;
    }

    private static ZombiePlayerView Player(byte id, Vector3 position) =>
        new(id, position, UnturnedGodot.Player.EPlayerStance.Stand, moving: false);

    // ---- Health at spawn -------------------------------------------------------------------------

    // Spawn fills health from the table without anyone asking it to, and full health is what a fresh
    // zombie has: the whole population would otherwise be corpses the first tick anything read IsDead.
    [Fact]
    public void SpawnsAtTheTablesHealth()
    {
        var system = new ZombieSystem(new[] { Table(health: 250) }, OneBound(), FlatGround);
        system.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(0, 5f, 0)) }, new Random(1));
        ZombieInstance zombie = Assert.Single(system.Zombies);
        Assert.Equal(ZombieInstance.HealthFor(Table(health: 250), zombie.Speciality), zombie.MaxHealth);
        Assert.Equal(zombie.MaxHealth, zombie.Health);
        Assert.False(zombie.IsDead);
    }

    // Zombie.tellAlive's speciality adjustments: a crawler is half again as tough, a sprinter half as.
    [Theory]
    [InlineData(EZombieSpeciality.Normal, 100)]
    [InlineData(EZombieSpeciality.Crawler, 150)]
    [InlineData(EZombieSpeciality.Sprinter, 50)]
    [InlineData(EZombieSpeciality.Mega, 100)]
    public void SpecialityAdjustsHealth(EZombieSpeciality speciality, int expected) =>
        Assert.Equal((ushort)expected, ZombieInstance.HealthFor(Table(), speciality));

    // A mega table is written with thousands and is used as-is.
    [Fact]
    public void MegaUsesItsTablesOwnNumber() =>
        Assert.Equal(2999, ZombieInstance.HealthFor(Table(mega: true, health: 2999),
            EZombieSpeciality.Mega));

    [Fact]
    public void HealthForRejectsANullTable() =>
        Assert.Throws<ArgumentNullException>(() =>
            ZombieInstance.HealthFor(null!, EZombieSpeciality.Normal));

    // ---- Damage ----------------------------------------------------------------------------------

    [Fact]
    public void DamageTakesHealthOff()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie, Table(health: 100));
        Assert.False(system.Damage(zombie, 30, instigator: 1, Array.Empty<ZombiePlayerView>()));
        Assert.Equal(70, zombie.Health);
        Assert.Single(system.Zombies);
    }

    [Fact]
    public void LethalDamageKillsAndRemovesIt()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie, Table(health: 100));
        Assert.True(system.Damage(zombie, 100, instigator: 1, Array.Empty<ZombiePlayerView>()));
        Assert.True(zombie.IsDead);
        Assert.Empty(system.Zombies);
        Assert.Empty(system.ZombiesInBound(zombie.Bound));
    }

    [Fact]
    public void OverkillDoesNotWrapTheHealthBar()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie, Table(health: 10));
        Assert.True(system.Damage(zombie, ushort.MaxValue, 1, Array.Empty<ZombiePlayerView>()));
        Assert.Equal(0, zombie.Health);
    }

    [Fact]
    public void ZeroDamageIsNotAHit()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        Assert.False(system.Damage(zombie, 0, 1, Array.Empty<ZombiePlayerView>()));
        Assert.Equal(zombie.MaxHealth, zombie.Health);
    }

    [Fact]
    public void DamagingSomethingAlreadyDeadDoesNothing()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie, Table(health: 10));
        system.Damage(zombie, 10, 1, Array.Empty<ZombiePlayerView>());
        Assert.False(system.Damage(zombie, 10, 1, Array.Empty<ZombiePlayerView>()));
    }

    // A hit is how a zombie aggroes without hearing anything: Zombie.alert with startle, whatever the
    // stealth rules would have said.
    [Fact]
    public void BeingHitTurnsItOnItsAttacker()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie, Table(health: 100));
        Assert.Equal(byte.MaxValue, zombie.TargetPlayer);
        system.Damage(zombie, 10, instigator: 3,
            new[] { Player(3, new Vector3(30f, 5f, 0)) });
        Assert.Equal(3, zombie.TargetPlayer);
        Assert.Equal(EZombieState.Chase, zombie.State);
    }

    // …but only if the attacker is somebody the simulation still knows about.
    [Fact]
    public void AnUnknownAttackerCannotBeChased()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie, Table(health: 100));
        system.Damage(zombie, 10, instigator: 9, Array.Empty<ZombiePlayerView>());
        Assert.Equal(byte.MaxValue, zombie.TargetPlayer);
    }

    // A zombie that dies while hunting hands its aggro claim back; the tally decides how the NEXT one to
    // notice that player approaches them, so a corpse holding a claim skews the rest of the session.
    [Fact]
    public void DeathReleasesItsAggroClaim()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie, Table(health: 100));
        var players = new[] { Player(3, new Vector3(30f, 5f, 0)) };
        system.Damage(zombie, 10, 3, players);
        Assert.Equal(1, system.AgroOn(3));
        system.Damage(zombie, 200, 3, players);
        Assert.Equal(0, system.AgroOn(3));
    }

    [Fact]
    public void FindLocatesByReplicatedId()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        Assert.Same(zombie, system.Find(zombie.Id));
        Assert.Null(system.Find(9999));
    }

    [Fact]
    public void RemoveTakesItOutOfBothLists()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Remove(zombie);
        Assert.Empty(system.Zombies);
        Assert.Empty(system.ZombiesInBound(zombie.Bound));
    }

    [Fact]
    public void DamageRejectsNulls()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        Assert.Throws<ArgumentNullException>(() =>
            system.Damage(null!, 1, 0, Array.Empty<ZombiePlayerView>()));
        Assert.Throws<ArgumentNullException>(() => system.Damage(zombie, 1, 0, null!));
        Assert.Throws<ArgumentNullException>(() => system.Remove(null!));
    }

    // ---- End to end: how many punches a zombie takes -------------------------------------------

    // A 100-health civilian, punched in the chest from the front, at nine a swing.
    [Fact]
    public void ChestPunchesKillACivilianInTwelveSwings()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie, Table(health: 100));
        zombie.Yaw = 180f; // facing the attacker: no backstab
        var players = new[] { Player(1, new Vector3(0, 5f, 2f)) };

        int swings = 0;
        while (!zombie.IsDead && swings < 13) // bounded: a punch that stopped working must fail, not hang
        {
            ushort amount = PunchDamageResolver.Zombie(ELimb.Spine, new Vector3(0, 0, -1),
                ZombieHitbox.ForwardOf(zombie));
            system.Damage(zombie, amount, 1, players);
            swings++;
        }
        Assert.True(zombie.IsDead);
        Assert.Equal(12, swings); // 11 x 9 = 99, the twelfth finishes it
    }

    [Fact]
    public void HeadPunchesKillItInSeven()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie, Table(health: 100));
        zombie.Yaw = 180f;
        var players = new[] { Player(1, new Vector3(0, 5f, 2f)) };

        int swings = 0;
        while (!zombie.IsDead && swings < 8)
        {
            ushort amount = PunchDamageResolver.Zombie(ELimb.Skull, new Vector3(0, 0, -1),
                ZombieHitbox.ForwardOf(zombie));
            system.Damage(zombie, amount, 1, players);
            swings++;
        }
        Assert.True(zombie.IsDead);
        Assert.Equal(7, swings); // 16 a swing
    }
}
