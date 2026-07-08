using System.IO;
using Godot;

namespace UnturnedGodot.Unity;

// Compact on-disk format for an extracted mesh, so the 1.4 GB bundle is parsed once and runtime
// loads only the small per-object meshes it needs. Little-endian; positions + optional normals + indices.
public static class MeshCache
{
    private const uint Magic = 0x4853454D; // "MESH"

    public static void Write(Stream stream, Vector3[] vertices, Vector3[] normals, int[] indices)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(vertices.Length);
        foreach (Vector3 v in vertices)
        {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }

        bool hasNormals = normals.Length == vertices.Length && vertices.Length > 0;
        w.Write(hasNormals);
        if (hasNormals)
            foreach (Vector3 nrm in normals)
            {
                w.Write(nrm.X);
                w.Write(nrm.Y);
                w.Write(nrm.Z);
            }

        w.Write(indices.Length);
        foreach (int i in indices)
            w.Write(i);
    }

    public static (Vector3[] vertices, Vector3[] normals, int[] indices) Read(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a mesh cache stream");

        int vertexCount = r.ReadInt32();
        var vertices = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            vertices[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        Vector3[] normals = System.Array.Empty<Vector3>();
        if (r.ReadBoolean())
        {
            normals = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                normals[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        }

        int indexCount = r.ReadInt32();
        var indices = new int[indexCount];
        for (int i = 0; i < indexCount; i++)
            indices[i] = r.ReadInt32();

        return (vertices, normals, indices);
    }
}
