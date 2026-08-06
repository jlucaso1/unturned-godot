using System;

namespace UnturnedGodot.Zombies;

// Zombie.EZombieStunOverride: whether a hit's own circumstances have already decided the stun, ahead of
// the damage threshold.
public enum EZombieStunOverride : byte
{
    // Let the threshold decide (or, under Only_Critical_Stuns, decide against).
    None,
    Always,
    Never,
}

// Zombie.stun / askStun / getStunDamageThreshold: the stagger a hard enough hit knocks into a zombie.
//
// This is the "knockback" a player sees. A stunned zombie stops where it stands, drops its in-progress
// swing, and plays one of the Stun_N reels for a second before it can move again — the reason a strong
// melee hit buys distance.
//
// Worth knowing before reading further: a bare FIST cannot reach this. The threshold is 20 and a punch to
// the skull is floor(15 x 1.1) = 16, or floor(20.625) = 20 with a backstab — and the comparison is
// strictly greater. So none of this fires for the punch that is currently the only attack in the port. It
// is ported now because it belongs to the damage path rather than to any one weapon, and a melee asset's
// zombieStunOverride is exactly the field that will switch it on.
public static class ZombieStun
{
    // getStunDamageThreshold's built-in values, used when the difficulty asset overrides neither. A mega
    // shrugs off nearly eight times what a normal zombie does.
    public const int NormalThreshold = 20;
    public const int MegaThreshold = 150;

    // How long a stunned zombie stays down, server-side: "Time.time - lastStun > 1". The CLIENT plays the
    // reel for however long the clip runs, which is a different (and usually shorter) number — the two are
    // deliberately independent, exactly as they are in the original.
    public const float DurationSeconds = 1f;

    // The stun reels each speciality picks between. Zombie.stun rolls one at random and sends the index;
    // the client plays "Stun_" + it.
    public static readonly byte[] CrawlerClips = { 5, 7, 8 };
    public static readonly byte[] SprinterClips = { 6, 9, 10 };

    // Everyone else rolls Random.Range(0, 5) — Unity's int overload, so 0..4 inclusive of neither 5.
    public const byte StandingClipCount = 5;

    // The damage a hit must EXCEED to stagger this zombie. `overrideValue` is the difficulty asset's
    // Normal_Stun_Threshold / Mega_Stun_Threshold, which only applies when it is positive — the original
    // reads -1 as "unset" and falls back to the built-in.
    public static int ThresholdFor(bool isMega, int overrideValue = -1)
    {
        if (overrideValue > 0)
            return overrideValue;
        return isMega ? MegaThreshold : NormalThreshold;
    }

    // Whether this hit staggers, given the mode's two switches and whatever the weapon already decided.
    //
    // Zombie.askDamage's shape exactly: Can_Stun gates everything; an explicit Always stuns regardless of
    // damage; otherwise the threshold decides, but ONLY when Only_Critical_Stuns is off — with it on, a
    // hit that did not already earn an Always never stuns however hard it landed.
    public static bool ShouldStun(ushort amount, bool isMega, bool canStun, bool onlyCriticalStuns,
        EZombieStunOverride stunOverride = EZombieStunOverride.None, int thresholdOverride = -1)
    {
        if (!canStun || stunOverride == EZombieStunOverride.Never)
            return false;
        if (stunOverride == EZombieStunOverride.Always)
            return true;
        if (onlyCriticalStuns)
            return false;
        return amount > ThresholdFor(isMega, thresholdOverride);
    }

    // Which reel to play. `roll` is a uniform value in [0, 1): the original uses Random.value for the two
    // three-way splits and Random.Range for the standing five, and passing the roll in keeps this pure and
    // keeps the server's choice reproducible in a replay.
    public static byte ClipFor(EZombieSpeciality speciality, float roll)
    {
        roll = Math.Clamp(roll, 0f, 0.9999f);
        return speciality switch
        {
            // "< 0.33 / < 0.66 / else" — the thirds are not exact, and are copied as written.
            EZombieSpeciality.Crawler => Pick(CrawlerClips, roll),
            EZombieSpeciality.Sprinter => Pick(SprinterClips, roll),
            _ => (byte)(int)(roll * StandingClipCount),
        };
    }

    private static byte Pick(byte[] clips, float roll) =>
        roll < 0.33f ? clips[0] : roll < 0.66f ? clips[1] : clips[2];
}
