using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Extracts Unturned's real player character from the game's resources.assets (a SerializedFile; pixels live
// in resources.assets.resS): the Player_Client body mesh, skinned to its 16-bone skeleton, built as a Godot
// Skeleton3D + skinned MeshInstance3D. Bone rest poses and vertices convert Unity->Godot by the same Z-mirror
// reflection, so the skinning stays consistent. Reuses the object mesh/texture pipeline.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class CharacterModel
{
    private const int ClassSkinnedMeshRenderer = 137;
    private const int ClassTransform = 4;

    // Builds the skinned Player_Client character, or null when the game data is absent or can't be parsed
    // (the caller then falls back to the placeholder figure).
    public static Node3D? Build(string unturnedPath)
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

    private static Node3D? BuildInternal(string unturnedPath)
    {
        string assetsPath = Path.Combine(unturnedPath, "Unturned_Data", "resources.assets");
        string bundlePath = Path.Combine(unturnedPath, "Bundles", "core_linux.masterbundle");
        if (!File.Exists(assetsPath) || !File.Exists(bundlePath))
            return null;

        // resources.assets ships with its type trees stripped (enableTypeTree = 0). Type trees are identical
        // across files of the same Unity version, so decode it with the ones gathered from the masterbundle.
        IReadOnlyDictionary<int, List<TypeTreeNode>> classTypeTrees = ModelExtractor.ReadClassTypeTrees(bundlePath);
        SerializedFile file = SerializedFile.Read(File.ReadAllBytes(assetsPath), classTypeTrees);
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
            if (mesh.BoneIndices.Length == mesh.Vertices.Length * UnityMesh.BonesPerVertex && mesh.BindPoses.Count > 0)
                return BuildSkinnedCharacter(file, byId, smr, mesh, texture);

            // Fallback: no skinning data -> static bind-pose mesh.
            return new MeshInstance3D { Mesh = BuildMesh(mesh, texture, skinned: false), Name = "CharacterBody" };
        }
        return null;
    }

    // Builds a Skeleton3D from the renderer's bones (rest poses converted Unity->Godot) with the skinned
    // body mesh as its child. Uses Godot's CreateSkinFromRestTransforms so the bind matrices derive from the
    // rest hierarchy — the mesh is authored in that same bind pose, so this is exact.
    private static Node3D BuildSkinnedCharacter(SerializedFile file, Dictionary<long, SerializedObject> byId,
        Dictionary<string, object> smr, UnityMesh mesh, ImageTexture? texture)
    {
        var boneRefs = (List<object>)smr["m_Bones"];
        int boneCount = boneRefs.Count;
        var boneIds = new long[boneCount];
        var boneIndex = new Dictionary<long, int>(boneCount);
        for (int i = 0; i < boneCount; i++)
        {
            boneIds[i] = Id(boneRefs[i]);
            boneIndex[boneIds[i]] = i;
        }

        var skeleton = new Skeleton3D { Name = "Skeleton" };
        var rests = new Transform3D[boneCount];
        var parents = new int[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            Dictionary<string, object> t = Read(file, byId, boneIds[i]);
            skeleton.AddBone((string)Read(file, byId, Id(t["m_GameObject"]))["m_Name"]);
            rests[i] = LocalTransformOf(t);
            parents[i] = boneIndex.TryGetValue(Id(t["m_Father"]), out int p) ? p : -1;
        }
        for (int i = 0; i < boneCount; i++)
        {
            if (parents[i] >= 0)
                skeleton.SetBoneParent(i, parents[i]);
            skeleton.SetBoneRest(i, rests[i]);
        }
        skeleton.ResetBonePoses(); // pose = rest, i.e. the bind pose

        var body = new MeshInstance3D
        {
            Mesh = BuildMesh(mesh, texture, skinned: true),
            Skin = skeleton.CreateSkinFromRestTransforms(),
            Name = "CharacterBody",
        };
        skeleton.AddChild(body);
        body.Skeleton = body.GetPathTo(skeleton);
        GD.Print($"[unturned-godot] Character: real Player_Client skinned body loaded ({boneCount} bones).");
        return skeleton;
    }

    private static Transform3D LocalTransformOf(Dictionary<string, object> t)
    {
        var p = (Dictionary<string, object>)t["m_LocalPosition"];
        var r = (Dictionary<string, object>)t["m_LocalRotation"];
        var s = (Dictionary<string, object>)t["m_LocalScale"];
        return UnityMath.LocalToGodot(
            new Vector3(F(p["x"]), F(p["y"]), F(p["z"])),
            new Quaternion(F(r["x"]), F(r["y"]), F(r["z"]), F(r["w"])),
            new Vector3(F(s["x"]), F(s["y"]), F(s["z"])));
    }

    private static float F(object value) => System.Convert.ToSingle(value);

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
    // smooth normals from the flipped triangles, flip UV V. When skinned, also carry the per-vertex bone
    // indices + normalized weights (unaffected by the winding flip, which only reorders indices).
    private static ArrayMesh BuildMesh(UnityMesh mesh, ImageTexture? texture, bool skinned)
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
        if (skinned)
        {
            arrays[(int)Mesh.ArrayType.Bones] = mesh.BoneIndices;
            arrays[(int)Mesh.ArrayType.Weights] = NormalizeWeights(mesh.BoneWeights);
        }
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

    // Godot expects each vertex's 4 bone weights to sum to 1.
    private static float[] NormalizeWeights(float[] weights)
    {
        var result = new float[weights.Length];
        for (int v = 0; v + 3 < weights.Length; v += 4)
        {
            float sum = weights[v] + weights[v + 1] + weights[v + 2] + weights[v + 3];
            if (sum > 0f)
                for (int c = 0; c < 4; c++)
                    result[v + c] = weights[v + c] / sum;
            else
                result[v] = 1f; // degenerate: bind fully to the first bone
        }
        return result;
    }

    private static Dictionary<string, object> Read(SerializedFile file, Dictionary<long, SerializedObject> byId, long id) =>
        TypeTreeReader.Read(byId[id].TypeTree, file.ReaderFor(byId[id]));

    private static long Id(object pptr) => System.Convert.ToInt64(((Dictionary<string, object>)pptr)["m_PathID"]);
}
