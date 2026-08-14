using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Player;

// One bone of a ragdoll: the body hung off it, the box it collides as, and the joint tying it to its
// parent. Straight off the Characters/Ragdoll_* prefab, in Unity units and Unity space.
//
// `Parent` is the bone whose body this one is jointed TO, which is not always the bone's own parent in
// the skeleton: the prefab ties Left_Leg to the pelvis rather than to Left_Hip, because Left_Hip carries
// no body. Empty means a root body, which is jointed to nothing and is what the impulse is applied to.
public readonly record struct RagdollBone(
    string Name,
    string Parent,
    float Mass,
    Vector3 ColliderCenter,
    Vector3 ColliderSize,
    // CharacterJoint's twist range about m_Axis, in degrees, and its two swing limits about m_SwingAxis.
    float TwistLow,
    float TwistHigh,
    float Swing1,
    float Swing2)
{
    // A cone-twist joint takes one symmetric swing span and one symmetric twist span, where Unity's
    // CharacterJoint takes an asymmetric twist range and two independent swing limits.
    //
    // The twist span is HALF the range: the two are the same thing measured differently, and the shipped
    // limits are near-symmetric anyway (-30/+90 on a knee is the widest asymmetry in the set). The swing
    // span is the smaller of the two, because a cone that fits inside the original's ellipse can only be
    // more restrictive — a limb that stops slightly early reads as a stiff joint, while one that swings
    // past its limit reads as broken.
    public float TwistSpanDegrees => MathF.Abs(TwistHigh - TwistLow) * 0.5f;

    public float SwingSpanDegrees => MathF.Min(Swing1, Swing2);

    public bool IsRoot => Parent.Length == 0;
}

// A whole ragdoll, as the prefab describes it.
//
// Read from the game rather than authored here: the limits, masses and collider boxes are what make a
// body fall like the game's rather than like a pile of blocks, and they are all in the asset.
public sealed class RagdollDefinition
{
    public IReadOnlyList<RagdollBone> Bones { get; }

    public RagdollDefinition(IReadOnlyList<RagdollBone> bones) =>
        Bones = bones ?? throw new ArgumentNullException(nameof(bones));

    public bool IsUsable => Bones.Count > 1;

    // Which bone the killing blow's impulse is applied to. RagdollTool finds the spine and pushes it:
    //
    //     model.Find("Skeleton")?.Find("Spine")?.GetComponent<Rigidbody>()?.AddForce(ragdoll);
    //
    // A shove applied anywhere else spins the body about its own limbs instead of throwing it.
    public const string ImpulseBone = "Spine";

    // The prefab's own pelvis, which this port has no bone for: the live rig's three roots are Spine,
    // Left_Hip and Right_Hip, and the pelvis it hangs them all from is the Skeleton node itself rather
    // than a bone. Bodies jointed to it are re-parented onto the Spine, which sits immediately above it
    // and is the only bone that can stand in for it.
    public const string PelvisName = "Skeleton";

    public RagdollBone? Find(string name)
    {
        foreach (RagdollBone bone in Bones)
            if (string.Equals(bone.Name, name, StringComparison.Ordinal))
                return bone;
        return null;
    }

    // The bones in an order where every parent comes before its children, so a builder can create a body
    // and immediately joint it to one that already exists. Bones whose parent is missing are dropped:
    // a joint to nothing is a limb that falls off.
    public IReadOnlyList<RagdollBone> InParentOrder()
    {
        var ordered = new List<RagdollBone>(Bones.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);

        foreach (RagdollBone bone in Bones)
        {
            if (!bone.IsRoot)
                continue;
            ordered.Add(bone);
            placed.Add(bone.Name);
        }

        // Repeated sweeps rather than a recursive walk: the set is eleven bones, and a cycle in the data
        // (which nothing validates on the way in) has to terminate rather than recurse forever.
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (RagdollBone bone in Bones)
            {
                if (placed.Contains(bone.Name) || !placed.Contains(bone.Parent))
                    continue;
                ordered.Add(bone);
                placed.Add(bone.Name);
                grew = true;
            }
        }

        return ordered;
    }
}
