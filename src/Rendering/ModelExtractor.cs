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
    // The SerializedFile (meshes + object/material metadata) sits in the first ~171 MB of the 1.4 GB
    // decompressed blob; the .resS texture stream is the remaining ~1.18 GB. Mesh extraction only needs
    // the SerializedFile, so we cap the LZMA decode there (~3 s instead of ~10 s) and defer the texture
    // stream to ExtractTextures. See the cold-load streaming design.
    private const long MeshDecodeCap = 200L * 1024 * 1024;

    // Phase 1 (file based): decode only the SerializedFile and build the per-GUID meshes, recording each
    // submesh's texture key without touching the .resS pixel stream. Used by the synchronous/benchmark
    // build; the interactive cold load uses StreamExtract instead.
    public static int ExtractMeshes(string bundlePath, string objectBundlesDir, string treeBundlesDir,
        string assetsDir, HashSet<Guid> neededGuids, string cacheDir, ObjectAssetDatabase db,
        IReadOnlyList<FoliageAsset>? foliageAssets = null)
    {
        UnityBundle bundle = UnityBundle.Read(File.ReadAllBytes(bundlePath), MeshDecodeCap); // SerializedFile only
        byte[] sfBytes = Array.Empty<byte>();
        foreach (KeyValuePair<string, byte[]> f in bundle.Files)
            if (!f.Key.EndsWith(".resS") && !f.Key.EndsWith(".resource"))
                sfBytes = f.Value;

        int extracted = ExtractMeshesFromSerializedFile(sfBytes, objectBundlesDir, treeBundlesDir,
            assetsDir, neededGuids, cacheDir, db, neededTextures: null, foliageAssets);
        GD.Print($"[extract] meshes={extracted}");
        return extracted;
    }

    // Cold-load streaming: decode the single LZMA block once, reading the SerializedFile (meshes) first,
    // signalling the scene can be built, then continuing the SAME pass through the .resS stream and
    // writing each referenced texture as its bytes arrive — so textures appear progressively while the map
    // is already playable, and the 171 MB SerializedFile is never re-decompressed. Falls back to the
    // two-decode path if the bundle is not the expected single-LZMA-block shape.
    public static void StreamExtract(string bundlePath, string objectBundlesDir, string treeBundlesDir,
        string assetsDir, HashSet<Guid> neededGuids, string cacheDir, string textureCacheDir,
        ObjectAssetDatabase db, Action onMeshesReady, Action<string> onTextureWritten,
        IReadOnlyList<FoliageAsset>? foliageAssets = null)
    {
        byte[] bundle = File.ReadAllBytes(bundlePath);
        using MasterBundleStream? stream = MasterBundleStream.Open(bundle);
        if (stream == null)
        {
            GD.Print("[extract] bundle not single-block; falling back to two-pass decode.");
            ExtractMeshes(bundlePath, objectBundlesDir, treeBundlesDir, assetsDir, neededGuids, cacheDir,
                db, foliageAssets);
            onMeshesReady();
            ExtractTextures(bundlePath, cacheDir, textureCacheDir, onTextureWritten);
            return;
        }

        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(textureCacheDir);

        // Read nodes in blob order: the SerializedFile (offset 0) first, then the .resS/.resource streams.
        var ordered = new List<MasterBundleStream.Node>(stream.Nodes);
        ordered.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var neededTextures = new Dictionary<long, UnityTexture>();

        const int chunkSize = 16 * 1024 * 1024;
        foreach (MasterBundleStream.Node node in ordered)
        {
            bool isStream = node.Path.EndsWith(".resS") || node.Path.EndsWith(".resource");
            if (!isStream)
            {
                byte[] sfBytes = stream.Read((int)node.Size);
                ExtractMeshesFromSerializedFile(sfBytes, objectBundlesDir, treeBundlesDir, assetsDir,
                    neededGuids, cacheDir, db, neededTextures, foliageAssets);
                GD.Print($"[extract] meshes={CachedMeshCountLoose(cacheDir)} (streamed); {neededTextures.Count} textures pending");
                onMeshesReady();
            }
            else
            {
                string fileName = LastSegment(node.Path);
                var pending = new List<(long texId, UnityTexture tex)>();
                foreach (KeyValuePair<long, UnityTexture> kv in neededTextures)
                    if (kv.Value.StreamFileName == fileName)
                        pending.Add((kv.Key, kv.Value));
                pending.Sort((a, b) => a.tex.StreamOffset.CompareTo(b.tex.StreamOffset));

                var buffer = new byte[node.Size];
                long filled = 0;
                int next = 0;
                while (filled < node.Size)
                {
                    int want = (int)Math.Min(chunkSize, node.Size - filled);
                    byte[] part = stream.Read(want);
                    if (part.Length == 0)
                        break;
                    Array.Copy(part, 0, buffer, filled, part.Length);
                    filled += part.Length;
                    while (next < pending.Count &&
                        pending[next].tex.StreamOffset + pending[next].tex.StreamSize <= filled)
                    {
                        WriteStreamedTexture(pending[next].texId, pending[next].tex, fileName, buffer,
                            textureCacheDir, onTextureWritten);
                        next++;
                    }
                }
            }
        }
    }

    private static void WriteStreamedTexture(long texId, UnityTexture tex, string fileName, byte[] fileBytes,
        string textureCacheDir, Action<string> onTextureWritten)
    {
        string texKey = texId.ToString("x");
        string outPath = Path.Combine(textureCacheDir, texKey + ".tex");
        if (File.Exists(outPath))
        {
            onTextureWritten(texKey);
            return;
        }
        byte[]? pixels = tex.GetPixels(name => name == fileName ? fileBytes : null);
        if (pixels == null || pixels.Length == 0)
            return;
        using (var stream = File.Create(outPath))
            TextureCache.Write(stream, new CachedTexture(tex.Format, tex.Width, tex.Height, tex.MipCount, pixels));
        onTextureWritten(texKey);
    }

    private static string LastSegment(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static int CachedMeshCountLoose(string cacheDir) =>
        Directory.Exists(cacheDir) ? Directory.GetFiles(cacheDir, "*.mesh").Length : 0;

    // Builds the per-GUID meshes from an already-decoded SerializedFile. When neededTextures is supplied,
    // it is filled with the metadata of every referenced texture so a caller can stream in the pixels.
    private static int ExtractMeshesFromSerializedFile(byte[] sfBytes, string objectBundlesDir,
        string treeBundlesDir, string assetsDir, HashSet<Guid> neededGuids, string cacheDir,
        ObjectAssetDatabase db, Dictionary<long, UnityTexture>? neededTextures,
        IReadOnlyList<FoliageAsset>? foliageAssets = null)
    {
        SerializedFile file = SerializedFile.Read(sfBytes);
        PrefabGraph graph = PrefabGraph.Read(file);
        MaterialResolver materials = MaterialResolver.Read(graph, assetsDir);

        Directory.CreateDirectory(cacheDir);

        // Objects and trees (Unturned "resources") share this pipeline; each asset maps to a prefab in
        // the masterbundle under objects/<folder>/object.prefab or trees/<folder>/resource.prefab.
        // Reuse the caller's already-scanned asset DB (built from the same object + tree bundle dirs)
        // instead of re-scanning both trees here. Object vs tree — which sets the prefab-key prefix — is
        // recovered from each asset's directory (they have disjoint GUID spaces, so the merged DB loses
        // nothing).
        var work = new List<(ObjectAsset asset, string key)>();
        foreach (ObjectAsset a in db.All)
        {
            bool isTree = !Path.GetRelativePath(treeBundlesDir, a.Directory).StartsWith("..");
            string key = isTree
                ? "trees/" + FolderKey(a.Directory, treeBundlesDir)
                : a.BundleOverridePath is { Length: > 0 } ovr
                    ? OverrideKey(ovr)
                    : "objects/" + FolderKey(a.Directory, objectBundlesDir);
            work.Add((a, key));
        }

        var mappedGuids = new HashSet<Guid>();
        int extracted = 0;
        foreach ((ObjectAsset asset, string key) in work)
        {
            if (!neededGuids.Contains(asset.Guid) || !mappedGuids.Add(asset.Guid) ||
                !graph.PartsByKey.TryGetValue(key, out List<MeshPart>? parts))
                continue;

            MaterialPalette? palette = materials.PaletteFor(asset.MaterialPaletteGuid);
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var submeshes = new List<CachedSubmesh>();

            // A prefab's renderable geometry can span several child GameObjects (a tree is a trunk plus a
            // separate foliage mesh, each with its own material and local pose). Bake each part's
            // local-to-root transform into its vertices and concatenate them into one indexed mesh.
            foreach (MeshPart part in parts)
            {
                if (!graph.ObjectsByPathId.TryGetValue(part.MeshId, out SerializedObject? meshObj))
                    continue;
                UnityMesh mesh = UnityMesh.Read(TypeTreeReader.Read(meshObj.TypeTree, file.ReaderFor(meshObj)));
                if (!mesh.Usable)
                    continue;

                int baseVertex = verts.Count;
                for (int i = 0; i < mesh.Vertices.Length; i++)
                {
                    verts.Add(part.LocalToRoot * mesh.Vertices[i]);
                    uvs.Add(i < mesh.Uvs.Length ? mesh.Uvs[i] : Vector2.Zero);
                }

                for (int si = 0; si < mesh.Submeshes.Count; si++)
                {
                    (Color color, string texKey, UnityMaterial.Blend blend, long texId) =
                        materials.Resolve(si, palette, part.Materials);
                    if (neededTextures != null && texId != 0 && !neededTextures.ContainsKey(texId) &&
                        graph.ObjectsByPathId.TryGetValue(texId, out SerializedObject? texObj))
                        neededTextures[texId] = UnityTexture.Read(TypeTreeReader.Read(texObj.TypeTree, file.ReaderFor(texObj)));

                    int[] src = mesh.Submeshes[si];
                    var indices = new int[src.Length];
                    for (int k = 0; k < src.Length; k++)
                        indices[k] = src[k] + baseVertex;
                    submeshes.Add(new CachedSubmesh(indices, color, texKey, blend));
                }
            }

            if (submeshes.Count == 0)
                continue;

            // Runtime derives smooth normals from the winding-corrected geometry (ModelLibrary.SmoothNormals),
            // so the extracted per-vertex normals are never read — don't compute or store them.
            using var stream = File.Create(Path.Combine(cacheDir, asset.Guid.ToString("N") + ".mesh"));
            MeshCache.Write(stream, verts.ToArray(), Array.Empty<Vector3>(), uvs.ToArray(), submeshes);
            extracted++;
        }

        if (foliageAssets != null)
            extracted += ExtractFoliageMeshes(graph, foliageAssets, cacheDir, neededTextures);

        return extracted;
    }

    // Extracts the foliage meshes (grass/flowers/pebbles) the Foliage.blob instances. Unlike objects,
    // each is a bare Mesh referenced directly in the masterbundle by the .asset's container path, using a
    // single material; cache it per foliage GUID like an object mesh so the same texture pass and
    // ModelLibrary path pick it up. Alpha-clipped, since foliage textures are cut-out.
    private static int ExtractFoliageMeshes(PrefabGraph graph, IReadOnlyList<FoliageAsset> foliageAssets,
        string cacheDir, Dictionary<long, UnityTexture>? neededTextures)
    {
        int extracted = 0;
        foreach (FoliageAsset fa in foliageAssets)
        {
            string outPath = Path.Combine(cacheDir, fa.Guid.ToString("N") + ".mesh");
            if (File.Exists(outPath))
                continue;

            string meshKey = graph.AssetPrefix + fa.MeshPath.Replace('\\', '/').ToLowerInvariant();
            if (!graph.ContainerByPath.TryGetValue(meshKey, out long meshId) ||
                !graph.ObjectsByPathId.TryGetValue(meshId, out SerializedObject? meshObj))
                continue;

            UnityMesh mesh = UnityMesh.Read(TypeTreeReader.Read(meshObj.TypeTree, graph.File.ReaderFor(meshObj)));
            if (!mesh.Usable || mesh.Submeshes.Count == 0)
                continue;

            (string texKey, _) = ResolveTextureByPath(graph, fa.MaterialPath, neededTextures);

            var uvs = new Vector2[mesh.Vertices.Length];
            for (int i = 0; i < uvs.Length; i++)
                uvs[i] = i < mesh.Uvs.Length ? mesh.Uvs[i] : Vector2.Zero;
            Vector3[] normals = mesh.Normals.Length == mesh.Vertices.Length ? mesh.Normals : Array.Empty<Vector3>();

            var submeshes = new List<CachedSubmesh>();
            foreach (int[] src in mesh.Submeshes)
                submeshes.Add(new CachedSubmesh((int[])src.Clone(), Colors.White, texKey, UnityMaterial.Blend.Cutout));

            using var stream = File.Create(outPath);
            MeshCache.Write(stream, mesh.Vertices, normals, uvs, submeshes);
            extracted++;
        }
        return extracted;
    }

    // Resolves the _MainTex path id (hex key) of a material named by its masterbundle container path, and
    // records its metadata for the texture-streaming pass. Mirrors MaterialResolver's per-submesh lookup.
    private static (string texKey, long texId) ResolveTextureByPath(PrefabGraph graph, string materialPath,
        Dictionary<long, UnityTexture>? neededTextures)
    {
        if (materialPath.Length == 0)
            return (string.Empty, 0);
        string matKey = graph.AssetPrefix + materialPath.Replace('\\', '/').ToLowerInvariant();
        if (!graph.ContainerByPath.TryGetValue(matKey, out long matId) ||
            !graph.ObjectsByPathId.TryGetValue(matId, out SerializedObject? matObj))
            return (string.Empty, 0);

        Dictionary<string, object> matDict = TypeTreeReader.Read(matObj.TypeTree, graph.File.ReaderFor(matObj));
        (int fileId, long texId) = UnityMaterial.GetTexture(matDict, "_MainTex");
        if (fileId != 0 || texId == 0 || !graph.ObjectsByPathId.TryGetValue(texId, out SerializedObject? texObj))
            return (string.Empty, 0);

        if (neededTextures != null && !neededTextures.ContainsKey(texId))
            neededTextures[texId] = UnityTexture.Read(TypeTreeReader.Read(texObj.TypeTree, graph.File.ReaderFor(texObj)));
        return (texId.ToString("x"), texId);
    }

    // Phase 2: decode the full bundle (now including the ~1.18 GB .resS pixel stream) and write the
    // textures referenced by the already-extracted meshes. Derives the needed set from the mesh cache, so
    // it is independent of phase 1 and resumable (skips textures already cached). Reports each written key
    // through onTextureWritten so a caller can hot-swap it into the live scene as it lands.
    public static int ExtractTextures(string bundlePath, string cacheDir, string textureCacheDir,
        Action<string>? onTextureWritten = null)
    {
        HashSet<long> needed = NeededTextureIds(cacheDir);
        if (needed.Count == 0)
            return 0;

        UnityBundle bundle = UnityBundle.Read(File.ReadAllBytes(bundlePath)); // full decode (.resS needed)
        byte[] sfBytes = Array.Empty<byte>();
        foreach (KeyValuePair<string, byte[]> f in bundle.Files)
            if (!f.Key.EndsWith(".resS") && !f.Key.EndsWith(".resource"))
                sfBytes = f.Value;

        SerializedFile file = SerializedFile.Read(sfBytes);
        var objectsByPathId = new Dictionary<long, SerializedObject>();
        foreach (SerializedObject o in file.Objects)
            objectsByPathId[o.PathId] = o;

        Directory.CreateDirectory(textureCacheDir);
        int written = 0;
        foreach (long texId in needed)
        {
            string texKey = texId.ToString("x");
            string outPath = Path.Combine(textureCacheDir, texKey + ".tex");
            if (File.Exists(outPath))
            {
                onTextureWritten?.Invoke(texKey); // already cached (resume) — still let the caller apply it
                written++;
                continue;
            }
            if (!objectsByPathId.TryGetValue(texId, out SerializedObject? texObj))
                continue;

            UnityTexture tex = UnityTexture.Read(TypeTreeReader.Read(texObj.TypeTree, file.ReaderFor(texObj)));
            byte[]? pixels = tex.GetPixels(name => bundle.Files.TryGetValue(name, out byte[]? f) ? f : null);
            if (pixels == null || pixels.Length == 0)
                continue;

            using (var stream = File.Create(outPath))
                TextureCache.Write(stream, new CachedTexture(tex.Format, tex.Width, tex.Height, tex.MipCount, pixels));
            written++;
            onTextureWritten?.Invoke(texKey);
        }

        GD.Print($"[extract] textures written/cached={written}");
        return written;
    }

    // The set of texture path ids the cached meshes reference (submesh texture keys are the id in hex).
    private static HashSet<long> NeededTextureIds(string cacheDir)
    {
        var ids = new HashSet<long>();
        if (!Directory.Exists(cacheDir))
            return ids;

        foreach (string path in Directory.GetFiles(cacheDir, "*.mesh"))
        {
            using var stream = File.OpenRead(path);
            (_, _, _, List<CachedSubmesh> submeshes) = MeshCache.Read(stream);
            foreach (CachedSubmesh sm in submeshes)
                if (sm.TextureKey.Length > 0 &&
                    long.TryParse(sm.TextureKey, System.Globalization.NumberStyles.HexNumber, null, out long id))
                    ids.Add(id);
        }
        return ids;
    }

    private static string FolderKey(string directory, string bundlesDir) =>
        Path.GetRelativePath(bundlesDir, directory).Replace('\\', '/').ToLowerInvariant();

    private static string OverrideKey(string overridePath) =>
        overridePath.Replace('\\', '/').Trim('/').ToLowerInvariant(); // already "objects/..."
}
