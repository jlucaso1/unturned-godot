using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// One-time extraction of the movement audio: for each OneShotAudioDefinition the physics materials
// reference (FootstepWalk/FootstepRun/BipedLand), read its MonoBehaviour (volume/pitch + AudioClip list),
// slice each clip's FSB5 blob out of the masterbundle's .resource stream and rebuild it as a standard .ogg
// (Fmod5Sharp — Unturned's clips are FSB5/Vorbis), cached per definition under audioCacheDir.
//
// The clips are byte ranges in a stream node exactly like texture pixels are, so a cold load plans them
// into the object streamer's bundle pass (see ModelExtractor.StreamExtract) and pays for no second decode:
// Plan/WriteClip/CompleteDefs are that interface. Extract is the standalone fallback for everything the
// pass did not cover — an unstreamable bundle, an interrupted pass, or a load whose meshes were cached and
// therefore ran no pass at all.
public static class AudioExtractor
{
    // Cache layout: <audioCacheDir>/<DefKey>/def.bin + <clip>.ogg. A def.bin marks the def as complete.
    public static bool IsCached(string audioCacheDir, string defName) =>
        File.Exists(Path.Combine(audioCacheDir, defName, "def.bin"));

    // The cache key carries both source identity and the normalized full asset path. Definitions with the
    // same leaf name routinely live in different surface folders; using only that leaf let the later one
    // overwrite def.bin and made both surfaces play the same clips.
    public static string DefKey(string bundleTag, string assetPath)
    {
        string leaf = TextureKey.TagFor(DefNameOf(assetPath));
        string prefix = bundleTag.Length == 0 ? leaf : bundleTag + "-" + leaf;
        return TextureKey.Discriminate(prefix, assetPath);
    }

