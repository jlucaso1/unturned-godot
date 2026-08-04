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
    public static int ExtractMeshes(ContentSource source, HashSet<Guid> neededGuids, string cacheDir,
        ObjectAssetDatabase db, IReadOnlyList<FoliageAsset>? foliageAssets = null,
        CancellationToken cancellationToken = default)
    {
        string bundlePath = source.BundlePath;
        string bundleTag = source.CacheTag;
        if (cancellationToken.IsCancellationRequested)
            return 0;
        byte[] raw = File.ReadAllBytes(bundlePath);
        if (cancellationToken.IsCancellationRequested)
            return 0;
        UnityBundle bundle = UnityBundle.Read(raw, SerializedFileCap(raw)); // SerializedFile only
        var produced = new HashSet<Guid>();
        var staleLevels = new HashSet<Guid>();
        int extracted = 0;
        int serializedFile = 0;
        foreach (KeyValuePair<string, byte[]> f in bundle.Files)
        {
            if (cancellationToken.IsCancellationRequested)
                return extracted;
            if (f.Key.EndsWith(".resS") || f.Key.EndsWith(".resource"))
                continue;

            string fileTag = serializedFile++ == 0 ? bundleTag : $"{bundleTag}-{serializedFile}";
            extracted += ExtractMeshesFromSerializedFile(f.Value, source, fileTag, neededGuids, cacheDir,
                db, neededTextures: null, foliageAssets, produced, staleLevelGuids: staleLevels,
                streamedVertexSource: bundlePath, cancellationToken: cancellationToken);
        }
        if (!cancellationToken.IsCancellationRequested)
            RecordMisses(bundlePath, cacheDir, neededGuids, foliageAssets, produced, staleLevels);
        AppShutdown.PrintUnlessQuitting($"[extract] meshes={extracted}");
        return extracted;
    }

    // Marks every needed GUID this pass did NOT produce a mesh for as a known miss (no prefab in the
    // bundle, no renderable submesh, unresolved foliage path), merged into whatever earlier maps recorded
    // for the SAME bundle. Anything that did produce a mesh is cleared from the set, so a game update that
    // adds a prefab heals the entry. Only call this after a pass that ran to completion — recording misses
    // from a half-finished decode would permanently blacklist GUIDs that were simply never reached.
    private static void RecordMisses(string bundlePath, string cacheDir, HashSet<Guid> neededGuids,
        IReadOnlyList<FoliageAsset>? foliageAssets, HashSet<Guid> produced,
        HashSet<Guid>? staleLevels = null)
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
            if (staleLevels?.Contains(guid) != true) // an unrefreshed lower level must stay cold
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
    public static void StreamExtract(ContentSource source, HashSet<Guid> neededGuids, string cacheDir,
        string textureCacheDir,
        ObjectAssetDatabase db, Action onMeshesReady, Action<string> onTextureWritten,
        IReadOnlyList<FoliageAsset>? foliageAssets = null,
        IReadOnlyDictionary<string, Guid[]>? layerWantsByPath = null,
        Action<Guid, CachedTexture>? onLayerTexture = null,
        AudioExtractor.Request? audio = null,
        CancellationToken cancellationToken = default)
    {
        string bundlePath = source.BundlePath;
        string bundleTag = source.CacheTag;
        IReadOnlyDictionary<string, Guid[]> layerWants =
            layerWantsByPath ?? new Dictionary<string, Guid[]>(StringComparer.Ordinal);
        long textureSourceStamp = ExtractionIndex.StampFor(bundlePath);

        using MasterBundleStream? stream = MasterBundleStream.OpenFile(bundlePath);
        if (cancellationToken.IsCancellationRequested)
            return;
        if (stream == null)
        {
            AppShutdown.PrintUnlessQuitting("[extract] bundle not single-block; falling back to two-pass decode.");
            ExtractMeshes(source, neededGuids, cacheDir, db, foliageAssets, cancellationToken);
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
        var staleLevels = new HashSet<Guid>();

        // Strictly physical order, whatever the bundle's layout: the decoder only moves forward, so a node
        // that is skipped rather than read leaves every following one misaligned. A bundle with more than
        // one serialized file interleaves them with their own streams, and recording those streams for
        // later handed the next file's parse a lump of texture bytes.
        int serializedLeft = 0;
        foreach (MasterBundleStream.Node node in ordered)
            if (!IsStreamNode(node.Path))
                serializedLeft++;

        var written = new List<OwedRange>();
        // The audio definitions this bundle owes ride along in the same pass: their clips are byte ranges
        // in the .resource node at the end of the blob, planned from the same SerializedFile that names
        // the textures. Extracting them separately cost a second full decode of the whole bundle — 1.4 GB
        // of LZMA for 20 MB of clips — and it ran after the load had already compacted its heap, so the
        // transient it allocated stayed resident for the rest of the session.
        // One plan for the whole bundle, carried through every serialized file: a raw clip group is
        // resolved a container path at a time, so a bundle that splits one over two files completes it
        // only once both have contributed. Per file, the first wrote a def.bin holding its own share.
        AudioExtractor.StreamPlan? audioPlan = null;
        int audioOwed = 0;
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

                audioPlan = ReadSerializedNode(stream, (int)node.Size, source, fileTag, neededGuids,
                    cacheDir, db, neededTextures, layerTextures, foliageAssets, layerWants, onLayerTexture,
                    produced, staleLevels, audio, audioPlan, cancellationToken);

                // Settle what needs no stream at all, once per texture: pixels stored inline are in hand
                // already, and anything extracted on an earlier boot only needs its key handed back. Both
                // then stay out of the plan, so the pass reads no further than it truly owes.
                CollectOwed(neededTextures, layerTextures, resolved, textureCacheDir,
                    bundlePath, textureSourceStamp, onTextureWritten, owedByFile, written);
                // Only the clips this file added: the earlier ones are already owed to a node that may
                // well have been read by now, and owing them twice would plan ranges the decoder has
                // already gone past.
                if (audioPlan != null)
                    audioOwed = OweAudioClips(audioPlan, audioOwed, i, ordered, owedByFile, written);

                // Only once the LAST serialized file is in: the scene is built off this signal, and firing
                // it after the first of several left the objects from the rest as fallback boxes.
                if (--serializedLeft == 0)
                {
                    // A cancelled pass has produced nothing, or nearly nothing, and RecordMisses reads
                    // exactly that to decide what this bundle cannot make: recording it would blacklist
                    // every GUID the pass never reached and delete its cache, so the next launch would
                    // suppress extraction and render them as boxes until the bundle stamp changed. The
                    // file-based path guards its own call for the same reason.
                    if (cancellationToken.IsCancellationRequested || AppShutdown.IsShuttingDown)
                        return;

                    // All serialized files have now been scanned. A miss index written by an earlier
                    // file would permanently hide GUIDs owned by a later file after an interrupted pass.
                    RecordMisses(bundlePath, cacheDir, neededGuids, foliageAssets, produced, staleLevels);
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

            ForwardRegions.Read(stream.Read, readTo, regions, (index, bytes) =>
            {
                OwedRange owed = written[index];
                if (owed.AudioPlan != null)
                    AudioExtractor.WriteClip(owed.AudioPlan, owed.AudioClip, bytes);
                else if (owed.LayerPath == null)
                    WriteStreamedTextureBytes(owed.Tag, owed.TexId, owed.Texture, bytes, textureCacheDir,
                        bundlePath, textureSourceStamp, onTextureWritten);
                else
                {
                    CachedTexture cached = CachedTexture.From(owed.Texture, bytes);
                    foreach (Guid material in layerWants[owed.LayerPath])
                        onLayerTexture?.Invoke(material, cached);
                }
            }, cancellationToken: cancellationToken);

            if (cancellationToken.IsCancellationRequested)
                return;

            owedByFile.Remove(fileName);
            AppShutdown.PrintUnlessQuitting($"[extract] {Path.GetFileName(bundlePath)}: {regions.Count} "
                + $"ranges out of {readTo >> 20}/{node.Size >> 20} MB of {fileName}");

            if (nothingFurtherOwed)
            {
                CompleteAudio(audioPlan);
                return; // every byte anyone asked for is in hand
            }
        }

        CompleteAudio(audioPlan);
    }

    // One byte range the pass owes somebody: a texture's pixels, a terrain layer's, or an audio clip's
    // FSB5 blob. They share one index space because the forward reader hands ranges back by index.
    private readonly record struct OwedRange(string Tag, long TexId, string? LayerPath, UnityTexture Texture,
        AudioExtractor.StreamPlan? AudioPlan, int AudioClip)
    {
        internal static OwedRange ForTexture(string tag, long texId, UnityTexture texture) =>
            new(tag, texId, null, texture, null, 0);

        internal static OwedRange ForLayer(string containerPath, UnityTexture texture) =>
            new(string.Empty, 0, containerPath, texture, null, 0);

        internal static OwedRange ForAudioClip(AudioExtractor.StreamPlan plan, int clip) =>
            new(string.Empty, 0, null, new UnityTexture(), plan, clip);
    }

    // Owes the stream nodes every clip the plan has gained since `from`, and returns the new mark. A clip
    // whose definition omits the source name (older single-stream bundles) is served by the bundle's only
    // .resource node, which is the same rule the standalone extractor applies.
    private static int OweAudioClips(AudioExtractor.StreamPlan plan, int from, int atNode,
        List<MasterBundleStream.Node> ordered, Dictionary<string, List<BundlePass.Want>> owedByFile,
        List<OwedRange> written)
    {
        string? soleResource = null;
        foreach (MasterBundleStream.Node node in ordered)
            if (node.Path.EndsWith(".resource", StringComparison.Ordinal))
                soleResource = soleResource == null ? LastSegment(node.Path) : "";

        for (int i = from; i < plan.Clips.Count; i++)
        {
            AudioExtractor.StreamPlan.Clip clip = plan.Clips[i];
            string file = clip.StreamFile.Length > 0 ? clip.StreamFile : soleResource ?? "";
            if (file.Length == 0)
                continue;

            // Only a node still ahead of the decoder can serve a range. A serialized file that names a
            // clip in a stream this pass has already walked past would otherwise sit in owedByFile
            // forever: BundlePass drops it, nothing writes the clip, and the definition would be marked
            // complete without it. Say so instead, and let the next pass extract it properly.
            if (NodeIndexOf(ordered, file) <= atNode)
            {
                plan.MarkUnservable(clip.Definition);
                continue;
            }

            Owe(owedByFile, file).Add(new BundlePass.Want(file, clip.Offset, clip.Size, written.Count));
            written.Add(OwedRange.ForAudioClip(plan, i));
        }

        return plan.Clips.Count;
    }

    // Where a stream file sits in blob order, or -1 when this bundle does not carry it (which the same
    // check then treats as unreachable, because it is).
    private static int NodeIndexOf(List<MasterBundleStream.Node> ordered, string fileName)
    {
        for (int i = 0; i < ordered.Count; i++)
            if (LastSegment(ordered[i].Path).Equals(fileName, StringComparison.Ordinal))
                return i;
        return -1;
    }

    // Writes each definition's def.bin once its clips are in. Only on the pass's normal exits: a cancelled
    // pass leaves no completeness marker, so the next boot (or the deferred fallback) extracts it properly
    // instead of finding a definition that claims to be cached with half its clips missing.
    private static void CompleteAudio(AudioExtractor.StreamPlan? plan)
    {
        if (plan == null)
            return;
        int completed = AudioExtractor.CompleteDefs(plan);
        if (completed > 0)
            AppShutdown.PrintUnlessQuitting($"[audio] {completed} definitions extracted in the bundle pass");
    }

    // Sorts everything referenced so far into "needs no stream" (written or signalled here and now) and
    // "owed by a stream file" (planned when that node comes round). Each texture is settled once.
    private static void CollectOwed(Dictionary<(string Tag, long Id), UnityTexture> neededTextures,
        Dictionary<string, UnityTexture> layerTextures, HashSet<(string, long)> resolved,
        string textureCacheDir, string bundlePath, long sourceStamp, Action<string> onTextureWritten,
        Dictionary<string, List<BundlePass.Want>> owedByFile, List<OwedRange> written)
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
            written.Add(OwedRange.ForTexture(tag, texId, texture));
        }

        foreach ((string containerPath, UnityTexture texture) in layerTextures)
        {
            bool planned = false;
            foreach (OwedRange owed in written)
                if (owed.LayerPath == containerPath)
                {
                    planned = true;
                    break;
                }

            if (planned)
                continue;

            Owe(owedByFile, texture.StreamFileName).Add(new BundlePass.Want(texture.StreamFileName,
                texture.StreamOffset, texture.StreamSize, written.Count));
            written.Add(OwedRange.ForLayer(containerPath, texture));
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
    // Returns the audio this file owes, if any: the plan holds offsets and names only, never the file.
    private static AudioExtractor.StreamPlan? ReadSerializedNode(MasterBundleStream stream, int size,
        ContentSource source, string bundleTag,
        HashSet<Guid> neededGuids, string cacheDir, ObjectAssetDatabase db,
        Dictionary<(string Tag, long Id), UnityTexture> neededTextures, Dictionary<string, UnityTexture> layerTextures,
        IReadOnlyList<FoliageAsset>? foliageAssets,
        IReadOnlyDictionary<string, Guid[]> layerWants, Action<Guid, CachedTexture>? onLayerTexture,
        HashSet<Guid> produced, HashSet<Guid> staleLevels, AudioExtractor.Request? audio,
        AudioExtractor.StreamPlan? audioPlan, CancellationToken cancellationToken)
    {
        SerializedFile file = SerializedFile.Read(stream.Read(size));
        // `streamedVertexSource` is the bundle itself, and reading it costs a second forward pass — this
        // pass cannot serve the ranges, because they sit in a node it has not reached and it cannot
        // rewind once it has. Only a cold extraction with streamed geometry still to fetch pays for it,
        // and what it buys is the difference between a vehicle with wheels and one without.
        ExtractMeshesFrom(file, source, bundleTag, neededGuids, cacheDir, db, neededTextures, foliageAssets,
            produced, source.IsCore ? source.BundlePath : null, staleLevels,
            streamedVertexSource: source.BundlePath, cancellationToken: cancellationToken);

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

        // Planned from this same decoded file, into the bundle's running plan: the definitions name their
        // clips by path id, and the clips name byte ranges in the .resource node this pass is about to
        // walk past anyway.
        return audio == null ? audioPlan : AudioExtractor.Plan(file, audio, audioPlan);
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

    // The file every Decal asset's texture is filed under, inside its own folder in the bundle.
    private const string DecalTextureFile = "decal.png";

    // The cached lower level sits beside "<guid>.mesh" under a suffix the plain-mesh scan cannot match.
    // Named in the cache layer because the dependency index has to open both levels of a GUID and cannot
    // reach into the extractor.
    public const string Lod1Suffix = MeshCache.Lod1Suffix;

    // A lower level is only worth caching if it is materially cheaper than the base one. Every level kept
    // costs a second MultiMesh and a second copy of the batch's placement transforms for the whole
    // session, so a level within a tenth of the base triangle count is dropped rather than kept for a
    // saving that would not repay it. In practice this rejects only a handful of authored levels.
    private const float Lod1MaxTriangleRatio = 0.9f;

    // Writes through a temporary in the same directory and renames into place. A cached mesh is judged
    // current by its 4-byte header alone, so a process killed part-way through a direct write would leave
    // a truncated file that every later run accepts and then throws on.
    private static void WriteMeshAtomically(string path, string tempPath, Vector3[] vertices,
        Vector3[] normals, Vector2[] uvs, List<CachedSubmesh> submeshes)
        => WriteAtomically(path, tempPath, s => MeshCache.Write(s, vertices, normals, uvs, submeshes));

    // The same discipline for any cache file judged current by its header: write a temporary in the same
    // directory, then rename into place. The collider cache used to be written directly, one statement
    // below its mesh, and a kill mid-write left a truncated file that every later run accepted.
    private static void WriteAtomically(string path, string tempPath, Action<FileStream> write)
    {
        try
        {
            using (var stream = File.Create(tempPath))
                write(stream);
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
    private static int ExtractMeshesFromSerializedFile(byte[] sfBytes, ContentSource source,
        string bundleTag, HashSet<Guid> neededGuids,
        string cacheDir, ObjectAssetDatabase db, Dictionary<(string Tag, long Id), UnityTexture>? neededTextures,
        IReadOnlyList<FoliageAsset>? foliageAssets = null, HashSet<Guid>? producedGuids = null,
        string? typeTreeCacheFor = null, HashSet<Guid>? staleLevelGuids = null,
        string? streamedVertexSource = null, CancellationToken cancellationToken = default) =>
        ExtractMeshesFrom(SerializedFile.Read(sfBytes), source, bundleTag, neededGuids, cacheDir, db,
            neededTextures, foliageAssets, producedGuids, typeTreeCacheFor, staleLevelGuids,
            streamedVertexSource, cancellationToken);

    // Same, for a caller that already holds the decoded file (the streaming pass reads it once and then
    // uses it for the meshes, the type trees and the terrain layers alike).
    private static int ExtractMeshesFrom(SerializedFile file, ContentSource source, string bundleTag,
        HashSet<Guid> neededGuids, string cacheDir,
        ObjectAssetDatabase db, Dictionary<(string Tag, long Id), UnityTexture>? neededTextures,
        IReadOnlyList<FoliageAsset>? foliageAssets = null, HashSet<Guid>? producedGuids = null,
        string? typeTreeCacheFor = null, HashSet<Guid>? staleLevelGuids = null,
        string? streamedVertexSource = null, CancellationToken cancellationToken = default)
    {

        // The per-class type trees are a by-product of having read this file. Handing them to the cache
        // here is what keeps the skybox and the character importer from decoding the same 170 MB prefix
        // again, on the main thread, in the middle of the load.
        if (typeTreeCacheFor != null)
            WriteTypeTreeCache(typeTreeCacheFor, file.TypeTreesByClassId);
        PrefabGraph graph = PrefabGraph.Read(file);
        MaterialResolver materials = MaterialResolver.Read(graph, source.AssetsDir, bundleTag);

        Directory.CreateDirectory(cacheDir);

        // Objects, trees (Unturned "resources") and vehicles share this pipeline; each asset maps to a
        // prefab in the masterbundle under objects/<folder>/object.prefab, trees/<folder>/resource.prefab
        // or vehicles/<folder>/vehicle.prefab. Reuse the caller's already-scanned asset DB (built from the
        // same bundle dirs) instead of re-scanning them here. Which of the three an asset is — which sets
        // the prefab-key prefix — is recovered from its directory.
        var work = new List<(ObjectAsset asset, string key)>();
        foreach (ObjectAsset a in db.All)
            work.Add((a, PrefabKey.For(a, source)));

        // The vertex buffers this file's needed prefabs keep in a .resS, read before anything is built:
        // BuildLevel drops a part whose mesh cannot be decoded, and a streamed buffer is exactly that
        // until its bytes are in hand. Costs one forward pass over the bundle, and only when something
        // still to be extracted actually needs one — a warm cache asks for nothing.
        // Planned from the prefabs whose mesh is NOT already cached against this bundle, not from every
        // needed one. A pass runs whenever anything is missing — one texture, one terrain layer, one
        // newly placed inline-only object — and planning from the whole needed set made every such pass
        // open a second decoder and re-read the streamed buffers of vehicles it already had, for the ~6 s
        // the forward pass costs. With nothing missing to plan for, ReadStreamedVertices never opens the
        // bundle at all.
        (HashSet<Guid> uncached, HashSet<Guid> cached) = streamedVertexSource == null
            ? (neededGuids, new HashSet<Guid>())
            : SplitByCacheState(cacheDir, neededGuids, streamedVertexSource);
        IReadOnlyDictionary<long, byte[]> streamedVertices = streamedVertexSource == null
            ? new Dictionary<long, byte[]>()
            : ReadStreamedVertices(streamedVertexSource, graph, work, uncached, cancellationToken);
        // A cancelled pass hands back whatever ranges it got to before it stopped, and building from a
        // partial set would cache meshes that are missing their streamed parts AND current by their own
        // magic — permanently, because nothing would ever ask for them again. Write nothing instead: the
        // next boot starts the extraction over, which is what cancelling it means everywhere else here.
        if (cancellationToken.IsCancellationRequested)
            return 0;

        var mappedGuids = new HashSet<Guid>();
        int extracted = 0;
        foreach ((ObjectAsset asset, string key) in work)
        {
            if (!neededGuids.Contains(asset.Guid) || !mappedGuids.Add(asset.Guid))
                continue;

            // A decal ships no prefab at all — just a decal.png beside its .dat inside the bundle, and the
            // size it projects at in its own .dat. Everything downstream (the texture phase, the per-GUID
            // batching, the placement transform) already handles a mesh, so it becomes one here rather
            // than a second pipeline: the quad its texture is drawn on. See WriteDecalQuad.
            if (asset.Type == EObjectType.Decal)
            {
                if (!cached.Contains(asset.Guid)
                    && !WriteDecalQuad(graph, bundleTag, cacheDir, asset, key, neededTextures))
                    continue;

                if (!cached.Contains(asset.Guid))
                    extracted++;
                producedGuids?.Add(asset.Guid);
                continue;
            }

            if (!graph.PartsByKey.TryGetValue(key, out List<MeshPart>? parts))
                continue;

            // Already cached against this bundle: leave it alone. Rebuilding it would produce the same
            // mesh at best, and at worst a worse one — the streamed pass above is planned for what is
            // missing, so a cached prefab with a streamed part would rebuild without those bytes and
            // overwrite a good entry with one that has silently lost them. Counted as produced because
            // it is: RecordMisses reads that set to decide what this bundle failed to make, and leaving
            // a cached GUID out of it would mark the entry a miss and then delete it.
            if (cached.Contains(asset.Guid))
            {
                producedGuids?.Add(asset.Guid);
                continue;
            }

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
                    UnityMesh mesh = UnityMesh.Read(
                        TypeTreeReader.Read(meshObj.TypeTree, file.ReaderFor(meshObj)),
                        streamedVertices.TryGetValue(part.MeshId, out byte[]? streamed) ? streamed : null);
                    if (!mesh.Usable)
                    {
                        complete = false;
                        continue;
                    }

                    int baseVertex = verts.Count;
                    // Normals transform by the inverse-transpose of the part's basis (correct under the
                    // non-uniform scales some prefab children carry), stored in Unity space like the vertices.
                    Basis normalBasis = part.LocalToRoot.Basis.Inverse().Transposed();
                    // A part placed through a mirroring transform — a vehicle's left wheels are the right
                    // ones scaled by -1 on an axis, and a few props do the same — comes out with its
                    // triangles wound the other way, so every face on it is backface-culled. Baking the
                    // pose into the vertices is what makes that this reader's problem: Unity keeps the
                    // scale on the transform and flips its own winding at draw time. Reverse the triangles
                    // instead, which is the same fix applied once rather than per frame.
                    bool mirrored = part.LocalToRoot.Basis.Determinant() < 0f;
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
                        int[] indices = MeshIndices.Rebase(src, baseVertex, mirrored);
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
                // Keep the base mesh — a placeholder box at every distance is worse than a stale coarse
                // shape past the switch distance — but do not let this GUID be stamped as current, or the
                // stale level would be loaded forever. Left unstamped it reads as cold and is retried.
                staleLevelGuids?.Add(asset.Guid);
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
            // A previous source's collider must never survive for a colliding GUID, but the delete has to
            // come after the replacement is in place, not before: the rename overwrites, and deleting first
            // left a window where a kill or a failed write ended with no file at all — which the
            // completeness check reads as "this prefab legitimately has none", so the object would have
            // stayed collisionless with nothing to notice it.
            string colliderPath = Path.Combine(cacheDir, asset.Guid.ToString("N") + ".collider");
            List<CachedCollider>? colliders =
                graph.CollidersByKey.TryGetValue(key, out List<ColliderPart>? colliderParts)
                    ? BuildColliders(colliderParts, graph, file)
                    : null;
            if (colliders is { Count: > 0 })
                WriteAtomically(colliderPath, stem + ".collider.tmp", s => ColliderCache.Write(s, colliders));
            else
                File.Delete(colliderPath); // this source really has no colliders: absence is the right state
        }

        if (foliageAssets != null)
            extracted += ExtractFoliageMeshes(graph, bundleTag, foliageAssets, cacheDir, neededTextures,
                producedGuids);

        return extracted;
    }

    // The needed GUIDs this bundle does not already hold a current mesh for — the same completeness test
    // ContentExtraction.Plan applies, asked here because ExtractMeshesFrom is given the needed set rather
    // than the missing one and the streamed-vertex pass is far too expensive to run for meshes that are
    // already cached. Known misses are excluded: a GUID with no prefab in this bundle will not gain one.
    // Returns both halves, because the build loop needs the other one: `Cached` is what this bundle
    // already holds a current mesh for, and rebuilding one of those is not merely wasted work — the
    // streamed pass was planned for `Uncached` alone, so a cached vehicle rebuilt here would find no
    // stream bytes, lose its wheels to BuildLevel's partial-level rule, and overwrite the good entry
    // stamped current. A GUID that is a known miss is in neither set: it has no prefab in this bundle,
    // so it is neither cached nor worth planning for.
    private static (HashSet<Guid> Uncached, HashSet<Guid> Cached) SplitByCacheState(
        string cacheDir, HashSet<Guid> neededGuids, string bundlePath)
    {
        long stamp = ExtractionIndex.StampFor(bundlePath);
        HashSet<Guid> misses = ExtractionIndex.Load(
            Path.Combine(cacheDir, ExtractionIndex.FileNameFor(bundlePath)), stamp);
        HashSet<Guid> uncached = ExtractionIndex.MissingMeshes(cacheDir, neededGuids, misses, bundlePath, stamp);

        var cached = new HashSet<Guid>();
        foreach (Guid guid in neededGuids)
            if (guid != Guid.Empty && !misses.Contains(guid) && !uncached.Contains(guid))
                cached.Add(guid);
        return (uncached, cached);
    }

    // Every .resS-backed vertex buffer the prefabs about to be extracted depend on, read in one forward
    // pass over the bundle. Planned from the same (asset, key) list the build below walks, and from both
    // levels of each prefab, so a level is never built from a set of parts the plan did not cover.
    //
    // Collision meshes are deliberately not planned: a MeshCollider's own low-poly geometry is inline in
    // every bundle shipped, and asking for ranges nobody needs would make the pass read further into the
    // stream for nothing.
    private static IReadOnlyDictionary<long, byte[]> ReadStreamedVertices(string bundlePath,
        PrefabGraph graph, List<(ObjectAsset asset, string key)> work, HashSet<Guid> neededGuids,
        CancellationToken cancellationToken)
    {
        var requests = new List<VertexStreamReader.Request>();
        var seen = new HashSet<long>();
        var planned = new HashSet<Guid>();

        void Plan(List<MeshPart>? parts)
        {
            if (parts == null)
                return;
            foreach (MeshPart part in parts)
            {
                if (!seen.Add(part.MeshId)
                    || !graph.ObjectsByPathId.TryGetValue(part.MeshId, out SerializedObject? meshObj))
                {
                    continue;
                }
                UnityMesh.StreamRef stream = UnityMesh.ReadStreamRef(
                    TypeTreeReader.Read(meshObj.TypeTree, graph.File.ReaderFor(meshObj)));
                if (stream.IsStreamed)
                    requests.Add(new VertexStreamReader.Request(part.MeshId, stream));
            }
        }

        foreach ((ObjectAsset asset, string key) in work)
        {
            if (!neededGuids.Contains(asset.Guid) || !planned.Add(asset.Guid))
                continue;
            if (!graph.PartsByKey.TryGetValue(key, out List<MeshPart>? parts))
                continue;
            Plan(parts);
            Plan(graph.Lod1PartsByKey.TryGetValue(key, out List<MeshPart>? lod1) ? lod1 : null);
        }

        if (requests.Count == 0)
            return new Dictionary<long, byte[]>();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Dictionary<long, byte[]> bytes =
            VertexStreamReader.Read(bundlePath, requests, cancellationToken);
        AppShutdown.PrintUnlessQuitting($"[extract] streamed vertex buffers: {bytes.Count}/{requests.Count} "
            + $"read in {watch.ElapsedMilliseconds} ms");
        return bytes;
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
            bool isLadder = UnityLayers.IsLadder(p.Layer);
            switch (p.Kind)
            {
                case EColliderKind.Box:
                    result.Add(CachedCollider.Box(p.LocalToRoot, p.Center, p.Size, isLadder));
                    break;
                case EColliderKind.Sphere:
                    result.Add(CachedCollider.Sphere(p.LocalToRoot, p.Center, p.Radius, isLadder));
                    break;
                case EColliderKind.Capsule:
                    result.Add(CachedCollider.Capsule(p.LocalToRoot, p.Center, p.Radius, p.Height,
                        p.Direction, isLadder));
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
                    result.Add(CachedCollider.Mesh(p.LocalToRoot, mesh.Vertices, indices.ToArray(), isLadder));
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

    // Writes a decal asset's mesh: one quad, DecalX by DecalY metres, lying flat in the object's own XZ
    // plane with its decal.png stretched across it. The placement's authored rotation is what stands it up
    // against a wall, so nothing here needs to know which way it faces.
    //
    // Unturned projects these onto whatever is under them, so a decal on uneven ground creases where this
    // quad stays flat. Every one the official maps place sits on a wall or a road, where the two agree.
    private static bool WriteDecalQuad(PrefabGraph graph, string bundleTag, string cacheDir,
        ObjectAsset asset, string key, Dictionary<(string Tag, long Id), UnityTexture>? neededTextures)
    {
        if (asset.DecalX <= 0f || asset.DecalY <= 0f)
            return false;

        string texturePath = graph.AssetPrefix + key + "/" + DecalTextureFile;
        if (!graph.ContainerByPath.TryGetValue(texturePath, out long textureId)
            || !graph.ObjectsByPathId.TryGetValue(textureId, out SerializedObject? textureObj))
        {
            return false;
        }

        if (neededTextures != null && !neededTextures.ContainsKey((bundleTag, textureId)))
        {
            neededTextures[(bundleTag, textureId)] =
                UnityTexture.Read(TypeTreeReader.Read(textureObj.TypeTree, graph.File.ReaderFor(textureObj)));
        }

        float halfX = asset.DecalX / 2f;
        float halfZ = asset.DecalY / 2f;
        var vertices = new[]
        {
            new Vector3(-halfX, 0f, -halfZ), new Vector3(halfX, 0f, -halfZ),
            new Vector3(halfX, 0f, halfZ), new Vector3(-halfX, 0f, halfZ),
        };
        var normals = new[] { Vector3.Up, Vector3.Up, Vector3.Up, Vector3.Up };
        var uvs = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };

        // Cutout, and culled on neither side: a decal texture is mostly transparent, and which way the
        // authored rotation ends up pointing the quad is not something the asset says.
        var submesh = new CachedSubmesh(new[] { 0, 1, 2, 0, 2, 3 }, Colors.White,
            TextureKey.For(bundleTag, textureId), UnityMaterial.Blend.Cutout, cull: EShaderCull.TwoSided);

        string path = Path.Combine(cacheDir, asset.Guid.ToString("N") + ".mesh");
        using (var stream = File.Create(path))
            MeshCache.Write(stream, vertices, normals, uvs, new[] { submesh });
        File.Delete(Path.Combine(cacheDir, asset.Guid.ToString("N") + ".collider"));
        return true;
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

}
