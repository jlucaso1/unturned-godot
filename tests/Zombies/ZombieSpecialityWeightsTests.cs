using System;
using System.Collections.Generic;
using System.Linq;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

public class ZombieSpecialityWeightsTests
{
    // "List is empty" -> default, which under the game's own numbering is NONE rather than NORMAL.
    [Fact]
    public void Empty_PicksTheDefault()
    {
        var weights = new ZombieSpecialityWeights();
        Assert.Equal(0f, weights.TotalWeight);
        Assert.Equal(EZombieSpeciality.None, weights.Pick(0.5f));
    }

    // "Default CompareTo uses less than, so we negate to put highest weights at the front of the list."
    [Fact]
    public void Entries_AreSortedByDescendingWeight()
    {
        var weights = new ZombieSpecialityWeights();
        weights.Add(EZombieSpeciality.Acid, 0.05f);
        weights.Add(EZombieSpeciality.Crawler, 0.5f);
        weights.Add(EZombieSpeciality.Burner, 0.2f);

        Assert.Equal(new[] { 0.5f, 0.2f, 0.05f }, weights.Entries.Select(e => e.Weight).ToArray());
    }

    // "weight = Mathf.Max(weight, 0.0f)": a negative weight is clamped, not allowed to eat the total.
    [Fact]
    public void NegativeWeight_IsClampedToZero()
    {
        var weights = new ZombieSpecialityWeights();
        weights.Add(EZombieSpeciality.Crawler, -3f);
        weights.Add(EZombieSpeciality.Sprinter, 0.25f);

        Assert.Equal(0.25f, weights.TotalWeight);
        Assert.Equal(0f, weights.Entries.Single(e => e.Value == EZombieSpeciality.Crawler).Weight);
    }

    // The walk: scale one uniform draw by the total, then subtract along the (descending) list.
    [Fact]
    public void Pick_WalksTheSortedListSubtractingAsItGoes()
    {
        var weights = new ZombieSpecialityWeights();
        weights.Add(EZombieSpeciality.Crawler, 0.5f);   // [0]: draw * 1.0 in [0, 0.5)
        weights.Add(EZombieSpeciality.Sprinter, 0.3f);  // [1]: [0.5, 0.8)
        weights.Add(EZombieSpeciality.Acid, 0.2f);      // [2]: [0.8, 1.0)

        Assert.Equal(EZombieSpeciality.Crawler, weights.Pick(0f));
        Assert.Equal(EZombieSpeciality.Crawler, weights.Pick(0.49f));
        Assert.Equal(EZombieSpeciality.Sprinter, weights.Pick(0.5f));
        Assert.Equal(EZombieSpeciality.Sprinter, weights.Pick(0.79f));
        Assert.Equal(EZombieSpeciality.Acid, weights.Pick(0.8f));
        Assert.Equal(EZombieSpeciality.Acid, weights.Pick(0.99f));
    }

    // "Maybe edge case with small numbers at end of list? Default to highest weight." Random.value is
    // [0, 1), so a draw of exactly 1 cannot happen in the game — but the fallback is ported anyway
    // because floating-point subtraction can walk off the end with the draw just under it.
    [Fact]
    public void Pick_PastTheEnd_FallsBackToTheHeaviest()
    {
        var weights = new ZombieSpecialityWeights();
        weights.Add(EZombieSpeciality.Sprinter, 0.3f);
        weights.Add(EZombieSpeciality.Crawler, 0.7f); // heaviest, so the sort puts it at [0]

        Assert.Equal(EZombieSpeciality.Crawler, weights.Pick(1f));
    }

    // "Not ideal, but many configurations exist assuming normal is the default with all chances adding
    // up to 100%."
    [Fact]
    public void NormalRemainder_TakesWhateverIsLeft()
    {
        var weights = new ZombieSpecialityWeights();
        weights.Add(EZombieSpeciality.Crawler, 0.2f);
        weights.Add(EZombieSpeciality.Sprinter, 0.2f);
        weights.AddNormalRemainder();

        Assert.Equal(1f, weights.TotalWeight, 5);
        Assert.Equal(0.6f, weights.Entries.Single(e => e.Value == EZombieSpeciality.Normal).Weight, 5);
    }

    // An asset whose weights sum past 1 produces NO normal zombies rather than an error: the remainder
    // is negative and the Max clamps it to zero.
    [Fact]
    public void NormalRemainder_OversubscribedTableYieldsNoNormals()
    {
        var weights = new ZombieSpecialityWeights();
        weights.Add(EZombieSpeciality.Crawler, 0.8f);
        weights.Add(EZombieSpeciality.Sprinter, 0.8f);
        weights.AddNormalRemainder();

        Assert.Equal(0f, weights.Entries.Single(e => e.Value == EZombieSpeciality.Normal).Weight);
        Assert.Equal(1.6f, weights.TotalWeight, 5);
        // Every draw lands on one of the two real entries.
        for (int i = 0; i < 100; i++)
            Assert.NotEqual(EZombieSpeciality.Normal, weights.Pick(i / 100f));
    }

