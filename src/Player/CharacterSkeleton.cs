using System.Collections.Generic;
using Godot;
using UnturnedGodot.Player;

namespace UnturnedGodot;

// The character's skeleton with a small animation runtime. It holds the decoded legacy clips the entity's
// Animation component ships (Idle_/Move_ per stance) and, each frame, samples the current clip, crossfades
// from the previous one (Unturned's CharacterAnimator.state -> CrossFade over BLEND = 0.25 s), applies the
// pose over the bind rest, and bends the spine + skull toward the look pitch (HumanAnimator: each takes
// half the pitch). State selection follows PlayerAnimator.updateState: moving -> Move_<stance>, else
// Idle_<stance>, with STAND/SPRINT sharing the stand clips.
//
// There are TWO layers, as in the game. The movement state is layer 0 and never stops: updateState pushes
// it every frame whatever else is happening. A gesture — a punch, and every hand interaction after it —
// is layer 1, laid over the top through GestureOverlay, and MixAnimation is what says which bones it may
// write (Unturned's CharacterAnimator.mixAnimation -> AnimationState.AddMixingTransform). The punch clips
// are authored full-body, so that mask is the difference between a swing that moves an arm and a swing
// that stops the legs dead for its whole length.
//
// The per-frame engine boundary is kept minimal (profiled at ~5% of all samples before): _Process is only
// enabled while the rig is visible (no IsVisibleInTree poll per frame), rest poses are cached on the C#
// side so a full ResetBonePoses is never issued — only channels that stop being animated are reset — and
// the pitch bend composes with the sampled pose in C# instead of reading the bone back from the engine.
public partial class CharacterSkeleton : Skeleton3D
{
    private const float BlendDuration = 0.25f; // CharacterAnimator.BLEND

    private readonly Dictionary<string, AnimationClipData> _clips = new();
    private string _current = "";
    private float _time;
    private Dictionary<int, BonePose>? _fromPose; // frozen snapshot the crossfade blends out of (own copy)
    // Reused sample/blend buffers so the per-frame pose path allocates nothing (the sampler refills them).
    private readonly Dictionary<int, BonePose> _poseBuf = new();
    private readonly Dictionary<int, BonePose> _blendBuf = new();
    private readonly Dictionary<int, BonePose> _gestureBuf = new(); // layer 1's own sample buffer
    private readonly Dictionary<int, BonePose> _mergeBuf = new();   // the two layers composed
    private string _gestureBufClip = "";                            // which clip _gestureBuf holds
    private static readonly Dictionary<int, BonePose> Nothing = new(); // an empty pose; never written to
    private float _blend = 1f;
    private float _pitchBend; // Godot pitch degrees, split across spine + skull
    private int _spine = -1;
    private int _skull = -1;
    private bool _stateApplied;
    private EPlayerStance _lastStateStance;
    private bool _lastStateMoving;

    // Where the character looks from, read off the prefab rather than guessed: the bone that carries the
    // eye and where the eye sits IN that bone's own frame. It is emphatically not the bone's origin —
    // Skull sits at the neck, and the head's authored Spot child is most of half a metre further up.
    private int _eyeBone = -1;
    private Vector3 _eyeLocal;

    // Set on the one rig that is looked THROUGH rather than at, with the operator's manual nudge.
    private bool _anchorEye;
    private Vector3 _eyeNudge;

    // Layer 1: a one-shot laid over the movement state — a punch, and every hand interaction after it.
    // The playhead's own rules live in GestureOverlay, where they are unit-tested; this node owns the
    // sampling and the bones.
    private readonly GestureOverlay _gesture = new();

    // Which bones each gesture clip is allowed to write, by clip name — Unturned's mixAnimation calls,
    // made once per rig. A clip with no entry here overlays the whole body, which is what
    // `mixAnimation(name)` with no mixing transforms means.
    private readonly Dictionary<string, BoneMask?> _mixes = new();

