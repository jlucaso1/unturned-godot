using Godot;

namespace UnturnedGodot.Data;

// Turns a PlacedObject's Unity TRS into a Godot Transform3D (pure, so it is unit-tested).
public static class ObjectPlacement
{
    public static Transform3D ComputeTransform(PlacedObject obj)
    {
        Quaternion unityRot = UnityMath.EulerToUnityQuaternion(obj.EulerDegrees);
        Quaternion godotRot = UnityMath.UnityToGodotRotation(unityRot);

        // Unity TRS applies local scale before rotation (R*S), i.e. scale the rotated
        // axis columns. Godot's Basis.Scaled scales rows (S*R), so build columns directly.
        Basis r = new Basis(godotRot);
        Basis basis = new Basis(r.X * obj.Scale.X, r.Y * obj.Scale.Y, r.Z * obj.Scale.Z);
        Vector3 origin = Landscape.UnityToGodot(obj.Position);
        return new Transform3D(basis, origin);
    }
}
