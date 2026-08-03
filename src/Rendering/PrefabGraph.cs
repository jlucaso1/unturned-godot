using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

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

// One collider on a prefab GameObject: its Unity primitive parameters (or a collision-mesh id) and its pose
// relative to the prefab root. Values stay in Unity space; the Unity->Godot flip and shape construction
// happen when the collision body is built.
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

    private ColliderPart(EColliderKind kind, Transform3D localToRoot, Vector3 center, Vector3 size,
        float radius, float height, int direction, long meshId)
    {
        Kind = kind;
        LocalToRoot = localToRoot;
        Center = center;
        Size = size;
        Radius = radius;
        Height = height;
        Direction = direction;
        MeshId = meshId;
    }

    public static ColliderPart Box(Transform3D t, Vector3 center, Vector3 size)
        => new(EColliderKind.Box, t, center, size, 0f, 0f, 0, 0);
    public static ColliderPart Sphere(Transform3D t, Vector3 center, float radius)
        => new(EColliderKind.Sphere, t, center, Vector3.Zero, radius, 0f, 0, 0);
    public static ColliderPart Capsule(Transform3D t, Vector3 center, float radius, float height, int direction)
        => new(EColliderKind.Capsule, t, center, Vector3.Zero, radius, height, direction, 0);
    public static ColliderPart Mesh(Transform3D t, long meshId)
        => new(EColliderKind.Mesh, t, Vector3.Zero, Vector3.Zero, 0f, 0f, 0, meshId);
}

