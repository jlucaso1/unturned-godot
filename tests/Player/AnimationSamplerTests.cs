using System.Collections.Generic;
using Godot;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

public class AnimationSamplerTests
{
    [Fact]
    public void SampleVector_ClampsBeforeFirstAndAfterLast()
    {
        var keys = new[] { (0f, new Vector3(0, 0, 0)), (1f, new Vector3(10, 0, 0)) };
        Assert.Equal(new Vector3(0, 0, 0), AnimationSampler.SampleVector(keys, -5f));
        Assert.Equal(new Vector3(10, 0, 0), AnimationSampler.SampleVector(keys, 5f));
    }

    [Fact]
    public void SampleVector_LerpsBetweenKeys()
    {
        var keys = new[] { (0f, new Vector3(0, 0, 0)), (2f, new Vector3(10, 0, 0)) };
        Assert.Equal(new Vector3(2.5f, 0, 0), AnimationSampler.SampleVector(keys, 0.5f));
    }

    [Fact]
    public void SampleRotation_SlerpsBetweenKeys()
    {
        Quaternion a = Quaternion.Identity;
        Quaternion b = new Quaternion(Vector3.Up, Mathf.Pi / 2f); // 90° yaw
        var keys = new[] { (0f, a), (1f, b) };
        Quaternion mid = AnimationSampler.SampleRotation(keys, 0.5f);
        Assert.Equal(a.Slerp(b, 0.5f), mid);
    }

    [Fact]
    public void SampleRotation_ClampsAtEnds()
    {
        var keys = new[] { (0f, Quaternion.Identity), (1f, new Quaternion(Vector3.Up, 1f)) };
        Assert.Equal(keys[0].Item2, AnimationSampler.SampleRotation(keys, -1f));
        Assert.Equal(keys[1].Item2, AnimationSampler.SampleRotation(keys, 2f));
    }

    [Fact]
    public void Sample_AdvancesAcrossMultipleSegments()
    {
        // Three keys: sampling in the last segment forces the keyframe search to step past the first.
        var v = new[] { (0f, new Vector3(0, 0, 0)), (1f, new Vector3(10, 0, 0)), (2f, new Vector3(20, 0, 0)) };
        Assert.Equal(new Vector3(15, 0, 0), AnimationSampler.SampleVector(v, 1.5f));

        var r = new[]
        {
            (0f, Quaternion.Identity),
            (1f, new Quaternion(Vector3.Up, 0.5f)),
            (2f, new Quaternion(Vector3.Up, 1f)),
        };
        Assert.Equal(r[1].Item2.Slerp(r[2].Item2, 0.5f), AnimationSampler.SampleRotation(r, 1.5f));
    }

    [Fact]
    public void SampleOverwrite_UpdatesInPlace_AndKeepsForeignKeys()
    {
        // The steady-playback variant rewrites the clip's own bones but does NOT clear: keys from a
        // previous clip survive (which is exactly why Play() flushes the buffer on a clip switch),
        // while the clearing Sample removes them.
        var clip = new AnimationClipData
        {
            Length = 1f,
            Bones = new Dictionary<int, BoneCurves>
            {
                [1] = new BoneCurves { Position = new[] { (0f, Vector3.Zero), (1f, new Vector3(10, 0, 0)) } },
            },
        };
        var buf = new Dictionary<int, BonePose> { [9] = new(9, null, new Vector3(5, 5, 5), null) };

        AnimationSampler.SampleOverwrite(clip, 0.5f, buf);
        Assert.Equal(new Vector3(5, 0, 0), buf[1].Position);
        Assert.True(buf.ContainsKey(9)); // stale key survives the overwrite...

        AnimationSampler.Sample(clip, 0.5f, buf);
        Assert.False(buf.ContainsKey(9)); // ...and the clearing variant flushes it
        Assert.Equal(new Vector3(5, 0, 0), buf[1].Position);
    }

    [Fact]
    public void Blend_CarriesChannelPresentInOnlyOnePose()
    {
        var from = new Dictionary<int, BonePose> { [1] = new(1, null, new Vector3(3, 0, 0), null) };
        var to = new Dictionary<int, BonePose> { [2] = new(2, null, new Vector3(9, 0, 0), null) };
        Dictionary<int, BonePose> blended = AnimationSampler.Blend(from, to, 0.5f);
        Assert.Equal(new Vector3(3, 0, 0), blended[1].Position); // only in 'from' -> keep it
        Assert.Equal(new Vector3(9, 0, 0), blended[2].Position); // only in 'to' -> keep it
    }

    [Fact]
    public void Sample_ProducesEachChannelIndependently()
    {
        var clip = new AnimationClipData
        {
            Length = 1f,
            Bones = new Dictionary<int, BoneCurves>
            {
                [2] = new BoneCurves { Rotation = new[] { (0f, Quaternion.Identity) } }, // rotation only
                [3] = new BoneCurves // position + scale, no rotation
                {
                    Position = new[] { (0f, new Vector3(1, 2, 3)) },
                    Scale = new[] { (0f, Vector3.One) },
                },
            },
        };
        Dictionary<int, BonePose> pose = AnimationSampler.Sample(clip, 0f);
        Assert.True(pose[2].Rotation.HasValue);
        Assert.Null(pose[2].Position);
        Assert.Null(pose[2].Scale);
        Assert.Null(pose[3].Rotation);
        Assert.True(pose[3].Position.HasValue && pose[3].Scale.HasValue);
    }

