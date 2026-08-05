namespace UnturnedGodot.Damage;

// SDG.Unturned.ELimb, in the game's own order — it is a [NetEnum], so the ordinals are the values the
// wire carries. Which limb a hit landed on is the only thing that varies the damage a single swing does,
// so it is the first thing this file needs: every IDamageMultiplier below is a table keyed by it.
//
// The four BACK/FRONT entries belong to quadrupeds (animals), which is why PlayerDamageMultiplier has a
// case for them and ZombieDamageMultiplier does not.
public enum ELimb : byte
{
    LeftFoot,
    LeftLeg,
    RightFoot,
    RightLeg,
    LeftHand,
    LeftArm,
    RightHand,
    RightArm,
    LeftBack,
    RightBack,
    LeftFront,
    RightFront,
    Spine,
    Skull,
}
