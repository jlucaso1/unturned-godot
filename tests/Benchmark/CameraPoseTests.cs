using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Benchmark;
using Xunit;

namespace UnturnedGodot.Tests.Benchmark;

public class CameraPoseTests
{
    [Fact]
    public void ParsesANamedPose()
    {
        Assert.True(CameraPose.TryParseList("heavy=-406.89,49.58,579.35,-7.5,-17.6",
            out IReadOnlyList<CameraPose> poses, out string error));
        Assert.Equal("", error);
        CameraPose pose = Assert.Single(poses);
        Assert.Equal("heavy", pose.Name);
        Assert.Equal(new Vector3(-406.89f, 49.58f, 579.35f), pose.Position);
        Assert.Equal(-7.5f, pose.PitchDegrees);
        Assert.Equal(-17.6f, pose.YawDegrees);
    }

    [Fact]
    public void ParsesSeveralPosesAndTrimsWhitespace()
    {
        Assert.True(CameraPose.TryParseList(" a=1,2,3,4,5 ; b = 6, 7, 8, 9, 10 ",
            out IReadOnlyList<CameraPose> poses, out _));
        Assert.Equal(2, poses.Count);
        Assert.Equal("a", poses[0].Name);
        Assert.Equal(new Vector3(1, 2, 3), poses[0].Position);
        Assert.Equal("b", poses[1].Name);
        Assert.Equal(new Vector3(6, 7, 8), poses[1].Position);
        Assert.Equal(9f, poses[1].PitchDegrees);
        Assert.Equal(10f, poses[1].YawDegrees);
    }

    [Fact]
    public void NamesAPoseByPositionWhenItHasNoName()
    {
        Assert.True(CameraPose.TryParseList("1,2,3,4,5;named=6,7,8,9,10;11,12,13,14,15",
            out IReadOnlyList<CameraPose> poses, out _));
        Assert.Equal(new[] { "pose0", "named", "pose2" }, new[] { poses[0].Name, poses[1].Name, poses[2].Name });
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankInputIsAnEmptyListRatherThanAnError(string? spec)
    {
        Assert.True(CameraPose.TryParseList(spec, out IReadOnlyList<CameraPose> poses, out string error));
        Assert.Empty(poses);
        Assert.Equal("", error);
    }

    [Fact]
    public void EmptyEntriesBetweenSeparatorsAreSkipped()
    {
        Assert.True(CameraPose.TryParseList(";a=1,2,3,4,5;;", out IReadOnlyList<CameraPose> poses, out _));
        Assert.Single(poses);
        Assert.Equal("a", poses[0].Name);
    }

    // A pose that cannot be read has to be reported rather than dropped: a benchmark that silently
    // measured five poses instead of the six that were asked for reads as a result, not as a mistake.
    [Theory]
    [InlineData("1,2,3,4", "wanted 5 numbers")]
    [InlineData("1,2,3,4,5,6", "wanted 5 numbers")]
    [InlineData("a=1,2,three,4,5", "not a finite number")]
    [InlineData("a=1,2,3,4,NaN", "not a finite number")]
    [InlineData("a=Infinity,2,3,4,5", "not a finite number")]
    [InlineData("=1,2,3,4,5", "the name before '=' is empty")]
    public void RejectsAMalformedEntry(string spec, string expected)
    {
        Assert.False(CameraPose.TryParseList(spec, out IReadOnlyList<CameraPose> poses, out string error));
        Assert.Contains(expected, error, StringComparison.Ordinal);
        Assert.Empty(poses);
    }

    // Two poses with one name would produce two metric keys that collide in the report, so the second
    // would silently overwrite the first's numbers.
    [Fact]
    public void RejectsADuplicateName()
    {
        Assert.False(CameraPose.TryParseList("a=1,2,3,4,5;a=6,7,8,9,10",
            out IReadOnlyList<CameraPose> poses, out string error));
        Assert.Contains("used twice", error, StringComparison.Ordinal);
        Assert.Empty(poses);
    }

    [Fact]
    public void TryParseUsesTheFallbackNameOnlyWhenNoneIsGiven()
    {
        Assert.True(CameraPose.TryParse("1,2,3,4,5", "fallback", out CameraPose unnamed, out _));
        Assert.Equal("fallback", unnamed.Name);
        Assert.True(CameraPose.TryParse("given=1,2,3,4,5", "fallback", out CameraPose named, out _));
        Assert.Equal("given", named.Name);
    }

    // The whole point of sharing the five numbers with SHOT_CAM is that the measured frame and the
    // screenshotted frame are the same frame. SHOT_CAM goes through Node3D.RotationDegrees, which
    // composes Euler angles in YXZ order, so the transform this builds has to match that exactly.
    [Fact]
    public void BuildsTheSameTransformNode3DWouldFromRotationDegrees()
    {
        Assert.True(CameraPose.TryParse("heavy=-406.89,49.58,579.35,-7.5,-17.6", "x",
            out CameraPose pose, out _));
        Transform3D actual = pose.ToTransform();

        var expected = new Transform3D(
            Basis.FromEuler(new Vector3(Mathf.DegToRad(-7.5f), Mathf.DegToRad(-17.6f), 0f), EulerOrder.Yxz),
            new Vector3(-406.89f, 49.58f, 579.35f));

        Assert.Equal(expected.Origin, actual.Origin);
        Assert.True(expected.Basis.IsEqualApprox(actual.Basis));
    }

    [Fact]
    public void AZeroRotationPoseLooksDownMinusZLikeAnUnrotatedCamera()
    {
        Assert.True(CameraPose.TryParse("flat=10,20,30,0,0", "x", out CameraPose pose, out _));
        Transform3D transform = pose.ToTransform();
        Assert.Equal(new Vector3(10, 20, 30), transform.Origin);
        Assert.True((-transform.Basis.Z).IsEqualApprox(Vector3.Forward));
    }
}
