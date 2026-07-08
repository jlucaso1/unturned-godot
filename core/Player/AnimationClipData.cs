using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Player;

// A legacy Unity AnimationClip decoded into Godot space: per-bone keyframe tracks and the clip length. Built
// once from the entity's Animation component; sampled every frame by AnimationSampler.
public sealed class BoneCurves
{
    public (float Time, Quaternion Value)[] Rotation { get; init; } = System.Array.Empty<(float, Quaternion)>();
    public (float Time, Vector3 Value)[] Position { get; init; } = System.Array.Empty<(float, Vector3)>();
    public (float Time, Vector3 Value)[] Scale { get; init; } = System.Array.Empty<(float, Vector3)>();
}

public sealed class AnimationClipData
{
    public float Length { get; init; }
    public IReadOnlyDictionary<int, BoneCurves> Bones { get; init; } = new Dictionary<int, BoneCurves>();
}