    // C#-side rest cache + written-channel tracking: bones the previous frame animated and this frame
    // doesn't fall back to these rests, everything else is left untouched (it already rests).
    private const byte ChanRot = 1, ChanPos = 2, ChanScale = 4;
    private Quaternion[] _restRotations = System.Array.Empty<Quaternion>();
    private Vector3[] _restPositions = System.Array.Empty<Vector3>();
    private Vector3[] _restScales = System.Array.Empty<Vector3>();
    private byte[] _written = System.Array.Empty<byte>();  // channels applied by the previous frame
    private byte[] _writing = System.Array.Empty<byte>();  // channels applied by the current frame
    // Engine-side mirrors of every bone pose channel, so values that did not change this frame (most
    // position/scale tracks are constant, and idle rotations barely move) skip the marshaled write.
    private Quaternion[] _appliedRotations = System.Array.Empty<Quaternion>();
    private Vector3[] _appliedPositions = System.Array.Empty<Vector3>();
    private Vector3[] _appliedScales = System.Array.Empty<Vector3>();

    public bool HasAnyPose => _clips.Count > 0;

    // The decoded clip set, exposed so cheap clones can share it (clips are immutable once stored).
    public IReadOnlyDictionary<string, AnimationClipData> Clips => _clips;

    public void StoreClip(string name, AnimationClipData clip) => _clips[name] = clip;

    // The registered mixing transforms, exposed so a clone inherits them rather than swinging full-body.
    public IReadOnlyDictionary<string, BoneMask?> Mixes => _mixes;

    // CharacterAnimator.mixAnimation: puts `clip` on layer 1 and restricts it to the named shoulders (and
    // optionally the skull), recursively — so Punch_Left reaches Left_Shoulder, Left_Arm, Left_Hand and
    // Left_Hook, and nothing else. PlayerAnimator registers exactly this set once, when the player's rigs
    // are built; the port does it in CharacterModel for the same reason.
    //
    // All three false is the game's `mixAnimation(name)` overload: layer 1 with no mixing transforms at
    // all, which restricts nothing and overlays the whole body.
    public void MixAnimation(string clip, bool mixLeftShoulder, bool mixRightShoulder, bool mixSkull = false)
    {
        var roots = new List<string>(3);
        if (mixLeftShoulder)
            roots.Add("Left_Shoulder");
        if (mixRightShoulder)
            roots.Add("Right_Shoulder");
        if (mixSkull)
            roots.Add("Skull");
        MixAnimation(clip, Subtrees(roots));
    }

    // The same registration against an already-resolved mask (null = the whole body).
    public void MixAnimation(string clip, BoneMask? mask) => _mixes[clip] = mask;

    // The bones under the named ones, by name — a rig that does not carry one simply contributes nothing,
    // the way AddMixingTransform against a transform the skeleton does not have would.
    public BoneMask? Subtrees(IReadOnlyList<string> boneNames)
    {
        var roots = new List<int>(boneNames.Count);
        foreach (string name in boneNames)
        {
            int bone = FindBone(name);
            if (bone >= 0)
                roots.Add(bone);
        }
        if (roots.Count == 0)
            return null;

        int count = GetBoneCount();
        var parents = new int[count];
        for (int i = 0; i < count; i++)
            parents[i] = GetBoneParent(i);
        return BoneMask.Subtrees(parents, roots);
    }

    public void BindPitchBones(int spine, int skull)
    {
        _spine = spine;
        _skull = skull;
    }

    public (int Spine, int Skull) PitchBones => (_spine, _skull);

    // The eye this character looks from, as the prefab authors it (see CharacterModel: the Spot the game
    // pairs with its first-person camera). `local` is the offset within `bone`'s own frame.
    public void BindEye(int bone, Vector3 local)
    {
        _eyeBone = bone;
        _eyeLocal = local;
    }

    // The binding, so a clone of this rig inherits it rather than losing the eye with the copy.
    public (int Bone, Vector3 Local) Eye => (_eyeBone, _eyeLocal);

