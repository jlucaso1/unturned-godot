using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Reading Characters/Ragdoll_Zombie out of the real install.
//
// Every number a corpse falls by is in that prefab, so this is what says the port is describing the game's
// ragdoll rather than one it invented. It is also the test that goes red the day Valve reshapes the
// prefab — which would otherwise surface as bodies falling subtly wrong, with nothing to point at.
public class RagdollExtractorRealTests : TestClass
{
    public RagdollExtractorRealTests(Node testScene) : base(testScene) { }

    private static string? Install => UnturnedInstall.Find();

    private static RagdollDefinition? Zombie() =>
        Install == null ? null : RagdollExtractor.Read(Install, RagdollExtractor.ZombiePrefab);

    // Eleven bodies: the pelvis, the spine, the skull, and two of each limb segment.
    [Test]
    public void TheZombieRagdollCarriesABodyPerLimb()
    {
        if (Install == null)
            return;
        RagdollDefinition? ragdoll = Zombie();
        Assert.NotNull(ragdoll);

        var names = new List<string>();
        foreach (RagdollBone bone in ragdoll!.Bones)
            names.Add(bone.Name);

        foreach (string expected in new[]
                 {
                     "Skeleton", "Spine", "Skull",
                     "Left_Arm", "Left_Hand", "Right_Arm", "Right_Hand",
                     "Left_Leg", "Left_Foot", "Right_Leg", "Right_Foot",
                 })
        {
            Assert.Contains(expected, names);
        }

        Assert.True(ragdoll.IsUsable);
    }

    // The pelvis is the only body with no joint above it; everything else hangs off something. A second
    // root would be a limb that simply falls away from the corpse.
    [Test]
    public void OnlyThePelvisIsARoot()
    {
        if (Install == null)
            return;
        RagdollDefinition ragdoll = Zombie()!;

        var roots = new List<string>();
        foreach (RagdollBone bone in ragdoll.Bones)
            if (bone.IsRoot)
                roots.Add(bone.Name);

        Assert.Equal(new[] { RagdollDefinition.PelvisName }, roots);
    }

    // The chain the prefab actually describes. Note that the legs hang off the PELVIS rather than off
    // Left_Hip — that bone carries no body — which is the reason the builder cannot simply follow the
    // skeleton's own parent links.
    [Test]
    public void TheJointChainIsTheOneThePrefabDescribes()
    {
        if (Install == null)
            return;
        RagdollDefinition ragdoll = Zombie()!;

        Assert.Equal("Skeleton", ragdoll.Find("Spine")!.Value.Parent);
        Assert.Equal("Skeleton", ragdoll.Find("Left_Leg")!.Value.Parent);
        Assert.Equal("Skeleton", ragdoll.Find("Right_Leg")!.Value.Parent);
        Assert.Equal("Spine", ragdoll.Find("Skull")!.Value.Parent);
        Assert.Equal("Spine", ragdoll.Find("Left_Arm")!.Value.Parent);
        Assert.Equal("Left_Arm", ragdoll.Find("Left_Hand")!.Value.Parent);
        Assert.Equal("Left_Leg", ragdoll.Find("Left_Foot")!.Value.Parent);
    }

    // The limits are what stop a corpse folding through itself. A knee that twists -30..+90 and a neck
    // that twists +-60 are the shipped values; asserted loosely, because the point is that they were READ
    // rather than defaulted to zero.
    [Test]
    public void TheJointLimitsCameOutOfTheAsset()
    {
        if (Install == null)
            return;
        RagdollDefinition ragdoll = Zombie()!;

        RagdollBone knee = ragdoll.Find("Left_Leg")!.Value;
        Assert.Equal(-30f, knee.TwistLow, 1);
        Assert.Equal(90f, knee.TwistHigh, 1);
        Assert.Equal(15f, knee.Swing1, 1);

        RagdollBone neck = ragdoll.Find("Skull")!.Value;
        Assert.Equal(-60f, neck.TwistLow, 1);
        Assert.Equal(60f, neck.TwistHigh, 1);
        Assert.Equal(30f, neck.Swing1, 1);

        // Every body has a mass and a box; a zero anywhere means a field was not read.
        foreach (RagdollBone bone in ragdoll.Bones)
        {
            Assert.True(bone.Mass > 0f, $"{bone.Name} has no mass");
            Assert.True(bone.ColliderSize.Length() > 0f, $"{bone.Name} has no collider");
        }
    }

    // The builder creates bodies in this order, so a parent always exists before the joint that needs it.
    [Test]
    public void ParentOrderPutsThePelvisFirstAndEveryParentBeforeItsChildren()
    {
        if (Install == null)
            return;
        IReadOnlyList<RagdollBone> ordered = Zombie()!.InParentOrder();

        Assert.Equal(RagdollDefinition.PelvisName, ordered[0].Name);

        var seen = new HashSet<string>();
        foreach (RagdollBone bone in ordered)
        {
            if (!bone.IsRoot)
                Assert.Contains(bone.Parent, seen);
            seen.Add(bone.Name);
        }

        Assert.Equal(Zombie()!.Bones.Count, ordered.Count);
    }

    // The player ragdoll is the same shape, and it is what makes this worth sharing: the port has no
    // dying players yet, and when it does they should not need a second extractor.
    [Test]
    public void ThePlayerRagdollReadsToo()
    {
        if (Install == null)
            return;
        RagdollDefinition? player = RagdollExtractor.Read(Install, RagdollExtractor.PlayerPrefab);

        Assert.NotNull(player);
        Assert.True(player!.IsUsable);
        Assert.NotNull(player.Find("Spine"));
    }
}
