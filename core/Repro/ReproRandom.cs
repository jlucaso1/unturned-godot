using System;

namespace UnturnedGodot.Repro;

// The zombie brain's randomness, made reproducible.
//
// ZombieSystem draws from a System.Random for spawn rolls, the approach-path spread, the retreat
// scatter and the leave delay. That is fine to play with and impossible to capture: the BCL's
// generator keeps its state in private fields with no way to read or restore them, so a dump taken
// mid-session could never put the sequence back where it was and a replay would diverge the first
// time a zombie gave up a hunt.
//
// This is the same generator with a state that fits in one 64-bit integer, so a dump can carry it:
// PCG-XSH-RR 64/32 (O'Neill 2014), integer-only, identical on every platform and runtime. It derives
// from Random so it drops straight into the existing call sites.
public sealed class ReproRandom : Random
{
    // The LCG constants PCG uses: Knuth's 64-bit multiplier and an odd increment.
    private const ulong Multiplier = 6364136223846793005UL;
    private const ulong Increment = 1442695040888963407UL;

    private ulong _state;

    public ReproRandom(ulong seed)
    {
        // PCG's seeding: mix the seed in through one step so nearby seeds do not start correlated.
        _state = seed + Increment;
        NextUInt();
    }

    // The whole generator, readable and restorable — this is what makes a dump replayable.
    public ulong State
    {
        get => _state;
        set => _state = value;
    }

    private uint NextUInt()
    {
        ulong previous = _state;
        _state = (previous * Multiplier) + Increment;
        // XSH-RR output permutation: xorshift high bits down, then rotate by the top five.
        var xorshifted = (uint)(((previous >> 18) ^ previous) >> 27);
        var rotate = (int)(previous >> 59);
        return (xorshifted >> rotate) | (xorshifted << ((-rotate) & 31));
    }

    private ulong NextULong() => ((ulong)NextUInt() << 32) | NextUInt();

    public override int Next() => Next(int.MaxValue);

    public override int Next(int maxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxValue);
        // Lemire's multiply-shift: one draw, no modulo bias worth the rejection loop's variable
        // number of draws (which would make the sequence depend on the bound in a way that is
        // harder to reason about when reading a dump).
        return (int)(((ulong)NextUInt() * (ulong)(uint)maxValue) >> 32);
    }

    public override int Next(int minValue, int maxValue)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minValue, maxValue);
        return minValue + Next((int)((long)maxValue - minValue));
    }

    // 24 bits of mantissa: the float's full precision, and never 1.0f.
    public override float NextSingle() => (NextUInt() >> 8) * (1f / (1 << 24));

    public override double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    // Random.Sample deliberately not overridden: every public entry point above is, so nothing can
    // reach the base generator, and an override no caller can reach is a line no test can cover.

    public override long NextInt64() => (long)(NextULong() >> 1);

    public override long NextInt64(long maxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxValue);
        return maxValue == 0 ? 0 : NextInt64() % maxValue;
    }

    public override long NextInt64(long minValue, long maxValue)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minValue, maxValue);
        return minValue + NextInt64(maxValue - minValue);
    }

    public override void NextBytes(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        NextBytes(buffer.AsSpan());
    }

    public override void NextBytes(Span<byte> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (byte)NextUInt();
    }
}
