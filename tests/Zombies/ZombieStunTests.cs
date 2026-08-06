using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

// Zombie.getStunDamageThreshold and the gate above it. These are the numbers that decide whether a weapon
// staggers a zombie at all, and they interact in a way that is easy to get subtly wrong: two mode switches
// and a per-weapon override on top of a threshold.
public class ZombieStunTests
{
    [Fact]
    public void TheBuiltInThresholdsAreTwentyAndAHundredAndFifty()
    {
        Assert.Equal(20, ZombieStun.ThresholdFor(isMega: false));
        Assert.Equal(150, ZombieStun.ThresholdFor(isMega: true));
    }

    // The difficulty asset's Normal_Stun_Threshold / Mega_Stun_Threshold. Only a POSITIVE value counts;
    // the original reads -1 (and 0) as unset and falls back.
    [Theory]
    [InlineData(-1, 20)]
    [InlineData(0, 20)]
    [InlineData(5, 5)]
    [InlineData(999, 999)]
    public void APositiveDifficultyOverrideReplacesTheBuiltIn(int over, int expected) =>
        Assert.Equal(expected, ZombieStun.ThresholdFor(isMega: false, over));

    // The comparison is STRICTLY greater. This is not pedantry: a punch to the skull with a backstab
    // lands on exactly 20, and whether that staggers is the difference between the fist being a stun
    // weapon and not being one. It is not.
    [Fact]
    public void DamageEqualToTheThresholdDoesNotStun()
    {
        Assert.False(Stuns(20));
        Assert.True(Stuns(21));
    }

    [Fact]
    public void AMegaShrugsOffWhatStaggersANormalZombie()
    {
        Assert.True(ZombieStun.ShouldStun(100, isMega: false, canStun: true, onlyCriticalStuns: false));
        Assert.False(ZombieStun.ShouldStun(100, isMega: true, canStun: true, onlyCriticalStuns: false));
        Assert.True(ZombieStun.ShouldStun(151, isMega: true, canStun: true, onlyCriticalStuns: false));
    }

    // Provider.modeConfigData.Zombies.Can_Stun, which HARD turns off outright: nothing staggers.
    [Fact]
    public void WithCanStunOffNothingStunsAtAll()
    {
        Assert.False(ZombieStun.ShouldStun(9999, isMega: false, canStun: false, onlyCriticalStuns: false));
        Assert.False(ZombieStun.ShouldStun(9999, isMega: false, canStun: false, onlyCriticalStuns: false,
            EZombieStunOverride.Always));
    }

    // Only_Critical_Stuns: the threshold stops applying entirely, and only a hit that already earned an
    // Always — a backstab, or a strong melee swing — staggers.
    [Fact]
    public void OnlyCriticalStunsIgnoresTheThresholdAndKeepsTheOverride()
    {
        Assert.False(ZombieStun.ShouldStun(9999, isMega: false, canStun: true, onlyCriticalStuns: true));
        Assert.True(ZombieStun.ShouldStun(1, isMega: false, canStun: true, onlyCriticalStuns: true,
            EZombieStunOverride.Always));
    }

    [Fact]
    public void AnAlwaysOverrideStunsWhateverTheDamage() =>
        Assert.True(ZombieStun.ShouldStun(1, isMega: true, canStun: true, onlyCriticalStuns: false,
            EZombieStunOverride.Always));

    [Fact]
    public void ANeverOverrideRefusesWhateverTheDamage() =>
        Assert.False(ZombieStun.ShouldStun(9999, isMega: false, canStun: true, onlyCriticalStuns: false,
            EZombieStunOverride.Never));

    // Each speciality plays from its own set of reels, and only from those: a crawler playing a standing
    // stagger would have it snap upright off the floor.
    [Theory]
    [InlineData(EZombieSpeciality.Crawler)]
    [InlineData(EZombieSpeciality.Sprinter)]
    public void ASpecialitysReelsComeFromItsOwnSet(EZombieSpeciality speciality)
    {
        byte[] expected = speciality == EZombieSpeciality.Crawler
            ? ZombieStun.CrawlerClips
            : ZombieStun.SprinterClips;

        var seen = new System.Collections.Generic.HashSet<byte>();
        for (float roll = 0f; roll < 1f; roll += 0.01f)
            seen.Add(ZombieStun.ClipFor(speciality, roll));

        Assert.Equal(3, seen.Count);
        foreach (byte clip in seen)
            Assert.Contains(clip, expected);
    }

    // Random.Range(0, 5) is the int overload: 0..4, and 5 is never rolled.
    [Fact]
    public void AStandingZombieRollsZeroToFour()
    {
        var seen = new System.Collections.Generic.HashSet<byte>();
        for (float roll = 0f; roll < 1f; roll += 0.001f)
            seen.Add(ZombieStun.ClipFor(EZombieSpeciality.Normal, roll));

        Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, new System.Collections.Generic.SortedSet<byte>(seen));
    }

    // A roll at or past 1 must not index off the end of the reel set — the caller passes a float from an
    // RNG, and NextSingle's contract is [0, 1) but a Godot RandfRange is inclusive.
    [Fact]
    public void ARollAtTheEdgeStaysInRange()
    {
        Assert.Contains(ZombieStun.ClipFor(EZombieSpeciality.Crawler, 1f), ZombieStun.CrawlerClips);
        Assert.Contains(ZombieStun.ClipFor(EZombieSpeciality.Sprinter, 2f), ZombieStun.SprinterClips);
        Assert.InRange(ZombieStun.ClipFor(EZombieSpeciality.Normal, 1f), (byte)0, (byte)4);
        Assert.InRange(ZombieStun.ClipFor(EZombieSpeciality.Normal, -1f), (byte)0, (byte)4);
    }

    private static bool Stuns(ushort amount) =>
        ZombieStun.ShouldStun(amount, isMega: false, canStun: true, onlyCriticalStuns: false);
}
