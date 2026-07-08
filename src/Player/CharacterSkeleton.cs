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
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class CharacterSkeleton : Skeleton3D
{
    private const float BlendDuration = 0.25f; // CharacterAnimator.BLEND

    private readonly Dictionary<string, AnimationClipData> _clips = new();
    private string _current = "";
    private float _time;
    private Dictionary<int, BonePose>? _fromPose; // frozen snapshot the crossfade blends out of
    private float _blend = 1f;
    private float _pitchBend; // Godot pitch degrees, split across spine + skull
    private int _spine = -1;
    private int _skull = -1;

    public bool HasAnyPose => _clips.Count > 0;

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
        _fromPose = _current.Length > 0 ? CurrentPose() : null; // snapshot to blend out of
        _current = clip;
        _time = 0f;
        _blend = _fromPose == null ? 1f : 0f;
    }

    // Jumps the current clip to an absolute time and poses immediately (used to inspect a frame off-line;
    // in play the animation advances by real delta in _Process).
    public void Seek(float time)
    {
        _time = time;
        _blend = 1f;
        Apply(CurrentPose());
    }

    public override void _Process(double delta)
    {
        if (_current.Length == 0)
            return;
        _time += (float)delta;

        Dictionary<int, BonePose> pose = CurrentPose();
        if (_blend < 1f && _fromPose != null)
        {
            _blend = Mathf.Min(1f, _blend + ((float)delta / BlendDuration));
            pose = AnimationSampler.Blend(_fromPose, pose, _blend);
        }
        Apply(pose);
    }

    private Dictionary<int, BonePose> CurrentPose()
    {
        AnimationClipData clip = _clips[_current];
        float t = clip.Length > 0f ? _time % clip.Length : 0f; // loop
        return AnimationSampler.Sample(clip, t);
    }

    private void Apply(Dictionary<int, BonePose> pose)
    {
        ResetBonePoses(); // bind rest, then layer the clip's channels
        foreach (BonePose p in pose.Values)
        {
            if (p.Rotation is { } r)
                SetBonePoseRotation(p.Bone, r);
            if (p.Position is { } pos)
                SetBonePosePosition(p.Bone, pos);
            if (p.Scale is { } s)
                SetBonePoseScale(p.Bone, s);
        }

        // Bend the upper body toward the look pitch: spine and skull each take half (HumanAnimator does
        // spine.Rotate(0, _pitch*0.5, 0) + skull.Rotate(0, _pitch*0.5, 0) — local Y, which after the bones'
        // ~-90° Z rest maps to the character's left-right axis, i.e. a pitch nod).
        float half = _pitchBend * 0.5f;
        BendPitch(_spine, half);
        BendPitch(_skull, half);
    }

    // Rotates a bone about its local Y (the pitch axis after the Unity->Godot conversion) on top of its pose.
    private void BendPitch(int bone, float degrees)
    {
        if (bone < 0)
            return;
        var delta = new Quaternion(Vector3.Up, Mathf.DegToRad(degrees));
        SetBonePoseRotation(bone, GetBonePoseRotation(bone) * delta);
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
