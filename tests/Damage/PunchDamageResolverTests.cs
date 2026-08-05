using Godot;
using UnturnedGodot.Damage;
using Xunit;

namespace UnturnedGodot.Tests.Damage;

// The arithmetic DamageTool does between the table and the health bar. The interesting part is not the
// multiplication — it is the floor, the clamp and the two gates, because those are where a punch stops
// being a number and starts being a rule.
public class PunchDamageResolverTests
{
    private static readonly Vector3 North = new(0, 0, -1);
    private static readonly Vector3 South = new(0, 0, 1);

    // A zombie facing AWAY from the swing takes the backstab multiplier; one facing into it does not.
    // 15 x 0.6 (spine) = 9, floored; x1.25 = 11.25, floored to 11.
    [Fact]
    public void SpineHitFromTheFrontIsNine() =>
        Assert.Equal(9, PunchDamageResolver.Zombie(ELimb.Spine, North, South));

    [Fact]
    public void SpineHitFromBehindTakesTheBackstabMultiplier() =>
        Assert.Equal(11, PunchDamageResolver.Zombie(ELimb.Spine, North, North));

    [Fact]
    public void SkullHitIsSixteen() =>
        Assert.Equal(16, PunchDamageResolver.Zombie(ELimb.Skull, North, South)); // 16.5 floored

    [Fact]
    public void LimbHitIsFour() =>
        Assert.Equal(4, PunchDamageResolver.Zombie(ELimb.LeftArm, North, South)); // 4.5 floored

    // Backstab is "dot > 0.5", i.e. within 60 degrees, and not merely "not facing the swing".
    [Theory]
    [InlineData(0f, true)]     // straight into its back
    [InlineData(45f, true)]    // dot 0.707
    [InlineData(75f, false)]   // dot 0.259
    [InlineData(180f, false)]  // face to face
    public void BackstabIsASixtyDegreeCone(float degrees, bool expected)
    {
        float radians = Mathf.DegToRad(degrees);
        var facing = new Vector3(Mathf.Sin(radians), 0, -Mathf.Cos(radians));
        Assert.Equal(expected, PunchDamageResolver.IsBackstab(North, facing));
    }

    [Fact]
    public void BackstabNeedsBothVectors()
    {
        Assert.False(PunchDamageResolver.IsBackstab(Vector3.Zero, North));
        Assert.False(PunchDamageResolver.IsBackstab(North, Vector3.Zero));
    }

    // Armor is a MULTIPLIER on `times`, and a low enough one floors the product to zero — which
    // damageZombie treats as "no damage happened", not as one point of chip damage.
    [Fact]
    public void ArmorThatFloorsToZeroDropsTheHitEntirely() =>
        Assert.Equal(0, PunchDamageResolver.Zombie(ELimb.LeftArm, North, South, armor: 0.2f)); // 0.9

    [Fact]
    public void ArmorScalesTheHit() =>
        Assert.Equal(18, PunchDamageResolver.Zombie(ELimb.Spine, North, South, armor: 2f));

    // A skull hit reads Armor_Multiplier and everything else reads NonHeadshot_Armor_Multiplier; NORMAL
    // leaves both at one, so this pins that they are read from the right column.
    [Fact]
    public void GlobalArmorMultiplierIsPerHeadshot()
    {
        var config = new ModeDamageConfig { ZombieArmor = 2f, ZombieNonHeadshotArmor = 0.5f };
        Assert.Equal(33, PunchDamageResolver.Zombie(ELimb.Skull, North, South, config: config));
        Assert.Equal(4, PunchDamageResolver.Zombie(ELimb.Spine, North, South, config: config));
    }

    // Mathf.Min(ushort.MaxValue, …): the zombie path CLAMPS rather than wrapping.
    [Fact]
    public void ZombieDamageClampsAtUshortMax() =>
        Assert.Equal(ushort.MaxValue, PunchDamageResolver.Zombie(ELimb.Spine, North, South, times: 1e6f));

    // ---- askDamage's subtraction ---------------------------------------------------------------

