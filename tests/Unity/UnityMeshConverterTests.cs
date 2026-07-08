using System.Collections.Generic;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class UnityMeshConverterTests
{
    private static Vector3 V(float x, float y, float z) => new(x, y, z);

    [Fact]
    public void ToGodot_ReflectsPositionByF_AndNormalByCofactor()
    {
        var mesh = new UnityMesh
        {
            Vertices = new[] { V(1, 2, 3) },
            Normals = new[] { V(0.6f, 0f, 0.8f) },
            Submeshes = new List<int[]>(),
        };

        UnityMeshConverter.GodotMesh g = UnityMeshConverter.ToGodot(mesh);

        Assert.Equal(V(1, 2, -3), g.Vertices[0]);           // position: F -> negate Z
        Assert.Equal(V(-0.6f, 0f, 0.8f), g.Normals[0]);     // normal: cofactor -F -> negate X,Y; keep Z
    }

    [Fact]
    public void ToGodot_KeepsWinding()
    {
        // The reflection already turns Unity's CW winding into Godot's CCW front-face order, so it is kept.
        var mesh = new UnityMesh
        {
            Vertices = new[] { V(0, 0, 0), V(0, 0, 0), V(0, 0, 0) },
            Submeshes = new List<int[]> { new[] { 0, 1, 2 } },
        };
        Assert.Equal(new[] { 0, 1, 2 }, UnityMeshConverter.ToGodot(mesh).Indices);
    }

    // The regression guard: the translated normal must point the SAME way as the geometric normal of its
    // Godot triangle. Translating normals as +F (negate Z, like a position) instead of the cofactor -F is
    // exactly this test failing, and is why the skinned body first rendered lit inside-out (normals pointing
    // in, front faces culled). Any future feature that mistranslates a mesh trips here.
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
        Vector3 geometric = (gb - ga).Cross(gc - ga); // Godot triangle's own outward normal
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
}
