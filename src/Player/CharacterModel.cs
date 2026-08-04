using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
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

    // Everything entity-specific about a skinned character import; the skeleton, skin weights and clips are
    // read from the data, so any of the game's skinned entities imports through the same pipeline.
    private sealed class EntityConfig
    {
        public required string Root;         // prefab root GameObject name in resources.assets
        public required string[] Clips;      // legacy Animation clips to decode (missing ones just skip)
        public required string PrimaryIdle;  // resting clip; falls back to the component's default clip
        public required Color Skin;          // flat colour the skin UV regions are filled with
        public required int FaceIndex;       // Items/Faces/<n> overlay composited by UV

        // Which of the prefab's two skinned rigs to take. Unturned keeps the first-person arms in a
        // "Viewmodel" subtree beside the third-person body; both are rooted at the same GameObject, so
        // the subtree is the only thing that separates them.
        public bool FirstPerson;
    }

    // The prefab subtree holding the first-person arms, as PlayerAnimator names it.
    private const string ViewmodelSubtree = "Viewmodel";

    // Customization.SKINS[0] — the default light skin tone — and Items/Faces/0, the default face.
    private static readonly EntityConfig PlayerEntity = new()
    {
        Root = "Player_Client",
        // Idle/move per stance, plus the bare-handed swings PlayerEquipment.punch plays. A clip the
        // character does not carry is simply skipped, so listing one costs nothing when it is absent.
        Clips = new[]
        {
            "Idle_Stand", "Idle_Crouch", "Idle_Prone", "Move_Walk", "Move_Run", "Move_Crouch", "Move_Prone",
            "Punch_Left", "Punch_Right",
        },
        PrimaryIdle = "Idle_Stand",
        Skin = new Color(244f / 255f, 230f / 255f, 210f / 255f),
        FaceIndex = 0,
    };

    // The same player, taken from the prefab's first-person arms rig. Same skin and clips: it is the same
    // character, drawn from inside its own head.
    private static readonly EntityConfig PlayerViewmodelEntity = new()
    {
        Root = PlayerEntity.Root,
        Clips = PlayerEntity.Clips,
        PrimaryIdle = PlayerEntity.PrimaryIdle,
        Skin = PlayerEntity.Skin,
        FaceIndex = PlayerEntity.FaceIndex,
        FirstPerson = true,
    };

    // ZombieClothing.paint: the greenish zombie skin tones and the fixed zombie face (Items/Faces/19).
    // Clip variants are the ones Zombie.cs plays: Idle_0..2 + crawler 3 / sprinter 4, Move_0..3 + crawler 4 /
    // sprinter 5, Attack_0..8, plus the startle/stun reactions for later.
    private static EntityConfig ZombieEntity(bool isMega) => new()
    {
        Root = "Zombie_Client",
        Clips = new[]
        {
            "Idle_0", "Idle_1", "Idle_2", "Idle_3", "Idle_4",
            "Move_0", "Move_1", "Move_2", "Move_3", "Move_4", "Move_5",
            "Attack_0", "Attack_1", "Attack_2", "Attack_3", "Attack_4",
            "Attack_5", "Attack_6", "Attack_7", "Attack_8",
            "Startle_0", "Startle_1", "Startle_2", "Startle_3", "Startle_4", "Startle_5", "Startle_6",
            "Stun_0", "Stun_1", "Stun_2", "Stun_3", "Stun_4",
        },
        PrimaryIdle = "Idle_0",
        Skin = isMega ? new Color(89f / 255f, 99f / 255f, 89f / 255f) : new Color(99f / 255f, 124f / 255f, 99f / 255f),
        FaceIndex = 19,
    };

    private static readonly Color MegaZombieSkin =
        new(89f / 255f, 99f / 255f, 89f / 255f);

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
    public static Node3D? Build(string unturnedPath) => BuildEntity(unturnedPath, PlayerEntity);

    // The player's two rigs. There is deliberately no first-person EYE transform here: Unturned's
    // ViewmodelCamera is a child of firstSkeleton/Spine/Skull — the VIEWMODEL skeleton's skull, not the
    // body's — so its authored offset only means anything on the rig it was authored against. When the
    // body rig stands in for the arms (see ViewmodelFromBody), there is no authored answer to compose
    // with, and inventing one is how the arms end up at ninety degrees to the view.
    public readonly record struct PlayerRigs(Node3D? Body, Node3D? Viewmodel);

    // Both rigs from ONE read of resources.assets: the third-person body, and the first-person arms
    // rigged into the prefab's Viewmodel subtree with the same clips, so a swing plays identically from
    // either side of the camera. Either may be null — no game data at all, or a prefab that carries no
    // Viewmodel subtree, in which case first person falls back to the body rig.
    //
    // One entry point rather than two calls because opening that file is the expensive part (~100 ms and
    // tens of MB of transient heap), and it happens inside the load's character stage. Same reason
    // BuildZombieTemplates imports once for both zombie looks.
    public static PlayerRigs BuildPlayerRigs(string unturnedPath)
    {
        AssetSource source;
        Node3D? body;
        try
        {
            if (Open(unturnedPath) is not { } opened)
                return default;
            source = opened;
            if (EnvFlag.IsOn(OS.GetEnvironment("UG_DUMP_PLAYER_RIG"), whenUnset: false))
                DumpRigs(source);
            body = BuildFrom(source, PlayerEntity);
        }
        catch (System.Exception e)
        {
            Log.PrintErr($"[unturned-godot] Character: failed to load real {PlayerEntity.Root} "
                + $"({e.GetType().Name}: {e.Message}); using placeholder.\n{e.StackTrace}");
            return default;
        }

        // The arms are caught on their own, because the two rigs fail differently. No body means the
        // placeholder figure; no viewmodel means first person shows no hands, which is what a prefab
        // without a Viewmodel subtree already gives. Sharing one catch would let an unsupported mesh in
        // that subtree throw away a body that imported perfectly and drop the whole player to a capsule.
        Node3D? viewmodel = null;
        try
        {
            viewmodel = BuildFrom(source, PlayerViewmodelEntity);
        }
        catch (System.Exception e)
        {
            Log.PrintErr($"[unturned-godot] Character: failed to load the {ViewmodelSubtree} rig "
                + $"({e.GetType().Name}: {e.Message}); falling back to the body rig for first person.");
        }

        if (viewmodel is CharacterSkeleton arms)
            return new PlayerRigs(body, arms);

        // The import produced something, but not a rig — a static bind-pose mesh, which cannot be posed
        // and so would hang in front of the camera in one frozen attitude. It is a Node nothing owns and
        // nothing else will free, so it is freed here rather than left to the collector that does not
        // exist for Godot objects.
        viewmodel?.Free();
        return new PlayerRigs(body, ViewmodelFromBody(body));
    }

    // First person without the prefab's own arms rig.
    //
    // The Viewmodel subtree carries a single renderer, Model_0, and Model_0's skin weights live in the
    // compressed vertex stream this pipeline does not decode — so BuildFrom finds no skinnable mesh
    // there and returns a static bind-pose mesh, which cannot be posed and therefore cannot throw a
    // punch. That is why first person had no swing while third person did: not a missing clip, a
    // missing RIG.
    //
    // The body rig is skinnable (Model_1 decodes), carries the same Punch_Left/Punch_Right clips, and is
    // the same character, so a clone of it stands in. It is a STAND-IN and not the real thing: the game
    // draws purpose-built arms here, and the framing that goes with them (ViewmodelCamera) is authored
    // against that rig's skeleton, so it cannot be borrowed. Until Model_0 decodes, first person shows
    // the body from inside its own head rather than the arms the game would draw.
    private static CharacterSkeleton? ViewmodelFromBody(Node3D? body)
    {
        CharacterSkeleton? clone = Clone(body);
        if (clone != null)
            clone.Name = "Viewmodel";
        return clone;
    }

    // Builds the skinned Zombie_Client character with the real zombie skin tone and face.
    public static Node3D? BuildZombie(string unturnedPath, bool isMega) =>
        BuildEntity(unturnedPath, ZombieEntity(isMega));

    // Both zombie looks use the same prefab, mesh, skeleton, skin and animation clips. Import that large
    // resources.assets payload once, then give the mega template only its tiny material variant. Besides
    // halving warm-up work, this guarantees that meeting the first mega later cannot trigger another full
    // synchronous character import in the gameplay frame.
    public static (Node3D? Normal, Node3D? Mega) BuildZombieTemplates(string unturnedPath)
    {
        Node3D? normal = BuildZombie(unturnedPath, isMega: false);
        Node3D? mega = Clone(normal);
        bool materialChanged = false;
        if (mega?.GetChildOrNull<MeshInstance3D>(0) is { } megaBody
            && megaBody.Mesh?.SurfaceGetMaterial(0) is ShaderMaterial sourceMaterial)
        {
            var material = (ShaderMaterial)sourceMaterial.Duplicate();
            material.SetShaderParameter("skin_color", MegaZombieSkin);
            megaBody.MaterialOverride = material;
            materialChanged = true;
        }

        // A future game version may replace the clothes shader/material shape. Preserve appearance in
        // that case by paying the second import here, behind loading, instead of silently making megas
        // normal-green or deferring the work to their first gameplay frame.
        if (normal != null && !materialChanged)
        {
            mega?.Free();
            mega = BuildZombie(unturnedPath, isMega: true);
        }
        return (normal, mega);
    }

    // Cheap copy of an already-imported character: rebuilds the bone hierarchy and shares the mesh,
    // skin and decoded clips with the template, so populating hundreds of entities decodes the game
    // data exactly once per look.
    public static CharacterSkeleton? Clone(Node3D? template)
    {
        if (template is not CharacterSkeleton source
            || source.GetChildOrNull<MeshInstance3D>(0) is not { } sourceBody)
            return null;

        var skeleton = new CharacterSkeleton { Name = "Skeleton" };
        for (int i = 0; i < source.GetBoneCount(); i++)
            skeleton.AddBone(source.GetBoneName(i));
        for (int i = 0; i < source.GetBoneCount(); i++)
        {
            int parent = source.GetBoneParent(i);
            if (parent >= 0)
                skeleton.SetBoneParent(i, parent);
            skeleton.SetBoneRest(i, source.GetBoneRest(i));
        }
        skeleton.ResetBonePoses();

        var body = new MeshInstance3D
        {
            Mesh = sourceBody.Mesh,
            Skin = sourceBody.Skin,
            MaterialOverride = sourceBody.MaterialOverride,
            Name = "CharacterBody",
        };
        skeleton.AddChild(body);
        body.Skeleton = body.GetPathTo(skeleton);

        foreach ((string name, AnimationClipData clip) in source.Clips)
            skeleton.StoreClip(name, clip);
        return skeleton;
    }

    private static Node3D? BuildEntity(string unturnedPath, EntityConfig entity)
    {
        try
        {
            return Open(unturnedPath) is { } source ? BuildFrom(source, entity) : null;
        }
        catch (System.Exception e)
        {
            Log.PrintErr($"[unturned-godot] Character: failed to load real {entity.Root} ({e.GetType().Name}: {e.Message}); using placeholder.\n{e.StackTrace}");
            return null;
        }
    }

    // resources.assets, decoded, plus the bundle its textures come from. Reading it costs ~100 ms and
    // tens of MB of transient heap, so anything that needs more than one rig out of it opens it once.
    private readonly record struct AssetSource(SerializedFile File,
        Dictionary<long, SerializedObject> ById, string BundlePath);

    private static AssetSource? Open(string unturnedPath)
    {
        string? assetsPath = UnturnedInstall.FindDataFile(unturnedPath, "resources.assets");
        string? bundlePath = UnturnedInstall.FindMasterBundle(unturnedPath);
        if (assetsPath == null || bundlePath == null)
            return null;

        // resources.assets ships with its type trees stripped (enableTypeTree = 0). Type trees are identical
        // across files of the same Unity version, so decode it with the ones gathered from the masterbundle.
        IReadOnlyDictionary<int, List<TypeTreeNode>> classTypeTrees = ModelExtractor.ReadClassTypeTrees(bundlePath);
        SerializedFile file = SerializedFile.Read(File.ReadAllBytes(assetsPath), classTypeTrees);
        var byId = new Dictionary<long, SerializedObject>();
        foreach (SerializedObject o in file.Objects)
            byId[o.PathId] = o;
        return new AssetSource(file, byId, bundlePath);
    }

    // Every skinned renderer the player prefab carries, with the chain of GameObject names above it and
    // whether its mesh decodes with per-vertex skinning. Which of them is the first-person rig — if the
    // prefab separates one at all — is a fact about an authored asset, and this port cannot read that
    // asset in an environment without a client install. Rather than guess a subtree name, ask the
    // install: UG_DUMP_PLAYER_RIG=1 prints what is actually in there.
    private static void DumpRigs(in AssetSource source)
    {
        SerializedFile file = source.File;
        Dictionary<long, SerializedObject> byId = source.ById;
        int found = 0;
        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != ClassSkinnedMeshRenderer)
                continue;
            Dictionary<string, object> smr;
            try { smr = Read(file, byId, o.PathId); }
            catch { continue; }

            long goId = Id(smr["m_GameObject"]);
            var chain = new List<string>();
            long transformId = TransformOf(file, byId, goId);
            while (transformId != 0)
            {
                Dictionary<string, object> t = Read(file, byId, transformId);
                chain.Add((string)Read(file, byId, Id(t["m_GameObject"]))["m_Name"]);
                transformId = Id(t["m_Father"]);
            }
            if (chain.Count == 0 || chain[^1] != PlayerEntity.Root)
                continue;

            string skinning = "no mesh";
            if (byId.TryGetValue(Id(smr["m_Mesh"]), out SerializedObject? meshObj))
            {
                UnityMesh mesh = UnityMesh.Read(Read(file, byId, meshObj.PathId));
                skinning = !mesh.Usable ? "unusable"
                    : mesh.BoneIndices.Length == mesh.Vertices.Length * UnityMesh.BonesPerVertex
                        && mesh.BindPoses.Count > 0 ? "skinned" : "no weights";
            }
            chain.Reverse();
            Log.Print($"[rig-dump] {string.Join(" / ", chain)}  [{skinning}]");
            found++;
        }
        Log.Print($"[rig-dump] {found} skinned renderer(s) under {PlayerEntity.Root}");
    }

    private static Node3D? BuildFrom(in AssetSource source, EntityConfig entity)
    {
        SerializedFile file = source.File;
        Dictionary<long, SerializedObject> byId = source.ById;
        string bundlePath = source.BundlePath;

        // The character prefabs carry two LOD renderers (Model_0/Model_1). Import the first one whose
        // mesh actually decodes with full per-vertex skinning — Model_1, the in-game blocky character;
        // Model_0's weights live in a compressed stream this pipeline doesn't read. Keep the first
        // match as a static-mesh fallback so an unskinnable entity still shows its bind pose.
        Dictionary<string, object>? fallbackSmr = null;
        UnityMesh? fallbackMesh = null;
        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != ClassSkinnedMeshRenderer)
                continue;
            Dictionary<string, object> smr = Read(file, byId, o.PathId);
            // One walk up the father chain answers both questions, and every step of it is a decode.
            // The body import must not pick up the first-person arms, and vice versa.
            (string root, bool inViewmodel) =
                Ancestry(file, byId, Id(smr["m_GameObject"]), ViewmodelSubtree);
            if (root != entity.Root || inViewmodel != entity.FirstPerson)
                continue;
            if (!byId.TryGetValue(Id(smr["m_Mesh"]), out SerializedObject? meshObj))
                continue;
            UnityMesh mesh = UnityMesh.Read(Read(file, byId, meshObj.PathId));
            if (!mesh.Usable)
                continue;

            if (mesh.BoneIndices.Length == mesh.Vertices.Length * UnityMesh.BonesPerVertex && mesh.BindPoses.Count > 0)
                return BuildSkinnedCharacter(file, byId, smr, mesh, bundlePath, entity);
            if (fallbackSmr == null)
            {
                fallbackSmr = smr;
                fallbackMesh = mesh;
            }
        }

        // Fallback: no renderer with skinning data -> static bind-pose mesh.
        if (fallbackMesh != null)
            return new MeshInstance3D { Mesh = BuildMesh(fallbackMesh, entity, skinned: false), Name = "CharacterBody" };
        return null;
    }

    // Builds a Skeleton3D from the renderer's bones (rest poses converted Unity->Godot) with the skinned
    // body mesh as its child. Uses Godot's CreateSkinFromRestTransforms so the bind matrices derive from the
    // rest hierarchy — the mesh is authored in that same bind pose, so this is exact.
    private static Node3D BuildSkinnedCharacter(SerializedFile file, Dictionary<long, SerializedObject> byId,
        Dictionary<string, object> smr, UnityMesh mesh, string bundlePath, EntityConfig entity)
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
            Mesh = BuildMesh(mesh, entity, skinned: true, face: LoadFace(bundlePath, entity.FaceIndex)),
            Skin = skeleton.CreateSkinFromRestTransforms(), // bind matrices from the rest hierarchy
            Name = "CharacterBody",
        };
        skeleton.AddChild(body);
        body.Skeleton = body.GetPathTo(skeleton);

        StoreClips(file, byId, skeleton, boneByName, Id(smr["m_GameObject"]), entity);
        skeleton.BindPitchBones(boneByName.GetValueOrDefault("Spine", -1), boneByName.GetValueOrDefault("Skull", -1));
        skeleton.Play(entity.PrimaryIdle);

        Log.Print($"[unturned-godot] Character: real {entity.Root} skinned body loaded ({boneCount} bones, " +
            (skeleton.HasAnyPose ? "animated" : "bind pose") + ").");
        return skeleton;
    }

    // The clips the on-foot animator plays (PlayerAnimator.updateState): idle + move per stance. They live in
    // the entity's legacy Animation component (m_Animations), keyed by name. If Idle_Stand is missing we fall
    // back to the component's default clip (m_Animation) so a generic entity still gets a resting pose.
    private static void StoreClips(SerializedFile file, Dictionary<long, SerializedObject> byId,
        CharacterSkeleton skeleton, Dictionary<string, int> boneByName, long goId, EntityConfig entity)
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

        foreach (string name in entity.Clips)
            if (clips.TryGetValue(name, out long cid))
                skeleton.StoreClip(name, ReadClip(file, byId, boneByName, cid));

        if (!clips.ContainsKey(entity.PrimaryIdle) && Id(anim["m_Animation"]) is var def && def != 0)
            skeleton.StoreClip(entity.PrimaryIdle, ReadClip(file, byId, boneByName, def));
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
                    : CachedTexture.From(tex, pixels);
            }
        }
        return null;
    }

    // Walks the mesh's GameObject up to its prefab root, returning the root GameObject's name and
    // whether `subtree` was passed on the way. Unturned keeps the first-person arms in a "Viewmodel"
    // subtree of the same prefab (PlayerAnimator finds it under the main camera), so both rigs are rooted
    // at Player_Client and only the ancestry tells them apart — one walk answers both questions.
    private static (string Root, bool InSubtree) Ancestry(SerializedFile file,
        Dictionary<long, SerializedObject> byId, long goId, string subtree)
    {
        long transformId = TransformOf(file, byId, goId);
        if (transformId == 0)
            return (string.Empty, false);
        bool inSubtree = false;
        while (true)
        {
            Dictionary<string, object> t = Read(file, byId, transformId);
            var name = (string)Read(file, byId, Id(t["m_GameObject"]))["m_Name"];
            inSubtree |= name == subtree;
            long father = Id(t["m_Father"]);
            if (father == 0)
                return (name, inSubtree);
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
    private static ArrayMesh BuildMesh(UnityMesh mesh, EntityConfig entity, bool skinned, ImageTexture? face = null)
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
        material.SetShaderParameter("skin_color", entity.Skin);
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
