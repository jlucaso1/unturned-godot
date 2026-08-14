using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

public class PlayerEquipmentTests
{
    private static HandState Standing => new() { Stance = EPlayerStance.Stand };

    private static EPlayerGesture Punch(PlayerEquipment equipment, uint tick,
        EAttackInputFlags primary = EAttackInputFlags.Start,
        EAttackInputFlags secondary = EAttackInputFlags.None) =>
        equipment.Simulate(tick, primary, secondary, Standing);

    [Fact]
    public void Simulate_PrimaryStartThrowsTheLeftFist()
    {
        var equipment = new PlayerEquipment();
        Assert.Equal(EPlayerGesture.PunchLeft, Punch(equipment, 1));
    }

    [Fact]
    public void Simulate_SecondaryStartThrowsTheRightFist()
    {
        var equipment = new PlayerEquipment();
        Assert.Equal(EPlayerGesture.PunchRight,
            Punch(equipment, 1, EAttackInputFlags.None, EAttackInputFlags.Start));
    }

    [Fact]
    public void Simulate_NothingHappensWithoutAStartEdge()
    {
        var equipment = new PlayerEquipment();
        // Holding a button produces Start once; a tick carrying only Stop, or nothing, swings at nothing.
        Assert.Equal(EPlayerGesture.None, Punch(equipment, 1, EAttackInputFlags.None));
        Assert.Equal(EPlayerGesture.None, Punch(equipment, 2, EAttackInputFlags.Stop));
    }

    [Fact]
    public void Simulate_HoldsTheCooldownThenSwingsAgain()
    {
        var equipment = new PlayerEquipment();
        Assert.Equal(EPlayerGesture.PunchLeft, Punch(equipment, 10));

        // simulate_PunchInput: "simulation - lastPunch > 5", so ticks 11..15 are still on cooldown.
        for (uint tick = 11; tick <= 10 + PlayerEquipment.PunchCooldownTicks; tick++)
            Assert.Equal(EPlayerGesture.None, Punch(equipment, tick));

        Assert.Equal(EPlayerGesture.PunchLeft, Punch(equipment, 16));
    }

    [Fact]
    public void Simulate_OneFistPerTickWithPrimaryWinning()
    {
        var equipment = new PlayerEquipment();
        // Both buttons on the same tick: the primary's punch claims the cooldown, so the secondary's
        // cannot also fire — which is what the original's two sequential blocks amount to.
        Assert.Equal(EPlayerGesture.PunchLeft,
            Punch(equipment, 1, EAttackInputFlags.Start, EAttackInputFlags.Start));
    }

    [Fact]
    public void Simulate_NoPunchWhileProne()
    {
        var equipment = new PlayerEquipment();
        Assert.Equal(EPlayerGesture.None, equipment.Simulate(1, EAttackInputFlags.Start,
            EAttackInputFlags.None, new HandState { Stance = EPlayerStance.Prone }));

        // Refused, not merely delayed: standing up on the very next tick swings immediately, because a
        // punch that never happened must not have started a cooldown.
        Assert.Equal(EPlayerGesture.PunchLeft, Punch(equipment, 2));
    }

    [Fact]
    public void Simulate_NoPunchWhileBusy()
    {
        var equipment = new PlayerEquipment();
        Assert.Equal(EPlayerGesture.None, equipment.Simulate(1, EAttackInputFlags.Start,
            EAttackInputFlags.None, new HandState { Stance = EPlayerStance.Stand, IsBusy = true }));
    }

    private static HandState InSafezone(bool noWeapons) => new()
    {
        Stance = EPlayerStance.Stand,
        IsSafe = true,
        Safezone = new UnturnedGodot.Data.SafezoneNode(
            Godot.Vector3.Zero, 10f, isHeight: true, noWeapons, noBuildables: false),
    };

    // "if (player.movement.isSafe) { if (asset == null) { if (isSafeInfo == null || noWeapons)
    // return; // no punching } }" — with nothing equipped, which is always the case here, a safezone
    // that forbids weapons forbids the fist too. The safezones themselves come from LevelNodes now,
    // which parsed and discarded them until this change.
    [Fact]
    public void Simulate_NoPunchInASafezoneThatForbidsWeapons()
    {
        var equipment = new PlayerEquipment();

        Assert.Equal(EPlayerGesture.None, equipment.Simulate(1, EAttackInputFlags.Start,
            EAttackInputFlags.None, InSafezone(noWeapons: true)));
        // Refused, not merely delayed: leaving the zone on the next tick swings immediately.
        Assert.Equal(EPlayerGesture.PunchLeft, Punch(equipment, 2));
    }

