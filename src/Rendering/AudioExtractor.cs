using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// One-time extraction of the movement audio: for each OneShotAudioDefinition the physics materials
// reference (FootstepWalk/FootstepRun/BipedLand), read its MonoBehaviour (volume/pitch + AudioClip list),
// slice each clip's FSB5 blob out of the masterbundle's .resource stream and rebuild it as a standard .ogg
// (Fmod5Sharp — Unturned's clips are FSB5/Vorbis), cached per definition under audioCacheDir.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class AudioExtractor
{
    // Cache layout: <audioCacheDir>/<DefName>/def.bin + <clip>.ogg. A def.bin marks the def as complete.
    public static bool IsCached(string audioCacheDir, string defName) =>
        File.Exists(Path.Combine(audioCacheDir, defName, "def.bin"));

    // The cache key for a definition: its file name, prefixed by the bundle that carries it. Two bundles
    // can name a definition the same thing — a workshop item mirroring the game's folders is enough — and
    // on the bare name their entries were the same directory, so one skipped extraction because the other
    // had already written it, and the wrong footstep played.
    public static string DefKey(string bundleTag, string assetPath) =>
        bundleTag.Length == 0 ? DefNameOf(assetPath) : bundleTag + "_" + DefNameOf(assetPath);

    public static string DefNameOf(string assetPath)
    {
        string file = assetPath.Replace('\\', '/');
        int slash = file.LastIndexOf('/');
        if (slash >= 0)
            file = file[(slash + 1)..];
        return file.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) ? file[..^6] : file;
    }

    // Streams the bundle forward, keeping every SerializedFile and audio .resource node and
    // DISCARDING the ~1.2 GB texture .resS in chunks: audio never reads it, and materializing every
    // node (the old UnityBundle.Read path) held the whole decompressed bundle in memory at once —
    // multi-GB RSS spikes on the one-time audio extraction. Falls back to the full decode only for
    // bundles that are not the expected single-LZMA-block shape.
    private sealed record AudioNodes(List<byte[]> SerializedFiles, Dictionary<string, byte[]> Resources);

    private static AudioNodes ReadAudioNodes(string bundlePath)
    {
        byte[] raw = File.ReadAllBytes(bundlePath);
        using MasterBundleStream? stream = MasterBundleStream.Open(raw);
        if (stream == null)
        {
            UnityBundle bundle = UnityBundle.Read(raw);
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

    private static string ResourceName(string path)
    {
        string normalized = path.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private sealed record AudioFile(SerializedFile File, Dictionary<long, SerializedObject> ById,
        Dictionary<string, long> Containers);

    private sealed record AudioAsset(AudioFile File, SerializedObject Object);

    // A synthetic definition built from RAW AudioClips (assets the game plays directly, without a
    // OneShotAudioDefinition — e.g. ZombieManager's roar/groan arrays): the clip container paths
    // plus the caller-supplied volume/pitch envelope, cached under the group name like any def.
    public sealed record RawClipGroup(
        string Name, IReadOnlyList<string> ClipPaths, float Volume, float MinPitch, float MaxPitch);

    // Extracts every definition in defAssetPaths (masterbundle-relative, e.g.
    // "Effects/Physics/Footstep/Grass_Walk/Footstep_Grass_Walk.asset") plus every raw clip group
    // that is not cached yet.
    public static int Extract(string bundlePath, string bundleTag,
        IReadOnlyCollection<string> defAssetPaths, string audioCacheDir,
        IReadOnlyCollection<RawClipGroup>? clipGroups = null)
    {
        var missing = new List<string>();
        foreach (string p in defAssetPaths)
            if (!IsCached(audioCacheDir, DefKey(bundleTag, p)))
                missing.Add(p);
        var missingGroups = new List<RawClipGroup>();
        if (clipGroups != null)
            foreach (RawClipGroup g in clipGroups)
                if (!IsCached(audioCacheDir, g.Name))
                    missingGroups.Add(g);
        if (missing.Count == 0 && missingGroups.Count == 0)
            return 0;

        AppShutdown.PrintUnlessQuitting($"[audio] extracting {missing.Count} audio definitions and {missingGroups.Count} " +
            "clip groups from masterbundle (one-time)...");
        AudioNodes nodes = ReadAudioNodes(bundlePath);
        var files = new List<AudioFile>(nodes.SerializedFiles.Count);
        foreach (byte[] bytes in nodes.SerializedFiles)
        {
            SerializedFile file = SerializedFile.Read(bytes);
            var byId = new Dictionary<long, SerializedObject>();
            foreach (SerializedObject o in file.Objects)
                byId[o.PathId] = o;

            // Path ids are local to one SerializedFile. Keep each catalog attached to its own object map
            // so colliding ids in a multi-file workshop bundle never resolve into a different file.
            var containers = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (SerializedObject o in file.Objects)
            {
                if (o.ClassId != 142) // AssetBundle
                    continue;
                Dictionary<string, object> ab = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
                foreach (object entry in (List<object>)ab["m_Container"])
                {
                    var pair = (Dictionary<string, object>)entry;
                    containers[(string)pair["first"]] =
                        PathId(((Dictionary<string, object>)pair["second"])["asset"]);
                }
            }

            files.Add(new AudioFile(file, byId, containers));
        }

        int extracted = 0;
        foreach (string assetPath in missing)
        {
            if (FindAsset(files, assetPath) is not { } defAsset)
            {
                AppShutdown.WarnUnlessQuitting($"[audio] def not found in bundle: {assetPath}");
                continue;
            }

            Dictionary<string, object> def = TypeTreeReader.Read(defAsset.Object.TypeTree,
                defAsset.File.File.ReaderFor(defAsset.Object));
            float volumeMultiplier = Convert.ToSingle(def.GetValueOrDefault("volumeMultiplier", 1f));
            float minPitch = Convert.ToSingle(def.GetValueOrDefault("minPitch", 1f));
            float maxPitch = Convert.ToSingle(def.GetValueOrDefault("maxPitch", 1f));

            string defName = DefKey(bundleTag, assetPath);
            string defDir = Path.Combine(audioCacheDir, defName);
            Directory.CreateDirectory(defDir);

            var clipFiles = new List<string>();
            if (def.TryGetValue("clips", out object? clips))
            {
                foreach (object c in (List<object>)clips)
                {
                    long clipId = PathId(c);
                    if (!defAsset.File.ById.TryGetValue(clipId, out SerializedObject? clipObj))
                        continue;
                    Dictionary<string, object> clip = TypeTreeReader.Read(clipObj.TypeTree,
                        defAsset.File.File.ReaderFor(clipObj));
                    string name = clip.GetValueOrDefault("m_Name") as string ?? $"clip_{clipId:x}";
                    if (clip.GetValueOrDefault("m_Resource") is not Dictionary<string, object> res)
                        continue;
                    long offset = Convert.ToInt64(res["m_Offset"]);
                    int size = Convert.ToInt32(res["m_Size"]);
                    if (ResourceFor(res, nodes.Resources) is not { } resource
                        || offset < 0 || size <= 0 || offset + size > resource.Length)
                        continue;

                    byte[]? ogg = RebuildOgg(resource, offset, size, name);
                    if (ogg == null)
                        continue;
                    string fileName = name + ".ogg";
                    File.WriteAllBytes(Path.Combine(defDir, fileName), ogg);
                    clipFiles.Add(fileName);
                }
            }

            if (clipFiles.Count == 0)
            {
                AppShutdown.WarnUnlessQuitting($"[audio] no clips rebuilt for {defName}");
                continue;
            }

            using (FileStream s = File.Create(Path.Combine(defDir, "def.bin")))
                AudioDefCache.Write(s, new OneShotAudioDef(volumeMultiplier, minPitch, maxPitch, clipFiles));
            extracted++;
        }

        foreach (RawClipGroup group in missingGroups)
        {
            if (AppShutdown.IsShuttingDown)
                return extracted; // leaving: stop between groups, never mid-file
            string groupDir = Path.Combine(audioCacheDir, group.Name);
            Directory.CreateDirectory(groupDir);
            var clipFiles = new List<string>();
            foreach (string clipPath in group.ClipPaths)
            {
                if (FindAsset(files, clipPath) is not { } clipAsset)
                {
                    AppShutdown.WarnUnlessQuitting($"[audio] clip not found in bundle: {clipPath}");
                    continue;
                }
                long clipId = clipAsset.Object.PathId;
                Dictionary<string, object> clip = TypeTreeReader.Read(clipAsset.Object.TypeTree,
                    clipAsset.File.File.ReaderFor(clipAsset.Object));
                string name = clip.GetValueOrDefault("m_Name") as string ?? $"clip_{clipId:x}";
                if (clip.GetValueOrDefault("m_Resource") is not Dictionary<string, object> res)
                    continue;
                long offset = Convert.ToInt64(res["m_Offset"]);
                int size = Convert.ToInt32(res["m_Size"]);
                if (ResourceFor(res, nodes.Resources) is not { } resource
                    || offset < 0 || size <= 0 || offset + size > resource.Length)
                    continue;
                byte[]? ogg = RebuildOgg(resource, offset, size, name);
                if (ogg == null)
                    continue;
                string fileName = name + ".ogg";
                File.WriteAllBytes(Path.Combine(groupDir, fileName), ogg);
                clipFiles.Add(fileName);
            }
            if (clipFiles.Count == 0)
            {
                AppShutdown.WarnUnlessQuitting($"[audio] no clips rebuilt for group {group.Name}");
                continue;
            }
            using (FileStream s = File.Create(Path.Combine(groupDir, "def.bin")))
                AudioDefCache.Write(s,
                    new OneShotAudioDef(group.Volume, group.MinPitch, group.MaxPitch, clipFiles));
            extracted++;
        }

        AppShutdown.PrintUnlessQuitting($"[audio] extracted {extracted} definitions to {audioCacheDir}");
        return extracted;
    }

    private static AudioAsset? FindAsset(IReadOnlyList<AudioFile> files, string assetPath)
    {
        string suffix = assetPath.Replace('\\', '/').ToLowerInvariant();
        foreach (AudioFile file in files)
            foreach ((string path, long id) in file.Containers)
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && file.ById.TryGetValue(id, out SerializedObject? asset))
                    return new AudioAsset(file, asset);
        return null;
    }

    private static byte[]? ResourceFor(IReadOnlyDictionary<string, object> resourceRef,
        IReadOnlyDictionary<string, byte[]> resources)
    {
        if (resourceRef.GetValueOrDefault("m_Source") is string source
            && resources.TryGetValue(ResourceName(source), out byte[]? resource))
            return resource;

        // Older bundles omit m_Source because they carry only one audio stream.
        if (resources.Count == 1)
            foreach (byte[] only in resources.Values)
                return only;
        return null;
    }

    private static byte[]? RebuildOgg(byte[] resource, long offset, int size, string name)
    {
        try
        {
            var blob = new byte[size];
            Array.Copy(resource, offset, blob, 0, size);
            Fmod5Sharp.FmodTypes.FmodSoundBank bank = Fmod5Sharp.FsbLoader.LoadFsbFromByteArray(blob);
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
