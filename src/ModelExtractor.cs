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
            file, objectsByPathId, pathByRootGo, goToTransform, transformFather, transformGo,
            out Dictionary<string, List<long>> materialsByKey);
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
            materialsByKey.TryGetValue(key, out List<long>? rendererMaterials);
            var submeshes = new List<CachedSubmesh>(mesh.Submeshes.Count);
            for (int si = 0; si < mesh.Submeshes.Count; si++)
            {
                long matId = MaterialForSubmesh(si, palette, assetPrefix, containerByPath, rendererMaterials);
                (Color color, string texKey, UnityMaterial.Blend blend) = ResolveMaterial(matId,
                    objectsByPathId, file, bundle, textureCacheDir, writtenTextures);
                if (texKey.Length > 0)
                    textured++;
                submeshes.Add(new CachedSubmesh(mesh.Submeshes[si], color, texKey, blend));
            }

            using var stream = File.Create(Path.Combine(cacheDir, asset.Guid.ToString("N") + ".mesh"));
            MeshCache.Write(stream, mesh.Vertices, mesh.Normals, mesh.Uvs, submeshes);
            extracted++;
        }

        GD.Print($"[extract] meshes={extracted} texturedSubmeshes={textured} textures={writtenTextures.Count}");
        return extracted;
    }

    // Picks the material id for a submesh: the palette's material (batched objects) if it covers this
    // submesh, otherwise the object's own MeshRenderer material (rocks/trees have no palette). 0 = none.
    private static long MaterialForSubmesh(int submeshIndex, MaterialPalette? palette, string assetPrefix,
        Dictionary<string, long> containerByPath, List<long>? rendererMaterials)
    {
        if (palette != null && submeshIndex < palette.MaterialPaths.Count)
        {
            string matPath = assetPrefix + palette.MaterialPaths[submeshIndex].Replace('\\', '/').ToLowerInvariant();
            return containerByPath.TryGetValue(matPath, out long id) ? id : 0;
        }
        if (rendererMaterials != null && submeshIndex < rendererMaterials.Count)
            return rendererMaterials[submeshIndex];
        return 0;
    }

    // Reads a material's flat color, blend mode and (optional) _MainTex texture, caching textures deduped.
    private static (Color color, string texKey, UnityMaterial.Blend blend) ResolveMaterial(long matId,
        Dictionary<long, SerializedObject> objectsByPathId, SerializedFile file, UnityBundle bundle,
        string textureCacheDir, HashSet<long> writtenTextures)
    {
        if (matId == 0 || !objectsByPathId.TryGetValue(matId, out SerializedObject? matObj))
            return (Colors.White, string.Empty, UnityMaterial.Blend.Opaque);

        Dictionary<string, object> matDict = TypeTreeReader.Read(matObj.TypeTree, file.ReaderFor(matObj));
        Color color = UnityMaterial.GetColor(matDict, "_Color") ?? Colors.White;
        UnityMaterial.Blend blend = UnityMaterial.GetBlendMode(matDict);
        string texKey = ResolveTexture(matDict, objectsByPathId, file, bundle, textureCacheDir, writtenTextures);
        return (color, texKey, blend);
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
        Dictionary<long, long> transformGo, out Dictionary<string, List<long>> materialsByKey)
    {
        var meshIdByKey = new Dictionary<string, long>();
        materialsByKey = new Dictionary<string, List<long>>();
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
                {
                    meshIdByKey[key] = meshId;
                    materialsByKey[key] = MeshRendererMaterials(file, objectsByPathId, goId);
                }
            }
            else if (!meshIdByKey.ContainsKey(key))
            {
                meshIdByKey[key] = meshId;
                materialsByKey[key] = MeshRendererMaterials(file, objectsByPathId, goId);
            }
        }
        return meshIdByKey;
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
            if (!objects.TryGetValue(compId, out SerializedObject? comp) || comp.ClassId != 23) // MeshRenderer
                continue;
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