    [Fact]
    public void Clear_EmptiesTheTable()
    {
        var weights = new ZombieSpecialityWeights();
        weights.Add(EZombieSpeciality.Crawler, 0.5f);
        weights.Clear();

        Assert.Equal(0f, weights.TotalWeight);
        Assert.Empty(weights.Entries);
    }

    // The Random overload is the one the spawn path uses; it must draw exactly once, because that count
    // is what every later pick in the same seeded stream depends on.
    [Fact]
    public void PickFromRandom_DrawsExactlyOnce()
    {
        var weights = new ZombieSpecialityWeights();
        weights.Add(EZombieSpeciality.Crawler, 1f);

        var a = new Random(4);
        weights.Pick(a);
        float afterOnePick = a.NextSingle();

        var b = new Random(4);
        b.NextSingle();
        Assert.Equal(b.NextSingle(), afterOnePick);
    }

    // The distribution over many draws, which is the property a player would actually notice.
    [Fact]
    public void Distribution_FollowsTheWeights()
    {
        var weights = new ZombieSpecialityWeights();
        weights.Add(EZombieSpeciality.Crawler, 0.2f);
        weights.Add(EZombieSpeciality.Sprinter, 0.2f);
        weights.AddNormalRemainder();

        var counts = new Dictionary<EZombieSpeciality, int>();
        const int draws = 60000;
        var random = new Random(9);
        for (int i = 0; i < draws; i++)
        {
            EZombieSpeciality kind = weights.Pick(random);
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }

        Assert.InRange(counts[EZombieSpeciality.Crawler] / (double)draws, 0.19, 0.21);
        Assert.InRange(counts[EZombieSpeciality.Sprinter] / (double)draws, 0.19, 0.21);
        Assert.InRange(counts[EZombieSpeciality.Normal] / (double)draws, 0.59, 0.61);
    }

    // ---- the extension predicates ----------------------------------------------------------------

    [Theory]
    [InlineData(EZombieSpeciality.RedVolatile, true)]
    [InlineData(EZombieSpeciality.BlueVolatile, true)]
    [InlineData(EZombieSpeciality.Sprinter, false)]
    [InlineData(EZombieSpeciality.Normal, false)]
    public void IsDLVolatile(EZombieSpeciality kind, bool expected) =>
        Assert.Equal(expected, kind.IsDLVolatile());

    [Theory]
    [InlineData(EZombieSpeciality.BossElectric, true)]
    [InlineData(EZombieSpeciality.BossKuwait, true)]
    [InlineData(EZombieSpeciality.BossBuakFinal, true)]
    [InlineData(EZombieSpeciality.BossAll, false)]   // deliberately absent from the original's list
    [InlineData(EZombieSpeciality.Mega, false)]
    [InlineData(EZombieSpeciality.Normal, false)]
    public void IsBoss(EZombieSpeciality kind, bool expected) => Assert.Equal(expected, kind.IsBoss());

    // "Boss zombies are considered mega as well" — and BOSS_ALL, which is not a boss, is a mega.
    [Theory]
    [InlineData(EZombieSpeciality.Mega, true)]
    [InlineData(EZombieSpeciality.BossAll, true)]
    [InlineData(EZombieSpeciality.BossFire, true)]
    [InlineData(EZombieSpeciality.Crawler, false)]
    [InlineData(EZombieSpeciality.Normal, false)]
    public void IsMega(EZombieSpeciality kind, bool expected) => Assert.Equal(expected, kind.IsMega());

    [Theory]
    [InlineData(EZombieSpeciality.Sprinter, true)]
    [InlineData(EZombieSpeciality.FlankerFriendly, true)]
    [InlineData(EZombieSpeciality.FlankerStalk, true)]
    [InlineData(EZombieSpeciality.RedVolatile, true)]
    [InlineData(EZombieSpeciality.BlueVolatile, true)]
    [InlineData(EZombieSpeciality.Crawler, false)]
    public void IsRunner(EZombieSpeciality kind, bool expected) =>
        Assert.Equal(expected, kind.IsRunner());

    [Theory]
    [InlineData(EZombieSpeciality.Acid, true)]
    [InlineData(EZombieSpeciality.BossNuclear, true)]
    [InlineData(EZombieSpeciality.BossAll, true)]
    [InlineData(EZombieSpeciality.BossBuakFinal, true)]
    [InlineData(EZombieSpeciality.Burner, false)]
    [InlineData(EZombieSpeciality.Normal, false)]
    public void IsRadioactive(EZombieSpeciality kind, bool expected) =>
        Assert.Equal(expected, kind.IsRadioactive());

    // The enum's values ARE the game's, because they go over the wire and into a repro dump.
    [Fact]
    public void EnumValues_AreTheGamesOwn()
    {
        Assert.Equal(0, (int)EZombieSpeciality.None);
        Assert.Equal(1, (int)EZombieSpeciality.Normal);
        Assert.Equal(2, (int)EZombieSpeciality.Mega);
        Assert.Equal(3, (int)EZombieSpeciality.Crawler);
        Assert.Equal(4, (int)EZombieSpeciality.Sprinter);
        Assert.Equal(8, (int)EZombieSpeciality.Acid);
        Assert.Equal(17, (int)EZombieSpeciality.RedVolatile);
        Assert.Equal(24, (int)EZombieSpeciality.BossBuakFinal);
    }
}
