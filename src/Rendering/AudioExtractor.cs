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

    public static string DefNameOf(string assetPath)
    {
        string file = assetPath.Replace('\\', '/');
        int slash = file.LastIndexOf('/');
        if (slash >= 0)
            file = file[(slash + 1)..];
        return file.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) ? file[..^6] : file;
    }

    // Streams the bundle forward, keeping only the SerializedFile and the audio .resource node and
    // DISCARDING the ~1.2 GB texture .resS in chunks: audio never reads it, and materializing every
    // node (the old UnityBundle.Read path) held the whole decompressed bundle in memory at once —
    // multi-GB RSS spikes on the one-time audio extraction. Falls back to the full decode only for
    // bundles that are not the expected single-LZMA-block shape.
    private static (byte[] SerializedFile, byte[] Resource) ReadAudioNodes(string bundlePath)
    {
        byte[] raw = File.ReadAllBytes(bundlePath);
        using MasterBundleStream? stream = MasterBundleStream.Open(raw);
        if (stream == null)
        {
            UnityBundle bundle = UnityBundle.Read(raw);
            byte[] sfFull = Array.Empty<byte>();
            byte[] resourceFull = Array.Empty<byte>();
            foreach (KeyValuePair<string, byte[]> f in bundle.Files)
            {
                if (f.Key.EndsWith(".resource"))
                    resourceFull = f.Value;
                else if (!f.Key.EndsWith(".resS"))
                    sfFull = f.Value;
            }
            return (sfFull, resourceFull);
        }

        var ordered = new List<MasterBundleStream.Node>(stream.Nodes);
        ordered.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        byte[] sf = Array.Empty<byte>();
        byte[] resource = Array.Empty<byte>();
        byte[]? discard = null;
        foreach (MasterBundleStream.Node node in ordered)
        {
            if (node.Path.EndsWith(".resource"))
            {
                resource = stream.Read((int)node.Size);
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
                sf = stream.Read((int)node.Size);
            }
        }
        return (sf, resource);
    }

    // A synthetic definition built from RAW AudioClips (assets the game plays directly, without a
    // OneShotAudioDefinition — e.g. ZombieManager's roar/groan arrays): the clip container paths
    // plus the caller-supplied volume/pitch envelope, cached under the group name like any def.
    public sealed record RawClipGroup(
        string Name, IReadOnlyList<string> ClipPaths, float Volume, float MinPitch, float MaxPitch);

    // Extracts every definition in defAssetPaths (masterbundle-relative, e.g.
    // "Effects/Physics/Footstep/Grass_Walk/Footstep_Grass_Walk.asset") plus every raw clip group
    // that is not cached yet.
    public static int Extract(string bundlePath, IReadOnlyCollection<string> defAssetPaths, string audioCacheDir,
        IReadOnlyCollection<RawClipGroup>? clipGroups = null)
    {
        var missing = new List<string>();
        foreach (string p in defAssetPaths)
            if (!IsCached(audioCacheDir, DefNameOf(p)))
                missing.Add(p);
        var missingGroups = new List<RawClipGroup>();
        if (clipGroups != null)
            foreach (RawClipGroup g in clipGroups)
                if (!IsCached(audioCacheDir, g.Name))
                    missingGroups.Add(g);
        if (missing.Count == 0 && missingGroups.Count == 0)
            return 0;

        GD.Print($"[audio] extracting {missing.Count} audio definitions and {missingGroups.Count} " +
            "clip groups from masterbundle (one-time)...");
        (byte[] sf, byte[] resource) = ReadAudioNodes(bundlePath);
        SerializedFile file = SerializedFile.Read(sf);

        var byId = new Dictionary<long, SerializedObject>();
        foreach (SerializedObject o in file.Objects)
            byId[o.PathId] = o;

        // Container catalog: path -> asset path id (same walk PrefabGraph does, kept local to this pass).
        var containers = new Dictionary<string, long>();
        string assetPrefix = string.Empty;
        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != 142) // AssetBundle
                continue;
            Dictionary<string, object> ab = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
            foreach (object entry in (List<object>)ab["m_Container"])
            {
                var pair = (Dictionary<string, object>)entry;
                var path = (string)pair["first"];
                containers[path] = PathId(((Dictionary<string, object>)pair["second"])["asset"]);
                int idx = path.IndexOf("effects/", StringComparison.Ordinal);
                if (assetPrefix.Length == 0 && idx > 0)
                    assetPrefix = path[..idx];
            }
        }

        int extracted = 0;
        foreach (string assetPath in missing)
        {
            string key = assetPrefix + assetPath.Replace('\\', '/').ToLowerInvariant();
            if (!containers.TryGetValue(key, out long defId) ||
                !byId.TryGetValue(defId, out SerializedObject? defObj))
            {
                GD.PushWarning($"[audio] def not found in bundle: {assetPath}");
                continue;
            }

            Dictionary<string, object> def = TypeTreeReader.Read(defObj.TypeTree, file.ReaderFor(defObj));
            float volumeMultiplier = Convert.ToSingle(def.GetValueOrDefault("volumeMultiplier", 1f));
            float minPitch = Convert.ToSingle(def.GetValueOrDefault("minPitch", 1f));
            float maxPitch = Convert.ToSingle(def.GetValueOrDefault("maxPitch", 1f));

            string defName = DefNameOf(assetPath);
            string defDir = Path.Combine(audioCacheDir, defName);
            Directory.CreateDirectory(defDir);

            var clipFiles = new List<string>();
            if (def.TryGetValue("clips", out object? clips))
            {
                foreach (object c in (List<object>)clips)
                {
                    long clipId = PathId(c);
                    if (!byId.TryGetValue(clipId, out SerializedObject? clipObj))
                        continue;
                    Dictionary<string, object> clip = TypeTreeReader.Read(clipObj.TypeTree, file.ReaderFor(clipObj));
                    string name = clip.GetValueOrDefault("m_Name") as string ?? $"clip_{clipId:x}";
                    if (clip.GetValueOrDefault("m_Resource") is not Dictionary<string, object> res)
                        continue;
                    long offset = Convert.ToInt64(res["m_Offset"]);
                    int size = Convert.ToInt32(res["m_Size"]);
                    if (offset < 0 || size <= 0 || offset + size > resource.Length)
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
                GD.PushWarning($"[audio] no clips rebuilt for {defName}");
                continue;
            }

            using (FileStream s = File.Create(Path.Combine(defDir, "def.bin")))
                AudioDefCache.Write(s, new OneShotAudioDef(volumeMultiplier, minPitch, maxPitch, clipFiles));
            extracted++;
        }

        foreach (RawClipGroup group in missingGroups)
        {
            string groupDir = Path.Combine(audioCacheDir, group.Name);
            Directory.CreateDirectory(groupDir);
            var clipFiles = new List<string>();
            foreach (string clipPath in group.ClipPaths)
            {
                string key = assetPrefix + clipPath.Replace('\\', '/').ToLowerInvariant();
                if (!containers.TryGetValue(key, out long clipId)
                    || !byId.TryGetValue(clipId, out SerializedObject? clipObj))
                {
                    GD.PushWarning($"[audio] clip not found in bundle: {clipPath}");
                    continue;
                }
                Dictionary<string, object> clip = TypeTreeReader.Read(clipObj.TypeTree, file.ReaderFor(clipObj));
                string name = clip.GetValueOrDefault("m_Name") as string ?? $"clip_{clipId:x}";
                if (clip.GetValueOrDefault("m_Resource") is not Dictionary<string, object> res)
                    continue;
                long offset = Convert.ToInt64(res["m_Offset"]);
                int size = Convert.ToInt32(res["m_Size"]);
                if (offset < 0 || size <= 0 || offset + size > resource.Length)
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
                GD.PushWarning($"[audio] no clips rebuilt for group {group.Name}");
                continue;
            }
            using (FileStream s = File.Create(Path.Combine(groupDir, "def.bin")))
                AudioDefCache.Write(s,
                    new OneShotAudioDef(group.Volume, group.MinPitch, group.MaxPitch, clipFiles));
            extracted++;
        }

        GD.Print($"[audio] extracted {extracted} definitions to {audioCacheDir}");
        return extracted;
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
            GD.PushWarning($"[audio] failed to rebuild '{name}': {e.Message}");
            return null;
        }
    }

    private static long PathId(object pptr) => Convert.ToInt64(((Dictionary<string, object>)pptr)["m_PathID"]);
}
