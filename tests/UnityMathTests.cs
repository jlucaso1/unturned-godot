using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class UnityMathTests
{
    private static void AssertQuat(Quaternion expected, Quaternion actual, float tol = 1e-4f)
    {
        // q and -q represent the same rotation; compare up to sign.
        float dot = expected.X * actual.X + expected.Y * actual.Y +
                    expected.Z * actual.Z + expected.W * actual.W;
        if (dot < 0) actual = new Quaternion(-actual.X, -actual.Y, -actual.Z, -actual.W);
        Assert.True(Mathf.Abs(expected.X - actual.X) < tol, $"X {expected.X} vs {actual.X}");
        Assert.True(Mathf.Abs(expected.Y - actual.Y) < tol, $"Y {expected.Y} vs {actual.Y}");
        Assert.True(Mathf.Abs(expected.Z - actual.Z) < tol, $"Z {expected.Z} vs {actual.Z}");
        Assert.True(Mathf.Abs(expected.W - actual.W) < tol, $"W {expected.W} vs {actual.W}");
    }

    [Fact]
    public void Euler_Identity()
    {
        AssertQuat(new Quaternion(0, 0, 0, 1), UnityMath.EulerToUnityQuaternion(Vector3.Zero));
    }

    [Theory]
    [InlineData(90, 0, 0, 0.70710677f, 0, 0, 0.70710677f)]
    [InlineData(0, 90, 0, 0, 0.70710677f, 0, 0.70710677f)]
    [InlineData(0, 0, 90, 0, 0, 0.70710677f, 0.70710677f)]
    [InlineData(180, 0, 0, 1, 0, 0, 0)]
    public void Euler_SingleAxis_MatchesUnity(float ex, float ey, float ez,
        float qx, float qy, float qz, float qw)
    {
        var q = UnityMath.EulerToUnityQuaternion(new Vector3(ex, ey, ez));
        AssertQuat(new Quaternion(qx, qy, qz, qw), q);
    }

    [Fact]
    public void Euler_ZXY_Order_CombinedAngles()
    {
        // Unity's ZXY order (qY*qX*qZ) verified independently via a separate Hamilton-product impl.
        var q = UnityMath.EulerToUnityQuaternion(new Vector3(30, 60, 90));
        AssertQuat(new Quaternion(0.5f, 0.1830127f, 0.5f, 0.6830127f), q);
    }

    [Theory]
    [InlineData(30, 60, 90)]
    [InlineData(-45, 120, 15)]
    [InlineData(10, -200, 300)]
    public void HandednessInvariant_RotationMatchesMirroredPoint(float ex, float ey, float ez)
    {
        // For every rotation, converting both the point and the rotation to Godot must commute:
        //   godotRot * mirror(p) == mirror(unityRot * p)
        var euler = new Vector3(ex, ey, ez);
        Quaternion unityRot = UnityMath.EulerToUnityQuaternion(euler);
        Quaternion godotRot = UnityMath.UnityToGodotRotation(unityRot);

        var p = new Vector3(3.5f, -2.1f, 7.8f);
        Vector3 lhs = godotRot * Landscape.UnityToGodot(p);
        Vector3 rhs = Landscape.UnityToGodot(unityRot * p);

        Assert.True((lhs - rhs).Length() < 1e-4f, $"{lhs} vs {rhs}");
    }

    [Fact]
    public void ComputeTransform_AppliesScaleTranslationRotation()
    {
        var obj = new PlacedObject(
            position: new Vector3(10, 20, 30),
            euler: new Vector3(0, 90, 0),
            scale: new Vector3(2, 3, 4),
            id: 1, guid: System.Guid.NewGuid());

        Transform3D t = ObjectPlacement.ComputeTransform(obj);

        Assert.Equal(new Vector3(10, 20, -30), t.Origin);
        // Column scale magnitudes are preserved regardless of rotation.
        Assert.Equal(2f, t.Basis.X.Length(), 3);
        Assert.Equal(3f, t.Basis.Y.Length(), 3);
        Assert.Equal(4f, t.Basis.Z.Length(), 3);
    }
}
