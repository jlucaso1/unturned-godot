namespace UnturnedGodot.Damage;

// SDG.Unturned.IDamageMultiplier: a base damage plus the per-limb scaling a weapon applies to it. Every
// damage source in Unturned — a gun's bullet, a machete's swing, a fist — is one of these, which is why
// the punch's own numbers below are expressed as multipliers rather than as bare amounts.
public interface IDamageMultiplier
{
    float Multiply(ELimb limb);
}

// PlayerDamageMultiplier. The four quadruped limbs fall through to `leg` so the same table can be aimed
// at an animal, which is exactly what the comment in the original says it is for.
public readonly record struct PlayerDamageMultiplier(
    float Damage, float Leg, float Arm, float Spine, float Skull) : IDamageMultiplier
{
    public float Multiply(ELimb limb) => limb switch
    {
        ELimb.LeftFoot or ELimb.LeftLeg or ELimb.RightFoot or ELimb.RightLeg => Damage * Leg,
        ELimb.LeftHand or ELimb.LeftArm or ELimb.RightHand or ELimb.RightArm => Damage * Arm,
        ELimb.Spine => Damage * Spine,
        ELimb.Skull => Damage * Skull,
        // So that the player table can be applied to animals as well.
        ELimb.LeftBack or ELimb.RightBack or ELimb.LeftFront or ELimb.RightFront => Damage * Leg,
        _ => Damage,
    };
}

// ZombieDamageMultiplier. Identical shape to the player's, minus the quadruped fall-through: a zombie is
// a biped and its limbs are the biped ones.
public readonly record struct ZombieDamageMultiplier(
    float Damage, float Leg, float Arm, float Spine, float Skull) : IDamageMultiplier
{
    public float Multiply(ELimb limb) => limb switch
    {
        ELimb.LeftFoot or ELimb.LeftLeg or ELimb.RightFoot or ELimb.RightLeg => Damage * Leg,
        ELimb.LeftHand or ELimb.LeftArm or ELimb.RightHand or ELimb.RightArm => Damage * Arm,
        ELimb.Spine => Damage * Spine,
        ELimb.Skull => Damage * Skull,
        _ => Damage,
    };
}

// AnimalDamageMultiplier. An animal has no arms, so it carries one fewer column than the two above, and
// its `leg` covers only the QUADRUPED limbs: the biped leg entries are deliberately absent from the
// original's switch and fall through to the unscaled base, which is what a table written for a body with
// a different set of limbs does with limbs that body does not have.
public readonly record struct AnimalDamageMultiplier(
    float Damage, float Leg, float Spine, float Skull) : IDamageMultiplier
{
    public float Multiply(ELimb limb) => limb switch
    {
        ELimb.LeftBack or ELimb.RightBack or ELimb.LeftFront or ELimb.RightFront => Damage * Leg,
        ELimb.Spine => Damage * Spine,
        ELimb.Skull => Damage * Skull,
        _ => Damage,
    };
}
