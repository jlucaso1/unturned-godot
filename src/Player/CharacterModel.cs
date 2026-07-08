using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Extracts Unturned's real player character body mesh from the game's resources.assets (a SerializedFile;
// pixels live in resources.assets.resS) and builds a Godot MeshInstance3D. The base body is authored in a
// T-pose (bind pose), so rendered as a static mesh it stands in that pose — natural posing needs the
// 16-bone skeleton + an idle animation, a follow-up stage. Reuses the object mesh/texture pipeline.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class CharacterModel
{
    private const int ClassSkinnedMeshRenderer = 137;
    private const int ClassTransform = 4;

    // Builds the body mesh under the "Player_Client" prefab root, or null when the game data is absent or
    // can't be parsed (the caller then falls back to the placeholder figure).
    public static MeshInstance3D? Build(string unturnedPath)
    {
        try
        {
            return BuildInternal(unturnedPath);
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"[unturned-godot] Character: failed to load real body ({e.GetType().Name}: {e.Message}); using placeholder.\n{e.StackTrace}");
            return null;
        }
    }

    private static MeshInstance3D? BuildInternal(string unturnedPath)
    {
        string assetsPath = Path.Combine(unturnedPath, "Unturned_Data", "resources.assets");
        if (!File.Exists(assetsPath))
            return null;

        SerializedFile file = SerializedFile.Read(File.ReadAllBytes(assetsPath));

        // The game ships resources.assets with type trees stripped (enableTypeTree = 0), unlike the map
        // masterbundle. Our reader is type-tree driven, so without them we can't decode the character's
        // objects — that needs a hardcoded Unity class-layout database (a separate, larger foundation).
        if (file.Objects.Count == 0 || file.Objects[0].TypeTree.Count == 0)
        {
            GD.Print("[unturned-godot] Character: resources.assets has no type trees; using placeholder " +
                "(real body needs a Unity class-layout reader).");
            return null;
        }
        var byId = new Dictionary<long, SerializedObject>();
        foreach (SerializedObject o in file.Objects)
            byId[o.PathId] = o;

        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != ClassSkinnedMeshRenderer)
                continue;
            Dictionary<string, object> smr = Read(file, byId, o.PathId);
            long goId = Id(smr["m_GameObject"]);
            if (RootName(file, byId, goId) != "Player_Client")
                continue;

            if (!byId.TryGetValue(Id(smr["m_Mesh"]), out SerializedObject? meshObj))
                return null;
            UnityMesh mesh = UnityMesh.Read(Read(file, byId, meshObj.PathId));
            if (!mesh.Usable)
                return null;

            ImageTexture? texture = MainTexture(file, byId, (List<object>)smr["m_Materials"], assetsPath + ".resS");
            var instance = new MeshInstance3D { Mesh = BuildMesh(mesh, texture), Name = "CharacterBody" };
            GD.Print("[unturned-godot] Character: real Player_Client body mesh loaded (T-pose)");
            return instance;
        }
        return null;
    }

    // Walks the mesh's GameObject up to its prefab root and returns the root GameObject's name.
    private static string RootName(SerializedFile file, Dictionary<long, SerializedObject> byId, long goId)
    {
        long transformId = TransformOf(file, byId, goId);
        if (transformId == 0)
            return string.Empty;
        while (true)
        {
            Dictionary<string, object> t = Read(file, byId, transformId);
            long father = Id(t["m_Father"]);
            if (father == 0)
                return (string)Read(file, byId, Id(t["m_GameObject"]))["m_Name"];
            transformId = father;
        }
    }

    private static long TransformOf(SerializedFile file, Dictionary<long, SerializedObject> byId, long goId)
    {
        foreach (object component in (List<object>)Read(file, byId, goId)["m_Component"])
        {
            long id = Id(((Dictionary<string, object>)component)["component"]);
            if (byId.TryGetValue(id, out SerializedObject? comp) && comp.ClassId == ClassTransform)
                return id;
        }
        return 0;
    }

    private static ImageTexture? MainTexture(SerializedFile file, Dictionary<long, SerializedObject> byId,
        List<object> materials, string resSPath)
    {
        if (materials.Count == 0 || !byId.TryGetValue(Id(materials[0]), out SerializedObject? matObj))
            return null;
        (int fileId, long texId) = UnityMaterial.GetTexture(Read(file, byId, matObj.PathId), "_MainTex");
        if (fileId != 0 || !byId.TryGetValue(texId, out SerializedObject? texObj))
            return null;

        UnityTexture tex = UnityTexture.Read(Read(file, byId, texObj.PathId));
        byte[]? pixels = ReadStreamSlice(resSPath, tex);
        return pixels == null
            ? null
            : ModelLibrary.BuildTexture(new CachedTexture(tex.Format, tex.Width, tex.Height, tex.MipCount, pixels));
    }

    // Reads the texture's pixel slice straight out of the .resS file (it is large, so seek to the range).
    private static byte[]? ReadStreamSlice(string resSPath, UnityTexture tex)
    {
        if (tex.StreamPath.Length == 0)
            return tex.InlineData.Length > 0 ? tex.InlineData : null;
        if (!File.Exists(resSPath))
            return null;
        using FileStream fs = File.OpenRead(resSPath);
        fs.Seek(tex.StreamOffset, SeekOrigin.Begin);
        var buffer = new byte[tex.StreamSize];
        return fs.Read(buffer, 0, buffer.Length) == buffer.Length ? buffer : null;
    }

    // Mirrors ModelLibrary: convert vertices Unity->Godot (negate Z), reverse the mirrored winding, derive
    // smooth normals from the flipped triangles, flip UV V. One submesh (the body).
    private static ArrayMesh BuildMesh(UnityMesh mesh, ImageTexture? texture)
    {
        var verts = new Vector3[mesh.Vertices.Length];
        for (int i = 0; i < verts.Length; i++)
            verts[i] = Landscape.UnityToGodot(mesh.Vertices[i]);

        var uvs = new Vector2[mesh.Vertices.Length];
        for (int i = 0; i < uvs.Length; i++)
            uvs[i] = i < mesh.Uvs.Length ? new Vector2(mesh.Uvs[i].X, 1f - mesh.Uvs[i].Y) : Vector2.Zero;

        var indices = new List<int>();
        foreach (int[] submesh in mesh.Submeshes)
            for (int i = 0; i + 2 < submesh.Length; i += 3)
            {
                indices.Add(submesh[i]);
                indices.Add(submesh[i + 2]); // reversed
                indices.Add(submesh[i + 1]);
            }
        int[] index = indices.ToArray();

        var normals = new Vector3[verts.Length];
        for (int i = 0; i + 2 < index.Length; i += 3)
        {
            int a = index[i], b = index[i + 1], c = index[i + 2];
            Vector3 face = (verts[c] - verts[a]).Cross(verts[b] - verts[a]);
            normals[a] += face;
            normals[b] += face;
            normals[c] += face;
        }
        for (int i = 0; i < normals.Length; i++)
            normals[i] = normals[i].LengthSquared() > 0f ? normals[i].Normalized() : Vector3.Up;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = index;

        var arrayMesh = new ArrayMesh();
        arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        arrayMesh.SurfaceSetMaterial(0, new StandardMaterial3D
        {
            AlbedoTexture = texture,
            Roughness = 1f,
            SpecularMode = BaseMaterial3D.SpecularModeEnum.Disabled,
        });
        return arrayMesh;
    }

    private static Dictionary<string, object> Read(SerializedFile file, Dictionary<long, SerializedObject> byId, long id) =>
        TypeTreeReader.Read(byId[id].TypeTree, file.ReaderFor(byId[id]));

    private static long Id(object pptr) => System.Convert.ToInt64(((Dictionary<string, object>)pptr)["m_PathID"]);
}
