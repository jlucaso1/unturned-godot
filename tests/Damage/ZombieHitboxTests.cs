using Godot;
using UnturnedGodot.Damage;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Damage;

// Where on a zombie a punch landed. The property under test is not per-bone precision — there are no
// bone colliders here — but that the three bands the damage table actually distinguishes come out in the
// right order: a head is worth nearly four times a limb, and a chest twice one.
public class ZombieHitboxTests
{
    private static ZombieInstance Zombie(float yaw = 0f, Vector3 position = default,
        EZombieSpeciality speciality = EZombieSpeciality.Normal) => new()
        {
            Id = 1,
            Position = position,
            Yaw = yaw,
            Speciality = speciality,
            Health = 100,
            MaxHealth = 100,
        };

    // The brain's own facing convention: yaw 0 looks along -Z.
    [Theory]
    [InlineData(0f, 0f, -1f)]
    [InlineData(90f, -1f, 0f)]
    [InlineData(180f, 0f, 1f)]
    public void ForwardMatchesTheBrainsConvention(float yaw, float x, float z)
    {
        Vector3 forward = ZombieHitbox.ForwardOf(Zombie(yaw));
        Assert.Equal(x, forward.X, 3);
        Assert.Equal(z, forward.Z, 3);
    }

    [Fact]
    public void NormalBodyIsTheMoversCapsule() =>
        Assert.Equal(ZombieBody.CapsuleTop, ZombieHitbox.HeightOf(Zombie()), 3);

    // A mega is wider, and its body scales with that width — the same scale the client gives its avatar.
    [Fact]
    public void MegaIsTaller() =>
        Assert.True(ZombieHitbox.HeightOf(Zombie(speciality: EZombieSpeciality.Mega))
            > ZombieHitbox.HeightOf(Zombie()));

    [Theory]
    [InlineData(1.9f, ELimb.Skull)]
    [InlineData(1.65f, ELimb.Skull)]
    [InlineData(1.5f, ELimb.Spine)]
    [InlineData(1.0f, ELimb.Spine)]
    [InlineData(0.6f, ELimb.RightLeg)]
    [InlineData(0.1f, ELimb.RightFoot)]
    public void HeightPicksTheBand(float y, ELimb expected) =>
        Assert.Equal(expected, ZombieHitbox.LimbAt(Zombie(), new Vector3(0, y, 0)));

    // Off the centre line within the torso band is an arm, and which arm follows the zombie's own right
    // vector rather than the world's.
    [Fact]
    public void TorsoEdgeIsAnArm()
    {
        ZombieInstance zombie = Zombie();               // facing -Z, so its right is -X
        Assert.Equal(ELimb.LeftArm, ZombieHitbox.LimbAt(zombie, new Vector3(0.35f, 1.5f, 0)));
        Assert.Equal(ELimb.RightArm, ZombieHitbox.LimbAt(zombie, new Vector3(-0.35f, 1.5f, 0)));
    }

    [Fact]
    public void LowerTorsoEdgeIsAHand()
    {
        ZombieInstance zombie = Zombie();
        Assert.Equal(ELimb.LeftHand, ZombieHitbox.LimbAt(zombie, new Vector3(0.35f, 1.0f, 0)));
        Assert.Equal(ELimb.RightHand, ZombieHitbox.LimbAt(zombie, new Vector3(-0.35f, 1.0f, 0)));
    }

    // Both sides of the lower body, for the same reason: the leg and foot columns are the cheapest hits
    // in the table, so which one a swing lands on is worth being sure of.
    [Fact]
    public void LegsAndFeetHaveSides()
    {
        ZombieInstance zombie = Zombie();          // facing -Z, so its right is -X
        Assert.Equal(ELimb.LeftLeg, ZombieHitbox.LimbAt(zombie, new Vector3(0.2f, 0.6f, 0)));
        Assert.Equal(ELimb.RightLeg, ZombieHitbox.LimbAt(zombie, new Vector3(-0.2f, 0.6f, 0)));
        Assert.Equal(ELimb.LeftFoot, ZombieHitbox.LimbAt(zombie, new Vector3(0.2f, 0.1f, 0)));
        Assert.Equal(ELimb.RightFoot, ZombieHitbox.LimbAt(zombie, new Vector3(-0.2f, 0.1f, 0)));
    }

    // Turn the zombie around and the same world-space hit is the other arm.
    [Fact]
    public void SideFollowsTheZombiesFacing()
    {
        Assert.Equal(ELimb.RightArm,
            ZombieHitbox.LimbAt(Zombie(yaw: 180f), new Vector3(0.35f, 1.5f, 0)));
    }

    // The bands are fractions of the body, so they follow a zombie standing on a hill rather than
    // being absolute heights.
    [Fact]
    public void BandsAreRelativeToWhereItStands()
    {
        ZombieInstance high = Zombie(position: new Vector3(0, 50f, 0));
        Assert.Equal(ELimb.Skull, ZombieHitbox.LimbAt(high, new Vector3(0, 51.9f, 0)));
        Assert.Equal(ELimb.RightFoot, ZombieHitbox.LimbAt(high, new Vector3(0, 50.1f, 0)));
    }

    // A point outside the body clamps to the nearest band rather than producing nonsense.
    [Fact]
    public void PointsOutsideTheBodyClamp()
    {
        Assert.Equal(ELimb.Skull, ZombieHitbox.LimbAt(Zombie(), new Vector3(0, 9f, 0)));
        Assert.Equal(ELimb.RightFoot, ZombieHitbox.LimbAt(Zombie(), new Vector3(0, -9f, 0)));
    }

    // The whole query: a punch thrown at chest height from a metre away finds the spine.
    [Fact]
    public void RaycastFindsTheChest()
    {
        Assert.True(ZombieHitbox.Raycast(Zombie(), new Vector3(0, 1.2f, 1f), new Vector3(0, 0, -1),
            PunchDamageData.Reach, out float distance, out ELimb limb));
        Assert.Equal(ELimb.Spine, limb);
        Assert.Equal(0.6f, distance, 3);
    }

    [Fact]
    public void RaycastRespectsReach() =>
        Assert.False(ZombieHitbox.Raycast(Zombie(), new Vector3(0, 1.2f, 4f), new Vector3(0, 0, -1),
            PunchDamageData.Reach, out _, out _));

    // Aiming up at a zombie's head from close in: the ray enters the skull band.
    [Fact]
    public void RaycastCanFindTheHead()
    {
        Assert.True(ZombieHitbox.Raycast(Zombie(), new Vector3(0, 1.7f, 1f), new Vector3(0, 0, -1),
            PunchDamageData.Reach, out _, out ELimb limb));
        Assert.Equal(ELimb.Skull, limb);
    }
}
