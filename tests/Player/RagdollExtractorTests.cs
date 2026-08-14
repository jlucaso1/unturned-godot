using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Player;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Player;

// Reading a Unity ragdoll prefab: which bones carry a body, what each collides as, and how the joints
// between them are limited.
//
// Every number that makes a corpse fall convincingly is in that prefab. Authoring them here instead would
// be inventing a character's proportions and joint limits from nothing — so what these hold is that the
// numbers arrive intact, that the tree is walked rather than the file scanned, and that a prefab which is
// not what this expects comes back null instead of half-read. Null is a session with no skeletal ragdoll,
// which falls back to tumbling the body as one piece; a half-read one is a corpse that folds inside out.
public class RagdollExtractorTests
{
    private static SerializedFile Read(PrefabFileBuilder builder) =>
        SerializedFile.Read(builder.Build());

    private static readonly IReadOnlyList<PrefabFileBuilder.Bone> Skeleton = new[]
    {
        new PrefabFileBuilder.Bone("Spine", Mass: 8f,
            Center: (0f, 0.1f, 0f), Size: (0.3f, 0.5f, 0.2f)),
        new PrefabFileBuilder.Bone("Head", Parent: "Spine", Mass: 2f,
            Center: (0f, 0.2f, 0f), Size: (0.2f, 0.2f, 0.2f),
            TwistLow: -30f, TwistHigh: 30f, Swing1: 25f, Swing2: 15f),
        new PrefabFileBuilder.Bone("Arm", Parent: "Spine", Mass: 1.5f,
            Center: (0.1f, 0f, 0f), Size: (0.4f, 0.1f, 0.1f),
            TwistLow: -60f, TwistHigh: 60f, Swing1: 80f, Swing2: 40f),
    };

    [Fact]
    public void EveryBoneWithABodyIsRead()
    {
        RagdollDefinition? definition = RagdollExtractor.Read(
            Read(new PrefabFileBuilder().AddPrefab("Ragdoll_Zombie", Skeleton)), "Ragdoll_Zombie");

        Assert.NotNull(definition);
        Assert.Equal(3, definition.Bones.Count);
        Assert.True(definition.IsUsable);
    }

    // The root holds no body of its own, and must not become a bone: a corpse with an extra unanimated
    // segment at its origin is what counting it would produce.
    [Fact]
    public void TheRootItselfIsNotABone()
    {
        RagdollDefinition? definition = RagdollExtractor.Read(
            Read(new PrefabFileBuilder().AddPrefab("Ragdoll_Zombie", Skeleton)), "Ragdoll_Zombie");

        Assert.NotNull(definition);
        foreach (RagdollBone bone in definition.Bones)
            Assert.NotEqual("Ragdoll_Zombie", bone.Name);
    }

    [Fact]
    public void EachBonesMassColliderAndJointLimitsArriveIntact()
    {
        RagdollDefinition? definition = RagdollExtractor.Read(
            Read(new PrefabFileBuilder().AddPrefab("Ragdoll_Zombie", Skeleton)), "Ragdoll_Zombie");

        Assert.NotNull(definition);
        RagdollBone head = Find(definition, "Head");

        Assert.Equal("Spine", head.Parent);
        Assert.Equal(2f, head.Mass);
        Assert.Equal(new Vector3(0f, 0.2f, 0f), head.ColliderCenter);
        Assert.Equal(new Vector3(0.2f, 0.2f, 0.2f), head.ColliderSize);
        Assert.Equal(-30f, head.TwistLow);
        Assert.Equal(30f, head.TwistHigh);
        Assert.Equal(25f, head.Swing1);
        Assert.Equal(15f, head.Swing2);
    }

    // A bone's parent is recovered through its joint's m_ConnectedBody — the Rigidbody, not the
    // GameObject — so the reader has to know which GameObject each body belongs to. The root bone has no
    // joint at all and is parented to nothing, which is what makes it the root.
    [Fact]
    public void TheRootBoneIsParentedToNothing()
    {
        RagdollDefinition? definition = RagdollExtractor.Read(
            Read(new PrefabFileBuilder().AddPrefab("Ragdoll_Zombie", Skeleton)), "Ragdoll_Zombie");

        Assert.NotNull(definition);
        Assert.Equal(string.Empty, Find(definition, "Spine").Parent);
        Assert.Equal("Spine", Find(definition, "Arm").Parent);
    }

    // A prefab the file does not carry is null rather than an empty definition: the caller distinguishes
    // "no skeletal ragdoll this session" from "a ragdoll with no bones", and only the first is survivable.
    [Fact]
    public void APrefabThatIsNotInTheFileIsNull()
    {
        Assert.Null(RagdollExtractor.Read(
            Read(new PrefabFileBuilder().AddPrefab("Ragdoll_Zombie", Skeleton)), "Ragdoll_Player"));
    }

