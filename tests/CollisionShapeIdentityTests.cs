using System;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class CollisionShapeIdentityTests
{
    [Fact]
    public void PrimitiveKeyIsBitExactAcrossKindAndDimensions()
    {
        PrimitiveShapeIdentity key = CollisionShapeIdentity.Primitive(0, 1f, 2f, 3f);
        Assert.Equal(key, CollisionShapeIdentity.Primitive(0, 1f, 2f, 3f));
        Assert.NotEqual(key, CollisionShapeIdentity.Primitive(1, 1f, 2f, 3f));
        Assert.NotEqual(key, CollisionShapeIdentity.Primitive(0, float.BitIncrement(1f), 2f, 3f));
        Assert.NotEqual(CollisionShapeIdentity.Primitive(0, 0f),
            CollisionShapeIdentity.Primitive(0, -0f));
    }

    [Fact]
    public void MeshKeyUsesEveryFinalFaceComponentInOrder()
    {
        Vector3[] faces = { new(1, 2, 3), new(4, 5, 6), new(7, 8, 9) };
        string original = CollisionShapeIdentity.Mesh(faces);
        Assert.Equal(original, CollisionShapeIdentity.Mesh((Vector3[])faces.Clone()));

        faces[2].Z = float.BitIncrement(faces[2].Z);
        Assert.NotEqual(original, CollisionShapeIdentity.Mesh(faces));
        Assert.NotEqual(original, CollisionShapeIdentity.Mesh(faces.AsSpan(0, 2)));
    }
}
