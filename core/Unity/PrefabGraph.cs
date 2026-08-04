using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Unity;

// One renderable child of a prefab: its mesh, that mesh's materials, and its pose relative to the prefab
// root (baked into the vertices so multiple parts can share one indexed mesh).
public readonly struct MeshPart
{
    public readonly long MeshId;
    public readonly List<long> Materials;
    public readonly Transform3D LocalToRoot;

    public MeshPart(long meshId, List<long> materials, Transform3D localToRoot)
    {
        MeshId = meshId;
        Materials = materials;
        LocalToRoot = localToRoot;
    }
}

// One collider on a prefab GameObject: its Unity primitive parameters (or a collision-mesh id), its pose
// relative to the prefab root, and the Unity layer its GameObject sits on. Values stay in Unity space; the
// Unity->Godot flip and shape construction happen when the collision body is built.
public readonly struct ColliderPart
{
    public readonly EColliderKind Kind;
    public readonly Transform3D LocalToRoot;
    public readonly Vector3 Center; // Box/Sphere/Capsule, local to the collider
    public readonly Vector3 Size;   // Box
    public readonly float Radius;   // Sphere/Capsule
    public readonly float Height;   // Capsule
    public readonly int Direction;  // Capsule axis: 0=X, 1=Y, 2=Z
    public readonly long MeshId;    // Mesh (collision mesh, distinct from the render Model_0)

    // The GameObject's Unity layer (UnityLayers). What a collider is FOR, and the only place the shipped
    // content says it: a ladder's climbing volume is an ordinary box everywhere else.
    public readonly int Layer;

    private ColliderPart(EColliderKind kind, Transform3D localToRoot, Vector3 center, Vector3 size,
        float radius, float height, int direction, long meshId, int layer)
    {
        Kind = kind;
        LocalToRoot = localToRoot;
        Center = center;
        Size = size;
        Radius = radius;
        Height = height;
        Direction = direction;
        MeshId = meshId;
        Layer = layer;
    }

    public static ColliderPart Box(Transform3D t, Vector3 center, Vector3 size, int layer = 0)
        => new(EColliderKind.Box, t, center, size, 0f, 0f, 0, 0, layer);
    public static ColliderPart Sphere(Transform3D t, Vector3 center, float radius, int layer = 0)
        => new(EColliderKind.Sphere, t, center, Vector3.Zero, radius, 0f, 0, 0, layer);
    public static ColliderPart Capsule(Transform3D t, Vector3 center, float radius, float height,
        int direction, int layer = 0)
        => new(EColliderKind.Capsule, t, center, Vector3.Zero, radius, height, direction, 0, layer);
    public static ColliderPart Mesh(Transform3D t, long meshId, int layer = 0)
        => new(EColliderKind.Mesh, t, Vector3.Zero, Vector3.Zero, 0f, 0f, 0, meshId, layer);
}

