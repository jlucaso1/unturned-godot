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
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
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
        }

        public readonly List<Clip> Clips = new();
        internal readonly List<PendingDef> Definitions = new();
        public string CacheDirectory { get; internal set; } = "";
        public int DefinitionCount => Definitions.Count;
    }

    // Plans one SerializedFile's share of a request: which clip ranges the pass has to hand back and which
    // definitions they complete. Returns null when this file carries none of what is missing, so a caller
    // with several files pays nothing for the ones that answer nothing.
    public static StreamPlan? Plan(SerializedFile file, Request request)
    {
        List<string> missing = MissingDefs(request);
        List<RawClipGroup> missingGroups = MissingGroups(request);
        if (missing.Count == 0 && missingGroups.Count == 0)
            return null;

        var catalog = new FileCatalog(file);
        var plan = new StreamPlan { CacheDirectory = request.AudioCacheDir };

        foreach (string assetPath in missing)
        {
            if (catalog.Find(assetPath) is not { } defObject)
                continue;

            Dictionary<string, object> def = TypeTreeReader.Read(defObject.TypeTree, file.ReaderFor(defObject));
            string defName = DefKey(request.BundleTag, assetPath);
            var pending = new StreamPlan.PendingDef
            {
                Directory = Path.Combine(request.AudioCacheDir, defName),
                Volume = Convert.ToSingle(def.GetValueOrDefault("volumeMultiplier", 1f)),
                MinPitch = Convert.ToSingle(def.GetValueOrDefault("minPitch", 1f)),
                MaxPitch = Convert.ToSingle(def.GetValueOrDefault("maxPitch", 1f)),
            };

            int index = plan.Definitions.Count;
            int planned = 0;
            if (def.TryGetValue("clips", out object? clips))
                foreach (object c in (List<object>)clips)
                {
                    long clipId = PathId(c);
                    if (catalog.ById(clipId) is { } clipObject
                        && PlanClip(plan, pending, file, clipObject, clipId, index, planned))
                    {
                        planned++;
                    }
                }

            if (planned > 0)
                plan.Definitions.Add(pending);
        }

        foreach (RawClipGroup group in missingGroups)
        {
            var pending = new StreamPlan.PendingDef
            {
                Directory = Path.Combine(request.AudioCacheDir, group.Name),
                Volume = group.Volume,
                MinPitch = group.MinPitch,
                MaxPitch = group.MaxPitch,
            };

            int index = plan.Definitions.Count;
            int planned = 0;
            foreach (string clipPath in group.ClipPaths)
                if (catalog.Find(clipPath) is { } clipObject
                    && PlanClip(plan, pending, file, clipObject, clipObject.PathId, index, planned))
                {
                    planned++;
                }

            if (planned > 0)
                plan.Definitions.Add(pending);
        }

        return plan.Clips.Count > 0 ? plan : null;
    }

    // Rebuilds one planned clip from the exact bytes of its range and writes the .ogg beside its
    // definition. Best effort per clip: a blob FMOD cannot rebuild leaves that clip out rather than
    // failing the definition, which is what the whole-bundle path did too.
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
        Directory.CreateDirectory(def.Directory);
        File.WriteAllBytes(clipOutput, ogg);
        def.ClipFiles[clip.Slot] = fileName;
    }

    // Marks every definition whose clips landed as complete. def.bin is the cache's only completeness
    // marker, so it is written last and only for definitions that actually got clips: a def.bin over an
    // empty directory would make every later boot skip the definition and play silence.
    public static int CompleteDefs(StreamPlan plan)
    {
        int completed = 0;
        foreach (StreamPlan.PendingDef def in plan.Definitions)
        {
            // In the definition's own clip order, minus the slots whose blob FMOD could not rebuild.
            var clips = new List<string>(def.ClipFiles.Count);
            foreach (string? file in def.ClipFiles)
                if (file != null)
                    clips.Add(file);
            if (clips.Count == 0)
                continue;

            Directory.CreateDirectory(def.Directory);
            using (FileStream s = File.Create(Path.Combine(def.Directory, "def.bin")))
                AudioDefCache.Write(s, new OneShotAudioDef(def.Volume, def.MinPitch, def.MaxPitch, clips));
            completed++;
        }
        return completed;
    }

    private static bool PlanClip(StreamPlan plan, StreamPlan.PendingDef pending, SerializedFile file,
        SerializedObject clipObject, long clipId, int definition, int slot)
    {
        Dictionary<string, object> clip = TypeTreeReader.Read(clipObject.TypeTree, file.ReaderFor(clipObject));
        string name = clip.GetValueOrDefault("m_Name") as string ?? $"clip_{clipId:x}";
        if (clip.GetValueOrDefault("m_Resource") is not Dictionary<string, object> res)
            return false;

        long offset = Convert.ToInt64(res["m_Offset"]);
        int size = Convert.ToInt32(res["m_Size"]);
        if (offset < 0 || size <= 0)
            return false;

        // Older bundles omit m_Source because they carry only one audio stream; the empty name means
        // "whichever .resource this bundle has", which the caller resolves against its own node table.
        string source = res.GetValueOrDefault("m_Source") as string ?? "";
        plan.Clips.Add(new StreamPlan.Clip(ResourceName(source), offset, size, definition, slot, name,
            clipId));
        pending.ClipFiles.Add(null);
        return true;
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
        private readonly Dictionary<string, long> _containers = new(StringComparer.Ordinal);

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
                    _containers[(string)pair["first"]] =
                        PathId(((Dictionary<string, object>)pair["second"])["asset"]);
                }
            }
        }

        internal SerializedObject? ById(long id) => _byId.GetValueOrDefault(id);

        internal SerializedObject? Find(string assetPath)
        {
            string suffix = assetPath.Replace('\\', '/').ToLowerInvariant();
            foreach ((string path, long id) in _containers)
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && _byId.TryGetValue(id, out SerializedObject? asset))
                {
                    return asset;
                }

            return null;
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
        int extracted = 0;
        foreach (byte[] bytes in nodes.SerializedFiles)
        {
            if (AppShutdown.IsShuttingDown)
                return extracted; // leaving: stop between files, never mid-definition
            if (Plan(SerializedFile.Read(bytes), request) is not { } plan)
                continue;

            for (int i = 0; i < plan.Clips.Count; i++)
            {
                StreamPlan.Clip clip = plan.Clips[i];
                if (ResourceFor(clip.StreamFile, nodes.Resources) is not { } resource
                    || clip.Offset + clip.Size > resource.Length)
                {
                    continue;
                }

                var blob = new byte[clip.Size];
                Array.Copy(resource, clip.Offset, blob, 0, clip.Size);
                WriteClip(plan, i, blob);
            }

            extracted += CompleteDefs(plan);
        }

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