// Reads a masterbundle SerializedFile's prefab structure: every object by path id, the asset container,
// and each prefab's renderable mesh parts grouped by key (objects/<folder> or trees/<folder>), kept per
// LOD level so distant instances can render the authored lower-detail mesh instead of LOD-0. This
// is the shape ModelExtractor walks; material/texture resolution layers on top via MaterialResolver.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class PrefabGraph
{
    // VehicleAsset loads its model from this prefab (bundle.loadDeferred("Vehicle", ...)).
    private const string VehiclePrefabFile = "vehicle.prefab";

    // Unity class ids, as they appear in the SerializedFile's object table.
    private const int MeshFilterClassId = 33;
    private const int MeshRendererClassId = 23;
    private const int SkinnedMeshRendererClassId = 137;

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
        Dictionary<string, List<MeshPart>> partsByKey = MapObjectKeysToMeshes(
            file, out Dictionary<string, List<MeshPart>> lod1PartsByKey,
            objectsByPathId, pathByRootGo, goToTransform, transformFather, transformGo, localById);
        Dictionary<string, List<ColliderPart>> collidersByKey = MapObjectKeysToColliders(
            file, objectsByPathId, pathByRootGo, goToTransform, transformFather, transformGo, localById);

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

    // Groups each prefab's LOD-0 renderable parts by key. Highest detail is the "*_0" suffix (Model_0,
    // Foliage_0); a prefab whose meshes carry no LOD names falls back to its first mesh.
    private static Dictionary<string, List<MeshPart>> MapObjectKeysToMeshes(SerializedFile file,
        out Dictionary<string, List<MeshPart>> lod1ByKey,
        Dictionary<long, SerializedObject> objectsByPathId, Dictionary<long, string> pathByRootGo,
        Dictionary<long, long> goToTransform, Dictionary<long, long> transformFather,
        Dictionary<long, long> transformGo, Dictionary<long, Transform3D> localById)
    {
        var lod0 = new Dictionary<string, List<MeshPart>>();
        var lod1 = new Dictionary<string, List<MeshPart>>();
        var always = new Dictionary<string, List<MeshPart>>();
        var firstByKey = new Dictionary<string, MeshPart>();
        var nameCache = new Dictionary<long, string>();

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
            if (!goToTransform.TryGetValue(goId, out long tId))
                continue;

            // Walk up to the prefab root, composing each level's local pose (but not the root's, which
            // is where the world placement goes).
            Transform3D localToRoot = Transform3D.Identity;
            long cur = tId;
            while (transformFather.TryGetValue(cur, out long father) && father != 0)
            {
                if (localById.TryGetValue(cur, out Transform3D local))
                    localToRoot = local * localToRoot;
                cur = father;
            }
            if (!transformGo.TryGetValue(cur, out long rootGo) || !pathByRootGo.TryGetValue(rootGo, out string? path))
                continue;

            string key = PrefabKey(path);
            var part = new MeshPart(meshId, MeshRendererMaterials(file, objectsByPathId, goId), localToRoot);
            string partName = GameObjectName(file, objectsByPathId, nameCache, goId);
            bool isVehicle = path.EndsWith("/" + VehiclePrefabFile, StringComparison.Ordinal);
            switch (LevelOf(partName, isVehicle))
            {
                case 0:
                    Add(lod0, key, part);
                    break;
                case 1:
                    Add(lod1, key, part);
                    break;
                case AlwaysLevel:
                    // Rendered whatever the distance. Held aside rather than added to the lower level
                    // here: a prefab with no authored lower level must not gain one made of nothing but
                    // its seats and lights, which would then replace the whole vehicle at distance.
                    Add(lod0, key, part);
                    Add(always, key, part);
                    break;
            }
            firstByKey.TryAdd(key, part);
        }

        var result = new Dictionary<string, List<MeshPart>>();
        lod1ByKey = new Dictionary<string, List<MeshPart>>();
        foreach (KeyValuePair<string, MeshPart> kv in firstByKey)
        {
            bool authored = lod0.TryGetValue(kv.Key, out List<MeshPart>? parts);
            result[kv.Key] = authored ? parts! : new List<MeshPart> { kv.Value };
            // A lower level is only meaningful against an authored LOD-0 set. Where the fallback above
            // picked an unnamed first mesh, there is no level structure to switch between.
            if (!authored || !lod1.TryGetValue(kv.Key, out List<MeshPart>? lower))
                continue;
            if (always.TryGetValue(kv.Key, out List<MeshPart>? unlevelled))
                lower.AddRange(unlevelled); // the parts that render at every distance, this one included
            lod1ByKey[kv.Key] = lower;
        }
        return result;
    }

    private static void Add(Dictionary<string, List<MeshPart>> byKey, string key, MeshPart part)
    {
        if (!byKey.TryGetValue(key, out List<MeshPart>? list))
            byKey[key] = list = new List<MeshPart>();
        list.Add(part);
    }

    // Which detail level a prefab's renderable child belongs to, or AlwaysLevel for one that is drawn at
    // every distance. Unturned names a levelled renderer "<something>_<n>" — Model_0/Model_1 overwhelmingly
    // — and the port reads that suffix rather than the LODGroup component the editor also writes.
    //
    // A vehicle is the one prefab where that reading is wrong: VehicleAsset addresses a vehicle's parts by
    // name (Seats/Seat_#, Headlights, Sirens), so Seat_0 and Seat_1 are the driver's and passenger's seat,
    // not two levels of one seat. Take only the Model_<n> chain as levels there; everything else is a part
    // of the vehicle that is always drawn.
    private const int AlwaysLevel = -1;
    private const int IgnoredLevel = -2;

    private static int LevelOf(string partName, bool isVehicle)
    {
        if (!isVehicle)
        {
            if (partName.EndsWith("_0", StringComparison.Ordinal))
                return 0;
            return partName.EndsWith("_1", StringComparison.Ordinal) ? 1 : IgnoredLevel;
        }

        if (partName.StartsWith("Model_", StringComparison.Ordinal))
            return partName is "Model_0" ? 0 : partName is "Model_1" ? 1 : IgnoredLevel;

        // A depth mask writes depth only in Unity, hiding what is behind it rather than drawing itself;
        // rendered as ordinary geometry it would be a solid slab through the vehicle.
        return partName is "DepthMask" ? IgnoredLevel : AlwaysLevel;
    }

    // Each prefab's collision colliders, keyed like PartsByKey. Colliders on the server-only navmesh ("Nav")
    // or the editor placement blocker ("Block") are skipped — the player-blocking colliders sit on the
    // object root (and "Door" children). Values are Unity-space; shape building happens later.
    private static Dictionary<string, List<ColliderPart>> MapObjectKeysToColliders(SerializedFile file,
        Dictionary<long, SerializedObject> objectsByPathId, Dictionary<long, string> pathByRootGo,
        Dictionary<long, long> goToTransform, Dictionary<long, long> transformFather,
        Dictionary<long, long> transformGo, Dictionary<long, Transform3D> localById)
    {
        var byKey = new Dictionary<string, List<ColliderPart>>();
        var nameCache = new Dictionary<long, string>();

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
            if (GameObjectName(file, objectsByPathId, nameCache, goId) is "Nav" or "Block")
                continue;
            if (!goToTransform.TryGetValue(goId, out long tId))
                continue;

            // Walk up to the prefab root, composing each level's local pose (not the root's — that is the
            // world placement).
            Transform3D localToRoot = Transform3D.Identity;
            long cur = tId;
            while (transformFather.TryGetValue(cur, out long father) && father != 0)
            {
                if (localById.TryGetValue(cur, out Transform3D local))
                    localToRoot = local * localToRoot;
                cur = father;
            }
            if (!transformGo.TryGetValue(cur, out long rootGo) || !pathByRootGo.TryGetValue(rootGo, out string? path))
                continue;

            string key = PrefabKey(path);
            if (!byKey.TryGetValue(key, out List<ColliderPart>? list))
                byKey[key] = list = new List<ColliderPart>();
            list.Add(ReadCollider(kind, c, localToRoot));
        }
        return byKey;
    }

    private static ColliderPart ReadCollider(EColliderKind kind, Dictionary<string, object> c, Transform3D t)
        => kind switch
        {
            EColliderKind.Box => ColliderPart.Box(t, Vec(c["m_Center"]), Vec(c["m_Size"])),
            EColliderKind.Sphere => ColliderPart.Sphere(t, Vec(c["m_Center"]), F(c["m_Radius"])),
            EColliderKind.Capsule => ColliderPart.Capsule(t, Vec(c["m_Center"]), F(c["m_Radius"]),
                F(c["m_Height"]), Convert.ToInt32(c["m_Direction"])),
            _ => ColliderPart.Mesh(t, PathId((Dictionary<string, object>)c["m_Mesh"])),
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

    private static string GameObjectName(SerializedFile file, Dictionary<long, SerializedObject> objects,
        Dictionary<long, string> cache, long goId)
    {
        if (cache.TryGetValue(goId, out string? name))
            return name;
        name = objects.TryGetValue(goId, out SerializedObject? go)
            ? (string)TypeTreeReader.Read(go.TypeTree, file.ReaderFor(go))["m_Name"]
            : string.Empty;
        cache[goId] = name;
        return name;
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
