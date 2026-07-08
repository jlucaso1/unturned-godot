using System.Collections.Generic;
using Godot;
using UnturnedGodot.Player;

namespace UnturnedGodot;

// The character's skeleton with its per-stance resting poses precomputed. Unturned's PlayerAnimator picks a
// clip by stance (standing branch of updateState): STAND/SPRINT -> Idle_Stand, CROUCH -> Idle_Crouch,
// PRONE -> Idle_Prone. Each stored pose is frame 0 of that legacy clip (converted Unity->Godot), applied to
// the bone poses over the bind rest. Moving clips (Move_Walk/Run/Crouch/Prone) are a follow-up — they need
// playback rather than a single frame.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class CharacterSkeleton : Skeleton3D
{
    // A single bone's pose override; any of the three channels may be absent in the clip.
    public readonly record struct BonePose(int Bone, Quaternion? Rotation, Vector3? Position, Vector3? Scale);

    private readonly Dictionary<string, IReadOnlyList<BonePose>> _clips = new();
    private string _current = "";

    public bool HasAnyPose => _clips.Count > 0;

    public void StorePose(string clip, IReadOnlyList<BonePose> pose) => _clips[clip] = pose;

    public void ApplyStance(EPlayerStance stance) => ApplyClip(stance switch
    {
        EPlayerStance.Crouch => "Idle_Crouch",
        EPlayerStance.Prone => "Idle_Prone",
        _ => "Idle_Stand", // STAND and SPRINT
    });

    public void ApplyClip(string clip)
    {
        if (clip == _current || !_clips.TryGetValue(clip, out IReadOnlyList<BonePose>? pose))
            return; // unchanged, or the clip isn't present -> keep the current pose
        _current = clip;
        ResetBonePoses(); // back to the bind pose, then layer this clip's overrides
        foreach (BonePose p in pose)
        {
            if (p.Rotation is { } r)
                SetBonePoseRotation(p.Bone, r);
            if (p.Position is { } pos)
                SetBonePosePosition(p.Bone, pos);
            if (p.Scale is { } s)
                SetBonePoseScale(p.Bone, s);
        }
    }
}