    [Theory]
    [InlineData(100, 9, 91)]
    [InlineData(9, 9, 0)]     // exactly lethal
    [InlineData(5, 9, 0)]     // overkill floors rather than wrapping
    [InlineData(100, 0, 100)] // a zero hit is not a hit
    [InlineData(0, 9, 0)]     // already dead
    public void ApplyToHealth(int health, int amount, int expected) =>
        Assert.Equal((ushort)expected,
            PunchDamageResolver.ApplyToHealth((ushort)health, (ushort)amount));

    // ---- Resources ------------------------------------------------------------------------------

    // The gate every shipped tree fails: none of Bundles/Trees says Vulnerable_To_Fists, so punching a
    // pine in Unturned does exactly nothing. Twenty is what an asset that DOES opt in takes.
    [Fact]
    public void ResourceNeedsTheFistsOptIn()
    {
        Assert.Equal(0, PunchDamageResolver.Resource(vulnerableToFists: false));
        Assert.Equal(20, PunchDamageResolver.Resource(vulnerableToFists: true));
    }

    [Fact]
    public void ResourceScalesWithTimes() =>
        Assert.Equal(30, PunchDamageResolver.Resource(vulnerableToFists: true, times: 1.5f));

    // ---- Objects --------------------------------------------------------------------------------

    // Five to a rubble section, and only when the section wants no blade: a fist carries none.
    [Fact]
    public void ObjectNeedsNoBladeAndNoInvulnerability()
    {
        Assert.Equal(5, PunchDamageResolver.Object(rubbleBladeId: 0, rubbleIsVulnerable: true));
        Assert.Equal(0, PunchDamageResolver.Object(rubbleBladeId: 1, rubbleIsVulnerable: true));
        Assert.Equal(0, PunchDamageResolver.Object(rubbleBladeId: 0, rubbleIsVulnerable: false));
    }

    // A 75-health garbage pile — the health every Rubble object in the shipped maps carries — takes
    // fifteen punches. Bounded, because a regression that made the punch do nothing would otherwise
    // hang the run rather than fail it.
    [Fact]
    public void GarbagePileTakesFifteenPunches()
    {
        ushort health = 75;
        int swings = 0;
        while (health > 0 && swings < 16)
        {
            health = PunchDamageResolver.ApplyToHealth(health,
                PunchDamageResolver.Object(rubbleBladeId: 0, rubbleIsVulnerable: true));
            swings++;
        }
        Assert.Equal(0, health);
        Assert.Equal(15, swings);
    }

    // ---- Buildables and the vehicle -------------------------------------------------------------

    [Fact]
    public void BarricadeAndStructureTakeTwo()
    {
        Assert.Equal(2, PunchDamageResolver.Barricade());
        Assert.Equal(2, PunchDamageResolver.Structure());
    }

    [Fact]
    public void BuildablesTakeTheirModeMeleeMultiplier()
    {
        var config = new ModeDamageConfig { BarricadeMelee = 2f, StructureMelee = 0.25f };
        Assert.Equal(4, PunchDamageResolver.Barricade(config: config));
        Assert.Equal(0, PunchDamageResolver.Structure(config: config)); // 0.5 is below the "< 1" floor
    }

    [Fact]
    public void VehicleIsTurnedOffEntirely() =>
        Assert.Equal(0, PunchDamageResolver.Vehicle(times: 1000f));

    // The flat path is a plain (ushort) cast in the original, so it truncates toward zero, and the
    // manager that receives it drops anything below one.
    [Theory]
    [InlineData(20f, 1f, 20)]
    [InlineData(2f, 0.9f, 1)]   // 1.8 truncates
    [InlineData(2f, 0.4f, 0)]   // 0.8 is below the "< 1" floor
    [InlineData(5f, 0f, 0)]
    public void FlatTruncatesAndFloors(float damage, float times, int expected) =>
        Assert.Equal((ushort)expected, PunchDamageResolver.Flat(damage, times));

    [Fact]
    public void FlatRefusesNonFiniteProducts()
    {
        Assert.Equal(0, PunchDamageResolver.Flat(float.NaN));
        Assert.Equal(0, PunchDamageResolver.Flat(float.PositiveInfinity));
    }

    [Fact]
    public void PlayerDamageIsTheLimbTableTimesArmor() =>
        Assert.Equal(6f, PunchDamageResolver.Player(ELimb.Spine, times: 1f, armor: 0.5f), 3);
}