    // Rides this rig on its own eye, the way Unturned parents the first-person camera under the
    // viewmodel skeleton's head (ViewmodelAnchor has the reasoning). Re-applied after every pose,
    // because the stance clips move the head and a bind-pose offset only agrees with one of them.
    public void AnchorEyeToCamera(Vector3 nudge)
    {
        _anchorEye = true;
        _eyeNudge = nudge;
        AnchorEye();
    }

    // One skeleton read per posed frame, on the single rig the local player looks through — the
    // interop budget this class otherwise guards is per-BONE, and this is per-rig.
    private void AnchorEye()
    {
        if (!_anchorEye || _eyeBone < 0)
            return;
        Position = ViewmodelAnchor.RigPosition(
            ViewmodelAnchor.Eye(GetBoneGlobalPose(_eyeBone), _eyeLocal), _eyeNudge);
    }

    // Godot pitch degrees (0 = horizon, + up, - down); the body leans toward where the player looks.
    public void SetPitch(float godotPitchDegrees) => _pitchBend = godotPitchDegrees;

    // Picks and crossfades to the clip for a stance (Unturned's PlayerAnimator.updateState mapping).
    // Keep the discrete state dirty: local and remote callers commonly invoke this from their render or
    // physics loop, but the clip name cannot change while stance/movement are unchanged.
    // Returns false when the character carries no clip for that stance.
    //
    // A gesture never blocks this. updateState runs every frame regardless of what layer 1 is doing, and
    // the movement clip it chooses goes on running underneath the swing — which is exactly what stops a
    // punch from freezing the legs.
    public bool SetState(EPlayerStance stance, bool moving)
    {
        if (_stateApplied && _lastStateStance == stance && _lastStateMoving == moving)
            return true;

        string clip = ClipFor(stance, moving);
        if (!_clips.ContainsKey(clip))
            return false;
        Play(clip);
        _stateApplied = true;
        _lastStateStance = stance;
        _lastStateMoving = moving;
        return true;
    }

    // Lays a clip over the movement state for its own length, restricted to whatever MixAnimation
    // registered for it (CharacterAnimator.play with smooth: false, which is what PlayerEquipment.punch
    // uses — the swing starts at full weight on the frame the button went down, no fade in).
    //
    // Returns false when the character carries no such clip, which is how a gesture the entity cannot
    // perform is skipped rather than freezing it on a missing animation.
    public bool PlayOnce(string clip)
    {
        if (!_clips.TryGetValue(clip, out AnimationClipData? data) || data.Length <= 0f)
            return false;
        if (!_gesture.Begin(clip, data.Length, _mixes.GetValueOrDefault(clip)))
            return false;

        // A gesture alone is reason enough to sample: a rig parked on no movement clip at all still has
        // to run the swing. This can also drop the gesture again — a hidden rig does not swing — so the
        // answer is whether one is actually playing rather than whether the clip was there.
        UpdateProcessing();
        return _gesture.IsPlaying;
    }

    // True while a one-shot gesture is laid over the movement state.
    public bool IsPlayingOnce => _gesture.IsPlaying;

    // Restarts a clip from its beginning even when it is already the current one — an attack swing
    // re-triggering, where Play's already-playing early-out would swallow the restart.
    public void Replay(string clip)
    {
        if (clip == _current && _clips.ContainsKey(clip))
        {
            _fromPose = new Dictionary<int, BonePose>(CurrentPose());
            _time = 0f;
            _blend = 0f;
            return;
        }
        Play(clip);
    }

    public void Play(string clip)
    {
        if (clip == _current || !_clips.ContainsKey(clip))
            return; // already playing, or the clip isn't present -> keep going
        // The gesture is deliberately NOT dropped here: layer 0 and layer 1 are independent, and
        // Unturned's `state()` crossfade leaves whatever `play()` put on layer 1 alone.
        _stateApplied = false;
        // Snapshot to blend out of — an owned copy, since CurrentPose refills the shared buffer every frame.
        _fromPose = _current.Length > 0 ? new Dictionary<int, BonePose>(CurrentPose()) : null;
        _current = clip;
        _poseBuf.Clear(); // the new clip's bone set differs: flush before the overwrite sampling
        _time = 0f;
        _blend = _fromPose == null ? 1f : 0f;
        UpdateProcessing();
    }

