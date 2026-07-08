using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// One-time extraction: parse the masterbundle, walk the prefab graph to map each object GUID to its
// Model_0 mesh, and cache compact per-GUID mesh files. Excluded from coverage — orchestration glue
// over the (fully tested) Core parser; correctness is validated end-to-end by the rendered scene.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class ModelExtractor
{
    private const long SerializedFileCap = 200_000_000; // decode only the SerializedFile prefix

    public static int Extract(string bundlePath, string objectBundlesDir,
        HashSet<Guid> neededGuids, string cacheDir)
    {
        UnityBundle bundle = UnityBundle.Read(File.ReadAllBytes(bundlePath), SerializedFileCap);
        byte[] sfBytes = Array.Empty<byte>();
        foreach (KeyValuePair<string, byte[]> f in bundle.Files)
            if (!f.Key.EndsWith(".resS") && !f.Key.EndsWith(".resource"))
                sfBytes = f.Value;

        SerializedFile file = SerializedFile.Read(sfBytes);
        var objectsByPathId = new Dictionary<long, SerializedObject>();
        foreach (SerializedObject o in file.Objects)
            objectsByPathId[o.PathId] = o;

        Dictionary<long, string> pathByRootGo = ReadContainer(file);
        BuildTransformMaps(file, out var goToTransform, out var transformFather, out var transformGo);
        Dictionary<string, long> meshIdByKey = MapObjectKeysToMeshes(
            file, objectsByPathId, pathByRootGo, goToTransform, transformFather, transformGo);

        // Map each needed object GUID to its prefab key via the .dat folder path.
        ObjectAssetDatabase db = ObjectAssetDatabase.ScanDirectory(objectBundlesDir);

        Directory.CreateDirectory(cacheDir);
        int extracted = 0, noMesh = 0;
        var mappedGuids = new HashSet<Guid>();
        foreach (ObjectAsset asset in db.All)
        {
            if (!neededGuids.Contains(asset.Guid) || !mappedGuids.Add(asset.Guid))
                continue;
            string key = asset.BundleOverridePath is { Length: > 0 } ovr
                ? OverrideKey(ovr)
                : FolderKey(asset.Directory, objectBundlesDir);
            if (!meshIdByKey.TryGetValue(key, out long meshId) ||
                !objectsByPathId.TryGetValue(meshId, out SerializedObject? meshObj))
            {
                noMesh++;
                continue;
            }

            Dictionary<string, object> dict = TypeTreeReader.Read(meshObj.TypeTree, file.ReaderFor(meshObj));
            UnityMesh mesh = UnityMesh.Read(dict);
            if (!mesh.Usable)
                continue;

            using var stream = File.Create(Path.Combine(cacheDir, asset.Guid.ToString("N") + ".mesh"));
            MeshCache.Write(stream, mesh.Vertices, mesh.Normals, mesh.Indices);
            extracted++;
        }
        Godot.GD.Print($"[extract] needed={mappedGuids.Count} extracted={extracted} unmapped={noMesh}");
        return extracted;
    }

    private static Dictionary<long, string> ReadContainer(SerializedFile file)
    {
        var pathByRootGo = new Dictionary<long, string>();
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
                if (path.Contains("/objects/") && path.EndsWith("/object.prefab"))
                    pathByRootGo[assetId] = path;
            }
        }
        return pathByRootGo;
    }

    private static void BuildTransformMaps(SerializedFile file,
        out Dictionary<long, long> goToTransform,
        out Dictionary<long, long> transformFather,
        out Dictionary<long, long> transformGo)
    {
        goToTransform = new Dictionary<long, long>();
        transformFather = new Dictionary<long, long>();
        transformGo = new Dictionary<long, long>();

        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != 4) // Transform
                continue;
            Dictionary<string, object> t = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
            long goId = PathId((Dictionary<string, object>)t["m_GameObject"]);
            long fatherId = PathId((Dictionary<string, object>)t["m_Father"]);
            goToTransform[goId] = o.PathId;
            transformFather[o.PathId] = fatherId;
            transformGo[o.PathId] = goId;
        }
    }

    private static Dictionary<string, long> MapObjectKeysToMeshes(SerializedFile file,
        Dictionary<long, SerializedObject> objectsByPathId, Dictionary<long, string> pathByRootGo,
        Dictionary<long, long> goToTransform, Dictionary<long, long> transformFather,
        Dictionary<long, long> transformGo)
    {
        var meshIdByKey = new Dictionary<string, long>();
        var keyHasModel0 = new HashSet<string>();      // Model_0 is the highest-detail LOD
        var nameCache = new Dictionary<long, string>();

        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != 33) // MeshFilter
                continue;
            Dictionary<string, object> mf = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
            var meshPptr = (Dictionary<string, object>)mf["m_Mesh"];

            // Skip meshes that live in another file (built-in Unity primitives on light/collider parts).
            if (Convert.ToInt32(meshPptr["m_FileID"]) != 0)
                continue;
            long meshId = PathId(meshPptr);
            if (meshId == 0 || !objectsByPathId.ContainsKey(meshId))
                continue;

            long goId = PathId((Dictionary<string, object>)mf["m_GameObject"]);
            if (!goToTransform.TryGetValue(goId, out long tId))
                continue;
            while (transformFather.TryGetValue(tId, out long father) && father != 0)
                tId = father;
            if (!transformGo.TryGetValue(tId, out long rootGo) || !pathByRootGo.TryGetValue(rootGo, out string? path))
                continue;

            string key = PrefabKey(path);
            bool isModel0 = GameObjectName(file, objectsByPathId, nameCache, goId) == "Model_0";
            if (isModel0)
            {
                if (keyHasModel0.Add(key))
                    meshIdByKey[key] = meshId; // first Model_0 wins
            }
            else if (!meshIdByKey.ContainsKey(key))
            {
                meshIdByKey[key] = meshId;      // fall back to any part until a Model_0 shows up
            }
        }
        return meshIdByKey;
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

    // "assets/coremasterbundle/objects/small/business/cardboard_0/object.prefab" -> "small/business/cardboard_0"
    private static string PrefabKey(string path)
    {
        int idx = path.IndexOf("/objects/", StringComparison.Ordinal);
        string rest = path.Substring(idx + "/objects/".Length);
        return rest[..^"/object.prefab".Length];
    }

    // Folder ".../Bundles/Objects/Small/Business/Cardboard_0" -> "small/business/cardboard_0"
    private static string FolderKey(string directory, string objectBundlesDir)
    {
        string rel = Path.GetRelativePath(objectBundlesDir, directory);
        return rel.Replace('\\', '/').ToLowerInvariant();
    }

    // Bundle_Override_Path "/Objects/Medium/Furniture/Grave_0" -> "medium/furniture/grave_0"
    private static string OverrideKey(string overridePath)
    {
        string s = overridePath.Replace('\\', '/').Trim('/').ToLowerInvariant();
        return s.StartsWith("objects/", StringComparison.Ordinal) ? s["objects/".Length..] : s;
    }

    private static long PathId(Dictionary<string, object> pptr) => Convert.ToInt64(pptr["m_PathID"]);
}