    [Fact]
    public void Blend_SlerpsRotationPresentInBoth()
    {
        Quaternion q = new Quaternion(Vector3.Up, 1f);
        var from = new Dictionary<int, BonePose> { [1] = new(1, Quaternion.Identity, null, null) };
        var to = new Dictionary<int, BonePose> { [1] = new(1, q, null, null) };
        Assert.Equal(Quaternion.Identity.Slerp(q, 0.5f), AnimationSampler.Blend(from, to, 0.5f)[1].Rotation);
    }

    [Fact]
    public void Blend_EndpointsReturnFromAndTo()
    {
        var from = new Dictionary<int, BonePose> { [1] = new(1, null, new Vector3(0, 0, 0), null) };
        var to = new Dictionary<int, BonePose> { [1] = new(1, null, new Vector3(10, 0, 0), null) };

        Assert.Equal(new Vector3(0, 0, 0), AnimationSampler.Blend(from, to, 0f)[1].Position);
        Assert.Equal(new Vector3(10, 0, 0), AnimationSampler.Blend(from, to, 1f)[1].Position);
        Assert.Equal(new Vector3(5, 0, 0), AnimationSampler.Blend(from, to, 0.5f)[1].Position);
    }

    [Fact]
    public void Blend_CarriesBonesPresentInOnlyOnePose()
    {
        var from = new Dictionary<int, BonePose> { [1] = new(1, Quaternion.Identity, null, null) };
        var to = new Dictionary<int, BonePose> { [2] = new(2, Quaternion.Identity, null, null) };
        Dictionary<int, BonePose> blended = AnimationSampler.Blend(from, to, 0.5f);
        Assert.True(blended[1].Rotation.HasValue); // only in 'from'
        Assert.True(blended[2].Rotation.HasValue); // only in 'to'
    }

    // --- layering ---------------------------------------------------------------------------------

    // A punch is a full-body clip restricted to one arm. The masked bones take the swing; every other one
    // keeps the movement clip's pose, which is what lets the legs go on walking through it.
    [Fact]
    public void Overlay_TakesTheMaskedBonesAndLeavesTheRest()
    {
        Quaternion swing = new(Vector3.Up, Mathf.Pi / 2f);
        var walk = new Dictionary<int, BonePose>
        {
            [1] = new(1, Quaternion.Identity, null, null), // an arm the mask covers
            [2] = new(2, Quaternion.Identity, null, null), // a leg it does not
        };
        var punch = new Dictionary<int, BonePose>
        {
            [1] = new(1, swing, null, null),
            [2] = new(2, swing, null, null), // authored, but masked out
        };

        var dest = new Dictionary<int, BonePose>();
        AnimationSampler.Overlay(walk, punch, BoneMask.Subtrees(new[] { -1, -1, -1 }, new[] { 1 }), dest);

        Assert.Equal(swing, dest[1].Rotation);
        Assert.Equal(Quaternion.Identity, dest[2].Rotation);
    }

    // A layer only overrides the curves it carries: a gesture that rotates a bone without moving it lets
    // the movement clip's position track through rather than dropping the bone back to its rest.
    [Fact]
    public void Overlay_FallsThroughPerChannel()
    {
        var under = new Dictionary<int, BonePose>
        {
            [1] = new(1, Quaternion.Identity, new Vector3(0, 3, 0), new Vector3(2, 2, 2)),
        };
        var over = new Dictionary<int, BonePose>
        {
            [1] = new(1, new Quaternion(Vector3.Up, 1f), null, null), // rotation only
        };

        var dest = new Dictionary<int, BonePose>();
        AnimationSampler.Overlay(under, over, mask: null, dest);

        Assert.Equal(new Quaternion(Vector3.Up, 1f), dest[1].Rotation);
        Assert.Equal(new Vector3(0, 3, 0), dest[1].Position);
        Assert.Equal(new Vector3(2, 2, 2), dest[1].Scale);
    }

    // No mask is Unturned's mixAnimation with no mixing transforms: the gesture covers everything.
    [Fact]
    public void Overlay_WithNoMaskCoversEveryBone()
    {
        Quaternion swing = new(Vector3.Up, Mathf.Pi / 2f);
        var under = new Dictionary<int, BonePose> { [1] = new(1, Quaternion.Identity, null, null) };
        var over = new Dictionary<int, BonePose> { [1] = new(1, swing, null, null) };

        var dest = new Dictionary<int, BonePose>();
        AnimationSampler.Overlay(under, over, mask: null, dest);

        Assert.Equal(swing, dest[1].Rotation);
    }

    // A masked bone the movement clip says nothing about still reaches the pose — the gesture is the only
    // thing animating it, and dropping it would leave the arm on its bind rest mid-swing.
    [Fact]
    public void Overlay_AddsMaskedBonesTheLayerBelowDoesNotCarry()
    {
        var under = new Dictionary<int, BonePose> { [2] = new(2, Quaternion.Identity, null, null) };
        var over = new Dictionary<int, BonePose> { [1] = new(1, new Quaternion(Vector3.Up, 1f), null, null) };

        var dest = new Dictionary<int, BonePose>();
        AnimationSampler.Overlay(under, over, BoneMask.Subtrees(new[] { -1, -1, -1 }, new[] { 1 }), dest);

        Assert.Equal(2, dest.Count);
        Assert.Equal(new Quaternion(Vector3.Up, 1f), dest[1].Rotation);
    }

    // The destination is a reused per-frame buffer, so it must be refilled rather than added to.
    [Fact]
    public void Overlay_ClearsTheDestinationBuffer()
    {
        var dest = new Dictionary<int, BonePose> { [9] = new(9, Quaternion.Identity, null, null) };
        AnimationSampler.Overlay(new Dictionary<int, BonePose>(), new Dictionary<int, BonePose>(),
            mask: null, dest);

        Assert.Empty(dest);
    }
}
