using UnturnedGodot.Damage;
using Xunit;

namespace UnturnedGodot.Tests.Damage;

// The table itself, against the numbers PlayerEquipment declares. These read like restatements of the
// source, and that is what they are for: the whole value of a data-driven punch is that the numbers are
// the game's, so a change to one of them has to be a deliberate change to a test as well.
public class PunchDamageDataTests
{
    [Fact]
    public void FlatDamageMatchesPlayerEquipment()
    {
        Assert.Equal(2f, PunchDamageData.Barricade);
        Assert.Equal(2f, PunchDamageData.Structure);
        Assert.Equal(0f, PunchDamageData.Vehicle);
        Assert.Equal(20f, PunchDamageData.Resource);
        Assert.Equal(5f, PunchDamageData.Object);
    }

    [Fact]
    public void ReachAndServerRangeMatchTheOriginal()
    {
        Assert.Equal(1.75f, PunchDamageData.Reach);
        Assert.Equal(6f, PunchDamageData.MaxServerHitDistance);
        Assert.Equal(36f, PunchDamageData.MaxServerHitDistanceSquared);
    }

    [Fact]
    public void EveryBodyTableSharesTheFifteenPointBase()
    {
        Assert.Equal(15f, PunchDamageData.Player.Damage);
        Assert.Equal(15f, PunchDamageData.Zombie.Damage);
        Assert.Equal(15f, PunchDamageData.Animal.Damage);
    }

    // A fist to a zombie's head is worth 16.5, to its chest 9, and to an arm or a leg only 4.5 — its
    // limbs shrug a punch off twice as hard as a player's 9 do, and its chest takes 9 where a player's
    // takes 12.
    [Theory]
    [InlineData(ELimb.Skull, 16.5f)]
    [InlineData(ELimb.Spine, 9f)]
    [InlineData(ELimb.LeftArm, 4.5f)]
    [InlineData(ELimb.RightHand, 4.5f)]
    [InlineData(ELimb.LeftLeg, 4.5f)]
    [InlineData(ELimb.RightFoot, 4.5f)]
    public void ZombieLimbMultipliers(ELimb limb, float expected) =>
        Assert.Equal(expected, PunchDamageData.Zombie.Multiply(limb), 3);

    [Theory]
    [InlineData(ELimb.Skull, 16.5f)]
    [InlineData(ELimb.Spine, 12f)]
    [InlineData(ELimb.LeftArm, 9f)]
    [InlineData(ELimb.RightLeg, 9f)]
    public void PlayerLimbMultipliers(ELimb limb, float expected) =>
        Assert.Equal(expected, PunchDamageData.Player.Multiply(limb), 3);

    // The player table is deliberately usable on a quadruped: its four-legged limbs fall through to the
    // leg column rather than to the unscaled base.
    [Theory]
    [InlineData(ELimb.LeftBack)]
    [InlineData(ELimb.RightBack)]
    [InlineData(ELimb.LeftFront)]
    [InlineData(ELimb.RightFront)]
    public void PlayerTableScalesQuadrupedLimbsAsLegs(ELimb limb) =>
        Assert.Equal(9f, PunchDamageData.Player.Multiply(limb), 3);

    // The animal table scales the skull, the spine and the four QUADRUPED limbs. A biped leg has no
    // column in it at all and falls through to the unscaled 15, exactly as the original's switch does.
    [Fact]
    public void AnimalTableKnowsOnlyQuadrupedLimbs()
    {
        Assert.Equal(4.5f, PunchDamageData.Animal.Multiply(ELimb.LeftFront), 3);
        Assert.Equal(9f, PunchDamageData.Animal.Multiply(ELimb.Spine), 3);
        Assert.Equal(16.5f, PunchDamageData.Animal.Multiply(ELimb.Skull), 3);
        Assert.Equal(15f, PunchDamageData.Animal.Multiply(ELimb.LeftLeg), 3);
    }

    // ELimb is a [NetEnum] in the original, so its ordinals are wire values and cannot be reordered.
    [Fact]
    public void LimbOrdinalsAreTheWireValues()
    {
        Assert.Equal(0, (int)ELimb.LeftFoot);
        Assert.Equal(7, (int)ELimb.RightArm);
        Assert.Equal(12, (int)ELimb.Spine);
        Assert.Equal(13, (int)ELimb.Skull);
    }

    // The "> 1" gate: a target whose punch number is not greater than one is not hit at all. The vehicle
    // is the one the game turns off this way.
    [Fact]
    public void VehicleIsGatedOffAndEverythingElseIsNot()
    {
        Assert.False(PunchDamageData.CanDamage(EPunchTargetKind.Vehicle));
        Assert.False(PunchDamageData.CanDamage(EPunchTargetKind.None));
        Assert.True(PunchDamageData.CanDamage(EPunchTargetKind.Player));
        Assert.True(PunchDamageData.CanDamage(EPunchTargetKind.Zombie));
        Assert.True(PunchDamageData.CanDamage(EPunchTargetKind.Animal));
        Assert.True(PunchDamageData.CanDamage(EPunchTargetKind.Barricade));
        Assert.True(PunchDamageData.CanDamage(EPunchTargetKind.Structure));
        Assert.True(PunchDamageData.CanDamage(EPunchTargetKind.Resource));
        Assert.True(PunchDamageData.CanDamage(EPunchTargetKind.Object));
    }

    // A limb no table has a column for falls through to the unscaled base — the `return damage` every
    // one of the original's switches ends with, and the only behaviour an unrecognised wire value can
    // safely have.
    [Fact]
    public void AnUnknownLimbTakesTheUnscaledBase()
    {
        const ELimb unknown = (ELimb)200;
        Assert.Equal(15f, PunchDamageData.Player.Multiply(unknown), 3);
        Assert.Equal(15f, PunchDamageData.Zombie.Multiply(unknown), 3);
        Assert.Equal(15f, PunchDamageData.Animal.Multiply(unknown), 3);
    }

    // A zombie is a biped, so the quadruped limbs are not in its table either.
    [Theory]
    [InlineData(ELimb.LeftBack)]
    [InlineData(ELimb.RightBack)]
    [InlineData(ELimb.LeftFront)]
    [InlineData(ELimb.RightFront)]
    public void ZombieTableHasNoQuadrupedColumn(ELimb limb) =>
        Assert.Equal(15f, PunchDamageData.Zombie.Multiply(limb), 3);

    [Fact]
    public void FlatForAnswersOnlyTheNonBodyKinds()
    {
        Assert.Equal(20f, PunchDamageData.FlatFor(EPunchTargetKind.Resource));
        Assert.Equal(5f, PunchDamageData.FlatFor(EPunchTargetKind.Object));
        Assert.Equal(0f, PunchDamageData.FlatFor(EPunchTargetKind.Zombie));
    }

    // The mode config this port runs is NORMAL, and PlayConfigData's own numbers for it.
    [Fact]
    public void NormalModeMultipliers()
    {
        ModeDamageConfig normal = ModeDamageConfig.Normal;
        Assert.Equal(1f, normal.ZombieArmor);
        Assert.Equal(1f, normal.ZombieNonHeadshotArmor);
        Assert.Equal(1.25f, normal.ZombieBackstab);
        Assert.Equal(1f, normal.BarricadeMelee);
        Assert.Equal(1f, normal.StructureMelee);
        Assert.Equal(1f, normal.VehicleMelee);
    }
}
