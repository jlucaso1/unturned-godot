using System;
using Godot;

namespace UnturnedGodot.Damage;

// Turns "a fist landed here" into the whole number of health points that come off, by the same route
// DamageTool takes: a base from the limb, a pile of multiplicative `times`, then a floor and a clamp.
//
// Pure, and deliberately so. The floor is where a punch stops being arithmetic and starts being a rule —
// DamageTool.damageZombie floors the product and treats a floored ZERO as "no damage happened at all",
// which is what makes an armored limb genuinely immune rather than merely resistant, and the
// buildable/resource paths cast to ushort instead, which truncates the same way but wraps rather than
// clamping. Both are reproduced, and both are tested, because the difference between them is the
// difference between a 65536-point hit doing nothing and doing everything.
public static class PunchDamageResolver
{
    // DamageTool.damageZombie, for the parameters a punch fills in: respectArmor and allowBackstab both
    // true (PlayerEquipment sets them), applyGlobalArmorMultiplier left at its default true.
    //
    // `direction` is the swing's travel — the aim's forward — and `zombieForward` is the way the zombie
    // faces. Both are Godot-space and horizontal; a swing that agrees with the facing landed on the
    // zombie's BACK, which is the backstab.
    //
    // `armor` is getZombieArmor: the armor value of the clothing the zombie is wearing in the slot that
    // covers the limb that was hit (pants for legs, shirt for arms, gear for the spine). Nothing reads
    // ItemClothingAsset yet, so callers pass 1 and every limb is bare — which is also what the original
    // computes for a zombie whose slot rolled empty, and most of them do.
    public static ushort Zombie(ELimb limb, Vector3 direction, Vector3 zombieForward,
        float times = 1f, float armor = 1f, ModeDamageConfig? config = null)
    {
        config ??= ModeDamageConfig.Normal;

        float damage = PunchDamageData.Zombie.Multiply(limb);

        times *= armor;                                  // respectArmor
        if (IsBackstab(direction, zombieForward))        // allowBackstab
            times *= config.ZombieBackstab;
        times *= limb == ELimb.Skull                     // applyGlobalArmorMultiplier
            ? config.ZombieArmor
            : config.ZombieNonHeadshotArmor;

        // Mathf.FloorToInt, then min against ushort.MaxValue. A floored zero means the hit is dropped
        // before it reaches askDamage, which is why this returns 0 rather than 1.
        int rounded = (int)MathF.Floor(damage * times);
        if (rounded <= 0)
            return 0;
        return (ushort)Math.Min(ushort.MaxValue, rounded);
    }

    // Zombie.askDamage's subtraction: health floors at zero rather than wrapping, and a zombie already
    // at zero takes nothing. Returns the health left.
    public static ushort ApplyToHealth(ushort health, ushort amount) =>
        amount == 0 || health == 0 ? health
        : amount >= health ? (ushort)0
        : (ushort)(health - amount);

    // "Vector3.Dot(zombie.transform.forward, direction) > 0.5" — the swing and the facing pointing the
    // same way within 60 degrees. Both are normalized here rather than assumed to be: the aim forward
    // comes off a quantized yaw/pitch pair, and the facing off a yaw, so neither is exactly unit.
    public static bool IsBackstab(Vector3 direction, Vector3 zombieForward)
    {
        if (direction == Vector3.Zero || zombieForward == Vector3.Zero)
            return false;
        return direction.Normalized().Dot(zombieForward.Normalized()) > 0.5f;
    }

    // ResourceManager.damage / ObjectManager.damage / StructureManager / BarricadeManager all open the
    // same way: "(ushort)(damage * times)", then drop the hit if the result is below one. A plain C#
    // cast, so it truncates toward zero and — unlike the zombie path — WRAPS past 65535 rather than
    // clamping. Faithful, and the reason a caller may not hand this an arbitrary multiplier.
    public static ushort Flat(float damage, float times = 1f)
    {
        float product = damage * times;
        if (!float.IsFinite(product) || product < 1f)
            return 0;
        return unchecked((ushort)(uint)product);
    }

    // PlayerEquipment's "> 1" gate and the truncation behind it, in one place. Every flat target below
    // goes through here, which is what makes the gate one rule rather than five copies of it — and what
    // makes the vehicle's zero visibly the SAME rule that lets the resource's twenty through.
    private static ushort Gated(float damage, float times) =>
        PunchDamageData.CanDamage(damage) ? Flat(damage, times) : (ushort)0;

    // The punch against a resource: PlayerEquipment passes DAMAGE_RESOURCE with no mode multiplier of
    // its own (unlike the buildables below), and the spawnpoint must be alive and its asset must opt in
    // through Vulnerable_To_Fists.
    public static ushort Resource(bool vulnerableToFists, float times = 1f) =>
        vulnerableToFists ? Gated(PunchDamageData.Resource, times) : (ushort)0;

    // The punch against a destructible object's rubble. Two gates beyond the section being alive: the
    // asset's rubbleBladeID must be 0, because a fist carries no blade and a section that names one can
    // only be broken by a tool that does, and the asset must not be Rubble_Invulnerable.
    public static ushort Object(byte rubbleBladeId, bool rubbleIsVulnerable, float times = 1f) =>
        rubbleBladeId == 0 && rubbleIsVulnerable ? Gated(PunchDamageData.Object, times) : (ushort)0;

    // Barricades and structures take their mode's melee multiplier on top of `times`, which is the one
    // thing that separates them from the resource path above.
    public static ushort Barricade(float times = 1f, ModeDamageConfig? config = null) =>
        Gated(PunchDamageData.Barricade, times * (config ?? ModeDamageConfig.Normal).BarricadeMelee);

    public static ushort Structure(float times = 1f, ModeDamageConfig? config = null) =>
        Gated(PunchDamageData.Structure, times * (config ?? ModeDamageConfig.Normal).StructureMelee);

    // And the vehicle, which the same gate turns off outright — DAMAGE_VEHICLE is 0. Present so the
    // table is complete and so the gate is the thing under test rather than an absent branch.
    public static ushort Vehicle(float times = 1f, ModeDamageConfig? config = null) =>
        Gated(PunchDamageData.Vehicle, times * (config ?? ModeDamageConfig.Normal).VehicleMelee);

    // DamagePlayerParameters for a punch: the limb table, respectArmor true. No player carries clothing
    // here, so the armor factor is the caller's to supply and defaults to bare.
    public static float Player(ELimb limb, float times = 1f, float armor = 1f) =>
        PunchDamageData.Player.Multiply(limb) * times * armor;
}
