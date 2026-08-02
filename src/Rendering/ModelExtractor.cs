using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
    // Decoding stops at the end of the SerializedFile: the .resS pixel stream after it is only needed by
    // the texture pass. The cut-off comes from the bundle's own node table rather than a fixed size —
    // the game's SerializedFile is ~171 MB but a large workshop mod's can be several times that, and a
    // truncated SerializedFile fails to parse at all.
    private static long SerializedFileCap(byte[] bundle)
    {
        using MasterBundleStream? stream = MasterBundleStream.Open(bundle);
        if (stream == null)
            return long.MaxValue; // unknown shape: let the whole blob decode

        long end = 0;
        foreach (MasterBundleStream.Node node in stream.Nodes)
            if (!node.Path.EndsWith(".resS", StringComparison.Ordinal)
                && !node.Path.EndsWith(".resource", StringComparison.Ordinal))
            {
                end = Math.Max(end, node.Offset + node.Size);
            }

        return end > 0 ? end : long.MaxValue;
    }

    // The masterbundle's per-class-id type trees. Since type trees are identical across files of the same
    // Unity version, these decode the game's resources.assets (which ships with its type trees stripped).
    public static IReadOnlyDictionary<int, List<TypeTreeNode>> ReadClassTypeTrees(string bundlePath)
    {
        // Cache the tiny per-class type trees so entity imports don't re-read + re-decode the whole
        // masterbundle SerializedFile (~111 MB read + ~171 MB LZMA, ~3 s). Invalidate when the
        // masterbundle's mtime or size changes.
        string cachePath = ProjectSettings.GlobalizePath("user://type_trees.cache");
        var info = new FileInfo(bundlePath);
        long stamp = info.LastWriteTimeUtc.Ticks ^ info.Length;

        if (File.Exists(cachePath))
        {
            try
            {
                using FileStream cs = File.OpenRead(cachePath);
                Dictionary<int, List<TypeTreeNode>>? cached = TypeTreeCache.Read(cs, stamp);
                if (cached != null)
                    return cached;
            }
            catch (IOException) { /* corrupt/locked cache -> regenerate */ }
        }

        byte[] raw = File.ReadAllBytes(bundlePath);
        UnityBundle bundle = UnityBundle.Read(raw, SerializedFileCap(raw)); // SerializedFile only
        byte[] sfBytes = Array.Empty<byte>();
        foreach (KeyValuePair<string, byte[]> f in bundle.Files)
            if (!f.Key.EndsWith(".resS") && !f.Key.EndsWith(".resource"))
                sfBytes = f.Value;
        IReadOnlyDictionary<int, List<TypeTreeNode>> trees = SerializedFile.Read(sfBytes).TypeTreesByClassId;

        WriteTypeTreeCache(bundlePath, trees);
        return trees;
    }

    // Best-effort: a write failure just means the next reader decodes the bundle again.
    private static void WriteTypeTreeCache(string bundlePath,
        IReadOnlyDictionary<int, List<TypeTreeNode>> trees)
    {
        try
        {
            var info = new FileInfo(bundlePath);
            string cachePath = ProjectSettings.GlobalizePath("user://type_trees.cache");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            using FileStream ws = File.Create(cachePath);
            TypeTreeCache.Write(ws, trees, info.LastWriteTimeUtc.Ticks ^ info.Length);
        }
        catch (IOException) { /* best-effort cache; a write failure isn't fatal */ }
    }

    // Decodes just the masterbundle's SerializedFile (meshes + metadata; not the .resS pixel stream), for
    // reading inline assets such as the small face textures. Callers should cache the result themselves.
    public static SerializedFile ReadMasterbundleFile(string bundlePath)
    {
        byte[] raw = File.ReadAllBytes(bundlePath);
        UnityBundle bundle = UnityBundle.Read(raw, SerializedFileCap(raw));
        byte[] sfBytes = Array.Empty<byte>();
        foreach (KeyValuePair<string, byte[]> f in bundle.Files)
            if (!f.Key.EndsWith(".resS") && !f.Key.EndsWith(".resource"))
                sfBytes = f.Value;
        return SerializedFile.Read(sfBytes);
    }

    // Phase 1 (file based): decode only the SerializedFile and build the per-GUID meshes, recording each
    // submesh's texture key without touching the .resS pixel stream. Used by the synchronous/benchmark
    // build; the interactive cold load uses StreamExtract instead.
    public static int ExtractMeshes(string bundlePath, string bundleTag, string objectBundlesDir,
        string treeBundlesDir, string assetsDir, HashSet<Guid> neededGuids, string cacheDir,
        ObjectAssetDatabase db, IReadOnlyList<FoliageAsset>? foliageAssets = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return 0;
        byte[] raw = File.ReadAllBytes(bundlePath);
        if (cancellationToken.IsCancellationRequested)
            return 0;
        UnityBundle bundle = UnityBundle.Read(raw, SerializedFileCap(raw)); // SerializedFile only
        var produced = new HashSet<Guid>();
        int extracted = 0;
        int serializedFile = 0;
        foreach (KeyValuePair<string, byte[]> f in bundle.Files)
        {
            if (cancellationToken.IsCancellationRequested)
                return extracted;
            if (f.Key.EndsWith(".resS") || f.Key.EndsWith(".resource"))
                continue;

            string fileTag = serializedFile++ == 0 ? bundleTag : $"{bundleTag}-{serializedFile}";
            extracted += ExtractMeshesFromSerializedFile(f.Value, fileTag, objectBundlesDir, treeBundlesDir,
                assetsDir, neededGuids, cacheDir, db, neededTextures: null, foliageAssets, produced);
        }
        if (!cancellationToken.IsCancellationRequested)
            RecordMisses(bundlePath, cacheDir, neededGuids, foliageAssets, produced);
        AppShutdown.PrintUnlessQuitting($"[extract] meshes={extracted}");
        return extracted;
    }

    // Marks every needed GUID this pass did NOT produce a mesh for as a known miss (no prefab in the
    // bundle, no renderable submesh, unresolved foliage path), merged into whatever earlier maps recorded
    // for the SAME bundle. Anything that did produce a mesh is cleared from the set, so a game update that
    // adds a prefab heals the entry. Only call this after a pass that ran to completion — recording misses
    // from a half-finished decode would permanently blacklist GUIDs that were simply never reached.
    private static void RecordMisses(string bundlePath, string cacheDir, HashSet<Guid> neededGuids,
        IReadOnlyList<FoliageAsset>? foliageAssets, HashSet<Guid> produced)
    {
        string indexPath = Path.Combine(cacheDir, ExtractionIndex.FileNameFor(bundlePath));
        long stamp = ExtractionIndex.StampFor(bundlePath);
        HashSet<Guid> misses = ExtractionIndex.Load(indexPath, stamp);
        var failed = new HashSet<Guid>();

        foreach (Guid guid in neededGuids)
            if (guid != Guid.Empty)
                failed.Add(guid);
        if (foliageAssets != null)
            foreach (FoliageAsset fa in foliageAssets)
                failed.Add(fa.Guid);
        failed.ExceptWith(produced);
        misses.UnionWith(failed);
        misses.ExceptWith(produced);

        foreach (Guid guid in produced)
            ExtractionIndex.RecordMeshOwner(cacheDir, guid, bundlePath, stamp);
        foreach (Guid guid in failed)
            ExtractionIndex.RemoveCachedAsset(cacheDir, guid);

        ExtractionIndex.Save(indexPath, stamp, misses);
    }

    // Cold-load streaming: decode the single LZMA block once, reading the SerializedFile (meshes) first,
    // signalling the scene can be built, then continuing the SAME pass through the .resS stream and
    // writing each referenced texture as its bytes arrive — so textures appear progressively while the map
    // is already playable, and the 171 MB SerializedFile is never re-decompressed. Falls back to the
    // two-decode path if the bundle is not the expected single-LZMA-block shape.
    public static void StreamExtract(string bundlePath, string bundleTag, string objectBundlesDir,
        string treeBundlesDir, string assetsDir, HashSet<Guid> neededGuids, string cacheDir,
        string textureCacheDir,
        ObjectAssetDatabase db, Action onMeshesReady, Action<string> onTextureWritten,
        IReadOnlyList<FoliageAsset>? foliageAssets = null, bool isCoreBundle = false,
        IReadOnlyDictionary<string, Guid[]>? layerWantsByPath = null,
        Action<Guid, CachedTexture>? onLayerTexture = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, Guid[]> layerWants =
            layerWantsByPath ?? new Dictionary<string, Guid[]>(StringComparer.Ordinal);
        long textureSourceStamp = ExtractionIndex.StampFor(bundlePath);

        using MasterBundleStream? stream = MasterBundleStream.OpenFile(bundlePath);
        if (cancellationToken.IsCancellationRequested)
            return;
        if (stream == null)
        {
            AppShutdown.PrintUnlessQuitting("[extract] bundle not single-block; falling back to two-pass decode.");
            ExtractMeshes(bundlePath, bundleTag, objectBundlesDir, treeBundlesDir, assetsDir, neededGuids,
                cacheDir, db, foliageAssets, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;
            TextureDependencyIndex.RemoveStaleTextures(cacheDir, textureCacheDir, bundleTag, neededGuids,
                bundlePath, textureSourceStamp);
            onMeshesReady();
            ExtractTextures(bundlePath, bundleTag, cacheDir, textureCacheDir, onTextureWritten,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            // The terrain waits on this pass for its splat layers whichever route it took, so the fallback
            // owes them too: without this the map keeps its flat-colour terrain and the wait only ends
            // when the whole extraction does.
            if (layerWants.Count > 0)
                foreach ((string containerPath, CachedTexture texture) in
                    BundleTextures.ExtractAll(bundlePath, new List<string>(layerWants.Keys)))
                {
                    foreach (Guid material in layerWants[containerPath])
                        onLayerTexture?.Invoke(material, texture);
                }
            return;
        }

        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(textureCacheDir);

        // The SerializedFile (offset 0) comes first in blob order, then the .resS/.resource streams.
        var ordered = new List<MasterBundleStream.Node>(stream.Nodes);
        ordered.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var neededTextures = new Dictionary<(string Tag, long Id), UnityTexture>();
        var layerTextures = new Dictionary<string, UnityTexture>(StringComparer.Ordinal);
        var produced = new HashSet<Guid>();

        // Strictly physical order, whatever the bundle's layout: the decoder only moves forward, so a node
        // that is skipped rather than read leaves every following one misaligned. A bundle with more than
        // one serialized file interleaves them with their own streams, and recording those streams for
        // later handed the next file's parse a lump of texture bytes.
        int serializedLeft = 0;
        foreach (MasterBundleStream.Node node in ordered)
            if (!IsStreamNode(node.Path))
                serializedLeft++;

        var written = new List<(string Tag, long TexId, string? LayerPath, UnityTexture Texture)>();
        int readSerialized = 0;
        var owedByFile = new Dictionary<string, List<BundlePass.Want>>(StringComparer.Ordinal);
        // Path ids repeat between the serialized files of one bundle, so what is settled is tracked by
        // (file tag, id) — the same pair the cache is keyed by.
        var resolved = new HashSet<(string, long)>();

        for (int i = 0; i < ordered.Count; i++)
        {
            if (AppShutdown.IsShuttingDown || cancellationToken.IsCancellationRequested)
                return; // leaving: the cache keeps whatever completed and resumes next boot

            MasterBundleStream.Node node = ordered[i];
            if (!IsStreamNode(node.Path))
            {
                // A path id is unique only inside ONE serialized file, so a bundle carrying several of
                // them can repeat ids between them. The first file keeps the bundle's plain tag — which is
                // every bundle shipped today, and keeps their caches valid — and each further file gets
                // its own, so colliding ids cannot claim each other's cache entry.
                string fileTag = readSerialized == 0 ? bundleTag : $"{bundleTag}-{readSerialized + 1}";
                readSerialized++;

                ReadSerializedNode(stream, (int)node.Size, bundlePath, fileTag, objectBundlesDir,
                    treeBundlesDir, assetsDir, neededGuids, cacheDir, db, neededTextures, layerTextures,
                    foliageAssets, isCoreBundle, layerWants, onLayerTexture, produced);

                // Settle what needs no stream at all, once per texture: pixels stored inline are in hand
                // already, and anything extracted on an earlier boot only needs its key handed back. Both
                // then stay out of the plan, so the pass reads no further than it truly owes.
                CollectOwed(neededTextures, layerTextures, resolved, textureCacheDir,
                    bundlePath, textureSourceStamp, onTextureWritten, owedByFile, written);

                // Only once the LAST serialized file is in: the scene is built off this signal, and firing
                // it after the first of several left the objects from the rest as fallback boxes.
                if (--serializedLeft == 0)
                {
                    // All serialized files have now been scanned. A miss index written by an earlier
                    // file would permanently hide GUIDs owned by a later file after an interrupted pass.
                    RecordMisses(bundlePath, cacheDir, neededGuids, foliageAssets, produced);
                    AppShutdown.PrintUnlessQuitting($"[extract] meshes={CachedMeshCountLoose(cacheDir)} "
                        + $"(streamed); {neededTextures.Count + layerTextures.Count} textures referenced");
                    onMeshesReady();
                }

                // The file itself is by far the largest allocation of the pass (480 MB for a big workshop
                // bundle) and nothing past this point reads it — the texture ranges are plain offsets into
                // the stream. Hand it back now, or it stays resident for the whole texture tail. After
                // signalling, so the collection pause is off the path to a playable world; reading it
                // happens inside a method so the reference is provably gone by the time this runs.
                ReleaseDecodedFile();
                continue;
            }

            // What this stream owes is settled: only a serialized file read BEFORE it can name its ranges,
            // and one read after could never be served by a decoder that cannot rewind.
            string fileName = LastSegment(node.Path);
            List<BundlePass.Want> wants = owedByFile.TryGetValue(fileName, out List<BundlePass.Want>? owed)
                ? owed
                : new List<BundlePass.Want>();

            List<BundlePass.Step> plan = BundlePass.Plan(new[] { new BundlePass.Node(fileName, node.Size) },
                wants);
            IReadOnlyList<ForwardRegions.Region> regions = plan.Count > 0
                ? plan[0].Regions
                : Array.Empty<ForwardRegions.Region>();

            // Read no further than needed only when nothing after this node can still be owed: with a
            // serialized file yet to come, a later stream node may turn out to be wanted and every byte in
            // between has to be consumed to reach it.
            bool nothingFurtherOwed = serializedLeft == 0 && !AnyLaterNodeOwed(ordered, i, owedByFile);
            long readTo = node.Size;
            if (nothingFurtherOwed)
                readTo = plan.Count > 0 ? plan[0].ReadTo : 0; // owes nothing here either: stop at once

            ForwardRegions.Read(stream.Read, readTo, regions, (index, pixels) =>
            {
                (string tag, long texId, string? layerPath, UnityTexture texture) = written[index];
                if (layerPath == null)
                    WriteStreamedTextureBytes(tag, texId, texture, pixels, textureCacheDir,
                        bundlePath, textureSourceStamp, onTextureWritten);
                else
                {
                    CachedTexture cached = CachedTexture.From(texture, pixels);
                    foreach (Guid material in layerWants[layerPath])
                        onLayerTexture?.Invoke(material, cached);
                }
            }, cancellationToken: cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            owedByFile.Remove(fileName);
            AppShutdown.PrintUnlessQuitting($"[extract] {Path.GetFileName(bundlePath)}: {regions.Count} "
                + $"textures out of {readTo >> 20}/{node.Size >> 20} MB of {fileName}");

            if (nothingFurtherOwed)
                return; // every byte anyone asked for is in hand
        }
    }

    // Sorts everything referenced so far into "needs no stream" (written or signalled here and now) and
    // "owed by a stream file" (planned when that node comes round). Each texture is settled once.
    private static void CollectOwed(Dictionary<(string Tag, long Id), UnityTexture> neededTextures,
        Dictionary<string, UnityTexture> layerTextures, HashSet<(string, long)> resolved,
        string textureCacheDir, string bundlePath, long sourceStamp, Action<string> onTextureWritten,
        Dictionary<string, List<BundlePass.Want>> owedByFile,
        List<(string Tag, long TexId, string? LayerPath, UnityTexture Texture)> written)
    {
        foreach (((string tag, long texId), UnityTexture texture) in neededTextures)
        {
            if (!resolved.Add((tag, texId)))
                continue;

            if (texture.StreamPath.Length == 0)
            {
                // A Texture2D small enough to be stored inline has no stream range to wait for; the bytes
                // are already in hand. Sending it through the pass instead dropped it silently, which is
                // why two of this map's textures never arrived.
                if (texture.InlineData.Length > 0)
                    WriteStreamedTextureBytes(tag, texId, texture, texture.InlineData,
                        textureCacheDir, bundlePath, sourceStamp, onTextureWritten);
                continue;
            }

            if (AlreadyCached(tag, texId, textureCacheDir, bundlePath, sourceStamp, onTextureWritten))
                continue;

            Owe(owedByFile, texture.StreamFileName).Add(new BundlePass.Want(texture.StreamFileName,
                texture.StreamOffset, texture.StreamSize, written.Count));
            written.Add((tag, texId, null, texture));
        }

        foreach ((string containerPath, UnityTexture texture) in layerTextures)
        {
            bool planned = false;
            foreach ((string _, long _, string? layerPath, UnityTexture _) in written)
                if (layerPath == containerPath)
                {
                    planned = true;
                    break;
                }

            if (planned)
                continue;

            Owe(owedByFile, texture.StreamFileName).Add(new BundlePass.Want(texture.StreamFileName,
                texture.StreamOffset, texture.StreamSize, written.Count));
            written.Add((string.Empty, 0, containerPath, texture));
        }
    }

    private static List<BundlePass.Want> Owe(Dictionary<string, List<BundlePass.Want>> owedByFile,
        string fileName)
    {
        if (!owedByFile.TryGetValue(fileName, out List<BundlePass.Want>? wants))
            owedByFile[fileName] = wants = new List<BundlePass.Want>();
        return wants;
    }

    // True when a node after index `at` still owes something. Nodes already read cannot be revisited, so
    // only what is still ahead decides whether the pass has to keep consuming.
    private static bool AnyLaterNodeOwed(List<MasterBundleStream.Node> ordered, int at,
        Dictionary<string, List<BundlePass.Want>> owedByFile)
    {
        for (int i = at + 1; i < ordered.Count; i++)
            if (owedByFile.TryGetValue(LastSegment(ordered[i].Path), out List<BundlePass.Want>? wants)
                && wants.Count > 0)
            {
                return true;
            }

        return false;
    }

    // The SerializedFile phase, kept in its own frame so the decoded file is unreachable when it returns.
    private static void ReadSerializedNode(MasterBundleStream stream, int size, string bundlePath,
        string bundleTag, string objectBundlesDir, string treeBundlesDir, string assetsDir,
        HashSet<Guid> neededGuids, string cacheDir, ObjectAssetDatabase db,
        Dictionary<(string Tag, long Id), UnityTexture> neededTextures, Dictionary<string, UnityTexture> layerTextures,
        IReadOnlyList<FoliageAsset>? foliageAssets, bool isCoreBundle,
        IReadOnlyDictionary<string, Guid[]> layerWants, Action<Guid, CachedTexture>? onLayerTexture,
        HashSet<Guid> produced)
    {
        SerializedFile file = SerializedFile.Read(stream.Read(size));
        ExtractMeshesFrom(file, bundleTag, objectBundlesDir, treeBundlesDir, assetsDir, neededGuids,
            cacheDir, db, neededTextures, foliageAssets, produced, isCoreBundle ? bundlePath : null);

        // Meshes land before the potentially long texture tail. If shutdown interrupted that tail, the
        // next load sees current meshes and ExtractFoliageMeshes/Object extraction correctly skips them;
        // recover their Texture2D metadata from the cached material keys so this pass can resume the
        // missing pixels instead of permanently rendering opaque cards.
        var objectsByPathId = new Dictionary<long, SerializedObject>();
        foreach (SerializedObject obj in file.Objects)
            objectsByPathId[obj.PathId] = obj;
        foreach (long texId in TextureDependencyIndex.NeededTextureIds(cacheDir, bundleTag, neededGuids))
            if (!neededTextures.ContainsKey((bundleTag, texId))
                && objectsByPathId.TryGetValue(texId, out SerializedObject? texObj))
                neededTextures[(bundleTag, texId)] = UnityTexture.Read(TypeTreeReader.Read(texObj.TypeTree,
                    file.ReaderFor(texObj)));

        // The terrain's splat layers ride along in this same pass: they live in the same bundles, and
        // resolving them separately meant decoding every one of them a second time.
        foreach ((string containerPath, UnityTexture texture) in
            BundleTextures.Locate(file, new List<string>(layerWants.Keys)))
        {
            if (texture.StreamPath.Length == 0)
            {
                CachedTexture cached = CachedTexture.From(texture, texture.InlineData);
                foreach (Guid material in layerWants[containerPath])
                    onLayerTexture?.Invoke(material, cached);
            }
            else
                layerTextures[containerPath] = texture;
        }
    }

    // Returns the SerializedFile's memory to the OS rather than leaving it on the large object heap for
    // the rest of the load. Runs after the meshes are cached and the scene has been signalled, so the
    // pause it costs is off the path to a playable world.
    private static void ReleaseDecodedFile()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static bool IsStreamNode(string path) =>
        path.EndsWith(".resS", StringComparison.Ordinal)
        || path.EndsWith(".resource", StringComparison.Ordinal);

    // Writes an already-extracted streamed texture — its bytes are exactly the pixel region (the sliding
    // window read them directly), so no GetPixels region-copy is needed.
    // True when this texture is already on disk in the current cache format, in which case the caller is
    // told about it and the bytes never have to be decoded again.
    private static bool AlreadyCached(string bundleTag, long texId, string textureCacheDir,
        string bundlePath, long sourceStamp, Action<string> onTextureWritten)
    {
        string texKey = TextureKey.For(bundleTag, texId);
        string path = Path.Combine(textureCacheDir, texKey + ".tex");
        if (!TextureCache.IsCurrentForSource(path, bundlePath, sourceStamp))
        {
            // Do not let the scene briefly upload pixels from the previous bundle revision while the
            // current .resS range is still streaming. Missing/invalid source metadata is conservative:
            // remove only this cache key and let the normal extraction path recreate it.
            TextureCache.Remove(path);
            return false;
        }

        onTextureWritten(texKey);
        return true;
    }

    private static void WriteStreamedTextureBytes(string bundleTag, long texId, UnityTexture tex,
        byte[] pixels, string textureCacheDir, string bundlePath, long sourceStamp,
        Action<string> onTextureWritten)
    {
        string texKey = TextureKey.For(bundleTag, texId);
        string outPath = Path.Combine(textureCacheDir, texKey + ".tex");
        if (TextureCache.IsCurrentForSource(outPath, bundlePath, sourceStamp))
        {
            onTextureWritten(texKey);
            return;
        }
        if (pixels.Length == 0)
            return;
        using (var stream = File.Create(outPath))
            TextureCache.Write(stream, CachedTexture.From(tex, pixels));
        TextureCache.RecordSource(outPath, bundlePath, sourceStamp);
        onTextureWritten(texKey);
    }

    private static string LastSegment(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    // The cached lower level sits beside "<guid>.mesh" under a suffix the plain-mesh scan cannot match.
    // Named in the cache layer because the dependency index has to open both levels of a GUID and cannot
    // reach into the extractor.
    public const string Lod1Suffix = MeshCache.Lod1Suffix;

    // A lower level is only worth caching if it is materially cheaper than the base one. Every level kept
    // costs a second MultiMesh and a second copy of the batch's placement transforms for the whole
    // session, so a level that is within a tenth of the base triangle count is dropped: measured over the
    // extracted cache that is 4 of 222 authored levels, and the other 218 median 49% of the base.
    private const float Lod1MaxTriangleRatio = 0.9f;

    // Writes through a temporary in the same directory and renames into place. A cached mesh is judged
    // current by its 4-byte header alone, so a process killed part-way through a direct write would leave
    // a truncated file that every later run accepts and then throws on.
    private static void WriteMeshAtomically(string path, string tempPath, Vector3[] vertices,
        Vector3[] normals, Vector2[] uvs, List<CachedSubmesh> submeshes)
    {
        try
        {
            using (var stream = File.Create(tempPath))
                MeshCache.Write(stream, vertices, normals, uvs, submeshes);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
    }

    private static int TriangleTotal(List<CachedSubmesh> submeshes)
    {
        int total = 0;
        foreach (CachedSubmesh sub in submeshes)
            total += sub.Indices.Length / 3;
        return total;
    }

    private static int CachedMeshCountLoose(string cacheDir) =>
        Directory.Exists(cacheDir) ? PlainMeshFiles(cacheDir).Count : 0;

    // "*.mesh" also matches the cached lower levels, which are not separate objects and must not be
    // counted as extracted meshes or scanned as if they were one.
    private static List<string> PlainMeshFiles(string cacheDir)
    {
        var plain = new List<string>();
        foreach (string path in Directory.GetFiles(cacheDir, "*.mesh"))
            if (!path.EndsWith(Lod1Suffix, StringComparison.Ordinal))
                plain.Add(path);
        return plain;
    }

    // Builds the per-GUID meshes from an already-decoded SerializedFile. When neededTextures is supplied,
    // it is filled with the metadata of every referenced texture so a caller can stream in the pixels.
    // producedGuids, when supplied, collects every GUID that ended up with a cached mesh — the complement
    // of the needed set is what RecordMisses blacklists.
    private static int ExtractMeshesFromSerializedFile(byte[] sfBytes, string bundleTag,
        string objectBundlesDir, string treeBundlesDir, string assetsDir, HashSet<Guid> neededGuids,
        string cacheDir, ObjectAssetDatabase db, Dictionary<(string Tag, long Id), UnityTexture>? neededTextures,
        IReadOnlyList<FoliageAsset>? foliageAssets = null, HashSet<Guid>? producedGuids = null,
        string? typeTreeCacheFor = null) =>
        ExtractMeshesFrom(SerializedFile.Read(sfBytes), bundleTag, objectBundlesDir, treeBundlesDir,
            assetsDir, neededGuids, cacheDir, db, neededTextures, foliageAssets, producedGuids,
            typeTreeCacheFor);

    // Same, for a caller that already holds the decoded file (the streaming pass reads it once and then
    // uses it for the meshes, the type trees and the terrain layers alike).
    private static int ExtractMeshesFrom(SerializedFile file, string bundleTag, string objectBundlesDir,
        string treeBundlesDir, string assetsDir, HashSet<Guid> neededGuids, string cacheDir,
        ObjectAssetDatabase db, Dictionary<(string Tag, long Id), UnityTexture>? neededTextures,
        IReadOnlyList<FoliageAsset>? foliageAssets = null, HashSet<Guid>? producedGuids = null,
        string? typeTreeCacheFor = null)
    {

        // The per-class type trees are a by-product of having read this file. Handing them to the cache
        // here is what keeps the skybox and the character importer from decoding the same 170 MB prefix
        // again, on the main thread, in the middle of the load.
        if (typeTreeCacheFor != null)
            WriteTypeTreeCache(typeTreeCacheFor, file.TypeTreesByClassId);
        PrefabGraph graph = PrefabGraph.Read(file);
        MaterialResolver materials = MaterialResolver.Read(graph, assetsDir, bundleTag);

        Directory.CreateDirectory(cacheDir);

        // Objects and trees (Unturned "resources") share this pipeline; each asset maps to a prefab in
        // the masterbundle under objects/<folder>/object.prefab or trees/<folder>/resource.prefab.
        // Reuse the caller's already-scanned asset DB (built from the same object + tree bundle dirs)
        // instead of re-scanning both trees here. Object vs tree — which sets the prefab-key prefix — is
        // recovered from each asset's directory (they have disjoint GUID spaces, so the merged DB loses
        // nothing).
        // The prefab key is the asset's folder path inside the bundle, so its first segment is the name of
        // the bundle's own asset folder — "trees" for the game, "resources" for a workshop mod that keeps
        // its harvestables there. Deriving it from the directory keeps both working.
        string objectsRoot = RootKey(objectBundlesDir);
        string treesRoot = RootKey(treeBundlesDir);

        var work = new List<(ObjectAsset asset, string key)>();
        foreach (ObjectAsset a in db.All)
        {
            bool isTree = !Path.GetRelativePath(treeBundlesDir, a.Directory).StartsWith("..");
            string key = isTree
                ? treesRoot + "/" + FolderKey(a.Directory, treeBundlesDir)
                : a.BundleOverridePath is { Length: > 0 } ovr
                    ? OverrideKey(ovr)
                    : objectsRoot + "/" + FolderKey(a.Directory, objectBundlesDir);
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

            // Builds one indexed mesh out of a prefab's parts at a single LOD level. Called for the
            // authored LOD-0 set and, where the prefab ships one, for its "*_1" siblings, so a distant
            // instance can render the lower level the artist already provided instead of full detail.
            bool BuildLevel(List<MeshPart> levelParts, out List<Vector3> verts, out List<Vector3> normals,
                out List<Vector2> uvs, out List<CachedSubmesh> submeshes, out bool allNormals,
                bool requireEveryPart = false,
                Dictionary<(string Tag, long Id), UnityTexture>? textureSink = null)
            {
                // A candidate level that is later rejected must not leave its textures owed: the streaming
                // tail would decode and cache an atlas for geometry no one ever writes or draws. Callers
                // that can reject pass their own sink and merge it once the level is accepted.
                Dictionary<(string Tag, long Id), UnityTexture>? sink = textureSink ?? neededTextures;
                bool complete = true;
                verts = new List<Vector3>();
                normals = new List<Vector3>();
                uvs = new List<Vector2>();
                submeshes = new List<CachedSubmesh>();
                allNormals = true; // authored normals are only usable when every part carries them
                                   // A prefab's renderable geometry can span several child GameObjects (a tree is a trunk plus a
                                   // separate foliage mesh, each with its own material and local pose). Bake each part's
                                   // local-to-root transform into its vertices and concatenate them into one indexed mesh.
                foreach (MeshPart part in levelParts)
                {
                    // A level is all-or-nothing. Skipping an undecodable part (stream-data geometry, for
                    // instance) would cache a level that silently drops that piece of the object when it
                    // activates, and TriangleTotal would read the hole as the level being cheaper.
                    if (!graph.ObjectsByPathId.TryGetValue(part.MeshId, out SerializedObject? meshObj))
                    {
                        complete = false;
                        continue;
                    }
                    UnityMesh mesh = UnityMesh.Read(TypeTreeReader.Read(meshObj.TypeTree, file.ReaderFor(meshObj)));
                    if (!mesh.Usable)
                    {
                        complete = false;
                        continue;
                    }

                    int baseVertex = verts.Count;
                    // Normals transform by the inverse-transpose of the part's basis (correct under the
                    // non-uniform scales some prefab children carry), stored in Unity space like the vertices.
                    Basis normalBasis = part.LocalToRoot.Basis.Inverse().Transposed();
                    bool hasNormals = mesh.Normals.Length == mesh.Vertices.Length;
                    allNormals &= hasNormals;
                    for (int i = 0; i < mesh.Vertices.Length; i++)
                    {
                        verts.Add(part.LocalToRoot * mesh.Vertices[i]);
                        normals.Add(hasNormals ? (normalBasis * mesh.Normals[i]).Normalized() : Vector3.Up);
                        uvs.Add(i < mesh.Uvs.Length ? mesh.Uvs[i] : Vector2.Zero);
                    }

                    for (int si = 0; si < mesh.Submeshes.Count; si++)
                    {
                        (Color color, string texKey, UnityMaterial.Blend blend, long texId,
                            float metallic, float smoothness, EShaderCull cull) =
                            materials.Resolve(si, palette, part.Materials);
                        if (sink != null && texId != 0 && !sink.ContainsKey((bundleTag, texId))
                            && neededTextures?.ContainsKey((bundleTag, texId)) != true
                            && graph.ObjectsByPathId.TryGetValue(texId, out SerializedObject? texObj))
                            sink[(bundleTag, texId)] = UnityTexture.Read(TypeTreeReader.Read(texObj.TypeTree, file.ReaderFor(texObj)));

                        int[] src = mesh.Submeshes[si];
                        var indices = new int[src.Length];
                        for (int k = 0; k < src.Length; k++)
                            indices[k] = src[k] + baseVertex;
                        submeshes.Add(new CachedSubmesh(indices, color, texKey, blend, metallic, smoothness, cull));
                    }
                }
                return submeshes.Count > 0 && (complete || !requireEveryPart);
            }

            if (!BuildLevel(parts, out List<Vector3> verts, out List<Vector3> normals,
                out List<Vector2> uvs, out List<CachedSubmesh> submeshes, out bool allNormals))
                continue;

            // The prefab's own lower level, cached beside it — and cached BEFORE the base mesh becomes
            // current. Presence of "<guid>.mesh" in the current format is the whole warm-cache signal, so
            // a crash between the two writes with the base first would leave an entry that is complete by
            // its own rules and permanently missing its level. Writing the level first makes the base
            // mesh the commit point for the pair.
            string stem = Path.Combine(cacheDir, asset.Guid.ToString("N"));
            string lod1Path = stem + Lod1Suffix;
            // Best-effort as a whole, like the type-tree cache: the lower level is an optimisation, and
            // losing it must not cost this asset its base mesh — let alone unwind through ExtractMeshes
            // and drop every asset still queued in this bundle to a placeholder box. The stale-file delete
            // is inside the boundary too, because a read-only or locked file throws there just as readily.
            try
            {
                File.Delete(lod1Path); // never keep a previous source's level for a colliding GUID
                // Written only when it actually builds and is materially cheaper: a level within
                // Lod1MaxTriangleRatio of the base saves almost no geometry while its MultiMesh and its
                // copy of the placement transforms stay resident for the whole session.
                var lodTextures = neededTextures != null
                    ? new Dictionary<(string Tag, long Id), UnityTexture>()
                    : null;
                if (graph.Lod1PartsByKey.TryGetValue(key, out List<MeshPart>? lod1Parts)
                    && BuildLevel(lod1Parts, out List<Vector3> lodVerts, out List<Vector3> lodNormals,
                        out List<Vector2> lodUvs, out List<CachedSubmesh> lodSubmeshes, out bool lodNormalsOk,
                        requireEveryPart: true, textureSink: lodTextures)
                    && TriangleTotal(lodSubmeshes) <= TriangleTotal(submeshes) * Lod1MaxTriangleRatio)
                {
                    WriteMeshAtomically(lod1Path, stem + ".lod1.tmp", lodVerts.ToArray(),
                        lodNormalsOk ? lodNormals.ToArray() : Array.Empty<Vector3>(), lodUvs.ToArray(), lodSubmeshes);
                    // Only now are these textures actually owed by something that will be drawn.
                    if (lodTextures != null)
                        foreach (KeyValuePair<(string Tag, long Id), UnityTexture> tex in lodTextures)
                            neededTextures![tex.Key] = tex.Value;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Deliberate lesser harm: this may leave a previous source's level beside a fresh base
                // mesh, which shows as the wrong coarse shape past the switch distance. Giving up on the
                // base mesh instead would put a placeholder box there at every distance.
                AppShutdown.PrintUnlessQuitting($"[extract] lower level not cached for {asset.Guid:N}: {e.Message}");
            }

            // Cache the authored per-vertex normals (Unturned's own hard/soft edges); ModelLibrary falls
            // back to deriving smooth normals only when a part shipped without them. Written through a
            // temporary so a kill mid-write cannot leave a truncated file whose header still reads current.
            WriteMeshAtomically(stem + ".mesh", stem + ".tmp", verts.ToArray(),
                allNormals ? normals.ToArray() : Array.Empty<Vector3>(), uvs.ToArray(), submeshes);
            extracted++;
            producedGuids?.Add(asset.Guid);

            // Cache the object's colliders next to its mesh (Unity units; converted when the body is built).
            string colliderPath = Path.Combine(cacheDir, asset.Guid.ToString("N") + ".collider");
            File.Delete(colliderPath); // never retain a previous source's collider for a colliding GUID
            if (graph.CollidersByKey.TryGetValue(key, out List<ColliderPart>? colliderParts))
            {
                List<CachedCollider> colliders = BuildColliders(colliderParts, graph, file);
                if (colliders.Count > 0)
                    using (var cs = File.Create(colliderPath))
                        ColliderCache.Write(cs, colliders);
            }
        }

        if (foliageAssets != null)
            extracted += ExtractFoliageMeshes(graph, bundleTag, foliageAssets, cacheDir, neededTextures,
                producedGuids);

        return extracted;
    }

    // Resolves each collider part to a cacheable collider: primitives pass their Unity parameters through;
    // a MeshCollider reads its collision mesh (its own low-poly geometry, distinct from the render mesh) into
    // raw Unity-space vertices + flattened triangle indices.
    private static List<CachedCollider> BuildColliders(List<ColliderPart> parts, PrefabGraph graph,
        SerializedFile file)
    {
        var result = new List<CachedCollider>(parts.Count);
        foreach (ColliderPart p in parts)
        {
            switch (p.Kind)
            {
                case EColliderKind.Box:
                    result.Add(CachedCollider.Box(p.LocalToRoot, p.Center, p.Size));
                    break;
                case EColliderKind.Sphere:
                    result.Add(CachedCollider.Sphere(p.LocalToRoot, p.Center, p.Radius));
                    break;
                case EColliderKind.Capsule:
                    result.Add(CachedCollider.Capsule(p.LocalToRoot, p.Center, p.Radius, p.Height, p.Direction));
                    break;
                default:
                    if (!graph.ObjectsByPathId.TryGetValue(p.MeshId, out SerializedObject? meshObj))
                        break;
                    UnityMesh mesh = UnityMesh.Read(TypeTreeReader.Read(meshObj.TypeTree, file.ReaderFor(meshObj)));
                    if (!mesh.Usable)
                        break;
                    var indices = new List<int>();
                    foreach (int[] sub in mesh.Submeshes)
                        indices.AddRange(sub);
                    result.Add(CachedCollider.Mesh(p.LocalToRoot, mesh.Vertices, indices.ToArray()));
                    break;
            }
        }
        return result;
    }

    // Extracts the foliage meshes (grass/flowers/pebbles) the Foliage.blob instances. Unlike objects,
    // each is a bare Mesh referenced directly in the masterbundle by the .asset's container path, using a
    // single material; cache it per foliage GUID like an object mesh so the same texture pass and
    // ModelLibrary path pick it up. Alpha-clipped, since foliage textures are cut-out.
    private static int ExtractFoliageMeshes(PrefabGraph graph, string bundleTag,
        IReadOnlyList<FoliageAsset> foliageAssets,
        string cacheDir, Dictionary<(string Tag, long Id), UnityTexture>? neededTextures, HashSet<Guid>? producedGuids = null)
    {
        int extracted = 0;
        foreach (FoliageAsset fa in foliageAssets)
        {
            string outPath = Path.Combine(cacheDir, fa.Guid.ToString("N") + ".mesh");

            string meshKey = graph.AssetPrefix + fa.MeshPath.Replace('\\', '/').ToLowerInvariant();
            if (!graph.ContainerByPath.TryGetValue(meshKey, out long meshId) ||
                !graph.ObjectsByPathId.TryGetValue(meshId, out SerializedObject? meshObj))
                continue;

            UnityMesh mesh = UnityMesh.Read(TypeTreeReader.Read(meshObj.TypeTree, graph.File.ReaderFor(meshObj)));
            if (!mesh.Usable || mesh.Submeshes.Count == 0)
                continue;

            // Foliage renders with Unturned's own Framework/Grass-family shaders, which hard-code
            // "OUT.Specular = 0.0" — the material's Standard-shader _Glossiness (0.5 Unity default) is
            // never read there, so carrying it would give grass a sky-reflection sheen the game never shows.
            (string texKey, Color color, float _, float _, EShaderCull foliageCull) =
                ResolveMaterialByPath(graph, bundleTag, fa.MaterialPath, neededTextures);

            var uvs = new Vector2[mesh.Vertices.Length];
            for (int i = 0; i < uvs.Length; i++)
                uvs[i] = i < mesh.Uvs.Length ? mesh.Uvs[i] : Vector2.Zero;
            Vector3[] normals = mesh.Normals.Length == mesh.Vertices.Length ? mesh.Normals : Array.Empty<Vector3>();

            // Always cutout: foliage uses Unturned's own clipping shader, whose materials don't carry the
            // Standard shader's _Mode (GetBlendMode would misread them as opaque and break the grass cards).
            var submeshes = new List<CachedSubmesh>();
            foreach (int[] src in mesh.Submeshes)
                submeshes.Add(new CachedSubmesh((int[])src.Clone(), color, texKey, UnityMaterial.Blend.Cutout,
                    cull: foliageCull)); // read from the material's own shader (Grass/Leaves author Cull Off)

            using (var stream = File.Create(outPath))
                MeshCache.Write(stream, mesh.Vertices, normals, uvs, submeshes);
            File.Delete(Path.Combine(cacheDir, fa.Guid.ToString("N") + ".collider"));
            extracted++;
            producedGuids?.Add(fa.Guid);
        }
        return extracted;
    }

    // Resolves a material named by its masterbundle container path into the _MainTex hex key (recorded for
    // the texture-streaming pass) and the _Color tint. Mirrors MaterialResolver's per-submesh lookup — some
    // foliage (the pebbles) has no texture at all and renders purely from _Color, exactly as in Unturned.
    private static (string texKey, Color color, float metallic, float smoothness, EShaderCull cull)
        ResolveMaterialByPath(PrefabGraph graph, string bundleTag, string materialPath,
            Dictionary<(string Tag, long Id), UnityTexture>? neededTextures)
    {
        if (materialPath.Length == 0)
            return (string.Empty, Colors.White, 0f, 0f, EShaderCull.Back);
        string matKey = graph.AssetPrefix + materialPath.Replace('\\', '/').ToLowerInvariant();
        if (!graph.ContainerByPath.TryGetValue(matKey, out long matId) ||
            !graph.ObjectsByPathId.TryGetValue(matId, out SerializedObject? matObj))
            return (string.Empty, Colors.White, 0f, 0f, EShaderCull.Back);

        Dictionary<string, object> matDict = TypeTreeReader.Read(matObj.TypeTree, graph.File.ReaderFor(matObj));
        Color color = UnityMaterial.GetColor(matDict, "_Color") ?? Colors.White;
        float metallic = UnityMaterial.GetFloat(matDict, "_Metallic") ?? 0f;
        float smoothness = UnityMaterial.GetFloat(matDict, "_Glossiness") ?? 0f;

        // The shader's authored culling, from the bundle (the Grass/Leaves family authors Cull Off).
        EShaderCull cull = EShaderCull.Back;
        (int shaderFileId, long shaderId) = UnityMaterial.GetShader(matDict);
        if (shaderFileId == 0 && shaderId != 0 && graph.ObjectsByPathId.TryGetValue(shaderId, out SerializedObject? shObj))
            cull = UnityShaderCulling.Read(TypeTreeReader.Read(shObj.TypeTree, graph.File.ReaderFor(shObj)));

        (int fileId, long texId) = UnityMaterial.GetTexture(matDict, "_MainTex");
        if (fileId != 0 || texId == 0 || !graph.ObjectsByPathId.TryGetValue(texId, out SerializedObject? texObj))
            return (string.Empty, color, metallic, smoothness, cull);

        if (neededTextures != null && !neededTextures.ContainsKey((bundleTag, texId)))
        {
            neededTextures[(bundleTag, texId)] =
                UnityTexture.Read(TypeTreeReader.Read(texObj.TypeTree, graph.File.ReaderFor(texObj)));
        }
        return (TextureKey.For(bundleTag, texId), color, metallic, smoothness, cull);
    }

    // Phase 2: decode the full bundle (now including the ~1.18 GB .resS pixel stream) and write the
    // textures referenced by the already-extracted meshes. Derives the needed set from the mesh cache, so
    // it is independent of phase 1 and resumable (skips textures already cached). Reports each written key
    // through onTextureWritten so a caller can hot-swap it into the live scene as it lands.
    public static int ExtractTextures(string bundlePath, string bundleTag, string cacheDir,
        string textureCacheDir, Action<string>? onTextureWritten = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return 0;
        if (NeededTextureIds(cacheDir, bundleTag, includeSecondary: true).Count == 0)
            return 0;

        UnityBundle bundle = UnityBundle.Read(File.ReadAllBytes(bundlePath)); // full decode (.resS needed)
        if (cancellationToken.IsCancellationRequested)
            return 0;
        Directory.CreateDirectory(textureCacheDir);
        long sourceStamp = ExtractionIndex.StampFor(bundlePath);
        int written = 0;
        int serializedFile = 0;
        foreach (KeyValuePair<string, byte[]> f in bundle.Files)
        {
            if (f.Key.EndsWith(".resS") || f.Key.EndsWith(".resource"))
                continue;

            string fileTag = serializedFile++ == 0 ? bundleTag : $"{bundleTag}-{serializedFile}";
            HashSet<long> needed = NeededTextureIds(cacheDir, fileTag, includeSecondary: false);
            if (needed.Count == 0)
                continue;

            SerializedFile file = SerializedFile.Read(f.Value);
            var objectsByPathId = new Dictionary<long, SerializedObject>();
            foreach (SerializedObject o in file.Objects)
                objectsByPathId[o.PathId] = o;

            foreach (long texId in needed)
            {
                if (AppShutdown.IsShuttingDown || cancellationToken.IsCancellationRequested)
                    return written; // leaving: stop between textures, never mid-file
                string texKey = TextureKey.For(fileTag, texId);
                string outPath = Path.Combine(textureCacheDir, texKey + ".tex");
                if (TextureCache.IsCurrentForSource(outPath, bundlePath, sourceStamp))
                {
                    onTextureWritten?.Invoke(texKey); // already cached (resume) — still let the caller apply it
                    written++;
                    continue;
                }
                TextureCache.Remove(outPath);
                if (!objectsByPathId.TryGetValue(texId, out SerializedObject? texObj))
                    continue;

                UnityTexture tex = UnityTexture.Read(TypeTreeReader.Read(texObj.TypeTree, file.ReaderFor(texObj)));
                byte[]? pixels = tex.GetPixels(name => bundle.Files.TryGetValue(name, out byte[]? data) ? data : null);
                if (pixels == null || pixels.Length == 0)
                    continue;

                using (var stream = File.Create(outPath))
                    TextureCache.Write(stream, CachedTexture.From(tex, pixels));
                TextureCache.RecordSource(outPath, bundlePath, sourceStamp);
                written++;
                onTextureWritten?.Invoke(texKey);
            }
        }

        AppShutdown.PrintUnlessQuitting($"[extract] textures written/cached={written}");
        return written;
    }

    // The set of texture path ids the cached meshes reference that belong to THIS bundle.
    //
    // The cache is shared by every map, so it routinely holds meshes in an older format: a map visited
    // before a format bump leaves entries the current extraction has no reason to rewrite. MeshCache.Read
    // throws on those, which would abort the whole texture pass over one stale file from an unrelated map,
    // so they are skipped the same way ModelLibrary skips them on the warm path.
    private static HashSet<long> NeededTextureIds(string cacheDir, string bundleTag,
        bool includeSecondary = false)
    {
        var ids = new HashSet<long>();
        if (!Directory.Exists(cacheDir))
            return ids;

        // Both levels, not just the plain one: a texture referenced only by an authored lower level would
        // otherwise never be marked needed, and the synchronous WorldBuilder path relies entirely on this
        // scan to decide what ExtractTextures decodes.
        foreach (string path in Directory.GetFiles(cacheDir, "*.mesh"))
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            if (!MeshCache.IsCurrent(data)) // stale format; a later extraction pass rewrites it
                continue;

            try
            {
                (_, _, _, List<CachedSubmesh> submeshes) = MeshCache.Read(data);
                foreach (CachedSubmesh sm in submeshes)
                    if (TextureKey.TryParse(sm.TextureKey, out string owner, out long id)
                        && (string.Equals(owner, bundleTag, StringComparison.Ordinal)
                            || (includeSecondary && IsBundleFileTag(owner, bundleTag))))
                        ids.Add(id);
            }
            catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException
                or IndexOutOfRangeException or OverflowException)
            {
                // The magic check reads four bytes, so a file truncated after them still gets here. Such an
                // entry is already unusable as a mesh; skip it rather than abort the bundle's texture pass.
            }
        }
        return ids;
    }

    private static bool IsBundleFileTag(string owner, string bundleTag)
    {
        if (string.Equals(owner, bundleTag, StringComparison.Ordinal))
            return true;
        string prefix = bundleTag + "-";
        return owner.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(owner.AsSpan(prefix.Length), out int fileNumber)
            && fileNumber >= 2;
    }

    private static string FolderKey(string directory, string bundlesDir) =>
        Path.GetRelativePath(bundlesDir, directory).Replace('\\', '/').ToLowerInvariant();

    // The bundle-side name of an asset folder: Bundles/Objects -> "objects", <mod>/Resources ->
    // "resources". This is the first segment of every prefab key built from that folder.
    private static string RootKey(string bundlesDir) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(bundlesDir)).ToLowerInvariant();

    private static string OverrideKey(string overridePath) =>
        overridePath.Replace('\\', '/').Trim('/').ToLowerInvariant(); // already "objects/..."
}
