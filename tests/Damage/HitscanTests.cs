using Godot;
using UnturnedGodot.Damage;
using Xunit;

namespace UnturnedGodot.Tests.Damage;

// Ray against the capsule a zombie occupies. Zombies are not in the physics world, so this is the only
// thing that decides whether a swing connected — and the case that matters most is the degenerate one:
// a punch is thrown from inside arm's reach, which is often inside the target.
public class HitscanTests
{
    // A body standing at the origin: 2 m tall, 0.4 m thick, the same capsule the mover sweeps.
    private const float Height = 2f;
    private const float Radius = 0.4f;
    private static readonly Vector3 Feet = Vector3.Zero;

    private static bool Cast(Vector3 origin, Vector3 direction, float maxDistance,
        out float distance, out Vector3 point) =>
        Hitscan.RayCapsule(origin, direction, maxDistance, Feet, Height, Radius, out distance, out point);

    [Fact]
    public void RayIntoTheChestHitsAtTheSurface()
    {
        Assert.True(Cast(new Vector3(0, 1f, 2f), new Vector3(0, 0, -1), 5f, out float distance,
            out Vector3 point));
        Assert.Equal(1.6f, distance, 3);          // 2 m away, 0.4 m of radius
        Assert.Equal(0.4f, point.Z, 3);
        Assert.Equal(1f, point.Y, 3);
    }

    [Fact]
    public void RayPastTheShoulderMisses() =>
        Assert.False(Cast(new Vector3(1f, 1f, 2f), new Vector3(0, 0, -1), 5f, out _, out _));

    [Fact]
    public void RayAboveTheHeadMisses() =>
        Assert.False(Cast(new Vector3(0, 3f, 2f), new Vector3(0, 0, -1), 5f, out _, out _));

    // The cap: a ray coming straight down the axis meets the top hemisphere, not the cylinder.
    [Fact]
    public void RayDownFromAboveHitsTheCrown()
    {
        Assert.True(Cast(new Vector3(0, 4f, 0), Vector3.Down, 5f, out float distance, out Vector3 point));
        Assert.Equal(2f, distance, 3);
        Assert.Equal(2f, point.Y, 3);
    }

    [Fact]
    public void RayUpFromBelowHitsTheBase()
    {
        Assert.True(Cast(new Vector3(0, -1f, 0), Vector3.Up, 5f, out float distance, out _));
        Assert.Equal(1f, distance, 3);
    }

    // Chest to chest. Refusing this would make a zombie unpunchable at exactly the range punching
    // happens at, so an origin inside the capsule connects at zero distance.
    [Fact]
    public void OriginInsideTheCapsuleHitsAtZero()
    {
        Assert.True(Cast(new Vector3(0, 1f, 0.1f), new Vector3(0, 0, -1), 2f, out float distance,
            out Vector3 point));
        Assert.Equal(0f, distance, 3);
        Assert.Equal(new Vector3(0, 1f, 0.1f), point);
    }

    // Reach is a hard limit: the same ray that connects at 5 m of allowance misses at 1 m of it.
    [Fact]
    public void ReachBoundsTheCast()
    {
        Assert.True(Cast(new Vector3(0, 1f, 2f), new Vector3(0, 0, -1), 2f, out _, out _));
        Assert.False(Cast(new Vector3(0, 1f, 2f), new Vector3(0, 0, -1), 1f, out _, out _));
    }

    [Fact]
    public void RayAwayFromTheBodyMisses() =>
        Assert.False(Cast(new Vector3(0, 1f, 2f), new Vector3(0, 0, 1), 5f, out _, out _));

    // A direction that is not unit length still measures distance in metres.
    [Fact]
    public void DirectionIsNormalized()
    {
        Assert.True(Cast(new Vector3(0, 1f, 2f), new Vector3(0, 0, -10), 5f, out float distance, out _));
        Assert.Equal(1.6f, distance, 3);
    }

    [Fact]
    public void DegenerateInputsMiss()
    {
        Assert.False(Cast(new Vector3(0, 1f, 2f), Vector3.Zero, 5f, out _, out _));
        Assert.False(Cast(new Vector3(0, 1f, 2f), new Vector3(0, 0, -1), 0f, out _, out _));
        Assert.False(Hitscan.RayCapsule(new Vector3(0, 1f, 2f), new Vector3(0, 0, -1), 5f, Feet,
            Height, radius: 0f, out _, out _));
    }

    // A capsule shorter than its own diameter is a sphere, and is treated as one rather than inverting.
    [Fact]
    public void CapsuleShorterThanASphereBehavesAsOne()
    {
        Assert.True(Hitscan.RayCapsule(new Vector3(0, 0.5f, 3f), new Vector3(0, 0, -1), 5f, Feet,
            height: 0.2f, radius: 0.5f, out float distance, out _));
        Assert.Equal(2.5f, distance, 3);
    }

    // A ray that clears the lower cap entirely and meets only the upper one. The two caps are solved
    // separately and the nearer wins, so a case where only ONE of them has a solution is what proves
    // the picker is not simply always taking the first.
    [Fact]
    public void ARayThatMeetsOnlyTheUpperCap()
    {
        Assert.True(Cast(new Vector3(1f, 2.5f, 0), new Vector3(-0.75f, -0.95f, 0), 5f,
            out float distance, out Vector3 point));
        Assert.True(point.Y > 1.6f, $"expected a crown hit, landed at {point.Y}");
        Assert.InRange(distance, 0.9f, 1.1f);
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]   // on the segment
    [InlineData(0f, 3f, 1f)]   // past the top
    [InlineData(2f, 1f, 4f)]   // beside it
    public void SquaredDistanceToSegment(float x, float y, float expected) =>
        Assert.Equal(expected,
            Hitscan.SquaredDistanceToSegment(new Vector3(x, y, 0), Vector3.Zero, new Vector3(0, 2f, 0)),
            3);

    [Fact]
    public void SquaredDistanceToADegenerateSegmentIsToItsPoint() =>
        Assert.Equal(9f,
            Hitscan.SquaredDistanceToSegment(new Vector3(3f, 0, 0), Vector3.Zero, Vector3.Zero), 3);
}