    public static string DefNameOf(string assetPath)
    {
        string file = assetPath.Replace('\\', '/');
        int slash = file.LastIndexOf('/');
        if (slash >= 0)
            file = file[(slash + 1)..];
        return file.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) ? file[..^6] : file;
    }

    // A synthetic definition built from RAW AudioClips (assets the game plays directly, without a
    // OneShotAudioDefinition — e.g. ZombieManager's roar/groan arrays): the clip container paths
    // plus the caller-supplied volume/pitch envelope, cached under the group name like any def.
    public sealed record RawClipGroup(
        string Name, IReadOnlyList<string> ClipPaths, float Volume, float MinPitch, float MaxPitch);

    // What one bundle owes the movement audio: the definitions to look for in its SerializedFiles, plus
    // the raw clip groups that ride along with the game's own bundle.
    public sealed record Request(string BundlePath, string BundleTag, IReadOnlyCollection<string> DefPaths,
        IReadOnlyCollection<RawClipGroup>? ClipGroups, string AudioCacheDir);

    // Everything one SerializedFile's audio costs the pass that carries it: the clip byte ranges to take
    // out of a stream node, and the definitions those clips complete. Built before any stream node is
    // read, because a forward-only decoder cannot go back for a range named after it went past.
    public sealed class StreamPlan
    {
        // A wanted clip: where its FSB5 blob sits in which stream file, which definition it belongs to, and
        // its place in that definition's own clip list. The pass hands ranges back in stream order, so the
        // slot is what keeps a def.bin written here identical to one written by the standalone extractor
        // — which is in turn what makes the two paths comparable, file for file, when verifying a change.
        public readonly record struct Clip(string StreamFile, long Offset, int Size, int Definition,
            int Slot, string Name, long ClipId);

        internal sealed class PendingDef
        {
            internal string Directory = "";
            internal float Volume = 1f;
            internal float MinPitch = 1f;
            internal float MaxPitch = 1f;
            internal readonly List<string?> ClipFiles = new();

            // Slots already owed to some file's stream ranges. A group's clips arrive over several files
            // and a slot is only filled once its bytes land, so "planned" and "written" are different
            // questions and asking the second one would plan the same clip once per file.
            internal readonly HashSet<int> Planned = new();

            // A clip this pass cannot serve after all — its stream node was already behind the decoder
            // when the definition naming it was read. def.bin is a permanent completeness marker, so
            // writing one now would cache a definition missing a clip that IS in the bundle, and no
            // later boot would ever look for it again. Leave no marker and let the next pass have it.
            internal bool Unservable;
        }

        public readonly List<Clip> Clips = new();
        internal readonly List<PendingDef> Definitions = new();

        // By cache directory, because that is what a definition IS on disk. One plan spans every
        // serialized file of a bundle, and a definition already planned from an earlier file must not be
        // planned a second time from a later one — its def.bin is not written yet, so it still reads as
        // missing. A raw clip group instead accumulates: its clips are found by container path, one at a
        // time, so a bundle that spreads them over several files completes the group only once every
        // file has contributed. Writing per file gave that group a def.bin holding one file's share.
        private readonly Dictionary<string, int> _byDirectory = new(StringComparer.Ordinal);

        public string CacheDirectory { get; internal set; } = "";
        public int DefinitionCount => Definitions.Count;

        internal bool Knows(string directory) => _byDirectory.ContainsKey(directory);

        internal int Add(PendingDef definition)
        {
            _byDirectory[definition.Directory] = Definitions.Count;
            Definitions.Add(definition);
            return Definitions.Count - 1;
        }

        internal (PendingDef Definition, int Index)? Existing(string directory) =>
            _byDirectory.TryGetValue(directory, out int index) ? (Definitions[index], index) : null;

        // Told by the pass when a planned range turns out to be unreachable in this decode.
        public void MarkUnservable(int definition)
        {
            if (definition >= 0 && definition < Definitions.Count)
                Definitions[definition].Unservable = true;
        }
    }

    // Plans one SerializedFile's share of a request into `plan` (a new one when null): which clip ranges
    // the pass has to hand back and which definitions they complete. Returns null when nothing is missing
    // and no plan was carried in, so a caller with several files pays nothing for the ones that answer
    // nothing. Pass the same plan back for every file of one bundle.
    public static StreamPlan? Plan(SerializedFile file, Request request, StreamPlan? plan = null)
    {
        List<string> missing = MissingDefs(request);
        List<RawClipGroup> missingGroups = MissingGroups(request);
        if (missing.Count == 0 && missingGroups.Count == 0)
            return plan;

        var catalog = new FileCatalog(file);
        plan ??= new StreamPlan { CacheDirectory = request.AudioCacheDir };

        foreach (string assetPath in missing)
        {
            string directory = Path.Combine(request.AudioCacheDir, DefKey(request.BundleTag, assetPath));
            // A definition's clips are path ids, which are local to the file that names them, so the
            // whole definition belongs to one file: the first that carries it is the only one that can.
            if (plan.Knows(directory) || catalog.Find(assetPath) is not { } defObject)
                continue;

            // Malformed audio metadata is a skipped definition, never a failed load: this now runs inside
            // the pass that also streams the meshes, object textures and terrain layers, and a workshop
            // asset whose serialized shape is not what these casts expect must not end that pass.
            try
            {
                PlanDefinition(plan, catalog, file, defObject, directory);
            }
            catch (Exception e) when (IsMalformedAsset(e))
            {
                AppShutdown.WarnUnlessQuitting($"[audio] skipping {assetPath}: unreadable definition "
                    + $"({e.GetType().Name})");
            }
        }

        foreach (RawClipGroup group in missingGroups)
        {
            try
            {
                PlanGroup(plan, catalog, file, group, Path.Combine(request.AudioCacheDir, group.Name));
            }
            catch (Exception e) when (IsMalformedAsset(e))
            {
                AppShutdown.WarnUnlessQuitting($"[audio] skipping group {group.Name}: unreadable clips "
                    + $"({e.GetType().Name})");
            }
        }

        // The carried plan, never null once one exists: it holds the definitions and slots planned from
        // earlier files, and a file that adds no range of its own must not throw that away — the next
        // file would then plan the same definitions again from scratch.
        return plan;
    }

    // Resolved off the plan and published only once the whole definition has been walked. Publishing as
    // it went left a definition half in the plan when a later clip threw, and CompleteDefs then wrote a
    // def.bin from that prefix — a permanently cached definition missing clips nobody would look for
    // again. Each clip is contained on its own, so one unreadable clip costs that clip, as an
    // unresolvable or unrebuildable one already did.
    private static void PlanDefinition(StreamPlan plan, FileCatalog catalog, SerializedFile file,
        SerializedObject defObject, string directory)
    {
        Dictionary<string, object> def = TypeTreeReader.Read(defObject.TypeTree, file.ReaderFor(defObject));
        var pending = new StreamPlan.PendingDef
        {
            Directory = directory,
            Volume = Convert.ToSingle(def.GetValueOrDefault("volumeMultiplier", 1f)),
            MinPitch = Convert.ToSingle(def.GetValueOrDefault("minPitch", 1f)),
            MaxPitch = Convert.ToSingle(def.GetValueOrDefault("maxPitch", 1f)),
        };

        if (def.GetValueOrDefault("clips") is not List<object> clips)
            return;

        // The slot is the clip's place in the definition's own list, so what lands on disk is in the
        // author's order however the stream hands the ranges back. Clips the file cannot resolve leave
        // their slot empty and CompleteDefs drops it.
        var resolved = new List<(StreamPlan.Clip Clip, int Slot)>();
        for (int slot = 0; slot < clips.Count; slot++)
        {
            pending.ClipFiles.Add(null);
            if (ResolveClip(catalog, file, clips[slot], slot) is { } clip)
                resolved.Add((clip, slot));
        }

        int index = plan.Add(pending);
        foreach ((StreamPlan.Clip clip, int _) in resolved)
            plan.Clips.Add(clip with { Definition = index });
    }

    // Raw clip groups are resolved one container path at a time, so a bundle whose serialized files split
    // them contributes to the same pending definition on each pass through.
    private static void PlanGroup(StreamPlan plan, FileCatalog catalog, SerializedFile file,
        RawClipGroup group, string directory)
    {
        StreamPlan.PendingDef pending;
        int index;
        if (plan.Existing(directory) is { } known)
        {
            (pending, index) = known;
        }
        else
        {
            pending = new StreamPlan.PendingDef
            {
                Directory = directory,
                Volume = group.Volume,
                MinPitch = group.MinPitch,
                MaxPitch = group.MaxPitch,
            };
            index = plan.Add(pending);
            for (int i = 0; i < group.ClipPaths.Count; i++)
                pending.ClipFiles.Add(null);
        }

        for (int slot = 0; slot < group.ClipPaths.Count; slot++)
        {
            if (pending.ClipFiles[slot] != null || pending.Planned.Contains(slot))
                continue; // an earlier file already owns this clip
            if (catalog.Find(group.ClipPaths[slot]) is not { } clipObject)
                continue;
            if (ResolveClip(catalog, file, clipObject, slot) is { } clip)
            {
                plan.Clips.Add(clip with { Definition = index });
                pending.Planned.Add(slot);
            }
        }
    }

    // What a serialized shape this reader does not expect throws as it is walked. Every one of these is
    // "this asset is not what it claims to be", which is a skipped definition rather than a failed load.
    private static bool IsMalformedAsset(Exception e) =>
        e is InvalidCastException or KeyNotFoundException or NullReferenceException or FormatException
            or OverflowException or ArgumentException or InvalidDataException;

    // Rebuilds one planned clip from the exact bytes of its range and writes the .ogg beside its
    // definition. Best effort per clip: a blob FMOD cannot rebuild leaves that clip out rather than
    // failing the definition, which is what the whole-bundle path did too.
    //
    // The write is best effort for the same reason, and now for a larger one: this runs inside the load's
    // shared bundle pass, so an unwritable audio cache would otherwise throw out through the forward
    // reader and take the meshes, object textures and terrain layers of that pass down with it. A sound
    // this session cannot cache is a sound; it is not a reason to stop building the world.
    public static void WriteClip(StreamPlan plan, int clipIndex, byte[] fsb)
    {
        if (clipIndex < 0 || clipIndex >= plan.Clips.Count)
            return;
        StreamPlan.Clip clip = plan.Clips[clipIndex];
        StreamPlan.PendingDef def = plan.Definitions[clip.Definition];
        byte[]? ogg = RebuildOgg(fsb, clip.Name);
        if (ogg == null)
            return;

        string fileName = SafeCachePath.UniqueFileName(clip.Name, "clip", clip.ClipId, ".ogg");
        if (!SafeCachePath.TryResolveChild(def.Directory, fileName, out string clipOutput))
            return;
        try
        {
            Directory.CreateDirectory(def.Directory);
            File.WriteAllBytes(clipOutput, ogg);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppShutdown.WarnUnlessQuitting($"[audio] could not cache '{clip.Name}': {e.Message}");
            return;
        }

        def.ClipFiles[clip.Slot] = fileName;
    }

    // Marks every definition whose clips landed as complete. def.bin is the cache's only completeness
    // marker, so it is written last and only for definitions that actually got clips: a def.bin over an
    // empty directory would make every later boot skip the definition and play silence. Per definition,
    // and contained for the same reason WriteClip is: this closes the shared bundle pass.
    public static int CompleteDefs(StreamPlan plan)
    {
        int completed = 0;
        foreach (StreamPlan.PendingDef def in plan.Definitions)
        {
            if (def.Unservable)
            {
                // A clip of this definition sits in a node the decoder had already gone past. Marking it
                // complete now would cache it permanently without a clip the bundle does in fact hold.
                AppShutdown.WarnUnlessQuitting($"[audio] {Path.GetFileName(def.Directory)} needs a range "
                    + "this pass could not reach; leaving it for the next one");
                continue;
            }

            // In the definition's own clip order, minus the slots whose blob FMOD could not rebuild or
            // whose file could not be written.
            var clips = new List<string>(def.ClipFiles.Count);
            foreach (string? file in def.ClipFiles)
                if (file != null)
                    clips.Add(file);
            if (clips.Count == 0)
                continue;

            // Through a temporary and renamed into place, like every other cache file judged current by
            // its mere existence: a def.bin truncated by a full disk mid-write would be read as a
            // complete definition by every later boot, and re-extraction would never look at it again.
            string marker = Path.Combine(def.Directory, "def.bin");
            string temporary = marker + ".tmp";
            try
            {
                Directory.CreateDirectory(def.Directory);
                using (FileStream s = File.Create(temporary))
                    AudioDefCache.Write(s, new OneShotAudioDef(def.Volume, def.MinPitch, def.MaxPitch, clips));
                File.Move(temporary, marker, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // No marker, so the clips already on disk are re-extracted next time rather than read as
                // a definition that never completed.
                AppShutdown.WarnUnlessQuitting($"[audio] could not complete {def.Directory}: {e.Message}");
                try { File.Delete(temporary); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                continue;
            }

            completed++;
        }
        return completed;
    }

    // A clip named by path id (a definition's own list), resolved through the catalog first.
    private static StreamPlan.Clip? ResolveClip(FileCatalog catalog, SerializedFile file, object pptr,
        int slot)
    {
        try
        {
            long clipId = PathId(pptr);
            return catalog.ById(clipId) is { } clipObject
                ? ResolveClip(catalog, file, clipObject, slot)
                : null;
        }
        catch (Exception e) when (IsMalformedAsset(e))
        {
            AppShutdown.WarnUnlessQuitting($"[audio] skipping clip {slot}: unreadable reference "
                + $"({e.GetType().Name})");
            return null;
        }
    }

    // The clip's own metadata. Contained per clip, exactly as an unresolvable or unrebuildable clip
    // already is: one bad clip costs that clip, not the definition it belongs to and not the pass.
    // The Definition index is filled in by the caller once the definition is published.
    private static StreamPlan.Clip? ResolveClip(FileCatalog catalog, SerializedFile file,
        SerializedObject clipObject, int slot)
    {
        try
        {
            long clipId = clipObject.PathId;
            Dictionary<string, object> clip =
                TypeTreeReader.Read(clipObject.TypeTree, file.ReaderFor(clipObject));
            string name = clip.GetValueOrDefault("m_Name") as string ?? $"clip_{clipId:x}";
            if (clip.GetValueOrDefault("m_Resource") is not Dictionary<string, object> res
                || res.GetValueOrDefault("m_Offset") is not { } rawOffset
                || res.GetValueOrDefault("m_Size") is not { } rawSize)
            {
                return null;
            }

            long offset = Convert.ToInt64(rawOffset);
            int size = Convert.ToInt32(rawSize);
            if (offset < 0 || size <= 0)
                return null;

            // Older bundles omit m_Source because they carry only one audio stream; the empty name means
            // "whichever .resource this bundle has", which the caller resolves against its own node table.
            string source = res.GetValueOrDefault("m_Source") as string ?? "";
            return new StreamPlan.Clip(ResourceName(source), offset, size, 0, slot, name, clipId);
        }
        catch (Exception e) when (IsMalformedAsset(e))
        {
            AppShutdown.WarnUnlessQuitting($"[audio] skipping clip {slot}: unreadable metadata "
                + $"({e.GetType().Name})");
            return null;
        }
    }

    private static List<string> MissingDefs(Request request)
    {
        var missing = new List<string>();
        foreach (string path in request.DefPaths)
            if (!IsCached(request.AudioCacheDir, DefKey(request.BundleTag, path)))
                missing.Add(path);
        return missing;
    }

    private static List<RawClipGroup> MissingGroups(Request request)
    {
        var missing = new List<RawClipGroup>();
        if (request.ClipGroups != null)
            foreach (RawClipGroup group in request.ClipGroups)
                if (!IsCached(request.AudioCacheDir, group.Name))
                    missing.Add(group);
        return missing;
    }

    // True when this request has nothing left to fetch, so neither the pass nor the fallback has to look
    // at the bundle at all.
    public static bool IsSatisfied(Request request) =>
        MissingDefs(request).Count == 0 && MissingGroups(request).Count == 0;

    // Every object of one SerializedFile, by path id and by the container path the bundle publishes it
    // under. Path ids are local to one file, so a bundle carrying several keeps one catalog per file:
    // colliding ids in a multi-file workshop bundle otherwise resolve into the wrong one.
    private sealed class FileCatalog
    {
        private readonly Dictionary<long, SerializedObject> _byId = new();

        // Keyed by the container path's last segment, which is the only part a wanted path is guaranteed
        // to pin down. The core bundle publishes tens of thousands of containers and a plan asks for one
        // per definition and per raw clip, so scanning them all per lookup is a product this pass now
        // pays inside the load rather than in a background extraction.
        private readonly Dictionary<string, List<(string Path, long Id)>> _byLeaf =
            new(StringComparer.OrdinalIgnoreCase);

        internal FileCatalog(SerializedFile file)
        {
            foreach (SerializedObject o in file.Objects)
                _byId[o.PathId] = o;

            foreach (SerializedObject o in file.Objects)
            {
                if (o.ClassId != 142) // AssetBundle
                    continue;
                Dictionary<string, object> ab = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
                foreach (object entry in (List<object>)ab["m_Container"])
                {
                    var pair = (Dictionary<string, object>)entry;
                    string path = (string)pair["first"];
                    long id = PathId(((Dictionary<string, object>)pair["second"])["asset"]);
                    string leaf = LastSegment(path);
                    if (!_byLeaf.TryGetValue(leaf, out List<(string, long)>? candidates))
                        _byLeaf[leaf] = candidates = new List<(string, long)>();
                    candidates.Add((path, id));
                }
            }
        }

        internal SerializedObject? ById(long id) => _byId.GetValueOrDefault(id);

        internal SerializedObject? Find(string assetPath)
        {
            string wanted = assetPath.Replace('\\', '/');
            if (!_byLeaf.TryGetValue(LastSegment(wanted), out List<(string Path, long Id)>? candidates))
                return null;

            foreach ((string path, long id) in candidates)
                if (EndsAtSegmentBoundary(path, wanted) && _byId.TryGetValue(id, out SerializedObject? asset))
                    return asset;

            return null;
        }

        // The wanted path is bundle-relative, so it matches a suffix of the published container path —
        // but only one that starts where a segment does. A plain EndsWith let "…/Walk/Boardwalk.asset"
        // answer a request for "…/Walk/ardwalk.asset"-shaped tails of a longer name.
        private static bool EndsAtSegmentBoundary(string path, string wanted)
        {
            if (!path.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
                return false;
            int start = path.Length - wanted.Length;
            return start == 0 || path[start - 1] == '/';
        }

        private static string LastSegment(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path[(slash + 1)..] : path;
        }
    }

    // Standalone fallback: decode the bundle by itself and serve the plan out of the .resource node it
    // carries. Only reached for what the cold-load pass did not cover, and returns without opening the
    // bundle when the cache already holds every definition.
    public static int Extract(string bundlePath, string bundleTag,
        IReadOnlyCollection<string> defAssetPaths, string audioCacheDir,
        IReadOnlyCollection<RawClipGroup>? clipGroups = null)
    {
        var request = new Request(bundlePath, bundleTag, defAssetPaths, clipGroups, audioCacheDir);
        if (IsSatisfied(request))
            return 0;

        AppShutdown.PrintUnlessQuitting($"[audio] extracting {MissingDefs(request).Count} audio definitions "
            + $"and {MissingGroups(request).Count} clip groups from masterbundle (one-time)...");
        AudioNodes nodes = ReadAudioNodes(bundlePath);
        // One plan across every serialized file this bundle carries, then written once. Planning and
        // completing per file marked a raw clip group cached from the first file that held any of its
        // clips, so a bundle that spread them left the rest unextracted for good.
        StreamPlan? plan = null;
        int served = 0;
        foreach (byte[] bytes in nodes.SerializedFiles)
        {
            if (AppShutdown.IsShuttingDown)
                return 0; // leaving: stop between files, and write no half-built definition
            plan = Plan(SerializedFile.Read(bytes), request, plan);
            if (plan == null)
                continue;

            // Only what this file added: a clip already served keeps the bytes it was written from.
            for (; served < plan.Clips.Count; served++)
            {
                StreamPlan.Clip clip = plan.Clips[served];
                if (ResourceFor(clip.StreamFile, nodes.Resources) is not { } resource
                    || clip.Offset + clip.Size > resource.Length)
                {
                    continue;
                }

                var blob = new byte[clip.Size];
                Array.Copy(resource, clip.Offset, blob, 0, clip.Size);
                WriteClip(plan, served, blob);
            }
        }

        int extracted = plan == null ? 0 : CompleteDefs(plan);
        if (MissingDefs(request) is { Count: > 0 } absent)
            AppShutdown.WarnUnlessQuitting($"[audio] {absent.Count} definition(s) not found in "
                + $"{Path.GetFileName(bundlePath)}, first: {absent[0]}");
        AppShutdown.PrintUnlessQuitting($"[audio] extracted {extracted} definitions to {audioCacheDir}");
        return extracted;
    }

    // Streams the bundle forward, keeping every SerializedFile and audio .resource node and
    // DISCARDING the ~1.2 GB texture .resS in chunks: audio never reads it, and materializing every
    // node (the old UnityBundle.Read path) held the whole decompressed bundle in memory at once —
    // multi-GB RSS spikes on the one-time audio extraction. Falls back to the full decode only for
    // bundles that are not the expected single-LZMA-block shape.
    private sealed record AudioNodes(List<byte[]> SerializedFiles, Dictionary<string, byte[]> Resources);

    private static AudioNodes ReadAudioNodes(string bundlePath)
    {
        // Straight off disk: the decoder pulls the compressed block through the file itself, so the
        // bundle is never held in memory on top of what it decompresses to.
        using MasterBundleStream? stream = MasterBundleStream.OpenFile(bundlePath);
        if (stream == null)
        {
            UnityBundle bundle = UnityBundle.Read(File.ReadAllBytes(bundlePath));
            var serializedFiles = new List<byte[]>();
            var resources = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, byte[]> f in bundle.Files)
            {
                if (f.Key.EndsWith(".resource"))
                    resources[ResourceName(f.Key)] = f.Value;
                else if (!f.Key.EndsWith(".resS"))
                    serializedFiles.Add(f.Value);
            }
            return new AudioNodes(serializedFiles, resources);
        }

        var ordered = new List<MasterBundleStream.Node>(stream.Nodes);
        ordered.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        var serialized = new List<byte[]>();
        var resourceNodes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        byte[]? discard = null;
        foreach (MasterBundleStream.Node node in ordered)
        {
            if (node.Path.EndsWith(".resource"))
            {
                resourceNodes[ResourceName(node.Path)] = stream.Read((int)node.Size);
            }
            else if (node.Path.EndsWith(".resS"))
            {
                // Forward-only stream: decompress through the texture blob in chunks, retaining none.
                discard ??= new byte[16 * 1024 * 1024];
                long remaining = node.Size;
                while (remaining > 0)
                {
                    int got = stream.Read(discard, 0, (int)Math.Min(discard.Length, remaining));
                    if (got == 0)
                        break;
                    remaining -= got;
                }
            }
            else
            {
                serialized.Add(stream.Read((int)node.Size));
            }
        }
        return new AudioNodes(serialized, resourceNodes);
    }

    public static string ResourceName(string path)
    {
        string normalized = path.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static byte[]? ResourceFor(string streamFile, IReadOnlyDictionary<string, byte[]> resources)
    {
        if (streamFile.Length > 0 && resources.TryGetValue(streamFile, out byte[]? resource))
            return resource;

        // Older bundles omit m_Source because they carry only one audio stream.
        if (resources.Count == 1)
            foreach (byte[] only in resources.Values)
                return only;
        return null;
    }

    private static byte[]? RebuildOgg(byte[] fsb, string name)
    {
        try
        {
            Fmod5Sharp.FmodTypes.FmodSoundBank bank = Fmod5Sharp.FsbLoader.LoadFsbFromByteArray(fsb);
            if (bank.Samples.Count == 0)
                return null;
            return bank.Samples[0].RebuildAsStandardFileFormat(out byte[]? data, out string? ext)
                && data != null && ext == "ogg"
                ? data
                : null;
        }
        catch (Exception e)
        {
            AppShutdown.WarnUnlessQuitting($"[audio] failed to rebuild '{name}': {e.Message}");
            return null;
        }
    }

    private static long PathId(object pptr) => Convert.ToInt64(((Dictionary<string, object>)pptr)["m_PathID"]);
}
