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
    public static int Extract(string bundlePath, string objectBundlesDir, string treeBundlesDir,
        string assetsDir, HashSet<Guid> neededGuids, string cacheDir, string textureCacheDir)
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
        BuildTransformMaps(file, out var goToTransform, out var transformFather, out var transformGo,
            out var localById);
        Dictionary<string, List<MeshPart>> partsByKey = MapObjectKeysToMeshes(
            file, objectsByPathId, pathByRootGo, goToTransform, transformFather, transformGo, localById);
        Dictionary<Guid, MaterialPalette> palettes = ScanPalettes(assetsDir);

        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(textureCacheDir);

        // Objects and trees (Unturned "resources") share this pipeline; each asset maps to a prefab in
        // the masterbundle under objects/<folder>/object.prefab or trees/<folder>/resource.prefab.
        var work = new List<(ObjectAsset asset, string key)>();
        foreach (ObjectAsset a in ObjectAssetDatabase.ScanDirectory(objectBundlesDir).All)
            work.Add((a, a.BundleOverridePath is { Length: > 0 } ovr
                ? OverrideKey(ovr)
                : "objects/" + FolderKey(a.Directory, objectBundlesDir)));
        foreach (ObjectAsset a in ObjectAssetDatabase.ScanDirectory(treeBundlesDir).All)
            work.Add((a, "trees/" + FolderKey(a.Directory, treeBundlesDir)));

        var writtenTextures = new HashSet<long>();
        var mappedGuids = new HashSet<Guid>();
        int extracted = 0, textured = 0;
        foreach ((ObjectAsset asset, string key) in work)
        {
            if (!neededGuids.Contains(asset.Guid) || !mappedGuids.Add(asset.Guid) ||
                !partsByKey.TryGetValue(key, out List<MeshPart>? parts))
                continue;

            palettes.TryGetValue(asset.MaterialPaletteGuid, out MaterialPalette? palette);
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var submeshes = new List<CachedSubmesh>();
            bool allNormals = true;

            // A prefab's renderable geometry can span several child GameObjects (a tree is a trunk plus a
            // separate foliage mesh, each with its own material and local pose). Bake each part's
            // local-to-root transform into its vertices and concatenate them into one indexed mesh.
            foreach (MeshPart part in parts)
            {
                if (!objectsByPathId.TryGetValue(part.MeshId, out SerializedObject? meshObj))
                    continue;
                UnityMesh mesh = UnityMesh.Read(TypeTreeReader.Read(meshObj.TypeTree, file.ReaderFor(meshObj)));
                if (!mesh.Usable)
                    continue;

                int baseVertex = verts.Count;
                Basis basis = part.LocalToRoot.Basis;
                for (int i = 0; i < mesh.Vertices.Length; i++)
                {
                    verts.Add(part.LocalToRoot * mesh.Vertices[i]);
                    bool hasNormal = i < mesh.Normals.Length;
                    allNormals &= hasNormal;
                    normals.Add(hasNormal ? (basis * mesh.Normals[i]).Normalized() : Vector3.Zero);
                    uvs.Add(i < mesh.Uvs.Length ? mesh.Uvs[i] : Vector2.Zero);
                }

                for (int si = 0; si < mesh.Submeshes.Count; si++)
                {
                    long matId = MaterialForSubmesh(si, palette, assetPrefix, containerByPath, part.Materials);
                    (Color color, string texKey, UnityMaterial.Blend blend) = ResolveMaterial(matId,
                        objectsByPathId, file, bundle, textureCacheDir, writtenTextures);
                    if (texKey.Length > 0)
                        textured++;
                    int[] src = mesh.Submeshes[si];
                    var indices = new int[src.Length];
                    for (int k = 0; k < src.Length; k++)
                        indices[k] = src[k] + baseVertex;
                    submeshes.Add(new CachedSubmesh(indices, color, texKey, blend));
                }
            }

            if (submeshes.Count == 0)
                continue;

            Vector3[] normalArray = allNormals ? normals.ToArray() : Array.Empty<Vector3>();
            using var stream = File.Create(Path.Combine(cacheDir, asset.Guid.ToString("N") + ".mesh"));
            MeshCache.Write(stream, verts.ToArray(), normalArray, uvs.ToArray(), submeshes);
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
                if (path.EndsWith("/object.prefab", StringComparison.Ordinal) ||
                    path.EndsWith("/resource.prefab", StringComparison.Ordinal))
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

    private static float F(object value) => Convert.ToSingle(value);

    // One renderable child of a prefab: its mesh, that mesh's materials, and its pose relative to the
    // prefab root (baked into the vertices so multiple parts can share one indexed mesh).
    private readonly struct MeshPart
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

    // Groups each prefab's LOD-0 renderable parts by key. Highest detail is the "*_0" suffix (Model_0,
    // Foliage_0); a prefab whose meshes carry no LOD names falls back to its first mesh.
    private static Dictionary<string, List<MeshPart>> MapObjectKeysToMeshes(SerializedFile file,
        Dictionary<long, SerializedObject> objectsByPathId, Dictionary<long, string> pathByRootGo,
        Dictionary<long, long> goToTransform, Dictionary<long, long> transformFather,
        Dictionary<long, long> transformGo, Dictionary<long, Transform3D> localById)
    {
        var lod0 = new Dictionary<string, List<MeshPart>>();
        var firstByKey = new Dictionary<string, MeshPart>();
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
            if (GameObjectName(file, objectsByPathId, nameCache, goId).EndsWith("_0", StringComparison.Ordinal))
            {
                if (!lod0.TryGetValue(key, out List<MeshPart>? list))
                    lod0[key] = list = new List<MeshPart>();
                list.Add(part);
            }
            firstByKey.TryAdd(key, part);
        }

        var result = new Dictionary<string, List<MeshPart>>();
        foreach (KeyValuePair<string, MeshPart> kv in firstByKey)
            result[kv.Key] = lod0.TryGetValue(kv.Key, out List<MeshPart>? parts)
                ? parts
                : new List<MeshPart> { kv.Value };
        return result;
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

    // ".../objects/small/business/cardboard_0/object.prefab" -> "objects/small/business/cardboard_0";
    // ".../trees/birch_1/resource.prefab" -> "trees/birch_1". The category prefix keeps object and tree
    // keys from colliding and matches the keys built from each asset's bundle folder.
    private static string PrefabKey(string path)
    {
        int idx = path.IndexOf("/objects/", StringComparison.Ordinal);
        if (idx < 0)
            idx = path.IndexOf("/trees/", StringComparison.Ordinal);
        string rest = path[(idx + 1)..];              // drop leading slash -> "objects/..." or "trees/..."
        return rest[..rest.LastIndexOf('/')];         // drop the "/*.prefab" filename
    }

    private static string FolderKey(string directory, string bundlesDir) =>
        Path.GetRelativePath(bundlesDir, directory).Replace('\\', '/').ToLowerInvariant();

    private static string OverrideKey(string overridePath) =>
        overridePath.Replace('\\', '/').Trim('/').ToLowerInvariant(); // already "objects/..."

    private static long PathId(Dictionary<string, object> pptr) => Convert.ToInt64(pptr["m_PathID"]);
}
