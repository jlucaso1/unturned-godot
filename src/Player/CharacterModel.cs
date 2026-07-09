using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Player;
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
    private const int ClassAnimation = 111; // the legacy Animation component
    // The entity to import. Only this name is entity-specific; everything else (skeleton, skin, default
    // animation) is read from the data, so other skinned entities import the same way with a different name.
    private const string EntityRoot = "Player_Client";

    // Customization.SKINS[0] — the default light skin tone the character's skin regions are filled with.
    private static readonly Color DefaultSkin = new(244f / 255f, 230f / 255f, 210f / 255f);
    private const int DefaultFace = 0; // Items/Faces/0 — the default face overlay (eyes + mouth, transparent)

    // Ports Unturned's Standard/Clothes body compositing (the parts a bare survivor uses): a flat skin
    // colour with the face overlaid where the mesh UV falls in the face patch. Unturned places the face by
    // UV, not geometry: faceUV = uv * 8 - (6, 7), i.e. the atlas region [6/8..7/8] x [7/8..1] maps to the
    // 0..1 face texture, masked to that patch. UVs here are already Godot's (V-flipped), so undo that to use
    // the Unity UV the maths is written for.
    private static readonly Shader BodyShader = new()
    {
        Code = """
        shader_type spatial;
        uniform sampler2D face_albedo : source_color, filter_nearest;
        uniform vec3 skin_color : source_color;
        void fragment() {
            // Undo the Godot V-flip to recover the Unity UV the face patch maths is written against.
            vec2 unity_uv = vec2(UV.x, 1.0 - UV.y);
            vec2 face_uv = unity_uv * 8.0 - vec2(6.0, 7.0); // atlas region [6/8..7/8]x[7/8..1] -> 0..1
            float mask = step(0.0, face_uv.x) * step(face_uv.x, 1.0) * step(0.0, face_uv.y) * step(face_uv.y, 1.0);
            vec4 face = texture(face_albedo, face_uv);
            ALBEDO = mix(skin_color, face.rgb, face.a * mask);
            ROUGHNESS = 1.0;
            SPECULAR = 0.0;
        }
        """,
    };

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
            if (RootName(file, byId, goId) != EntityRoot)
                continue;

            if (!byId.TryGetValue(Id(smr["m_Mesh"]), out SerializedObject? meshObj))
                return null;
            UnityMesh mesh = UnityMesh.Read(Read(file, byId, meshObj.PathId));
            if (!mesh.Usable)
                return null;

            if (mesh.BoneIndices.Length == mesh.Vertices.Length * UnityMesh.BonesPerVertex && mesh.BindPoses.Count > 0)
                return BuildSkinnedCharacter(file, byId, smr, mesh, bundlePath);

            // Fallback: no skinning data -> static bind-pose mesh.
            return new MeshInstance3D { Mesh = BuildMesh(mesh, skinned: false), Name = "CharacterBody" };
        }
        return null;
    }

    // Builds a Skeleton3D from the renderer's bones (rest poses converted Unity->Godot) with the skinned
    // body mesh as its child. Uses Godot's CreateSkinFromRestTransforms so the bind matrices derive from the
    // rest hierarchy — the mesh is authored in that same bind pose, so this is exact.
    private static Node3D BuildSkinnedCharacter(SerializedFile file, Dictionary<long, SerializedObject> byId,
        Dictionary<string, object> smr, UnityMesh mesh, string bundlePath)
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

        var skeleton = new CharacterSkeleton { Name = "Skeleton" };
        var rests = new Transform3D[boneCount];
        var parents = new int[boneCount];
        var boneByName = new Dictionary<string, int>(boneCount);
        for (int i = 0; i < boneCount; i++)
        {
            Dictionary<string, object> t = Read(file, byId, boneIds[i]);
            string name = (string)Read(file, byId, Id(t["m_GameObject"]))["m_Name"];
            skeleton.AddBone(name);
            boneByName[name] = i;
            rests[i] = LocalTransformOf(t);
            parents[i] = boneIndex.TryGetValue(Id(t["m_Father"]), out int p) ? p : -1;
        }
        for (int i = 0; i < boneCount; i++)
        {
            if (parents[i] >= 0)
                skeleton.SetBoneParent(i, parents[i]);
            skeleton.SetBoneRest(i, rests[i]);
        }
        skeleton.ResetBonePoses(); // pose = rest (the bind/T-pose); the idle clip below overrides it

        var body = new MeshInstance3D
        {
            Mesh = BuildMesh(mesh, skinned: true, face: LoadFace(bundlePath, DefaultFace)),
            Skin = skeleton.CreateSkinFromRestTransforms(), // bind matrices from the rest hierarchy
            Name = "CharacterBody",
        };
        skeleton.AddChild(body);
        body.Skeleton = body.GetPathTo(skeleton);

        StoreClips(file, byId, skeleton, boneByName, Id(smr["m_GameObject"]));
        skeleton.BindPitchBones(boneByName.GetValueOrDefault("Spine", -1), boneByName.GetValueOrDefault("Skull", -1));
        skeleton.SetState(UnturnedGodot.Player.EPlayerStance.Stand, moving: false);

        GD.Print($"[unturned-godot] Character: real {EntityRoot} skinned body loaded ({boneCount} bones, " +
            (skeleton.HasAnyPose ? "animated" : "bind pose") + ").");
        return skeleton;
    }

    // The clips the on-foot animator plays (PlayerAnimator.updateState): idle + move per stance. They live in
    // the entity's legacy Animation component (m_Animations), keyed by name. If Idle_Stand is missing we fall
    // back to the component's default clip (m_Animation) so a generic entity still gets a resting pose.
    private static void StoreClips(SerializedFile file, Dictionary<long, SerializedObject> byId,
        CharacterSkeleton skeleton, Dictionary<string, int> boneByName, long goId)
    {
        long animComp = AnimationComponentOf(file, byId, goId);
        if (animComp == 0)
            return;
        Dictionary<string, object> anim = Read(file, byId, animComp);

        var clips = new Dictionary<string, long>();
        foreach (object cp in (List<object>)anim["m_Animations"])
        {
            long cid = Id(cp);
            if (cid != 0 && byId.TryGetValue(cid, out SerializedObject? co))
                clips[(string)Read(file, byId, co.PathId)["m_Name"]] = cid;
        }

        foreach (string name in new[]
                 { "Idle_Stand", "Idle_Crouch", "Idle_Prone", "Move_Walk", "Move_Run", "Move_Crouch", "Move_Prone" })
            if (clips.TryGetValue(name, out long cid))
                skeleton.StoreClip(name, ReadClip(file, byId, boneByName, cid));

        if (!clips.ContainsKey("Idle_Stand") && Id(anim["m_Animation"]) is var def && def != 0)
            skeleton.StoreClip("Idle_Stand", ReadClip(file, byId, boneByName, def));
    }

    // Walks up from the mesh's GameObject to the one carrying a legacy Animation component (data-driven; not
    // keyed to a name), returning that component's PathId, or 0 if none.
    private static long AnimationComponentOf(SerializedFile file, Dictionary<long, SerializedObject> byId, long goId)
    {
        long go = goId;
        while (go != 0)
        {
            foreach (object component in (List<object>)Read(file, byId, go)["m_Component"])
            {
                long compId = Id(((Dictionary<string, object>)component)["component"]);
                if (byId.TryGetValue(compId, out SerializedObject? comp) && comp.ClassId == ClassAnimation)
                    return compId;
            }
            long transformId = TransformOf(file, byId, go);
            long father = transformId == 0 ? 0 : Id(Read(file, byId, transformId)["m_Father"]);
            go = father == 0 ? 0 : Id(Read(file, byId, father)["m_GameObject"]);
        }
        return 0;
    }

    // Decodes a legacy AnimationClip into per-bone keyframe tracks (all keyframes, not just frame 0),
    // converted Unity->Godot: rotations conjugated by the Z-mirror, positions' Z negated, scale unchanged.
    // Clip length is the latest keyframe time across every curve.
    private static AnimationClipData ReadClip(SerializedFile file, Dictionary<long, SerializedObject> byId,
        Dictionary<string, int> boneByName, long clipId)
    {
        var rot = new Dictionary<int, (float, Quaternion)[]>();
        var pos = new Dictionary<int, (float, Vector3)[]>();
        var scale = new Dictionary<int, (float, Vector3)[]>();
        float length = 0f;
        if (!byId.TryGetValue(clipId, out SerializedObject? clipObj))
            return new AnimationClipData();
        Dictionary<string, object> clip = Read(file, byId, clipObj.PathId);

        foreach (object c in (List<object>)clip["m_RotationCurves"])
            if (BoneKeys(c, boneByName, out int bone, out List<object> keys))
                rot[bone] = ReadKeys(keys, ref length, v => UnityMath.UnityToGodotRotation(
                    new Quaternion(F(v["x"]), F(v["y"]), F(v["z"]), F(v["w"]))));

        foreach (object c in (List<object>)clip["m_PositionCurves"])
            if (BoneKeys(c, boneByName, out int bone, out List<object> keys))
                pos[bone] = ReadKeys(keys, ref length, v => new Vector3(F(v["x"]), F(v["y"]), -F(v["z"])));

        foreach (object c in (List<object>)clip["m_ScaleCurves"])
            if (BoneKeys(c, boneByName, out int bone, out List<object> keys))
                scale[bone] = ReadKeys(keys, ref length, v => new Vector3(F(v["x"]), F(v["y"]), F(v["z"])));

        var bones = new Dictionary<int, BoneCurves>();
        foreach (int bone in Union(rot.Keys, pos.Keys, scale.Keys))
            bones[bone] = new BoneCurves
            {
                Rotation = rot.GetValueOrDefault(bone, System.Array.Empty<(float, Quaternion)>()),
                Position = pos.GetValueOrDefault(bone, System.Array.Empty<(float, Vector3)>()),
                Scale = scale.GetValueOrDefault(bone, System.Array.Empty<(float, Vector3)>()),
            };
        return new AnimationClipData { Length = length, Bones = bones };
    }

    // Reads a curve's keyframes as (time, value) pairs, tracking the overall clip length.
    private static (float, T)[] ReadKeys<T>(List<object> keys, ref float length,
        System.Func<Dictionary<string, object>, T> value)
    {
        var arr = new (float, T)[keys.Count];
        for (int i = 0; i < keys.Count; i++)
        {
            var key = (Dictionary<string, object>)keys[i];
            float time = F(key["time"]);
            arr[i] = (time, value((Dictionary<string, object>)key["value"]));
            length = Mathf.Max(length, time);
        }
        return arr;
    }

    private static IEnumerable<int> Union(IEnumerable<int> a, IEnumerable<int> b, IEnumerable<int> c)
    {
        var set = new HashSet<int>(a);
        set.UnionWith(b);
        set.UnionWith(c);
        return set;
    }

    // Resolves a curve to its target bone and its keyframe list; false if the bone isn't in the skeleton.
    private static bool BoneKeys(object curveEntry, Dictionary<string, int> boneByName, out int bone,
        out List<object> keys)
    {
        var entry = (Dictionary<string, object>)curveEntry;
        string path = (string)entry["path"];
        string name = path[(path.LastIndexOf('/') + 1)..];
        keys = (List<object>)((Dictionary<string, object>)entry["curve"])["m_Curve"];
        return boneByName.TryGetValue(name, out bone) && keys.Count > 0;
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

    // Loads the face texture (small, stored inline in the masterbundle SerializedFile) via a tiny on-disk
    // cache so it isn't re-decoded on every spawn.
    private static ImageTexture? LoadFace(string bundlePath, int faceIndex)
    {
        string cachePath = ProjectSettings.GlobalizePath($"user://face_{faceIndex}.tex");
        if (File.Exists(cachePath) && TextureCache.IsCurrent(cachePath))
        {
            try
            {
                using FileStream s = File.OpenRead(cachePath);
                return ModelLibrary.BuildTexture(TextureCache.Read(s));
            }
            catch (IOException) { /* corrupt cache -> regenerate */ }
        }

        CachedTexture? extracted = ExtractInlineTexture(ModelExtractor.ReadMasterbundleFile(bundlePath),
            $"assets/coremasterbundle/items/faces/{faceIndex}/texture.png");
        if (extracted is not { } face)
            return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            using FileStream w = File.Create(cachePath);
            TextureCache.Write(w, face);
        }
        catch (IOException) { /* best-effort cache */ }
        return ModelLibrary.BuildTexture(face);
    }

    // Reads a texture stored inline in a SerializedFile, found by its AssetBundle container path.
    private static CachedTexture? ExtractInlineTexture(SerializedFile mb, string containerPath)
    {
        var byId = new Dictionary<long, SerializedObject>();
        foreach (SerializedObject o in mb.Objects)
            byId[o.PathId] = o;

        foreach (SerializedObject o in mb.Objects)
        {
            if (o.ClassId != 142) // AssetBundle
                continue;
            Dictionary<string, object> ab = TypeTreeReader.Read(o.TypeTree, mb.ReaderFor(o));
            foreach (object entry in (List<object>)ab["m_Container"])
            {
                var pair = (Dictionary<string, object>)entry;
                if ((string)pair["first"] != containerPath)
                    continue;
                long texId = Id(((Dictionary<string, object>)pair["second"])["asset"]);
                if (!byId.TryGetValue(texId, out SerializedObject? texObj))
                    return null;
                UnityTexture tex = UnityTexture.Read(TypeTreeReader.Read(texObj.TypeTree, mb.ReaderFor(texObj)));
                byte[]? pixels = tex.GetPixels(_ => null); // inline
                return pixels == null || pixels.Length == 0
                    ? null
                    : new CachedTexture(tex.Format, tex.Width, tex.Height, tex.MipCount, pixels);
            }
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

    // Builds the Godot mesh via the single Unity->Godot translation (UnityMeshConverter): reflected
    // positions AND normals plus reversed winding, so the character keeps its authored hard-edge normals
    // and can't be lit inside-out. When skinned, also carry the per-vertex bone indices + normalized weights
    // (unaffected by the winding flip, which only reorders indices).
    private static ArrayMesh BuildMesh(UnityMesh mesh, bool skinned, ImageTexture? face = null)
    {
        UnityMeshConverter.GodotMesh g = UnityMeshConverter.ToGodot(mesh);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = g.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = g.Normals;
        arrays[(int)Mesh.ArrayType.TexUV] = g.Uvs;
        if (skinned)
        {
            arrays[(int)Mesh.ArrayType.Bones] = mesh.BoneIndices;
            arrays[(int)Mesh.ArrayType.Weights] = NormalizeWeights(mesh.BoneWeights);
        }
        arrays[(int)Mesh.ArrayType.Index] = g.Indices;

        var arrayMesh = new ArrayMesh();
        arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // The body's _MainTex is only a UV-region reference atlas; the game composites the character in the
        // Standard/Clothes shader. Reproduce that here: skin colour with the face overlaid by UV.
        var material = new ShaderMaterial { Shader = BodyShader };
        material.SetShaderParameter("skin_color", DefaultSkin);
        if (face != null) // unset -> Godot's default transparent sampler, so the face patch just shows skin
            material.SetShaderParameter("face_albedo", face);
        arrayMesh.SurfaceSetMaterial(0, material);
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
