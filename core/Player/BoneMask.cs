using System.Collections.Generic;

namespace UnturnedGodot.Player;

// Which bones a clip is allowed to touch — the port of Unity's legacy
// `AnimationState.AddMixingTransform(transform, recursive: true)`, which is what
// `CharacterAnimator.mixAnimation` calls to restrict a gesture to part of the body:
//
//     mixAnimation("Punch_Left",  mixLeftShoulder: true,  mixRightShoulder: false);
//     mixAnimation("Punch_Right", mixLeftShoulder: false, mixRightShoulder: true);
//
// The clips themselves are authored full-body — Punch_Left carries curves for all sixteen bones, legs
// included — so without the mask a swing overwrites the walk cycle and the legs stand still for its whole
// length. The mask is what makes a punch an ARM animation while the movement clip keeps the rest of the
// body running underneath.
//
// A mixing transform is recursive, so naming Left_Shoulder means Left_Shoulder and everything hanging off
// it (Left_Arm -> Left_Hand -> Left_Hook). Naming nothing at all is not represented here: Unturned's
// `mixAnimation(name)` overload adds no transforms, which in Unity means "no restriction", and the port
// spells that as a null mask rather than a mask that happens to contain every bone.
public sealed class BoneMask
{
    private readonly bool[] _bones;

    private BoneMask(bool[] bones) => _bones = bones;

    public int BoneCount => _bones.Length;

    public bool Contains(int bone) => (uint)bone < (uint)_bones.Length && _bones[bone];

    // The union of the subtrees rooted at `roots`, over a hierarchy given as a parent index per bone
    // (-1 for a root). Returns null when no root is named, which is Unity's "no mixing transforms" — the
    // clip plays over the whole rig.
    //
    // Walking UP from each bone rather than down from each root keeps this independent of the order bones
    // were added in: a rig whose child precedes its parent in the bone array still resolves.
    public static BoneMask? Subtrees(IReadOnlyList<int> parents, IReadOnlyList<int> roots)
    {
        if (roots.Count == 0)
            return null;

        var included = new bool[parents.Count];
        for (int bone = 0; bone < parents.Count; bone++)
        {
            // Bounded by the hierarchy's depth, and cannot loop forever on a malformed parent chain:
            // every rig this port builds is a tree, and the guard is the step count, not the shape.
            int walk = bone;
            for (int step = 0; walk >= 0 && step <= parents.Count; step++)
            {
                if (Contains(roots, walk))
                {
                    included[bone] = true;
                    break;
                }
                walk = walk < parents.Count ? parents[walk] : -1;
            }
        }
        return new BoneMask(included);
    }

    private static bool Contains(IReadOnlyList<int> roots, int bone)
    {
        for (int i = 0; i < roots.Count; i++)
            if (roots[i] == bone)
                return true;
        return false;
    }
}
