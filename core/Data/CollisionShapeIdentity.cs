using System;
using System.Runtime.InteropServices;
using Godot;

namespace UnturnedGodot.Data;

// Exact identities for already-baked physics geometry. These keys are deliberately formed after Unity
// scaling/reflection has been applied: different source colliders may then share one Shape3D only when
// the values handed to Godot are bit-identical.
public readonly record struct PrimitiveShapeIdentity(byte Kind, int X, int Y, int Z);

public static class CollisionShapeIdentity
{
    public static PrimitiveShapeIdentity Primitive(byte kind, float x, float y = 0f, float z = 0f) =>
        new(kind, BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(y),
            BitConverter.SingleToInt32Bits(z));

    public static string Mesh(ReadOnlySpan<Vector3> faces) =>
        ExactContentKey.Bytes(MemoryMarshal.AsBytes(faces));
}
