using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The animation runtime itself: clip selection, crossfading, the one-shot overlay, the pitch bend and the
// channel bookkeeping that decides which bones get written each frame.
//
// The pure halves of this already have unit tests in core/ — AnimationSampler samples and blends,
// GestureOverlay arbitrates a swing against the movement state, ViewmodelAnchor does the eye maths. What
// only exists here is the wiring between them and the engine: which clip a stance resolves to, what
// actually reaches Skeleton3D's bone poses, and what happens to a bone the previous frame animated and
// this one does not. That is the part a rig gets wrong silently — the pose simply looks off.
//
// CharacterSkeletonLifecycleTests covers when _Process runs; this covers what it does.
public class CharacterSkeletonAnimationTests : TestClass
{
    public CharacterSkeletonAnimationTests(Node testScene) : base(testScene) { }

    // Bone indices in the rig built by Rig() below.
    private const int Root = 0, Spine = 1, Skull = 2, Arm = 3;

    // Bone indices in PlayerRig(), which uses the game's own bone names so the mixAnimation overload can
    // resolve them.
    private const int BodySpine = 0, LeftShoulder = 1, LeftArm = 2, LeftHip = 3, LeftLeg = 4;

    // --- bindings ------------------------------------------------------------------------------------

    // The bindings a Clone copies over. They are plain accessors, but a clone that loses one is the bug
    // that parks a first-person camera inside its own neck, so the round trip is worth pinning.
    [Test]
    public void BindingsRoundTrip()
    {
        CharacterSkeleton rig = Rig();

        rig.BindPitchBones(Spine, Skull);
        rig.BindEye(Skull, new Vector3(0f, 0.45f, 0f));

        Assert.Equal((Spine, Skull), rig.PitchBones);
        Assert.Equal((Skull, new Vector3(0f, 0.45f, 0f)), rig.Eye);
        Assert.False(rig.HasAnyPose); // no clips stored yet

        rig.Free();
    }

    // The viewmodel rides its own eye: the rig's own Position is moved so the bound eye lands on the
    // camera. Anchoring is the difference between looking THROUGH the head and looking AT it.
    [Test]
    public void AnchoringTheEyeMovesTheRigOntoTheCamera()
    {
        CharacterSkeleton rig = Rig();
        rig.BindEye(Skull, new Vector3(0f, 0.45f, 0f));
        TestScene.AddChild(rig);

        Assert.Equal(Vector3.Zero, rig.Position);
        rig.AnchorEyeToCamera(Vector3.Zero);

        // Skull rests a metre up with a 0.45 m offset inside it, so the rig is pushed down by that much
        // to bring the eye back to the origin (ViewmodelAnchor.RigPosition: nudge - eye).
        Assert.True(rig.Position.Y < -1.4f, $"the rig did not ride its eye: {rig.Position}");

        rig.QueueFree();
    }

    // A rig with no eye bound must not move: the binding is optional, and a missing one used to be the
    // difference between a body drawn from inside its head and one hanging off the origin.
    [Test]
    public void AnchoringWithNoEyeBoundLeavesTheRigWhereItIs()
    {
        CharacterSkeleton rig = Rig();
        TestScene.AddChild(rig);

        rig.AnchorEyeToCamera(new Vector3(1f, 2f, 3f));

        Assert.Equal(Vector3.Zero, rig.Position);
        rig.QueueFree();
    }

    // --- clip selection ------------------------------------------------------------------------------

    // PlayerAnimator.updateState's mapping, stance by stance. Sprint deliberately shares the stand idle
    // (there is no Idle_Sprint) and runs Move_Run when moving, which is the pair most easily got wrong.
    [Test]
    public void EveryStanceResolvesToItsOwnClip()
    {
        var expected = new (EPlayerStance Stance, bool Moving, string Clip)[]
        {
            (EPlayerStance.Stand, false, "Idle_Stand"),
            (EPlayerStance.Sprint, false, "Idle_Stand"),
            (EPlayerStance.Crouch, false, "Idle_Crouch"),
            (EPlayerStance.Prone, false, "Idle_Prone"),
            (EPlayerStance.Climb, false, "Idle_Climb"),
            (EPlayerStance.Stand, true, "Move_Walk"),
            (EPlayerStance.Sprint, true, "Move_Run"),
            (EPlayerStance.Crouch, true, "Move_Crouch"),
            (EPlayerStance.Prone, true, "Move_Prone"),
            (EPlayerStance.Climb, true, "Move_Climb"),
        };

        foreach ((EPlayerStance stance, bool moving, string clip) in expected)
        {
            // One rig per case, carrying ONLY the expected clip: SetState returns false unless the clip
            // it resolved to is the one present, so this asserts the mapping rather than trusting it.
            CharacterSkeleton rig = Rig();
            rig.StoreClip(clip, Turn(1f, Root));

            Assert.True(rig.SetState(stance, moving),
                $"{stance}/{(moving ? "moving" : "idle")} did not resolve to {clip}");

            rig.Free();
        }
    }

