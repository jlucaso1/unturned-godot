using Godot;
using UnturnedGodot.Damage;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Damage;

// Rebuilding player.look.aim from the state the server holds. Getting the pitch convention wrong here
// points every punch at the sky, and nothing else in the state stream would notice — the angles are only
// ever handed to a remote avatar's spine bend, which is forgiving about being 90 degrees out.
public class PunchAimTests
{
    [Fact]
    public void OriginIsTheStanceEyeHeight()
    {
        var feet = new Vector3(10f, 5f, -3f);
        Assert.Equal(feet + (Vector3.Up * PlayerConfig.EyeHeightStand),
            PunchAim.Origin(feet, EPlayerStance.Stand));
        Assert.Equal(feet + (Vector3.Up * PlayerConfig.EyeHeightFor(EPlayerStance.Prone)),
            PunchAim.Origin(feet, EPlayerStance.Prone));
    }

    // A crouching player's eyes are lower than a standing one's, and a punch starts from them.
    [Fact]
    public void CrouchingLowersTheOrigin() =>
        Assert.True(PunchAim.Origin(Vector3.Zero, EPlayerStance.Crouch).Y
            < PunchAim.Origin(Vector3.Zero, EPlayerStance.Stand).Y);

    // At the horizon the aim reduces to the same facing convention the zombie brain uses, which is what
    // lets the backstab test compare the two vectors directly.
    [Theory]
    [InlineData(0f, 0f, 0f, -1f)]
    [InlineData(90f, -1f, 0f, 0f)]
    [InlineData(180f, 0f, 0f, 1f)]
    [InlineData(270f, 1f, 0f, 0f)]
    public void ForwardAtTheHorizon(float yaw, float x, float y, float z)
    {
        Vector3 forward = PunchAim.Forward(yaw, 0f);
        Assert.Equal(x, forward.X, 3);
        Assert.Equal(y, forward.Y, 3);
        Assert.Equal(z, forward.Z, 3);
    }

    [Fact]
    public void PositivePitchLooksUp()
    {
        Vector3 up = PunchAim.Forward(0f, 90f);
        Assert.Equal(1f, up.Y, 3);
        Assert.Equal(0f, up.X, 3);
        Assert.Equal(0f, up.Z, 3);
    }

    [Fact]
    public void NegativePitchLooksDown() => Assert.Equal(-1f, PunchAim.Forward(0f, -90f).Y, 3);

    [Fact]
    public void ForwardIsAlwaysUnitLength()
    {
        for (float yaw = 0f; yaw < 360f; yaw += 37f)
            for (float pitch = -80f; pitch <= 80f; pitch += 23f)
                Assert.Equal(1f, PunchAim.Forward(yaw, pitch).Length(), 3);
    }

    // The wire carries 0..180 with 90 at the horizon; PlayerController writes `_pitch + 90` into it.
    [Theory]
    [InlineData(90f, 0f)]
    [InlineData(180f, 90f)]
    [InlineData(0f, -90f)]
    public void PitchFromWire(float wire, float expected) =>
        Assert.Equal(expected, PunchAim.PitchFromWire(wire), 3);

    // The whole round trip a punch takes: the controller quantizes its own pitch onto the wire, the
    // server dequantizes it, and the aim has to come back pointing where the player was looking.
    [Fact]
    public void SurvivesTheWireRoundTrip()
    {
        const float lookDown = -35f;
        byte quantized = UnturnedGodot.Net.NetAngles.QuantizePitch(lookDown + PunchAim.WirePitchOffset);
        float restored = PunchAim.PitchFromWire(UnturnedGodot.Net.NetAngles.DequantizePitch(quantized));
        Assert.Equal(lookDown, restored, 0); // whole degrees on the wire
        Assert.True(PunchAim.Forward(0f, restored).Y < 0f);
    }
}
