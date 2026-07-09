using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class UnityMeshConverterTests
{
    private static Vector3 V(float x, float y, float z) => new(x, y, z);

    [Fact]
    public void ToGodot_ReflectsPositionAndNormalByF()
    {
        var mesh = new UnityMesh
        {
            Vertices = new[] { V(1, 2, 3) },
            Normals = new[] { V(0.6f, 0f, 0.8f) },
            Submeshes = new List<int[]>(),
        };

        UnityMeshConverter.GodotMesh g = UnityMeshConverter.ToGodot(mesh);

        Assert.Equal(V(1, 2, -3), g.Vertices[0]);           // position: F -> negate Z
        Assert.Equal(V(0.6f, 0f, -0.8f), g.Normals[0]);     // normal: F -> negate Z (inverse-transpose of F)
    }

    [Fact]
    public void ToGodot_KeepsWinding()
    {
        // Unity fronts are clockwise and so are Godot's; the Z reflection keeps the clockwise order intact
        // for the mirrored geometry, so the index order is preserved.
        var mesh = new UnityMesh
        {
            Vertices = new[] { V(0, 0, 0), V(0, 0, 0), V(0, 0, 0) },
            Submeshes = new List<int[]> { new[] { 0, 1, 2 } },
        };
        Assert.Equal(new[] { 0, 1, 2 }, UnityMeshConverter.ToGodot(mesh).Indices);
    }

    // The regression guard: the translated normal must point the SAME way as its Godot triangle's FRONT
    // face normal — which, under Godot's clockwise front-face convention, is Cross(e2, e1) of the wound
    // triangle (see the terrain builder, which derives its normals the same way). Combined with the
    // placement-composition test below this pins the one correct translation: kept winding + normals by F.
    [Fact]
    public void ToGodot_NormalAgreesWithWinding()
    {
        // A triangle with a Z-facing authored normal, so the reflection is actually exercised. The authored
        // normal is the triangle's own outward geometric normal in Unity.
        Vector3 a = V(0, 0, 0), b = V(1, 0, 0), c = V(0, 0, 1);
        Vector3 unityNormal = (b - a).Cross(c - a).Normalized(); // (0, -1, 0)
        var mesh = new UnityMesh
        {
            Vertices = new[] { a, b, c },
            Normals = new[] { unityNormal, unityNormal, unityNormal },
            Submeshes = new List<int[]> { new[] { 0, 1, 2 } },
        };

        UnityMeshConverter.GodotMesh g = UnityMeshConverter.ToGodot(mesh);

        Vector3 ga = g.Vertices[g.Indices[0]], gb = g.Vertices[g.Indices[1]], gc = g.Vertices[g.Indices[2]];
        Vector3 geometric = (gc - ga).Cross(gb - ga); // front normal under Godot's clockwise winding: Cross(e2, e1)
        Assert.True(g.Normals[0].Dot(geometric) > 0f,
            $"translated normal {g.Normals[0]} must agree with the winding geometry {geometric}");
    }

    [Fact]
    public void ToGodot_NoSourceNormals_LeavesNormalsEmpty()
    {
        var mesh = new UnityMesh { Vertices = new[] { V(0, 0, 0) }, Submeshes = new List<int[]>() };
        Assert.Empty(UnityMeshConverter.ToGodot(mesh).Normals);
    }

    [Fact]
    public void ToGodot_FlipsUvV_AndDefaultsMissing()
    {
        var mesh = new UnityMesh
        {
            Vertices = new[] { V(0, 0, 0), V(0, 0, 0) },
            Uvs = new[] { new Vector2(0.25f, 0.75f) }, // one UV for two verts
            Submeshes = new List<int[]>(),
        };
        UnityMeshConverter.GodotMesh g = UnityMeshConverter.ToGodot(mesh);
        Assert.Equal(new Vector2(0.25f, 0.25f), g.Uvs[0]); // V flipped
        Assert.Equal(Vector2.Zero, g.Uvs[1]);              // missing UV -> zero
    }

    // The parity guard the city streets exposed: a normal must land where Unity puts it, after BOTH the
    // mesh translation AND the placement rotation (ObjectPlacement converts rotations as R' = F*R*F, so
    // the mesh-side translation must be F for the composition R'*(F*n) = F*(R*n) to hold). PEI's road
    // quads are authored lying flat with +Z-up normals and stood upright by a -90-degree X placement:
    // with the cofactor -F they composed to world -Y — lit from below, permanently in shade.
    [Theory]
    [InlineData(-90f, 0f, 0f)]   // the road-quad case: authored +Z-up stood upright
    [InlineData(90f, 0f, 0f)]
    [InlineData(0f, 0f, 0f)]     // unrotated placement
    [InlineData(-90f, 137f, 0f)] // with an arbitrary yaw on top
    [InlineData(33f, -71f, 12f)] // fully arbitrary rotation
    public void ToGodot_NormalComposesWithPlacementRotation(float ex, float ey, float ez)
    {
        Vector3 unityNormal = new Vector3(0.36f, 0.48f, 0.8f).Normalized();
        var mesh = new UnityMesh
        {
            Vertices = new[] { V(0, 0, 0) },
            Normals = new[] { unityNormal },
            Submeshes = new List<int[]>(),
        };
        var euler = new Vector3(ex, ey, ez);

        // Where Unity puts the normal in world space, mirrored into Godot's frame (F negates Z).
        Quaternion unityRot = UnityMath.EulerToUnityQuaternion(euler);
        Vector3 unityWorld = UnityQuaternionRotate(unityRot, unityNormal);
        var expected = new Vector3(unityWorld.X, unityWorld.Y, -unityWorld.Z);

        // Where our pipeline puts it: the translated mesh normal through the placement transform.
        Vector3 translated = UnityMeshConverter.ToGodot(mesh).Normals[0];
        Transform3D placement = ObjectPlacement.ComputeTransform(
            new PlacedObject(Vector3.Zero, euler, Vector3.One, 0, System.Guid.Empty));
        Vector3 composed = placement.Basis * translated;

        Assert.True(composed.DistanceTo(expected) < 1e-5f,
            $"euler {euler}: composed {composed} must equal Unity's mirrored world normal {expected}");
    }

    // Rotate a vector by a Unity (left-handed frame) quaternion — the standard sandwich q*v*q^-1, valid
    // in any handedness as long as everything stays in the same frame.
    private static Vector3 UnityQuaternionRotate(Quaternion q, Vector3 v)
    {
        var u = new Vector3(q.X, q.Y, q.Z);
        return (2f * u.Dot(v) * u) + (((q.W * q.W) - u.Dot(u)) * v) + (2f * q.W * u.Cross(v));
    }
}
