using Godot;

namespace UnturnedGodot.Data;

// Reproduces Unity's rotation conventions exactly so ported placements match the original.
public static class UnityMath
{
    // Unity's Quaternion.Euler uses ZXY order: q = qY * qX * qZ (Hamilton product).
    // Components are Unity's numeric convention; handedness is fixed separately.
    public static Quaternion EulerToUnityQuaternion(Vector3 degrees)
    {
        float hx = Mathf.DegToRad(degrees.X) * 0.5f;
        float hy = Mathf.DegToRad(degrees.Y) * 0.5f;
        float hz = Mathf.DegToRad(degrees.Z) * 0.5f;

        float cX = Mathf.Cos(hx), sX = Mathf.Sin(hx);
        float cY = Mathf.Cos(hy), sY = Mathf.Sin(hy);
        float cZ = Mathf.Cos(hz), sZ = Mathf.Sin(hz);

        float x = cY * sX * cZ + sY * cX * sZ;
        float y = sY * cX * cZ - cY * sX * sZ;
        float z = cY * cX * sZ - sY * sX * cZ;
        float w = cY * cX * cZ + sY * sX * sZ;
        return new Quaternion(x, y, z, w);
    }

    // Mirroring Z (Unity->Godot) conjugates the rotation by diag(1,1,-1): (x,y,z,w) -> (-x,-y,z,w).
    public static Quaternion UnityToGodotRotation(Quaternion unity) =>
        new(-unity.X, -unity.Y, unity.Z, unity.W);

    // A Unity local transform (position/rotation/scale) converted to Godot space by the Z-mirror F: the
    // position's Z flips, the rotation is conjugated by F, and scale is unchanged. Scale multiplies the
    // rotated columns (Unity's R*S order). This is the F*M*F reflection expressed via TRS, and is how bone
    // rest poses convert so a whole skeleton hierarchy composes consistently.
    public static Transform3D LocalToGodot(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var r = new Basis(UnityToGodotRotation(rotation));
        var basis = new Basis(r.X * scale.X, r.Y * scale.Y, r.Z * scale.Z);
        return new Transform3D(basis, new Vector3(position.X, position.Y, -position.Z));
    }
}
