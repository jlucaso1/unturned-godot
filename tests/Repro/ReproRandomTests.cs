using System;
using UnturnedGodot.Repro;
using Xunit;

namespace UnturnedGodot.Tests.Repro;

// The generator exists so a dump can carry the session's own sequence. Everything below is that
// contract: the same state produces the same numbers, the state can be moved between instances, and
// the distributions are the ones the callers assume.
public class ReproRandomTests
{
    [Fact]
    public void SameSeed_ProducesTheSameSequence()
    {
        var a = new ReproRandom(12345);
        var b = new ReproRandom(12345);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(a.NextSingle(), b.NextSingle());
            Assert.Equal(a.Next(97), b.Next(97));
            Assert.Equal(a.NextDouble(), b.NextDouble());
        }
    }

    [Fact]
    public void DifferentSeeds_Diverge()
    {
        var a = new ReproRandom(1);
        var b = new ReproRandom(2);
        bool differed = false;
        for (int i = 0; i < 20 && !differed; i++)
            differed = a.NextSingle() != b.NextSingle();
        Assert.True(differed);
    }

    // The whole point: a dump stores State, and restoring it continues the sequence exactly.
    [Fact]
    public void RestoringTheState_ContinuesTheSequence()
    {
        var original = new ReproRandom(7);
        for (int i = 0; i < 13; i++)
            original.NextSingle();
        ulong saved = original.State;

        var restored = new ReproRandom(999) { State = saved };
        for (int i = 0; i < 25; i++)
            Assert.Equal(original.NextSingle(), restored.NextSingle());
    }

    [Fact]
    public void NextSingle_StaysInRange()
    {
        var random = new ReproRandom(3);
        for (int i = 0; i < 5000; i++)
        {
            float value = random.NextSingle();
            Assert.InRange(value, 0f, 0.9999999f);
        }
    }

    [Fact]
    public void NextDouble_StaysInRange()
    {
        var random = new ReproRandom(4);
        for (int i = 0; i < 5000; i++)
            Assert.InRange(random.NextDouble(), 0.0, 0.99999999999);
    }

    [Fact]
    public void Next_CoversItsBoundWithoutExceedingIt()
    {
        var random = new ReproRandom(5);
        var seen = new bool[4];
        for (int i = 0; i < 2000; i++)
        {
            int value = random.Next(4);
            Assert.InRange(value, 0, 3);
            seen[value] = true;
        }
        Assert.All(seen, Assert.True);
    }

    [Fact]
    public void Next_WithNoBound_IsNonNegative()
    {
        var random = new ReproRandom(6);
        for (int i = 0; i < 200; i++)
            Assert.InRange(random.Next(), 0, int.MaxValue - 1);
    }

    [Fact]
    public void Next_WithZeroBound_IsAlwaysZero() => Assert.Equal(0, new ReproRandom(8).Next(0));

    [Fact]
    public void Next_WithARange_StaysInside()
    {
        var random = new ReproRandom(9);
        for (int i = 0; i < 500; i++)
            Assert.InRange(random.Next(-5, 5), -5, 4);
    }

    [Fact]
    public void Next_WithAnEmptyRange_ReturnsTheBound() =>
        Assert.Equal(3, new ReproRandom(10).Next(3, 3));

    [Fact]
    public void NegativeBounds_Throw()
    {
        var random = new ReproRandom(11);
        Assert.Throws<ArgumentOutOfRangeException>(() => random.Next(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.Next(5, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.NextInt64(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.NextInt64(5, 1));
    }

    [Fact]
    public void Int64_StaysInRange()
    {
        var random = new ReproRandom(12);
        Assert.Equal(0L, random.NextInt64(0));
        for (int i = 0; i < 200; i++)
        {
            Assert.InRange(random.NextInt64(), 0L, long.MaxValue);
            Assert.InRange(random.NextInt64(100), 0L, 99L);
            Assert.InRange(random.NextInt64(-10, 10), -10L, 9L);
        }
    }

    [Fact]
    public void NextBytes_FillsAndIsReproducible()
    {
        var a = new byte[64];
        var b = new byte[64];
        new ReproRandom(13).NextBytes(a);
        new ReproRandom(13).NextBytes(b.AsSpan());
        Assert.Equal(a, b);
        Assert.Contains(a, value => value != 0);
        Assert.Throws<ArgumentNullException>(() => new ReproRandom(1).NextBytes((byte[])null!));
    }
}
