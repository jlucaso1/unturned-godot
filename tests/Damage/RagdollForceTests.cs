using Godot;
using UnturnedGodot.Damage;
using Xunit;

namespace UnturnedGodot.Tests.Damage;

// The shove a dying body takes with it. Pure arithmetic, copied from DamageTool.damageZombie and
// RagdollTool — and shared between the three bodies that can die, because the original's three entry
// points differ only in which prefab they instantiate.
public class RagdollForceTests
{
    // `direction * amount`: the DAMAGE is in the vector, which is why a heavy hit throws a body further
    // than a weak one and a headshot (worth more) shoves harder than a hit to the arm.
    [Fact]
    public void TheImpulseScalesWithTheDamage()
    {
        Vector3 weak = RagdollForce.Impulse(Vector3.Forward, 10);
        Vector3 heavy = RagdollForce.Impulse(Vector3.Forward, 40);

        Assert.Equal(4f, heavy.Length() / weak.Length(), 3);
        Assert.Equal(Vector3.Forward, weak.Normalized());
    }

    [Fact]
    public void AMeleeAssetsMultiplierScalesItFurther() =>
        Assert.Equal(2f,
            RagdollForce.Impulse(Vector3.Forward, 10, 2f).Length()
            / RagdollForce.Impulse(Vector3.Forward, 10).Length(), 3);

    // The direction is normalized first: a caller handing in an un-normalized aim (the swing's forward
    // comes off a quantized yaw/pitch pair, so it never is exactly unit) must not change how hard the
    // body is thrown.
    [Fact]
    public void TheDirectionIsNormalizedBeforeItIsScaled()
    {
        Vector3 unit = RagdollForce.Impulse(Vector3.Forward, 20);
        Vector3 longer = RagdollForce.Impulse(Vector3.Forward * 37f, 20);

        Assert.Equal(unit, longer);
    }

    [Fact]
    public void NoDirectionOrNoDamageIsNoShove()
    {
        Assert.Equal(Vector3.Zero, RagdollForce.Impulse(Vector3.Zero, 100));
        Assert.Equal(Vector3.Zero, RagdollForce.Impulse(Vector3.Forward, 0));
    }

    // ragdollZombie's own adjustments. The LIFT is what turns a shove into a tumble — without it the
    // body slides along the floor instead of falling over.
    [Fact]
    public void TheThrowIsLiftedSoTheBodyTumbles()
    {
        Vector3 thrown = RagdollForce.Thrown(Vector3.Zero, sideways: 0f, forward: 0f);

        Assert.Equal(RagdollForce.Lift * RagdollForce.Scale, thrown.Y, 3);
        Assert.Equal(0f, thrown.X, 3);
        Assert.Equal(0f, thrown.Z, 3);
    }

    // The sideways scatter is why two identical kills never fall the same way.
    [Fact]
    public void TheThrowIsScatteredSideways()
    {
        Vector3 left = RagdollForce.Thrown(Vector3.Zero, sideways: -1f, forward: 0f);
        Vector3 right = RagdollForce.Thrown(Vector3.Zero, sideways: 1f, forward: 0f);

        Assert.Equal(-RagdollForce.Scatter * RagdollForce.Scale, left.X, 3);
        Assert.Equal(RagdollForce.Scatter * RagdollForce.Scale, right.X, 3);
        Assert.Equal(left.Y, right.Y, 3); // the lift is unchanged by the scatter
    }

    // The whole vector is scaled last, so the impulse and the adjustments stay in proportion.
    [Fact]
    public void EverythingIsScaledTogether()
    {
        Vector3 thrown = RagdollForce.Thrown(new Vector3(1f, 2f, 3f), sideways: 0f, forward: 0f);

        Assert.Equal(1f * RagdollForce.Scale, thrown.X, 3);
        Assert.Equal((2f + RagdollForce.Lift) * RagdollForce.Scale, thrown.Y, 3);
        Assert.Equal(3f * RagdollForce.Scale, thrown.Z, 3);
    }

    // The draws come from an RNG whose range the caller controls, so out-of-range values must clamp
    // rather than throw a corpse across the map.
    [Theory]
    [InlineData(5f)]
    [InlineData(-5f)]
    public void AnOutOfRangeDrawIsClamped(float draw)
    {
        Vector3 thrown = RagdollForce.Thrown(Vector3.Zero, draw, draw);

        Assert.Equal(RagdollForce.Scatter * RagdollForce.Scale, Mathf.Abs(thrown.X), 3);
        Assert.Equal(RagdollForce.Scatter * RagdollForce.Scale, Mathf.Abs(thrown.Z), 3);
    }

    // End to end, and the property a player actually reads: hit something harder, and the body goes
    // further in the direction it was hit.
    [Fact]
    public void AHarderHitThrowsTheBodyFurtherInTheSameDirection()
    {
        Vector3 weak = RagdollForce.Thrown(RagdollForce.Impulse(Vector3.Forward, 10), 0f, 0f);
        Vector3 heavy = RagdollForce.Thrown(RagdollForce.Impulse(Vector3.Forward, 100), 0f, 0f);

        Assert.True(heavy.Z < weak.Z, "the harder hit did not throw the body further");
        Assert.True(heavy.Z < 0f, "the body was thrown back at the puncher");
    }
}