    // Two ragdolls in one file — the game ships Ragdoll_Zombie and Ragdoll_Player — and each has to come
    // back with only its own bones. A reader that scanned the file for loose Rigidbodies would merge them.
    [Fact]
    public void TwoPrefabsInOneFileDoNotBleedIntoEachOther()
    {
        var builder = new PrefabFileBuilder()
            .AddPrefab("Ragdoll_Zombie", Skeleton)
            .AddPrefab("Ragdoll_Player", new[]
            {
                new PrefabFileBuilder.Bone("PlayerSpine", Mass: 9f),
            });
        SerializedFile file = SerializedFile.Read(builder.Build());

        RagdollDefinition? zombie = RagdollExtractor.Read(file, "Ragdoll_Zombie");
        RagdollDefinition? player = RagdollExtractor.Read(file, "Ragdoll_Player");

        Assert.NotNull(zombie);
        Assert.NotNull(player);
        Assert.Equal(3, zombie.Bones.Count);
        Assert.Equal("PlayerSpine", Assert.Single(player.Bones).Name);
    }

    // A GameObject in the subtree with no Rigidbody is not a bone — real rigs are full of them, holding
    // nothing but a transform. It still has to be WALKED, because its children may be bones.
    [Fact]
    public void AnIntermediateObjectWithNoBodyIsWalkedThroughRatherThanStoppedAt()
    {
        RagdollDefinition? definition = RagdollExtractor.Read(Read(new PrefabFileBuilder()
            .AddPrefab("Ragdoll_Zombie", new[]
            {
                new PrefabFileBuilder.Bone("Spine", Mass: 8f),
                new PrefabFileBuilder.Bone("Holder", Parent: "Spine", HasBody: false, HasCollider: false),
                new PrefabFileBuilder.Bone("Hand", Parent: "Holder", Mass: 0.5f),
            })), "Ragdoll_Zombie");

        Assert.NotNull(definition);
        Assert.Equal(2, definition.Bones.Count);
        Assert.Contains(definition.Bones, bone => bone.Name == "Hand");
        Assert.DoesNotContain(definition.Bones, bone => bone.Name == "Holder");
    }

    // A body with no collider is still a bone; its box is simply zero-sized. The alternative — dropping
    // it — would break the chain and orphan everything below it.
    [Fact]
    public void ABodyWithNoColliderIsStillABone()
    {
        RagdollDefinition? definition = RagdollExtractor.Read(Read(new PrefabFileBuilder()
            .AddPrefab("Ragdoll_Zombie", new[]
            {
                new PrefabFileBuilder.Bone("Spine", Mass: 8f),
                new PrefabFileBuilder.Bone("Ghost", Parent: "Spine", Mass: 1f, HasCollider: false),
            })), "Ragdoll_Zombie");

        Assert.NotNull(definition);
        RagdollBone ghost = Find(definition, "Ghost");
        Assert.Equal(Vector3.Zero, ghost.ColliderSize);
        Assert.Equal(1f, ghost.Mass);
    }

    // A single-bone prefab parses but is not usable: one body is a corpse tumbling as one piece, which is
    // the fallback rather than a ragdoll.
    [Fact]
    public void ASingleBonePrefabIsReadButNotUsable()
    {
        RagdollDefinition? definition = RagdollExtractor.Read(Read(new PrefabFileBuilder()
            .AddPrefab("Ragdoll_Zombie", new[] { new PrefabFileBuilder.Bone("Spine", Mass: 8f) })),
            "Ragdoll_Zombie");

        Assert.NotNull(definition);
        Assert.False(definition.IsUsable);
    }

    // A prefab whose root has no bodies anywhere under it has nothing to build, and null is what says so.
    [Fact]
    public void APrefabWithNoBodiesAtAllIsNull()
    {
        Assert.Null(RagdollExtractor.Read(Read(new PrefabFileBuilder()
            .AddPrefab("Ragdoll_Zombie", new[]
            {
                new PrefabFileBuilder.Bone("Spine", HasBody: false, HasCollider: false),
            })), "Ragdoll_Zombie"));
    }

    [Fact]
    public void ANullFileOrPrefabNameIsRejectedRatherThanDereferenced()
    {
        Assert.Throws<ArgumentNullException>(() => RagdollExtractor.Read((SerializedFile)null!, "x"));
        Assert.Throws<ArgumentNullException>(() =>
            RagdollExtractor.Read(null!, "x", _ => new Dictionary<int, List<TypeTreeNode>>()));
        Assert.Throws<ArgumentNullException>(() =>
            RagdollExtractor.Read("/install", null!, _ => new Dictionary<int, List<TypeTreeNode>>()));
        Assert.Throws<ArgumentNullException>(() => RagdollExtractor.Read("/install", "x", null!));
    }

    // An install this cannot read is null rather than a throw: the type trees come from the masterbundle
    // and resources.assets from beside it, and a session missing either still has to start.
    [Fact]
    public void AnInstallWithNoResourcesFileIsNull()
    {
        using var dir = new TempDir();

        Assert.Null(RagdollExtractor.Read(dir.Path, RagdollExtractor.ZombiePrefab,
            _ => new Dictionary<int, List<TypeTreeNode>>()));
    }

    private static RagdollBone Find(RagdollDefinition definition, string name)
    {
        foreach (RagdollBone bone in definition.Bones)
            if (bone.Name == name)
                return bone;
        throw new InvalidOperationException($"no bone named {name}");
    }
}
