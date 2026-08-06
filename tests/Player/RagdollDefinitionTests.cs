using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

// The ragdoll the game describes, as data: the joint chain, and the two conversions a cone-twist needs.
public class RagdollDefinitionTests
{
    private static RagdollBone Bone(string name, string parent, float twistLow = 0f, float twistHigh = 0f,
        float swing1 = 30f, float swing2 = 30f) =>
        new(name, parent, 1f, Vector3.Zero, Vector3.One, twistLow, twistHigh, swing1, swing2);

    // The shipped zombie chain, shortened: the legs hang off the PELVIS rather than off Left_Hip, which
    // carries no body. That is the whole reason the builder cannot follow the skeleton's own parents.
    private static RagdollDefinition Zombie() => new(new[]
    {
        Bone("Skull", "Spine"),
        Bone("Left_Hand", "Left_Arm"),
        Bone("Left_Arm", "Spine"),
        Bone("Left_Foot", "Left_Leg"),
        Bone("Left_Leg", "Skeleton"),
        Bone("Spine", "Skeleton"),
        Bone("Skeleton", ""),
    });

    [Fact]
    public void OnlyABoneWithNoParentIsARoot()
    {
        Assert.True(Bone("Skeleton", "").IsRoot);
        Assert.False(Bone("Spine", "Skeleton").IsRoot);
    }

    // Unity's CharacterJoint takes an asymmetric twist RANGE; a cone-twist takes a symmetric SPAN, which
    // is half of it. A knee is -30..+90, so 60.
    [Theory]
    [InlineData(-30f, 90f, 60f)]
    [InlineData(-60f, 60f, 60f)]
    [InlineData(-90f, 30f, 60f)]
    [InlineData(0f, 0f, 0f)]
    public void TheTwistSpanIsHalfTheRange(float low, float high, float expected) =>
        Assert.Equal(expected, Bone("x", "y", low, high).TwistSpanDegrees, 3);

    // And two independent swing limits become one cone, which takes the SMALLER: a cone inside the
    // original's ellipse can only be more restrictive, and a limb that stops early reads as a stiff
    // joint while one that swings past its limit reads as broken.
    [Theory]
    [InlineData(15f, 30f, 15f)]
    [InlineData(30f, 15f, 15f)]
    [InlineData(30f, 30f, 30f)]
    public void TheSwingSpanIsTheSmallerOfTheTwo(float swing1, float swing2, float expected) =>
        Assert.Equal(expected, Bone("x", "y", swing1: swing1, swing2: swing2).SwingSpanDegrees, 3);

    [Fact]
    public void FindNamesABoneOrNothing()
    {
        RagdollDefinition zombie = Zombie();

        Assert.Equal("Spine", zombie.Find("Skull")!.Value.Parent);
        Assert.Null(zombie.Find("Tail"));
    }

    // The builder creates a body and joints it to one that already exists, so every parent has to come
    // first — whatever order the prefab's own walk produced.
    [Fact]
    public void ParentOrderPutsEveryParentBeforeItsChildren()
    {
        IReadOnlyList<RagdollBone> ordered = Zombie().InParentOrder();

        Assert.Equal("Skeleton", ordered[0].Name);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (RagdollBone bone in ordered)
        {
            if (!bone.IsRoot)
                Assert.Contains(bone.Parent, seen);
            seen.Add(bone.Name);
        }

        Assert.Equal(7, ordered.Count);
    }

    // A bone whose parent is not in the set is DROPPED rather than emitted as a root: a joint to nothing
    // is a limb that falls off the corpse on its own.
    [Fact]
    public void ABoneWhoseParentIsMissingIsDropped()
    {
        var definition = new RagdollDefinition(new[]
        {
            Bone("Skeleton", ""),
            Bone("Spine", "Skeleton"),
            Bone("Orphan", "NoSuchBone"),
        });

        IReadOnlyList<RagdollBone> ordered = definition.InParentOrder();

        Assert.Equal(2, ordered.Count);
        Assert.DoesNotContain(ordered, b => b.Name == "Orphan");
    }

    // The chain is data and nothing validates it on the way in, so a cycle has to terminate rather than
    // recurse — and both of its bones are dropped, because neither can be reached from a root.
    [Fact]
    public void ACyclicChainTerminates()
    {
        var definition = new RagdollDefinition(new[]
        {
            Bone("Skeleton", ""),
            Bone("A", "B"),
            Bone("B", "A"),
        });

        Assert.Equal(new[] { "Skeleton" }, definition.InParentOrder().ConvertToNames());
    }

    // One bone is not a ragdoll: there is nothing to joint it to, so a builder should tumble the body
    // whole instead.
    [Fact]
    public void ASingleBoneIsNotUsable()
    {
        Assert.False(new RagdollDefinition(new[] { Bone("Skeleton", "") }).IsUsable);
        Assert.False(new RagdollDefinition(Array.Empty<RagdollBone>()).IsUsable);
        Assert.True(Zombie().IsUsable);
    }

    [Fact]
    public void ANullBoneListIsRefused() =>
        Assert.Throws<ArgumentNullException>(() => new RagdollDefinition(null!));
}

internal static class RagdollBoneListExtensions
{
    public static string[] ConvertToNames(this IReadOnlyList<RagdollBone> bones)
    {
        var names = new string[bones.Count];
        for (int i = 0; i < bones.Count; i++)
            names[i] = bones[i].Name;
        return names;
    }
}