    [Fact]
    public void Simulate_PunchesInASafezoneThatAllowsWeapons() =>
        Assert.Equal(EPlayerGesture.PunchLeft, new PlayerEquipment().Simulate(
            1, EAttackInputFlags.Start, EAttackInputFlags.None, InSafezone(noWeapons: false)));

    // "isSafeInfo == null || noWeapons" — a safezone whose details cannot be resolved is treated as the
    // STRICTEST case, not the most permissive.
    [Fact]
    public void Simulate_SafeWithNoResolvedZone_ForbidsThePunch() =>
        Assert.Equal(EPlayerGesture.None, new PlayerEquipment().Simulate(
            1, EAttackInputFlags.Start, EAttackInputFlags.None,
            new HandState { Stance = EPlayerStance.Stand, IsSafe = true }));

    // A zone the player is standing OUTSIDE of gates nothing, even when it forbids weapons.
    [Fact]
    public void Simulate_NotSafe_IgnoresTheZoneEntirely() =>
        Assert.Equal(EPlayerGesture.PunchLeft, new PlayerEquipment().Simulate(
            1, EAttackInputFlags.Start, EAttackInputFlags.None,
            InSafezone(noWeapons: true) with { IsSafe = false }));

    [Fact]
    public void Simulate_SwingsOnTheFirstTickOfASession()
    {
        // Tick 0 with no punch behind it: the cooldown must not read "0 - 0 = 0" as a recent swing and
        // swallow the player's very first click.
        var equipment = new PlayerEquipment();
        Assert.Equal(EPlayerGesture.PunchLeft, Punch(equipment, 0));
    }

    [Fact]
    public void Simulate_CooldownSurvivesTheTickCounterWrapping()
    {
        var equipment = new PlayerEquipment();
        Assert.Equal(EPlayerGesture.PunchLeft, Punch(equipment, uint.MaxValue - 2));
        // Unsigned subtraction measures the gap correctly across the wrap; naive comparison would treat
        // the wrapped tick as ancient and let the swing through early.
        Assert.Equal(EPlayerGesture.None, Punch(equipment, 1));
        Assert.Equal(EPlayerGesture.PunchLeft, Punch(equipment, 4));
    }

    [Theory]
    [InlineData(EPlayerGesture.PunchLeft, "Punch_Left")]
    [InlineData(EPlayerGesture.PunchRight, "Punch_Right")]
    public void ClipFor_NamesTheAnimationTheCharacterCarries(EPlayerGesture gesture, string clip)
    {
        Assert.Equal(clip, PlayerGestures.ClipFor(gesture));
    }

    [Theory]
    [InlineData(EPlayerGesture.None)]
    [InlineData(EPlayerGesture.Wave)] // ported for the wire, no animation wired up yet
    public void ClipFor_IsNullForAGestureWithNoAnimationYet(EPlayerGesture gesture)
    {
        Assert.Null(PlayerGestures.ClipFor(gesture));
    }

    // Both fists swing through the same pair of clips — the game ships one MeleeAttack set, not one per
    // hand — so the sound does not distinguish them the way the animation does.
    [Theory]
    [InlineData(EPlayerGesture.PunchLeft)]
    [InlineData(EPlayerGesture.PunchRight)]
    public void SoundFor_GivesBothFistsTheSameSwing(EPlayerGesture gesture)
    {
        Assert.Equal(PlayerGestures.PunchSound, PlayerGestures.SoundFor(gesture));
    }

    [Theory]
    [InlineData(EPlayerGesture.None)]
    [InlineData(EPlayerGesture.Wave)]
    public void SoundFor_IsNullForAGestureWithNoSoundYet(EPlayerGesture gesture)
    {
        Assert.Null(PlayerGestures.SoundFor(gesture));
    }

    [Fact]
    public void For_MapsEachFistToItsGesture()
    {
        Assert.Equal(EPlayerGesture.PunchLeft, PlayerGestures.For(EPlayerPunch.Left));
        Assert.Equal(EPlayerGesture.PunchRight, PlayerGestures.For(EPlayerPunch.Right));
    }

    [Fact]
    public void Gestures_KeepTheGamesOwnWireOrdering()
    {
        // EPlayerGesture is a [NetEnum] in Unturned: the ordinals are what the wire carries, so they are
        // pinned rather than left to reordering.
        Assert.Equal(0, (byte)EPlayerGesture.None);
        Assert.Equal(4, (byte)EPlayerGesture.PunchLeft);
        Assert.Equal(5, (byte)EPlayerGesture.PunchRight);
    }
}
