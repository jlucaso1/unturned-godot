using System;
using Godot;
using UnturnedGodot.Damage;
using UnturnedGodot.UI;
using Xunit;

namespace UnturnedGodot.Tests.UI;

// SleekHitmarker's geometry. The four wedges are one texture rotated into each quadrant and slid outward
// along their own diagonals; a sign wrong anywhere makes them cross over each other instead of opening,
// which reads as a smear rather than as a hit.
public class HitmarkerTests
{
    private static (Vector2[] Positions, float[] Rotations) Animated(float t, float jitter = 0f)
    {
        var positions = new Vector2[4];
        var rotations = new float[4];
        Hitmarker.Animated(t, jitter, positions, rotations);
        return (positions, rotations);
    }

    [Fact]
    public void TheWedgesStartNearTheCentreAndFinishHalfWayOut()
    {
        (Vector2[] start, _) = Animated(0f);
        (Vector2[] end, _) = Animated(1f);
        var centre = new Vector2(0.5f, 0.5f);

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(Hitmarker.StartOffset, start[i].DistanceTo(centre), 3);
            Assert.Equal(Hitmarker.EndOffset, end[i].DistanceTo(centre), 3);
        }
    }

    // Each wedge is a quarter turn from the last, and they stay that way as the mark opens — that is what
    // keeps the four of them a cross rather than a scatter.
    [Fact]
    public void TheWedgesStayAQuarterTurnApart()
    {
        (Vector2[] positions, float[] rotations) = Animated(0.5f);
        var centre = new Vector2(0.5f, 0.5f);

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(i * 90f, rotations[i], 3);

            Vector2 a = positions[i] - centre;
            Vector2 b = positions[(i + 1) % 4] - centre;
            // Perpendicular neighbours: the dot product of two unit quarter-turn vectors is zero.
            Assert.Equal(0f, a.Normalized().Dot(b.Normalized()), 3);
        }
    }

    // The mark opens outward. A regression that reversed the lerp would still produce four wedges in the
    // right places at the right angles — just collapsing instead of blooming.
    [Fact]
    public void TheMarkOpensRatherThanCloses()
    {
        var centre = new Vector2(0.5f, 0.5f);
        float previous = 0f;

        for (float t = 0f; t <= 1f; t += 0.25f)
        {
            (Vector2[] positions, _) = Animated(t);
            float distance = positions[0].DistanceTo(centre);
            Assert.True(distance >= previous, $"the mark shrank between {previous} and {distance}");
            previous = distance;
        }
    }

    // The jitter turns the whole mark, not each wedge independently.
    [Fact]
    public void TheJitterTiltsAllFourWedgesTogether()
    {
        (Vector2[] plain, float[] plainRotations) = Animated(0.5f);
        (Vector2[] tilted, float[] tiltedRotations) = Animated(0.5f, Hitmarker.MaxAngleJitterDegrees);

        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(plainRotations[i] + Hitmarker.MaxAngleJitterDegrees, tiltedRotations[i], 3);
            Assert.NotEqual(plain[i], tilted[i]);
        }

        // And the shape is unchanged: still four wedges the same distance out.
        var centre = new Vector2(0.5f, 0.5f);
        Assert.Equal(plain[0].DistanceTo(centre), tilted[0].DistanceTo(centre), 3);
    }

    [Fact]
    public void TimeIsClampedRatherThanExtrapolated()
    {
        (Vector2[] before, _) = Animated(-1f);
        (Vector2[] start, _) = Animated(0f);
        (Vector2[] after, _) = Animated(5f);
        (Vector2[] end, _) = Animated(1f);

        Assert.Equal(start[0], before[0]);
        Assert.Equal(end[0], after[0]);
    }

    // The classic style is the fixed cross the game drew before the animation: 16 px out on each diagonal.
    [Fact]
    public void TheClassicStyleIsAFixedDiagonalCross()
    {
        var offsets = new Vector2[4];
        var rotations = new float[4];
        Hitmarker.Classic(offsets, rotations);

        foreach (Vector2 offset in offsets)
        {
            Assert.Equal(Hitmarker.ClassicOffset, MathF.Abs(offset.X));
            Assert.Equal(Hitmarker.ClassicOffset, MathF.Abs(offset.Y));
        }

        // All four quadrants, once each.
        Assert.Equal(4, new System.Collections.Generic.HashSet<Vector2>(offsets).Count);
        Assert.Equal(new[] { 0f, 90f, 180f, 270f }, rotations);
    }

    // A skull is the critical hit and everything else is an ordinary one — PlayerEquipment.punch's own
    // `info.limb == ELimb.SKULL ? CRITICAL : ENTITIY`, which is coarser than the damage table it sits
    // beside.
    [Theory]
    [InlineData(ELimb.Skull, EPlayerHit.Critical)]
    [InlineData(ELimb.Spine, EPlayerHit.Entity)]
    [InlineData(ELimb.LeftArm, EPlayerHit.Entity)]
    [InlineData(ELimb.RightFoot, EPlayerHit.Entity)]
    public void OnlyASkullIsCritical(ELimb limb, EPlayerHit expected) =>
        Assert.Equal(expected, Hitmarker.ForLimb(limb));

    // Critical and entity share one icon and differ only in colour, which is how SetStyle spells it — a
    // port that gave critical its own texture would be looking for a file that does not exist.
    [Fact]
    public void CriticalSharesTheEntityIconAndDiffersOnlyInColour()
    {
        Assert.True(Hitmarker.UsesEntityIcon(EPlayerHit.Entity));
        Assert.True(Hitmarker.UsesEntityIcon(EPlayerHit.Critical));
        Assert.False(Hitmarker.UsesEntityIcon(EPlayerHit.Build));

        Assert.NotEqual(Hitmarker.ColorFor(EPlayerHit.Critical), Hitmarker.ColorFor(EPlayerHit.Entity));
        Assert.Equal(Hitmarker.ColorFor(EPlayerHit.Entity), Hitmarker.ColorFor(EPlayerHit.Build));
    }

    // Half alpha on everything is what makes the overlay read as an overlay.
    [Fact]
    public void TheDefaultColoursAreHalfTransparent()
    {
        Assert.Equal(0.5f, Hitmarker.EntityColor.A);
        Assert.Equal(0.5f, Hitmarker.CriticalColor.A);
        Assert.Equal(0.5f, Hitmarker.CrosshairColor.A);
    }

    [Fact]
    public void TooFewWedgesIsRefused()
    {
        var three = new Vector2[3];
        var rotations = new float[4];

        Assert.Throws<ArgumentException>(() => Hitmarker.Animated(0f, 0f, three, rotations));
        Assert.Throws<ArgumentException>(() => Hitmarker.Classic(three, rotations));
    }
}
