using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Player;

// Samples a decoded AnimationClipData at a time (rotations slerped, positions/scales lerped between
// keyframes — Unity authors with cubic tangents, but linear between the game's dense keys is visually close),
// and blends two poses for crossfading between clips. Pure, so playback correctness is unit-tested.
public static class AnimationSampler
{
    public static Dictionary<int, BonePose> Sample(AnimationClipData clip, float time)
    {
        var pose = new Dictionary<int, BonePose>(clip.Bones.Count);
        foreach (KeyValuePair<int, BoneCurves> b in clip.Bones)
        {
            BoneCurves c = b.Value;
            pose[b.Key] = new BonePose(b.Key,
                c.Rotation.Length > 0 ? SampleRotation(c.Rotation, time) : null,
                c.Position.Length > 0 ? SampleVector(c.Position, time) : null,
                c.Scale.Length > 0 ? SampleVector(c.Scale, time) : null);
        }
        return pose;
    }

    public static Quaternion SampleRotation((float Time, Quaternion Value)[] keys, float time)
    {
        if (time <= keys[0].Time)
            return keys[0].Value;
        if (time >= keys[^1].Time)
            return keys[^1].Value;
        int i = 1;
        while (keys[i].Time < time)
            i++;
        float f = (time - keys[i - 1].Time) / (keys[i].Time - keys[i - 1].Time);
        return keys[i - 1].Value.Slerp(keys[i].Value, f);
    }

    public static Vector3 SampleVector((float Time, Vector3 Value)[] keys, float time)
    {
        if (time <= keys[0].Time)
            return keys[0].Value;
        if (time >= keys[^1].Time)
            return keys[^1].Value;
        int i = 1;
        while (keys[i].Time < time)
            i++;
        float f = (time - keys[i - 1].Time) / (keys[i].Time - keys[i - 1].Time);
        return keys[i - 1].Value.Lerp(keys[i].Value, f);
    }

    // Blends two sampled poses (t: 0 = from, 1 = to) per bone and channel. Bones present in only one pose are
    // carried as-is (matching clips animate the same bones, so this is the common, clean case).
    public static Dictionary<int, BonePose> Blend(
        Dictionary<int, BonePose> from, Dictionary<int, BonePose> to, float t)
    {
        var result = new Dictionary<int, BonePose>(to.Count);
        var bones = new HashSet<int>(from.Keys);
        bones.UnionWith(to.Keys);
        foreach (int bone in bones)
        {
            from.TryGetValue(bone, out BonePose a);
            to.TryGetValue(bone, out BonePose b);
            result[bone] = new BonePose(bone,
                BlendRotation(a.Rotation, b.Rotation, t),
                BlendVector(a.Position, b.Position, t),
                BlendVector(a.Scale, b.Scale, t));
        }
        return result;
    }

    private static Quaternion? BlendRotation(Quaternion? a, Quaternion? b, float t)
    {
        if (a is { } av && b is { } bv)
            return av.Slerp(bv, t);
        return b ?? a;
    }

    private static Vector3? BlendVector(Vector3? a, Vector3? b, float t)
    {
        if (a is { } av && b is { } bv)
            return av.Lerp(bv, t);
        return b ?? a;
    }
}