    // Jumps to an absolute time and poses immediately (used to inspect a frame off-line; in play the
    // animation advances by real delta in _Process).
    //
    // BOTH playheads move, because this means "show the rig at time t" and a gesture armed just before it
    // began at zero alongside the movement clip. Moving only the movement layer would answer CHAR_ANIM_TIME
    // for a swing with the swing's opening frame however late the time asked for — the whole point of the
    // CHAR_GESTURE screenshot path is to read a chosen frame OF the swing.
    public void Seek(float time)
    {
        if (_current.Length == 0 && !_gesture.IsPlaying)
            return; // nothing selected to seek within (a character that carries no clip at all)
        _time = time;
        _blend = 1f;
        _gesture.SeekTo(time);
        Apply(Compose(_current.Length > 0 ? CurrentPose() : null));
    }

    // _Process only runs while a clip is active AND the rig is effectively visible — checked on visibility
    // notifications instead of an engine IsVisibleInTree call every frame. In first person the whole body
    // is hidden, so the ~48 marshaled bone writes stop entirely; the pose (and the animation clock, which
    // also froze before) resumes on the first visible frame.
    private void UpdateProcessing()
    {
        bool visible = IsVisibleInTree();
        // A rig that stops being drawn stops advancing its clocks, so a gesture caught mid-swing would
        // freeze and then thaw — replaying a stale punch from where it stopped the next time the rig is
        // shown. Drop it instead: a swing nobody can see need not finish, and the movement layer
        // underneath is already the pose it would have come back to.
        //
        // "Stops being drawn" is the rule, and a rig that is not in the tree YET has not started: it is
        // being set up, and IsVisibleInTree is false for that too. Cancelling there would throw away a
        // gesture armed before the rig was parented — which is exactly what the CHAR_GESTURE screenshot
        // path does, and it would silently shoot the stance instead of the swing.
        if (IsInsideTree() && !visible)
            _gesture.Cancel();
        SetProcess(visible && (_current.Length > 0 || _gesture.IsPlaying));
        // A rig that was hidden froze its clock, so the pose it comes back with is the one it left.
        // Anchor before the first drawn frame rather than one frame into it.
        AnchorEye();
    }

    public override void _Notification(int what)
    {
        // READY is in this list because the engine turns _Process back ON there: Node's NOTIFICATION_READY
        // handler calls set_process(true) for any script that overrides _process, silently undoing the
        // SetProcess(false) the ENTER_TREE above had just made. The script's own notification runs after
        // that, so reconciling here is what sticks.
        //
        // Without it, a rig that entered the tree with no clip selected sampled _clips[""] on its first
        // drawn frame. Every Clone is such a rig — the clips travel with the copy but the playhead does
        // not — and the first-person Viewmodel is the one that reached a frame that way: PlayerController
        // attaches it in _Ready and only picks its clip from _PhysicsProcess, so a cold load, whose frames
        // run while the world is still streaming in, lands a render frame in between.
        if (what == NotificationVisibilityChanged || what == NotificationEnterTree || what == NotificationReady)
            UpdateProcessing();
    }

