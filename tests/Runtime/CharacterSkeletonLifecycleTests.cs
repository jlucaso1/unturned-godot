using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Regression cover for the one thing about CharacterSkeleton that no pure test can reach: WHEN the engine
// runs its _Process.
//
// The rig only samples a clip while it has one selected, and keeps _Process off otherwise. It used to
// arrange that on ENTER_TREE alone, which the engine quietly undid — Node's NOTIFICATION_READY handler
// calls set_process(true) for every script that overrides _process, and it runs before the script sees the
// same notification. A rig that entered the tree with no clip then sampled _clips[""] on its first drawn
// frame and threw. Every Clone is such a rig (the clips travel with the copy, the playhead does not), and
// the first-person Viewmodel is the one that reached a frame that way, on cold loads only, where frames run
// while the world is still streaming in.
//
// Nothing about that is visible from core/: it is engine notification ORDER. Hence this file.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public class CharacterSkeletonLifecycleTests : TestClass
{
    public CharacterSkeletonLifecycleTests(Node testScene) : base(testScene) { }

    // The bug, exactly: in the tree, no clip selected, one frame allowed to pass. Processing must be off —
    // if the engine's READY handler wins, this is the frame that indexed the clip dictionary with "".
    [Test]
    public async Task AClipLessRigDoesNotProcessAfterReady()
    {
        CharacterSkeleton rig = Rig();
        TestScene.AddChild(rig);

        Assert.False(rig.IsProcessing()); // READY must not have left it on

        await NextFrame();
        Assert.False(rig.IsProcessing());

        rig.QueueFree();
    }

    // The Viewmodel's own shape: attached with the clips loaded but no state chosen, because the caller
    // picks the state from a later physics tick. The frame in between is the one that used to throw.
    [Test]
    public async Task ARigThatPicksItsClipAfterEnteringTheTreeStartsPosing()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Turn(length: 1f));
        TestScene.AddChild(rig);

        Assert.True(rig.HasAnyPose);
        Assert.False(rig.IsProcessing()); // clips loaded, but nothing selected yet
        await NextFrame();

        Assert.True(rig.SetState(EPlayerStance.Stand, moving: false));
        Assert.True(rig.IsProcessing()); // and now it must actually run

        rig.QueueFree();
    }

    // The other half of the guard: keeping _Process off must not cost a rig that HAS a clip its animation.
    [Test]
    public async Task ARigWithASelectedClipPosesItsBones()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Turn(length: 1f));
        rig.Play("Idle_Stand");
        TestScene.AddChild(rig);

        Assert.True(rig.IsProcessing());

        Quaternion rest = rig.GetBoneRest(0).Basis.GetRotationQuaternion();
        // Several frames, so the assertion does not ride on one frame's delta being large enough to see.
        for (int i = 0; i < 5; i++)
            await NextFrame();

        Assert.True(rig.GetBonePoseRotation(0).AngleTo(rest) > 0.001f,
            "the rig stopped animating: its bone never left the rest pose");

        rig.QueueFree();
    }

    // Seek is the off-line inspection path (CHAR_ANIM_TIME). It reached the same clip lookup, so a
    // character carrying no clip at all threw there instead of on a frame. Called directly, so unlike the
    // _Process cases an exception fails the test rather than being logged by the engine's bridge.
    [Test]
    public void SeekOnAClipLessRigDoesNothing()
    {
        CharacterSkeleton rig = Rig();

        rig.Seek(0.5f);

        Assert.False(rig.HasAnyPose);
        rig.Free(); // never entered the tree
    }

    // A two-bone stand-in for the imported character: the pose path only needs bones and rests.
    private static CharacterSkeleton Rig()
    {
        var rig = new CharacterSkeleton { Name = "Rig" };
        rig.AddBone("Spine");
        rig.AddBone("Skull");
        rig.SetBoneParent(1, 0);
        rig.SetBoneRest(0, Transform3D.Identity);
        rig.SetBoneRest(1, new Transform3D(Basis.Identity, Vector3.Up));
        rig.ResetBonePoses();
        return rig;
    }

    // A clip that turns the root bone a quarter turn over its length, so "did it pose" is answerable by
    // looking at the bone rather than by trusting a flag.
    private static AnimationClipData Turn(float length) => new()
    {
        Length = length,
        Bones = new Dictionary<int, BoneCurves>
        {
            [0] = new BoneCurves
            {
                Rotation = new[]
                {
                    (0f, Quaternion.Identity),
                    (length, new Quaternion(Vector3.Up, Mathf.Pi * 0.5f)),
                },
            },
        },
    };

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