    // A character that does not carry the clip for a stance is left alone rather than frozen on a
    // missing animation — the NPCs and the zombie looks share this rig and carry different clip sets.
    [Test]
    public void AStanceWithNoClipIsRefused()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Turn(1f, Root));

        Assert.True(rig.SetState(EPlayerStance.Stand, moving: false));
        Assert.False(rig.SetState(EPlayerStance.Prone, moving: false)); // no Idle_Prone stored

        rig.Free();
    }

    // The dirty check: callers push the movement state every tick, so re-requesting the state already
    // applied must be a cheap true rather than a re-Play that restarts the clip every frame.
    [Test]
    public async Task RepeatingAStateDoesNotRestartTheClip()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Turn(1f, Root));
        TestScene.AddChild(rig);
        rig.SetState(EPlayerStance.Stand, moving: false);

        for (int i = 0; i < 3; i++)
            await NextFrame();
        Quaternion advanced = rig.GetBonePoseRotation(Root);

        Assert.True(rig.SetState(EPlayerStance.Stand, moving: false)); // same state again
        await NextFrame();

        // A restart would have snapped the clock back to the clip's first key.
        Assert.True(rig.GetBonePoseRotation(Root).AngleTo(advanced) < 0.2f,
            "re-requesting the current state restarted the clip");

        rig.QueueFree();
    }

    // --- posing --------------------------------------------------------------------------------------

    // Seek is the deterministic path: no frames, no delta, just "pose this clip at this time". Half way
    // through a quarter turn is a measurable eighth of a turn.
    [Test]
    public void SeekPosesTheClipAtAnAbsoluteTime()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Turn(1f, Root));
        rig.Play("Idle_Stand");

        rig.Seek(0.5f);

        float angle = rig.GetBonePoseRotation(Root).AngleTo(Quaternion.Identity);
        Assert.True(Mathf.Abs(angle - (Mathf.Pi * 0.25f)) < 0.02f,
            $"expected an eighth of a turn at the clip's midpoint, got {angle} rad");

        rig.Free();
    }

    // The pitch bend: HumanAnimator gives half the look pitch to the spine and half to the skull, and
    // the port composes it over the sampled pose rather than reading the bone back from the engine.
    // Bones that are not the pitch pair must not move.
    [Test]
    public void LookPitchBendsTheSpineAndSkullOnly()
    {
        CharacterSkeleton rig = Rig();
        rig.BindPitchBones(Spine, Skull);
        rig.StoreClip("Idle_Stand", Turn(1f, Root)); // animates Root alone
        rig.Play("Idle_Stand");

        rig.SetPitch(60f);
        rig.Seek(0f);

        // Half of 60 degrees each, applied over their rests, which the clip never touches.
        foreach (int bone in new[] { Spine, Skull })
        {
            float angle = rig.GetBonePoseRotation(bone).AngleTo(rig.GetBoneRest(bone).Basis.GetRotationQuaternion());
            Assert.True(Mathf.Abs(angle - Mathf.DegToRad(30f)) < 0.02f,
                $"bone {bone} bent {Mathf.RadToDeg(angle):0.0}°, expected 30°");
        }

        // The arm is neither animated nor a pitch bone: it must sit exactly on its rest.
        Assert.True(rig.GetBonePoseRotation(Arm).AngleTo(rig.GetBoneRest(Arm).Basis.GetRotationQuaternion()) < 0.001f,
            "a bone that is neither animated nor a pitch bone was written");

        rig.Free();
    }

    // The pitch composes over an ANIMATED pitch bone too, rather than replacing what the clip said —
    // a different code path from the rest-based bend above.
    [Test]
    public void LookPitchComposesOverAnAnimatedPitchBone()
    {
        CharacterSkeleton rig = Rig();
        rig.BindPitchBones(Spine, Skull);
        rig.StoreClip("Idle_Stand", Turn(1f, Spine)); // this time the clip animates the spine
        rig.Play("Idle_Stand");

        rig.SetPitch(0f);
        // Half way through: the clip loops, so sampling AT its length wraps back to the first key.
        rig.Seek(0.5f); // an eighth of a turn from the clip alone
        float clipOnly = rig.GetBonePoseRotation(Spine).AngleTo(Quaternion.Identity);

        rig.SetPitch(60f);
        rig.Seek(0.5f);
        float withPitch = rig.GetBonePoseRotation(Spine).AngleTo(Quaternion.Identity);

        Assert.True(Mathf.Abs(clipOnly - (Mathf.Pi * 0.25f)) < 0.02f, $"clip alone gave {clipOnly} rad");
        Assert.True(withPitch > clipOnly + 0.4f,
            $"the bend did not compose over the clip: {clipOnly} rad -> {withPitch} rad");

        rig.Free();
    }

    // Channels the previous frame animated and this one does not fall back to the bind rest. This is the
    // incremental form of a full ResetBonePoses, and getting it wrong leaves a bone stuck in the old
    // clip's pose — visible as a limb that keeps an attitude from an animation that already ended.
    [Test]
    public void ABoneTheNewClipDoesNotAnimateReturnsToItsRest()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Turn(1f, Arm));   // animates the arm
        rig.StoreClip("Idle_Crouch", Turn(1f, Root)); // animates the root instead

        rig.Play("Idle_Stand");
        rig.Seek(0.5f); // inside the clip: sampling at its length would wrap to the first key
        Assert.True(rig.GetBonePoseRotation(Arm).AngleTo(Quaternion.Identity) > 0.5f, "the arm never posed");

        rig.Play("Idle_Crouch");
        rig.Seek(0.5f);

        Assert.True(rig.GetBonePoseRotation(Arm).AngleTo(rig.GetBoneRest(Arm).Basis.GetRotationQuaternion()) < 0.001f,
            "the arm kept the previous clip's pose instead of falling back to its rest");

        rig.Free();
    }

    // Switching clips crossfades rather than snapping (CharacterAnimator.CrossFade over BLEND = 0.25 s).
    // Mid-blend the pose must be neither of the two clips' own poses.
    [Test]
    public async Task SwitchingClipsBlendsOutOfThePreviousPose()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Turn(1f, Root));
        rig.StoreClip("Idle_Crouch", Still(1f, Root)); // holds the root at the rest rotation
        TestScene.AddChild(rig);

        rig.Play("Idle_Stand");
        rig.Seek(0.5f); // an eighth of a turn in; sampling at the clip's length would wrap to zero
        Quaternion before = rig.GetBonePoseRotation(Root);

        rig.Play("Idle_Crouch"); // blend starts here, out of `before` and into identity
        await NextFrame();
        Quaternion blended = rig.GetBonePoseRotation(Root);

        Assert.True(blended.AngleTo(before) > 0.001f, "the blend never left the previous pose");
        Assert.True(blended.AngleTo(Quaternion.Identity) > 0.001f,
            "the blend snapped straight to the new clip instead of easing into it");

        rig.QueueFree();
    }

    // --- the one-shot overlay ------------------------------------------------------------------------

    // A punch lays over the movement state and lets it back through when it ends. The overlay's own rules
    // are unit-tested in core/; what is asserted here is that the skeleton drives its clock for real.
    [Test]
    public async Task AGestureOverlaysTheStateAndThenEnds()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Turn(1f, Root));
        rig.StoreClip("Punch_Right", Turn(0.05f, Arm)); // short, so a few frames outlast it
        TestScene.AddChild(rig);
        rig.SetState(EPlayerStance.Stand, moving: false);

        Assert.True(rig.PlayOnce("Punch_Right"));
        Assert.True(rig.IsPlayingOnce);

        for (int i = 0; i < 12; i++)
            await NextFrame();

        Assert.False(rig.IsPlayingOnce);
        rig.QueueFree();
    }

    // The movement state is layer 0 and a gesture cannot take it: updateState pushes a stance change
    // every frame whether or not something is swinging, and the clip it picks goes on running underneath.
    [Test]
    public async Task AStanceChangeMidSwingIsAppliedImmediately()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Turn(1f, Root));
        rig.StoreClip("Idle_Crouch", Turn(1f, Spine));
        rig.StoreClip("Punch_Right", Turn(5f, Arm)); // long enough to outlast the assertions
        TestScene.AddChild(rig);
        rig.SetState(EPlayerStance.Stand, moving: false);
        rig.PlayOnce("Punch_Right");

        Assert.True(rig.SetState(EPlayerStance.Crouch, moving: false));
        Assert.True(rig.IsPlayingOnce); // and the swing is unaffected by it

        for (int i = 0; i < 4; i++)
            await NextFrame();

        // Idle_Crouch is the only clip that turns the spine, so a spine off its rest is that clip running.
        Assert.True(
            rig.GetBonePoseRotation(Spine).AngleTo(rig.GetBoneRest(Spine).Basis.GetRotationQuaternion()) > 0.001f,
            "the crouch clip never started: the gesture swallowed the stance change");

        rig.QueueFree();
    }

    // --- mixing transforms -----------------------------------------------------------------------------

    // The bug this arrangement exists for: Unturned's punch clips are authored over the WHOLE skeleton,
    // legs included, so a swing played across the rig replaces the walk cycle and the legs stand still
    // for its whole length. mixAnimation restricts it to one arm; the movement layer keeps the rest.
    [Test]
    public async Task AMixedGestureLeavesTheLegsOnTheMovementClip()
    {
        CharacterSkeleton rig = PlayerRig();
        rig.StoreClip("Move_Run", Turn(0.2f, LeftArm, LeftLeg));   // a brisk cycle over arm and leg alike
        rig.StoreClip("Punch_Left", Still(30f, LeftArm, LeftLeg)); // full-body, as the real clip is
        rig.MixAnimation("Punch_Left", mixLeftShoulder: true, mixRightShoulder: false);
        TestScene.AddChild(rig);

        rig.SetState(EPlayerStance.Sprint, moving: true);
        await NextFrame();
        Assert.True(rig.PlayOnce("Punch_Left"));

        // Watch a window of the swing rather than counting frames into it: what is being asserted is that
        // the legs are not FROZEN, which is true at any frame rate.
        await NextFrame();
        Quaternion legFirst = rig.GetBonePoseRotation(LeftLeg);
        float legMoved = 0f, armMoved = 0f;
        for (int i = 0; i < 30; i++)
        {
            await NextFrame();
            legMoved = Mathf.Max(legMoved, rig.GetBonePoseRotation(LeftLeg).AngleTo(legFirst));
            armMoved = Mathf.Max(armMoved, rig.GetBonePoseRotation(LeftArm).AngleTo(Quaternion.Identity));
        }

        Assert.True(rig.IsPlayingOnce, "the swing ended before the window did");
        Assert.True(legMoved > 0.05f,
            $"the legs froze during the swing: they moved {Mathf.RadToDeg(legMoved):0.0}° in the whole window");
        Assert.True(armMoved < 0.02f,
            $"the swing did not reach the arm it was mixed onto: it drifted {Mathf.RadToDeg(armMoved):0.0}°");

        rig.QueueFree();
    }

    // The same rig and the same clips with no mixAnimation registered: Unturned's `mixAnimation(name)`
    // with no mixing transforms restricts nothing, so the gesture covers the whole body — which is what
    // a gesture the port has not mapped still does.
    [Test]
    public async Task AnUnmixedGestureStillCoversTheWholeBody()
    {
        CharacterSkeleton rig = PlayerRig();
        rig.StoreClip("Move_Run", Turn(0.2f, LeftArm, LeftLeg));
        rig.StoreClip("Punch_Left", Still(30f, LeftArm, LeftLeg));
        TestScene.AddChild(rig);

        rig.SetState(EPlayerStance.Sprint, moving: true);
        await NextFrame();
        rig.PlayOnce("Punch_Left");

        float legMoved = 0f;
        for (int i = 0; i < 30; i++)
        {
            await NextFrame();
            legMoved = Mathf.Max(legMoved, rig.GetBonePoseRotation(LeftLeg).AngleTo(Quaternion.Identity));
        }

        Assert.True(legMoved < 0.02f, "an unmixed gesture let the movement layer through");
        rig.QueueFree();
    }

    // A rig that does not carry the bones a mix names contributes no mixing transform, which in Unity is
    // no restriction at all. Silently masking EVERYTHING out instead would be a gesture that does nothing.
    [Test]
    public void AMixAgainstBonesTheRigDoesNotHaveRestrictsNothing()
    {
        CharacterSkeleton rig = Rig(); // Root/Spine/Skull/Arm: no Left_Shoulder anywhere

        Assert.Null(rig.Subtrees(new[] { "Left_Shoulder", "Right_Shoulder" }));

        rig.MixAnimation("Punch_Left", mixLeftShoulder: true, mixRightShoulder: true);
        Assert.True(rig.Mixes.ContainsKey("Punch_Left"));
        Assert.Null(rig.Mixes["Punch_Left"]);

        rig.Free();
    }

    // The mask is recursive, the way AddMixingTransform(t, recursive: true) is: naming the shoulder
    // reaches the arm and the hand under it, and stops at the other side of the body.
    [Test]
    public void AMixReachesTheWholeLimbUnderTheShoulder()
    {
        CharacterSkeleton rig = PlayerRig();

        BoneMask mask = rig.Subtrees(new[] { "Left_Shoulder" })!;

        Assert.True(mask.Contains(LeftShoulder));
        Assert.True(mask.Contains(LeftArm));
        Assert.False(mask.Contains(BodySpine));
        Assert.False(mask.Contains(LeftHip));
        Assert.False(mask.Contains(LeftLeg));

        rig.Free();
    }

    // A gesture the character cannot perform is skipped rather than freezing the rig on a clip that is
    // not there — how a zombie without a given swing keeps moving.
    [Test]
    public void AGestureWithNoClipIsSkipped()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Turn(1f, Root));

        Assert.False(rig.PlayOnce("Punch_Left")); // never stored
        Assert.False(rig.IsPlayingOnce);

        rig.Free();
    }

    // A zero-length clip is not a gesture: Begin refuses it, and treating it as one would leave the rig
    // owned by an overlay that never ends.
    [Test]
    public void AZeroLengthGestureIsRefused()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Punch_Left", Turn(0f, Arm));

        Assert.False(rig.PlayOnce("Punch_Left"));
        Assert.False(rig.IsPlayingOnce);

        rig.Free();
    }

    // Re-triggering the swing already playing restarts its clock. Play's already-playing early-out would
    // swallow that, which is why Replay exists — a second punch inside the first must swing again.
    [Test]
    public void ReplayRestartsTheClipThatIsAlreadyPlaying()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Punch_Right", Turn(1f, Arm));
        rig.Play("Punch_Right");
        rig.Seek(0.5f); // mid-swing; the clip loops, so its length samples as zero
        Quaternion atEnd = rig.GetBonePoseRotation(Arm);

        rig.Replay("Punch_Right");
        rig.Seek(0f);

        Assert.True(rig.GetBonePoseRotation(Arm).AngleTo(atEnd) > 0.5f,
            "the replay did not take the clip back to its opening frame");

        rig.Free();
    }

    // Replay with a clip that is NOT the current one is just a Play — the other branch of the same
    // method, and the one an attack of a different kind takes.
    [Test]
    public void ReplayWithADifferentClipSwitchesToIt()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Punch_Left", Turn(1f, Arm));
        rig.StoreClip("Punch_Right", Turn(1f, Root));
        rig.Play("Punch_Left");

        rig.Replay("Punch_Right");
        rig.Seek(0.5f);

        Assert.True(rig.GetBonePoseRotation(Root).AngleTo(Quaternion.Identity) > 0.5f,
            "Replay did not switch to the other clip");

        rig.Free();
    }

    // Playing a clip the rig does not carry keeps whatever is running, rather than clearing the current
    // clip and leaving the rig with nothing to sample.
    [Test]
    public void PlayingAMissingClipKeepsTheCurrentOne()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Turn(1f, Root));
        rig.Play("Idle_Stand");
        rig.Seek(1f);
        Quaternion posed = rig.GetBonePoseRotation(Root);

        rig.Play("Move_Walk"); // never stored
        rig.Seek(1f);

        Assert.Equal(posed, rig.GetBonePoseRotation(Root));
        rig.Free();
    }

    // A rig that stops being drawn hands a mid-swing gesture back to the movement state instead of
    // freezing it: a swing nobody can see need not finish, and thawing it later would replay a stale
    // punch from where it stopped.
    [Test]
    public async Task HidingTheRigEndsAGestureInsteadOfFreezingIt()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Turn(1f, Root));
        rig.StoreClip("Punch_Right", Turn(5f, Arm)); // long enough that time alone cannot end it
        TestScene.AddChild(rig);
        rig.SetState(EPlayerStance.Stand, moving: false);
        rig.PlayOnce("Punch_Right");
        Assert.True(rig.IsPlayingOnce);

        rig.Visible = false;
        await NextFrame();

        Assert.False(rig.IsPlayingOnce);
        Assert.False(rig.IsProcessing()); // and it stops sampling while hidden
        rig.QueueFree();
    }

    // --- every channel, not just rotation ------------------------------------------------------------

    // Clips carry position and scale tracks as well as rotation, and each channel has its own mirror and
    // its own write. Rotation is what the game's own clips mostly use, so the other two are the ones that
    // can rot unnoticed — a clip that moves a bone would silently not move it.
    [Test]
    public void PositionAndScaleTracksReachTheBones()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Moves(1f, Arm));
        rig.Play("Idle_Stand");

        rig.Seek(0.5f);

        Assert.True(rig.GetBonePosePosition(Arm).DistanceTo(rig.GetBoneRest(Arm).Origin) > 0.1f,
            "the clip's position track never reached the bone");
        Assert.True(rig.GetBonePoseScale(Arm).DistanceTo(Vector3.One) > 0.1f,
            "the clip's scale track never reached the bone");

        rig.Free();
    }

    // The same fallback the rotation channel has, for the other two: a bone the previous clip moved and
    // scaled must return to its rest when the new clip says nothing about it.
    [Test]
    public void PositionAndScaleFallBackToRestWhenTheNewClipDropsThem()
    {
        CharacterSkeleton rig = Rig();
        rig.StoreClip("Idle_Stand", Moves(1f, Arm));  // position + scale on the arm
        rig.StoreClip("Idle_Crouch", Turn(1f, Root)); // rotation on the root, nothing about the arm

        rig.Play("Idle_Stand");
        rig.Seek(0.5f);
        Assert.True(rig.GetBonePosePosition(Arm).DistanceTo(rig.GetBoneRest(Arm).Origin) > 0.1f,
            "the arm never moved in the first place");

        rig.Play("Idle_Crouch");
        rig.Seek(0.5f);

        Assert.True(rig.GetBonePosePosition(Arm).DistanceTo(rig.GetBoneRest(Arm).Origin) < 0.001f,
            "the arm kept the previous clip's position");
        Assert.True(rig.GetBonePoseScale(Arm).DistanceTo(Vector3.One) < 0.001f,
            "the arm kept the previous clip's scale");

        rig.Free();
    }

    // --- defensive paths ------------------------------------------------------------------------------

    // The clip set is exposed so a Clone can share it — the decode is expensive and the clips are
    // immutable, so hundreds of zombies read the one dictionary.
    [Test]
    public void TheClipSetIsExposedForClonesToShare()
    {
        CharacterSkeleton rig = Rig();
        AnimationClipData clip = Turn(1f, Root);
        rig.StoreClip("Idle_Stand", clip);

        Assert.True(rig.Clips.ContainsKey("Idle_Stand"));
        Assert.Same(clip, rig.Clips["Idle_Stand"]); // shared, not copied

        rig.Free();
    }

    // Processing turned on from OUTSIDE this class, with no clip selected. That is not hypothetical: it
    // is what the engine itself does at NOTIFICATION_READY, and it threw on the first drawn frame until
    // #86. The rig has to settle it rather than sample a clip that is not there.
    [Test]
    public async Task ProcessingTurnedOnFromOutsideWithNoClipSettlesItself()
    {
        CharacterSkeleton rig = Rig();
        TestScene.AddChild(rig);

        rig.SetProcess(true); // nothing selected; this is the state that used to throw
        await NextFrame();

        Assert.False(rig.IsProcessing());
        rig.QueueFree();
    }

    // A rig whose bones have not been added yet: CharacterModel builds the hierarchy in stages, so an
    // Apply can land before there is anything to pose. It must wait rather than cache an empty rest set
    // and then treat that as done.
    [Test]
    public void ARigWithNoBonesYetIsLeftAlone()
    {
        var rig = new CharacterSkeleton { Name = "Unassembled" }; // no AddBone at all
        rig.StoreClip("Idle_Stand", new AnimationClipData { Length = 1f }); // and no bone tracks

        rig.Play("Idle_Stand");
        rig.Seek(0.5f); // reaches Apply, which finds no bones

        Assert.Equal(0, rig.GetBoneCount());
        rig.Free();
    }

    // --- helpers -------------------------------------------------------------------------------------

    // A four-bone stand-in: Root -> Spine -> Skull, plus an Arm off the spine. Rests are what the pitch
    // bend and the stale-channel fallback are measured against, so they are deliberately not identity.
    private static CharacterSkeleton Rig()
    {
        var rig = new CharacterSkeleton { Name = "Rig" };
        foreach (string bone in new[] { "Root", "Spine", "Skull", "Arm" })
            rig.AddBone(bone);
        rig.SetBoneParent(Spine, Root);
        rig.SetBoneParent(Skull, Spine);
        rig.SetBoneParent(Arm, Spine);
        rig.SetBoneRest(Root, Transform3D.Identity);
        rig.SetBoneRest(Spine, new Transform3D(Basis.Identity, Vector3.Up * 0.5f));
        rig.SetBoneRest(Skull, new Transform3D(Basis.Identity, Vector3.Up * 0.5f));
        rig.SetBoneRest(Arm, new Transform3D(Basis.Identity, Vector3.Right * 0.3f));
        rig.ResetBonePoses();
        return rig;
    }

    // The player's own bone NAMES on a small rig, so mixAnimation resolves its shoulders: the left arm
    // chain off the spine, and a leg on a hip root of its own — which is how Player_Client is built, hips
    // and spine being separate roots rather than one skeleton root.
    private static CharacterSkeleton PlayerRig()
    {
        var rig = new CharacterSkeleton { Name = "PlayerRig" };
        foreach (string bone in new[] { "Spine", "Left_Shoulder", "Left_Arm", "Left_Hip", "Left_Leg" })
            rig.AddBone(bone);
        rig.SetBoneParent(LeftShoulder, BodySpine);
        rig.SetBoneParent(LeftArm, LeftShoulder);
        rig.SetBoneParent(LeftLeg, LeftHip);
        rig.SetBoneRest(BodySpine, new Transform3D(Basis.Identity, Vector3.Up));
        rig.SetBoneRest(LeftShoulder, new Transform3D(Basis.Identity, Vector3.Left * 0.2f));
        rig.SetBoneRest(LeftArm, new Transform3D(Basis.Identity, Vector3.Down * 0.3f));
        rig.SetBoneRest(LeftHip, new Transform3D(Basis.Identity, Vector3.Left * 0.1f));
        rig.SetBoneRest(LeftLeg, new Transform3D(Basis.Identity, Vector3.Down * 0.4f));
        rig.ResetBonePoses();
        return rig;
    }

    // A clip that turns every named bone a quarter turn over its length.
    private static AnimationClipData Turn(float length, params int[] bones) => new()
    {
        Length = length,
        Bones = Curves(bones, new BoneCurves
        {
            Rotation = new[] { (0f, Quaternion.Identity), (length, new Quaternion(Vector3.Up, Mathf.Pi * 0.5f)) },
        }),
    };

    // A clip that pins every named bone to its rest rotation for its whole length — something to blend
    // INTO, or lay OVER, whose pose is known exactly.
    private static AnimationClipData Still(float length, params int[] bones) => new()
    {
        Length = length,
        Bones = Curves(bones, new BoneCurves
        {
            Rotation = new[] { (0f, Quaternion.Identity), (length, Quaternion.Identity) },
        }),
    };

    private static Dictionary<int, BoneCurves> Curves(int[] bones, BoneCurves curves)
    {
        var tracks = new Dictionary<int, BoneCurves>(bones.Length);
        foreach (int bone in bones)
            tracks[bone] = curves;
        return tracks;
    }

    // A clip that moves and scales `bone` rather than rotating it — the two channels the game's own clips
    // exercise least, and so the two most able to break quietly.
    private static AnimationClipData Moves(float length, int bone) => new()
    {
        Length = length,
        Bones = new Dictionary<int, BoneCurves>
        {
            [bone] = new BoneCurves
            {
                Position = new[] { (0f, Vector3.Zero), (length, Vector3.Up * 2f) },
                Scale = new[] { (0f, Vector3.One), (length, Vector3.One * 3f) },
            },
        },
    };

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