    public override void _Process(double delta)
    {
        // Neither layer has anything to sample. UpdateProcessing normally keeps _Process off for this
        // state, so reaching it means something outside this class turned processing back on (the engine
        // does exactly that at NOTIFICATION_READY). Settle it rather than index _clips[""].
        if (_current.Length == 0 && !_gesture.IsPlaying)
        {
            SetProcess(false);
            return;
        }

        // Layer 1 first, so the frame a swing runs out on is already the movement pose alone rather than
        // one more frame held on the gesture's last key.
        bool ended = _gesture.Advance((float)delta);

        Dictionary<int, BonePose>? pose = null;
        if (_current.Length > 0)
        {
            _time += (float)delta;
            pose = CurrentPose();
            if (_blend < 1f && _fromPose != null)
            {
                _blend = Mathf.Min(1f, _blend + ((float)delta / BlendDuration));
                AnimationSampler.Blend(_fromPose, pose, _blend, _blendBuf);
                pose = _blendBuf;
            }
        }

        Apply(Compose(pose));
        if (ended)
            UpdateProcessing(); // a rig running the swing alone has nothing left to sample
    }

    // The two layers as one pose: the movement clip, with the gesture laid over the bones its mixing
    // transforms cover. `under` is null for a rig that carries no movement clip at all, where the
    // gesture is the only thing posing it.
    private Dictionary<int, BonePose> Compose(Dictionary<int, BonePose>? under)
    {
        if (!_gesture.IsPlaying)
            return under ?? Nothing; // no layer left: every channel written last frame falls to its rest

        AnimationClipData clip = _clips[_gesture.Clip];
        if (_gestureBufClip != _gesture.Clip)
        {
            _gestureBuf.Clear(); // a different clip's bone set: flush before the overwrite sampling
            _gestureBufClip = _gesture.Clip;
        }
        // The gesture's clock is NOT wrapped — it plays exactly once and the sampler clamps past the
        // last key, which is what an Animation.Play of a non-looping clip does.
        AnimationSampler.SampleOverwrite(clip, _gesture.Time, _gestureBuf);
        // `Nothing` for a rig with no movement clip: the mask still applies, so the bones outside it fall
        // to their rests rather than taking a swing they were never meant to see.
        AnimationSampler.Overlay(under ?? Nothing, _gestureBuf, _gesture.Mask, _mergeBuf);
        return _mergeBuf;
    }

    private Dictionary<int, BonePose> CurrentPose()
    {
        AnimationClipData clip = _clips[_current];
        float t = clip.Length > 0f ? _time % clip.Length : 0f;
        // Overwrite in place: the buffer holds exactly this clip's bones (Play clears it on switch),
        // so the per-frame dictionary Clear (bucket zeroing) is skipped entirely.
        AnimationSampler.SampleOverwrite(clip, t, _poseBuf);
        return _poseBuf;
    }

    private void Apply(Dictionary<int, BonePose> pose)
    {
        EnsureRestCache();
        float half = _pitchBend * 0.5f;

        // Write this frame's channels. The pitch bend is composed here, over the sampled rotation (or the
        // rest when the clip doesn't animate that bone) — exactly what reset-then-rotate produced, without
        // reading the bone back from the engine.
        System.Array.Clear(_writing, 0, _writing.Length);
        foreach (BonePose p in pose.Values)
        {
            byte channels = 0;
            if (p.Rotation is { } r)
            {
                WriteRotation(p.Bone, WithPitch(p.Bone, r, half));
                channels |= ChanRot;
            }
            if (p.Position is { } pos)
            {
                if (pos != _appliedPositions[p.Bone])
                {
                    SetBonePosePosition(p.Bone, pos);
                    _appliedPositions[p.Bone] = pos;
                }
                channels |= ChanPos;
            }
            if (p.Scale is { } s)
            {
                if (s != _appliedScales[p.Bone])
                {
                    SetBonePoseScale(p.Bone, s);
                    _appliedScales[p.Bone] = s;
                }
                channels |= ChanScale;
            }
            _writing[p.Bone] = channels;
        }

        // Pitch bones the clip didn't rotate this frame still bend, starting from their rest.
        BendUnanimated(_spine, half);
        BendUnanimated(_skull, half);

        // Channels the previous frame animated but this one didn't fall back to the bind rest — the
        // incremental form of the old full ResetBonePoses.
        for (int bone = 0; bone < _written.Length; bone++)
        {
            byte stale = (byte)(_written[bone] & ~_writing[bone]);
            if (stale == 0)
                continue;
            if ((stale & ChanRot) != 0)
                WriteRotation(bone, _restRotations[bone]);
            if ((stale & ChanPos) != 0 && _appliedPositions[bone] != _restPositions[bone])
            {
                SetBonePosePosition(bone, _restPositions[bone]);
                _appliedPositions[bone] = _restPositions[bone];
            }
            if ((stale & ChanScale) != 0 && _appliedScales[bone] != _restScales[bone])
            {
                SetBonePoseScale(bone, _restScales[bone]);
                _appliedScales[bone] = _restScales[bone];
            }
        }

        (_written, _writing) = (_writing, _written);

        // The pose has just moved the head; the eye follows it in the same frame it moved.
        AnchorEye();
    }

