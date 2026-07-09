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
// The per-frame engine boundary is kept minimal (profiled at ~5% of all samples before): _Process is only
// enabled while the rig is visible (no IsVisibleInTree poll per frame), rest poses are cached on the C#
// side so a full ResetBonePoses is never issued — only channels that stop being animated are reset — and
// the pitch bend composes with the sampled pose in C# instead of reading the bone back from the engine.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
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
    private float _blend = 1f;
    private float _pitchBend; // Godot pitch degrees, split across spine + skull
    private int _spine = -1;
    private int _skull = -1;

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

    public void BindPitchBones(int spine, int skull)
    {
        _spine = spine;
        _skull = skull;
    }

    // Godot pitch degrees (0 = horizon, + up, - down); the body leans toward where the player looks.
    public void SetPitch(float godotPitchDegrees) => _pitchBend = godotPitchDegrees;

    // Picks and crossfades to the clip for a stance (Unturned's PlayerAnimator.updateState mapping).
    public void SetState(EPlayerStance stance, bool moving) => Play(ClipFor(stance, moving));

    public void Play(string clip)
    {
        if (clip == _current || !_clips.ContainsKey(clip))
            return; // already playing, or the clip isn't present -> keep going
        // Snapshot to blend out of — an owned copy, since CurrentPose refills the shared buffer every frame.
        _fromPose = _current.Length > 0 ? new Dictionary<int, BonePose>(CurrentPose()) : null;
        _current = clip;
        _time = 0f;
        _blend = _fromPose == null ? 1f : 0f;
        UpdateProcessing();
    }

    // Jumps the current clip to an absolute time and poses immediately (used to inspect a frame off-line;
    // in play the animation advances by real delta in _Process).
    public void Seek(float time)
    {
        _time = time;
        _blend = 1f;
        Apply(CurrentPose());
    }

    // _Process only runs while a clip is active AND the rig is effectively visible — checked on visibility
    // notifications instead of an engine IsVisibleInTree call every frame. In first person the whole body
    // is hidden, so the ~48 marshaled bone writes stop entirely; the pose (and the animation clock, which
    // also froze before) resumes on the first visible frame.
    private void UpdateProcessing() => SetProcess(_current.Length > 0 && IsVisibleInTree());

    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged || what == NotificationEnterTree)
            UpdateProcessing();
    }

    public override void _Process(double delta)
    {
        _time += (float)delta;

        Dictionary<int, BonePose> pose = CurrentPose();
        if (_blend < 1f && _fromPose != null)
        {
            _blend = Mathf.Min(1f, _blend + ((float)delta / BlendDuration));
            AnimationSampler.Blend(_fromPose, pose, _blend, _blendBuf);
            pose = _blendBuf;
        }
        Apply(pose);
    }

    private Dictionary<int, BonePose> CurrentPose()
    {
        AnimationClipData clip = _clips[_current];
        float t = clip.Length > 0f ? _time % clip.Length : 0f; // loop
        AnimationSampler.Sample(clip, t, _poseBuf);
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
        (EPlayerStance.Sprint, true) => "Move_Run",
        (EPlayerStance.Stand, true) => "Move_Walk",
        (EPlayerStance.Crouch, true) => "Move_Crouch",
        (EPlayerStance.Prone, true) => "Move_Prone",
        (EPlayerStance.Crouch, false) => "Idle_Crouch",
        (EPlayerStance.Prone, false) => "Idle_Prone",
        _ => "Idle_Stand", // STAND / SPRINT idle
    };
}
