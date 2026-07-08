using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// One-time extraction: parse the masterbundle (fully, for the .resS texture stream), walk the prefab
// graph to map each object GUID to its Model_0 mesh, resolve per-submesh textures through the object's
// material palette, and cache compact per-GUID meshes + deduplicated textures. Excluded from coverage
// (orchestration glue over the fully tested Core parser); correctness is validated by the rendered scene.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class ModelExtractor
{
    public static int Extract(string bundlePath, string objectBundlesDir, string assetsDir,
        HashSet<Guid> neededGuids, string cacheDir, string textureCacheDir)
    {
        UnityBundle bundle = UnityBundle.Read(File.ReadAllBytes(bundlePath)); // full decode (.resS needed)
        byte[] sfBytes = Array.Empty<byte>();
        foreach (KeyValuePair<string, byte[]> f in bundle.Files)
            if (!f.Key.EndsWith(".resS") && !f.Key.EndsWith(".resource"))
                sfBytes = f.Value;

        SerializedFile file = SerializedFile.Read(sfBytes);
        var objectsByPathId = new Dictionary<long, SerializedObject>();
        foreach (SerializedObject o in file.Objects)
            objectsByPathId[o.PathId] = o;

        Dictionary<string, long> containerByPath = ReadContainer(file, out string assetPrefix,
            out Dictionary<long, string> pathByRootGo);
        BuildTransformMaps(file, out var goToTransform, out var transformFather, out var transformGo);
        Dictionary<string, long> meshIdByKey = MapObjectKeysToMeshes(
            file, objectsByPathId, pathByRootGo, goToTransform, transformFather, transformGo);
        Dictionary<Guid, MaterialPalette> palettes = ScanPalettes(assetsDir);

        ObjectAssetDatabase db = ObjectAssetDatabase.ScanDirectory(objectBundlesDir);
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(textureCacheDir);

        var writtenTextures = new HashSet<long>();
        var mappedGuids = new HashSet<Guid>();
        int extracted = 0, textured = 0;
        foreach (ObjectAsset asset in db.All)
        {
            if (!neededGuids.Contains(asset.Guid) || !mappedGuids.Add(asset.Guid))
                continue;
            string key = asset.BundleOverridePath is { Length: > 0 } ovr
                ? OverrideKey(ovr)
                : FolderKey(asset.Directory, objectBundlesDir);
            if (!meshIdByKey.TryGetValue(key, out long meshId) ||
                !objectsByPathId.TryGetValue(meshId, out SerializedObject? meshObj))
                continue;

            UnityMesh mesh = UnityMesh.Read(TypeTreeReader.Read(meshObj.TypeTree, file.ReaderFor(meshObj)));
            if (!mesh.Usable)
                continue;

            palettes.TryGetValue(asset.MaterialPaletteGuid, out MaterialPalette? palette);
            var submeshes = new List<CachedSubmesh>(mesh.Submeshes.Count);
            for (int si = 0; si < mesh.Submeshes.Count; si++)
            {
                (Color color, string texKey) = ResolveMaterial(si, palette, assetPrefix, containerByPath,
                    objectsByPathId, file, bundle, textureCacheDir, writtenTextures);
                if (texKey.Length > 0)
                    textured++;
                submeshes.Add(new CachedSubmesh(mesh.Submeshes[si], color, texKey));
            }

            using var stream = File.Create(Path.Combine(cacheDir, asset.Guid.ToString("N") + ".mesh"));
            MeshCache.Write(stream, mesh.Vertices, mesh.Normals, mesh.Uvs, submeshes);
            extracted++;
        }

        GD.Print($"[extract] meshes={extracted} texturedSubmeshes={textured} textures={writtenTextures.Count}");
        return extracted;
    }

    // Resolves a submesh's palette material into its flat color and (optional) _MainTex texture key,
    // caching textures deduplicated. Returns white + "" when there is no resolvable material.
    private static (Color color, string texKey) ResolveMaterial(int submeshIndex, MaterialPalette? palette,
        string assetPrefix, Dictionary<string, long> containerByPath,
        Dictionary<long, SerializedObject> objectsByPathId, SerializedFile file, UnityBundle bundle,
        string textureCacheDir, HashSet<long> writtenTextures)
    {
        if (palette == null || submeshIndex >= palette.MaterialPaths.Count)
            return (Colors.White, string.Empty);

        string matPath = assetPrefix + palette.MaterialPaths[submeshIndex].Replace('\\', '/').ToLowerInvariant();
        if (!containerByPath.TryGetValue(matPath, out long matId) ||
            !objectsByPathId.TryGetValue(matId, out SerializedObject? matObj))
            return (Colors.White, string.Empty);

        Dictionary<string, object> matDict = TypeTreeReader.Read(matObj.TypeTree, file.ReaderFor(matObj));
        Color color = UnityMaterial.GetColor(matDict, "_Color") ?? Colors.White;
        string texKey = ResolveTexture(matDict, objectsByPathId, file, bundle, textureCacheDir, writtenTextures);
        return (color, texKey);
    }

    private static string ResolveTexture(Dictionary<string, object> matDict,
        Dictionary<long, SerializedObject> objectsByPathId, SerializedFile file, UnityBundle bundle,
        string textureCacheDir, HashSet<long> writtenTextures)
    {
        (int fileId, long texId) = UnityMaterial.GetTexture(matDict, "_MainTex");
        if (fileId != 0 || texId == 0 || !objectsByPathId.TryGetValue(texId, out SerializedObject? texObj))
            return string.Empty;

        string texKey = texId.ToString("x");
        if (writtenTextures.Contains(texId))
            return texKey;

        UnityTexture tex = UnityTexture.Read(TypeTreeReader.Read(texObj.TypeTree, file.ReaderFor(texObj)));
        byte[]? pixels = tex.GetPixels(name => bundle.Files.TryGetValue(name, out byte[]? f) ? f : null);
        if (pixels == null || pixels.Length == 0)
            return string.Empty;

        using (var stream = File.Create(Path.Combine(textureCacheDir, texKey + ".tex")))
            TextureCache.Write(stream, new CachedTexture(tex.Format, tex.Width, tex.Height, tex.MipCount, pixels));
        writtenTextures.Add(texId);
        return texKey;
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
                if (path.Contains("/objects/") && path.EndsWith("/object.prefab"))
                    pathByRootGo[assetId] = path;
            }
        }
        return containerByPath;
    }

    private static Dictionary<Guid, MaterialPalette> ScanPalettes(string assetsDir)
    {
        var palettes = new Dictionary<Guid, MaterialPalette>();
        if (!Directory.Exists(assetsDir))
            return palettes;

        foreach (string path in Directory.EnumerateFiles(assetsDir, "*.asset", SearchOption.AllDirectories))
        {
            MaterialPalette? palette;
            try { palette = MaterialPalette.Read(DatParser.Parse(File.ReadAllText(path))); }
            catch (IOException) { continue; }
            if (palette != null && palette.MaterialPaths.Count > 0)
                palettes[palette.Guid] = palette;
        }
        return palettes;
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
            goToTransform[goId] = o.PathId;
            transformFather[o.PathId] = PathId((Dictionary<string, object>)t["m_Father"]);
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
            if (Convert.ToInt32(meshPptr["m_FileID"]) != 0)
                continue; // built-in Unity primitive on a light/collider part
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
            if (GameObjectName(file, objectsByPathId, nameCache, goId) == "Model_0")
            {
                if (keyHasModel0.Add(key))
                    meshIdByKey[key] = meshId;
            }
            else if (!meshIdByKey.ContainsKey(key))
            {
                meshIdByKey[key] = meshId;
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

    private static string FolderKey(string directory, string objectBundlesDir) =>
        Path.GetRelativePath(objectBundlesDir, directory).Replace('\\', '/').ToLowerInvariant();

    private static string OverrideKey(string overridePath)
    {
        string s = overridePath.Replace('\\', '/').Trim('/').ToLowerInvariant();
        return s.StartsWith("objects/", StringComparison.Ordinal) ? s["objects/".Length..] : s;
    }

    private static long PathId(Dictionary<string, object> pptr) => Convert.ToInt64(pptr["m_PathID"]);
}