// Reads a masterbundle SerializedFile's prefab structure: every object by path id, the asset container,
// and each prefab's renderable mesh parts grouped by key (objects/<folder> or trees/<folder>), kept per
// LOD level so distant instances can render the authored lower-detail mesh instead of LOD-0. This
// is the shape ModelExtractor walks; material/texture resolution layers on top via MaterialResolver.
//
// WHICH parts survive is PrefabParts' decision, and is tested there; this class is the walk that feeds it —
// reading the object graph and composing each part's pose relative to its prefab root.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class PrefabGraph
{
    // VehicleAsset loads its model from this prefab (bundle.loadDeferred("Vehicle", ...)).
    private const string VehiclePrefabFile = "vehicle.prefab";

    // Unity class ids, as they appear in the SerializedFile's object table.
    private const int MeshFilterClassId = 33;
    private const int MeshRendererClassId = 23;
    private const int SkinnedMeshRendererClassId = 137;
    private const int AnimationClassId = 111;
    private const int AnimatorClassId = 95;

    public SerializedFile File { get; }
    public IReadOnlyDictionary<long, SerializedObject> ObjectsByPathId { get; }
    public IReadOnlyDictionary<string, long> ContainerByPath { get; }
    public string AssetPrefix { get; }
    public IReadOnlyDictionary<string, List<MeshPart>> PartsByKey { get; }
    // The "*_1" sibling of each PartsByKey entry, where the prefab ships one. Absent for a prefab whose
    // author gave it a single level, which then renders at full detail everywhere, as it did before.
    public IReadOnlyDictionary<string, List<MeshPart>> Lod1PartsByKey { get; }
    public IReadOnlyDictionary<string, List<ColliderPart>> CollidersByKey { get; }

    private PrefabGraph(SerializedFile file, Dictionary<long, SerializedObject> objectsByPathId,
        Dictionary<string, long> containerByPath, string assetPrefix,
        Dictionary<string, List<MeshPart>> partsByKey,
        Dictionary<string, List<MeshPart>> lod1PartsByKey,
        Dictionary<string, List<ColliderPart>> collidersByKey)
    {
        File = file;
        ObjectsByPathId = objectsByPathId;
        ContainerByPath = containerByPath;
        AssetPrefix = assetPrefix;
        PartsByKey = partsByKey;
        Lod1PartsByKey = lod1PartsByKey;
        CollidersByKey = collidersByKey;
    }

    public static PrefabGraph Read(SerializedFile file)
    {
        var objectsByPathId = new Dictionary<long, SerializedObject>();
        foreach (SerializedObject o in file.Objects)
            objectsByPathId[o.PathId] = o;

        Dictionary<string, long> containerByPath = ReadContainer(file, out string assetPrefix,
            out Dictionary<long, string> pathByRootGo);
        BuildTransformMaps(file, out var goToTransform, out var transformFather, out var transformGo,
            out var localById);
        var walk = new PrefabWalk(file, objectsByPathId, pathByRootGo, goToTransform, transformFather,
            transformGo, localById);

        Dictionary<string, List<MeshPart>> partsByKey = MapObjectKeysToMeshes(
            file, out Dictionary<string, List<MeshPart>> lod1PartsByKey, objectsByPathId, walk);
        Dictionary<string, List<ColliderPart>> collidersByKey = MapObjectKeysToColliders(file, walk);

        return new PrefabGraph(file, objectsByPathId, containerByPath, assetPrefix, partsByKey,
            lod1PartsByKey, collidersByKey);
    }

    private static Dictionary<string, long> ReadContainer(SerializedFile file, out string assetPrefix,
        out Dictionary<long, string> pathByRootGo)
    {
        var containerByPath = new Dictionary<string, long>();
        pathByRootGo = new Dictionary<long, string>();
        assetPrefix = string.Empty;

        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != 142) // AssetBundle
                continue;
            Dictionary<string, object> ab = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
            foreach (object entry in (List<object>)ab["m_Container"])
            {
                var pair = (Dictionary<string, object>)entry;
                string path = (string)pair["first"];
                var info = (Dictionary<string, object>)pair["second"];
                long assetId = PathId((Dictionary<string, object>)info["asset"]);
                containerByPath[path] = assetId;

                int idx = path.IndexOf("objects/", StringComparison.Ordinal);
                if (assetPrefix.Length == 0 && idx > 0)
                    assetPrefix = path[..idx];
                // The three prefab names Unturned instantiates a placed asset from: ObjectAsset's
                // Object.prefab, ResourceAsset's Resource.prefab and VehicleAsset's Vehicle.prefab.
                if (path.EndsWith("/object.prefab", StringComparison.Ordinal) ||
                    path.EndsWith("/resource.prefab", StringComparison.Ordinal) ||
                    path.EndsWith("/" + VehiclePrefabFile, StringComparison.Ordinal))
                    pathByRootGo[assetId] = path;
            }
        }
        return containerByPath;
    }

    private static void BuildTransformMaps(SerializedFile file,
        out Dictionary<long, long> goToTransform,
        out Dictionary<long, long> transformFather,
        out Dictionary<long, long> transformGo,
        out Dictionary<long, Transform3D> localById)
    {
        goToTransform = new Dictionary<long, long>();
        transformFather = new Dictionary<long, long>();
        transformGo = new Dictionary<long, long>();
        localById = new Dictionary<long, Transform3D>();

        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != 4) // Transform
                continue;
            Dictionary<string, object> t = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
            long goId = PathId((Dictionary<string, object>)t["m_GameObject"]);
            goToTransform[goId] = o.PathId;
            transformFather[o.PathId] = PathId((Dictionary<string, object>)t["m_Father"]);
            transformGo[o.PathId] = goId;
            localById[o.PathId] = LocalTransformOf(t);
        }
    }

    // A Transform's local pose (Unity space; the Unity->Godot flip happens once on the final vertices).
    private static Transform3D LocalTransformOf(Dictionary<string, object> t)
    {
        var p = (Dictionary<string, object>)t["m_LocalPosition"];
        var r = (Dictionary<string, object>)t["m_LocalRotation"];
        var s = (Dictionary<string, object>)t["m_LocalScale"];
        var position = new Vector3(F(p["x"]), F(p["y"]), F(p["z"]));
        var rotation = new Quaternion(F(r["x"]), F(r["y"]), F(r["z"]), F(r["w"]));
        var scale = new Vector3(F(s["x"]), F(s["y"]), F(s["z"]));
        var b = new Basis(rotation);
        // Scale the basis columns (R*S); Basis.Scaled would apply S*R and skew rotated children.
        return new Transform3D(new Basis(b.X * scale.X, b.Y * scale.Y, b.Z * scale.Z), position);
    }

    // Where one component's GameObject sits inside its prefab: the prefab's key, the pose relative to the
    // prefab root, the name of the GameObject the component is on, whether that prefab is a vehicle, and
    // whether an Animation/Animator anywhere between the component and the prefab root drives this subtree.
    private readonly record struct Anchor(string Key, Transform3D LocalToRoot, string PartName,
        bool IsVehicle, bool Animated);

    // The walk from a component's GameObject up to its prefab root. Shared by the mesh and collider passes
    // so both answer the hidden-state question the same way.
    private sealed class PrefabWalk
    {
        private readonly SerializedFile _file;
        private readonly Dictionary<long, SerializedObject> _objects;
        private readonly Dictionary<long, string> _pathByRootGo;
        private readonly Dictionary<long, long> _goToTransform;
        private readonly Dictionary<long, long> _transformFather;
        private readonly Dictionary<long, long> _transformGo;
        private readonly Dictionary<long, Transform3D> _localById;
        private readonly Dictionary<long, (string Name, int Layer, bool Animated)> _goCache = new();

        public PrefabWalk(SerializedFile file, Dictionary<long, SerializedObject> objects,
            Dictionary<long, string> pathByRootGo, Dictionary<long, long> goToTransform,
            Dictionary<long, long> transformFather, Dictionary<long, long> transformGo,
            Dictionary<long, Transform3D> localById)
        {
            _file = file;
            _objects = objects;
            _pathByRootGo = pathByRootGo;
            _goToTransform = goToTransform;
            _transformFather = transformFather;
            _transformGo = transformGo;
            _localById = localById;
        }

        public string NameOf(long goId) => GameObjectOf(goId).Name;

        // The GameObject's Unity layer, or 0 (Default) when the file does not carry it. Read from the same
        // TypeTree pass as the name so a collider's layer costs no extra decode.
        public int LayerOf(long goId) => GameObjectOf(goId).Layer;

        private (string Name, int Layer, bool Animated) GameObjectOf(long goId)
        {
            if (_goCache.TryGetValue(goId, out (string Name, int Layer, bool Animated) cached))
                return cached;
            if (_objects.TryGetValue(goId, out SerializedObject? go))
            {
                Dictionary<string, object> fields = TypeTreeReader.Read(go.TypeTree, _file.ReaderFor(go));
                cached = ((string)fields["m_Name"],
                    fields.TryGetValue("m_Layer", out object? layer) ? Convert.ToInt32(layer) : 0,
                    CarriesAnimation(fields));
            }
            else
            {
                cached = (string.Empty, 0, false);
            }
            _goCache[goId] = cached;
            return cached;
        }

        // Whether this GameObject holds the component that poses a skeleton: Unturned's animated props
        // ("Close"/"Open" on a cabinet, a door, a container) hang one Animation off the prefab's "Root".
        private bool CarriesAnimation(Dictionary<string, object> gameObject)
        {
            if (!gameObject.TryGetValue("m_Component", out object? components)
                || components is not List<object> list)
            {
                return false;
            }

            foreach (object component in list)
            {
                long compId = PathId(
                    (Dictionary<string, object>)((Dictionary<string, object>)component)["component"]);
                if (_objects.TryGetValue(compId, out SerializedObject? comp)
                    && comp.ClassId is AnimationClassId or AnimatorClassId)
                {
                    return true;
                }
            }
            return false;
        }

        // Null when the GameObject is not part of a prefab this reader instantiates, or when the chain up
        // to the prefab root passes through one of Unturned's hidden alternate-state nodes: that geometry
        // is authored but never drawn on a level that has just loaded.
        public Anchor? AnchorOf(long goId)
        {
            if (!_goToTransform.TryGetValue(goId, out long tId))
                return null;

            // Walk up to the prefab root, composing each level's local pose (but not the root's, which is
            // where the world placement goes).
            Transform3D localToRoot = Transform3D.Identity;
            bool animated = false;
            long cur = tId;
            while (_transformFather.TryGetValue(cur, out long father) && father != 0)
            {
                if (_transformGo.TryGetValue(cur, out long ownerGo))
                {
                    (string name, _, bool carriesAnimation) = GameObjectOf(ownerGo);
                    if (PrefabParts.IsHiddenState(name))
                        return null;
                    animated |= carriesAnimation;
                }
                if (_localById.TryGetValue(cur, out Transform3D local))
                    localToRoot = local * localToRoot;
                cur = father;
            }

            if (!_transformGo.TryGetValue(cur, out long rootGo)
                || !_pathByRootGo.TryGetValue(rootGo, out string? path))
            {
                return null;
            }
            animated |= GameObjectOf(rootGo).Animated;

            return new Anchor(PrefabKey(path), localToRoot, NameOf(goId),
                path.EndsWith("/" + VehiclePrefabFile, StringComparison.Ordinal), animated);
        }
    }

    // Groups each prefab's renderable parts by key and detail level. PrefabPartSet owns the choice of what
    // is drawn; this only decides which components are renderable at all and where they sit.
    private static Dictionary<string, List<MeshPart>> MapObjectKeysToMeshes(SerializedFile file,
        out Dictionary<string, List<MeshPart>> lod1ByKey,
        Dictionary<long, SerializedObject> objectsByPathId, PrefabWalk walk)
    {
        var parts = new PrefabPartSet<MeshPart>();

        foreach (SerializedObject o in file.Objects)
        {
            // A static prop hangs its geometry off a MeshFilter; an animated one (the sliding and blast
            // doors, for instance) has a SkinnedMeshRenderer instead, which names its mesh the same way.
            // Only the MeshFilter path was read before, so those objects rendered as nothing at all.
            if (o.ClassId != MeshFilterClassId && o.ClassId != SkinnedMeshRendererClassId)
                continue;
            Dictionary<string, object> mf = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
            var meshPptr = (Dictionary<string, object>)mf["m_Mesh"];
            if (Convert.ToInt32(meshPptr["m_FileID"]) != 0)
                continue; // built-in Unity primitive on a light/collider part
            long meshId = PathId(meshPptr);
            if (meshId == 0 || !objectsByPathId.ContainsKey(meshId))
                continue;

            long goId = PathId((Dictionary<string, object>)mf["m_GameObject"]);
            if (walk.AnchorOf(goId) is not { } anchor)
                continue;

            Transform3D localToRoot = o.ClassId == SkinnedMeshRendererClassId
                ? SkinnedLocalToRoot(mf, anchor.LocalToRoot, anchor.Animated)
                : anchor.LocalToRoot;

            parts.Add(anchor.Key, anchor.PartName, anchor.IsVehicle,
                new MeshPart(meshId, MeshRendererMaterials(file, objectsByPathId, goId), localToRoot));
        }

        (Dictionary<string, List<MeshPart>> baseLevel, Dictionary<string, List<MeshPart>> lower) =
            parts.Resolve();
        lod1ByKey = lower;
        return baseLevel;
    }

    // Where a SkinnedMeshRenderer's mesh actually renders, relative to its prefab root.
    //
    // A skinned mesh does not have to sit where its GameObject does: Unity poses those vertices from the
    // bones alone — vertex = bone.localToWorld * bindpose * v — and never reads the renderer's own
    // transform. That is why the bone can live in a different subtree entirely: the glass in Cooler_0 is a
    // SkinnedMeshRenderer under "Root" whose only bone is "Hinge" under "Skeleton".
    //
    // Two things follow, and the shipped bundle needs both:
    //
    //  * With an Animation driving the skeleton, the bone poses the prefab was saved with are not the ones
    //    it renders at — that component is there precisely to overwrite them, and every one of these props
    //    carries a "Close"/"Open" pair whose resting state is the closed one. What draws on a level that
    //    has just loaded is the bind pose, and the bind pose is the space the vertices are already in.
    //    Composing the GameObject chain instead took the +90 degrees about X that "Root" carries and
    //    applied it to geometry that was already in the prefab's own frame: the display cases' glass, the
    //    counters' doors and the ovens' hobs all came out lying flat and thrown clear of the body they
    //    belong to. Measured over the whole core bundle, every one of the 39 animated skinned parts sits
    //    inside its prefab's static body at the bind pose and outside it under the GameObject chain.
    //  * With nothing driving it, the bones stay where the prefab put them and the renderer's own chain is
    //    the right answer after all — which is what the two vehicles that skin their tracks (the Tank's
    //    and the Explorer's) need. Forcing those to the prefab root threw the tracks 4 units off the hull.
    //
    // A renderer with no bones at all cannot be posed by a skeleton either, so it keeps its chain too.
    internal static Transform3D SkinnedLocalToRoot(Dictionary<string, object> renderer,
        Transform3D localToRoot, bool animated)
    {
        bool hasBones = renderer.TryGetValue("m_Bones", out object? bones)
            && bones is List<object> { Count: > 0 };
        return hasBones && animated ? Transform3D.Identity : localToRoot;
    }

    // Each prefab's collision colliders, keyed like PartsByKey. Colliders on the server-only navmesh ("Nav")
    // or the editor placement blocker ("Block") are skipped — the player-blocking colliders sit on the
    // object root (and "Door" children). Colliders under a hidden state node are skipped by the walk, so a
    // wreck's ragdoll capsules do not stand in the world as solid geometry nothing draws.
    // Values are Unity-space; shape building happens later.
    private static Dictionary<string, List<ColliderPart>> MapObjectKeysToColliders(SerializedFile file,
        PrefabWalk walk)
    {
        var byKey = new Dictionary<string, List<ColliderPart>>();

        foreach (SerializedObject o in file.Objects)
        {
            EColliderKind kind;
            switch (o.ClassId)
            {
                case 65: kind = EColliderKind.Box; break;     // BoxCollider
                case 135: kind = EColliderKind.Sphere; break; // SphereCollider
                case 136: kind = EColliderKind.Capsule; break; // CapsuleCollider
                case 64: kind = EColliderKind.Mesh; break;    // MeshCollider
                default: continue;
            }

            Dictionary<string, object> c = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
            long goId = PathId((Dictionary<string, object>)c["m_GameObject"]);
            if (walk.NameOf(goId) is "Nav" or "Block")
                continue;
            if (walk.AnchorOf(goId) is not { } anchor)
                continue;

            if (!byKey.TryGetValue(anchor.Key, out List<ColliderPart>? list))
                byKey[anchor.Key] = list = new List<ColliderPart>();
            list.Add(ReadCollider(kind, c, anchor.LocalToRoot, walk.LayerOf(goId)));
        }
        return byKey;
    }

    private static ColliderPart ReadCollider(EColliderKind kind, Dictionary<string, object> c, Transform3D t,
        int layer)
        => kind switch
        {
            EColliderKind.Box => ColliderPart.Box(t, Vec(c["m_Center"]), Vec(c["m_Size"]), layer),
            EColliderKind.Sphere => ColliderPart.Sphere(t, Vec(c["m_Center"]), F(c["m_Radius"]), layer),
            EColliderKind.Capsule => ColliderPart.Capsule(t, Vec(c["m_Center"]), F(c["m_Radius"]),
                F(c["m_Height"]), Convert.ToInt32(c["m_Direction"]), layer),
            _ => ColliderPart.Mesh(t, PathId((Dictionary<string, object>)c["m_Mesh"]), layer),
        };

    private static Vector3 Vec(object value)
    {
        var d = (Dictionary<string, object>)value;
        return new Vector3(F(d["x"]), F(d["y"]), F(d["z"]));
    }

    // The material path ids on the GameObject's MeshRenderer, in submesh order.
    private static List<long> MeshRendererMaterials(SerializedFile file,
        Dictionary<long, SerializedObject> objects, long goId)
    {
        var materials = new List<long>();
        if (!objects.TryGetValue(goId, out SerializedObject? go))
            return materials;

        Dictionary<string, object> gameObject = TypeTreeReader.Read(go.TypeTree, file.ReaderFor(go));
        foreach (object component in (List<object>)gameObject["m_Component"])
        {
            long compId = PathId((Dictionary<string, object>)((Dictionary<string, object>)component)["component"]);
            // MeshRenderer or SkinnedMeshRenderer: both carry the material list the same way.
            if (!objects.TryGetValue(compId, out SerializedObject? comp)
                || (comp.ClassId != MeshRendererClassId && comp.ClassId != SkinnedMeshRendererClassId))
            {
                continue;
            }

            Dictionary<string, object> renderer = TypeTreeReader.Read(comp.TypeTree, file.ReaderFor(comp));
            foreach (object m in (List<object>)renderer["m_Materials"])
                materials.Add(PathId((Dictionary<string, object>)m));
            break;
        }
        return materials;
    }

    // ".../objects/small/business/cardboard_0/object.prefab" -> "objects/small/business/cardboard_0";
    // ".../trees/birch_1/resource.prefab" -> "trees/birch_1". The category prefix keeps object and tree
    // keys from colliding and matches the keys built from each asset's bundle folder.
    // The container key without the bundle's own prefix or the file name: "assets/coremasterbundle/
    // objects/medium/props/x/object.prefab" -> "objects/medium/props/x". Every master bundle is built
    // under "Assets/<Name>MasterBundle", including workshop mods, so the first two segments are the
    // prefix. Matching on known folder names instead would miss a mod's own layout: the game keeps its
    // harvestables under Trees/ while a mod keeps them under Resources/.
    private static string PrefabKey(string path)
    {
        int start = 0;
        if (path.StartsWith("assets/", StringComparison.Ordinal))
        {
            int second = path.IndexOf('/', "assets/".Length);
            if (second > 0)
                start = second + 1;
        }

        string rest = path[start..];
        int file = rest.LastIndexOf('/');
        return file > 0 ? rest[..file] : rest; // drop the "/*.prefab" filename
    }

    private static float F(object value) => Convert.ToSingle(value);

    internal static long PathId(Dictionary<string, object> pptr) => Convert.ToInt64(pptr["m_PathID"]);
}
