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
        Sample(clip, time, pose);
        return pose;
    }

    // Buffer-reusing variant: clears and refills dest, so a per-frame caller keeps one dictionary alive
    // instead of allocating a fresh one every sampled frame.
    public static void Sample(AnimationClipData clip, float time, Dictionary<int, BonePose> dest)
    {
        dest.Clear();
        foreach (KeyValuePair<int, BoneCurves> b in clip.Bones)
        {
            BoneCurves c = b.Value;
            dest[b.Key] = new BonePose(b.Key,
                c.Rotation.Length > 0 ? SampleRotation(c.Rotation, time) : null,
                c.Position.Length > 0 ? SampleVector(c.Position, time) : null,
                c.Scale.Length > 0 ? SampleVector(c.Scale, time) : null);
        }
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
        Blend(from, to, t, result);
        return result;
    }

    // Buffer-reusing variant: clears and refills dest. Two passes replace the old key-union HashSet — first
    // every bone in `to` (blended against `from` when present), then the `from`-only bones carried as-is
    // (their `to` side is the all-null default, so the channel blend passes the `from` values through).
    public static void Blend(
        Dictionary<int, BonePose> from, Dictionary<int, BonePose> to, float t, Dictionary<int, BonePose> dest)
    {
        dest.Clear();
        foreach (KeyValuePair<int, BonePose> kv in to)
        {
            from.TryGetValue(kv.Key, out BonePose a);
            dest[kv.Key] = new BonePose(kv.Key,
                BlendRotation(a.Rotation, kv.Value.Rotation, t),
                BlendVector(a.Position, kv.Value.Position, t),
                BlendVector(a.Scale, kv.Value.Scale, t));
        }
        foreach (KeyValuePair<int, BonePose> kv in from)
            if (!to.ContainsKey(kv.Key))
                dest[kv.Key] = new BonePose(kv.Key,
                    BlendRotation(kv.Value.Rotation, null, t),
                    BlendVector(kv.Value.Position, null, t),
                    BlendVector(kv.Value.Scale, null, t));
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