    private Quaternion WithPitch(int bone, Quaternion sampled, float halfDegrees)
    {
        if (bone != _spine && bone != _skull)
            return sampled;
        // HumanAnimator does spine.Rotate(0, pitch*0.5, 0) + skull.Rotate(0, pitch*0.5, 0) — local Y,
        // which after the bones' ~-90° Z rest maps to the character's left-right axis, i.e. a pitch nod.
        return sampled * new Quaternion(Vector3.Up, Mathf.DegToRad(halfDegrees));
    }

    private void BendUnanimated(int bone, float halfDegrees)
    {
        if (bone < 0 || (_writing[bone] & ChanRot) != 0)
            return; // the pose already rotated it (pitch composed in WithPitch)
        WriteRotation(bone, _restRotations[bone] * new Quaternion(Vector3.Up, Mathf.DegToRad(halfDegrees)));
        _writing[bone] |= ChanRot;
    }

    private void WriteRotation(int bone, Quaternion value)
    {
        if (value == _appliedRotations[bone])
            return;
        SetBonePoseRotation(bone, value);
        _appliedRotations[bone] = value;
    }

    // One-time engine read of every bone's rest transform (the clone starts empty, so each instance
    // fills its own copy on first apply). The flag keeps the per-frame check free of interop:
    // GetBoneCount itself is a C#->Godot call and measured at several percent of the trace.
    private bool _restCached;

    private void EnsureRestCache()
    {
        if (_restCached)
            return;
        int count = GetBoneCount();
        if (count == 0)
            return; // rig not assembled yet; try again next apply
        _restCached = true;
        _restRotations = new Quaternion[count];
        _restPositions = new Vector3[count];
        _restScales = new Vector3[count];
        _written = new byte[count];
        _writing = new byte[count];
        for (int i = 0; i < count; i++)
        {
            Transform3D rest = GetBoneRest(i);
            _restRotations[i] = rest.Basis.GetRotationQuaternion();
            _restPositions[i] = rest.Origin;
            _restScales[i] = rest.Basis.Scale;
        }
        // The skeleton's poses ARE the rests here (the builder ResetBonePoses after assembly, and every
        // change since went through these mirrors), so the mirrors start as a copy of the rests.
        _appliedRotations = (Quaternion[])_restRotations.Clone();
        _appliedPositions = (Vector3[])_restPositions.Clone();
        _appliedScales = (Vector3[])_restScales.Clone();
    }

    private static string ClipFor(EPlayerStance stance, bool moving) => (stance, moving) switch
    {
        // PlayerAnimator.updateState puts climbing first: a player on a ladder has its own pair of clips.
        (EPlayerStance.Climb, true) => "Move_Climb",
        (EPlayerStance.Climb, false) => "Idle_Climb",
        (EPlayerStance.Sprint, true) => "Move_Run",
        (EPlayerStance.Stand, true) => "Move_Walk",
        (EPlayerStance.Crouch, true) => "Move_Crouch",
        (EPlayerStance.Prone, true) => "Move_Prone",
        (EPlayerStance.Crouch, false) => "Idle_Crouch",
        (EPlayerStance.Prone, false) => "Idle_Prone",
        _ => "Idle_Stand", // STAND / SPRINT idle
    };
}
