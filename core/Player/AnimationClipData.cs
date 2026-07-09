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

    private readonly IReadOnlyDictionary<int, BoneCurves> _bones = new Dictionary<int, BoneCurves>();

    public IReadOnlyDictionary<int, BoneCurves> Bones
    {
        get => _bones;
        init
        {
            _bones = value;
            // Flatten once at decode time: the per-frame sampler walks this dense array with an indexed
            // loop — enumerating the IReadOnlyDictionary interface boxed an enumerator per rig per frame,
            // which was the process's dominant steady-state allocation.
            var tracks = new (int Bone, BoneCurves Curves)[value.Count];
            int i = 0;
            foreach (KeyValuePair<int, BoneCurves> kv in value)
                tracks[i++] = (kv.Key, kv.Value);
            Tracks = tracks;
        }
    }

    internal (int Bone, BoneCurves Curves)[] Tracks { get; private init; } =
        System.Array.Empty<(int, BoneCurves)>();
}
