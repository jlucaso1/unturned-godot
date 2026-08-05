using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Pulls textures out of a master bundle by their AssetBundle container path (the lowercase Unity path,
// e.g. "assets/coremasterbundle/terrain/materials/pei/grass_00.png").
//
// Three costs, in order: reading the SerializedFile alone covers textures whose pixels are stored inline
// (all of the game's terrain layers); ExtractStreamed decodes forward through the bundle only as far as
// the last texture it still owes; ExtractAll decodes the whole blob into memory and is the fallback for
// bundles that are not the single-LZMA-block shape the streaming reader understands.
public static class BundleTextures
{
    // Inline textures only: the caller already holds the decoded SerializedFile.
    public static Dictionary<string, CachedTexture> ExtractInline(SerializedFile file,
        IReadOnlyCollection<string> containerPaths)
    {
        var result = new Dictionary<string, CachedTexture>();
        foreach ((string path, UnityTexture texture) in Locate(file, containerPaths))
            if (texture.StreamPath.Length == 0 && texture.InlineData.Length > 0)
                result[path] = CachedTexture.From(texture, texture.InlineData);

        return result;
    }

    // One forward pass over the bundle: the SerializedFile names the wanted textures, then the .resS nodes
    // are read only as far as the last one that is still owed. A texture kept inline costs nothing beyond
    // the SerializedFile, and a bundle whose textures are all inline never touches its stream at all.
    public static Dictionary<string, CachedTexture> ExtractStreamed(string bundlePath,
        IReadOnlyCollection<string> containerPaths)
    {
        var result = new Dictionary<string, CachedTexture>();
        if (containerPaths.Count == 0)
            return result;

        using MasterBundleStream? stream = MasterBundleStream.OpenFile(bundlePath);
        if (stream == null)
            return ExtractAll(bundlePath, containerPaths); // not a single LZMA block: decode the whole blob

        var ordered = new List<MasterBundleStream.Node>(stream.Nodes);
        ordered.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        // What each stream file still owes, once the SerializedFile has been read.
        var pending = new Dictionary<string, List<(string Path, UnityTexture Texture)>>(StringComparer.Ordinal);
        int owed = 0;

        foreach (MasterBundleStream.Node node in ordered)
        {
            if (!IsStreamNode(node.Path))
            {
                foreach ((string path, UnityTexture texture) in
                    Locate(SerializedFile.Read(stream.Read((int)node.Size)), containerPaths))
                {
                    if (texture.StreamPath.Length == 0)
                    {
                        if (texture.InlineData.Length > 0)
                            result[path] = CachedTexture.From(texture, texture.InlineData);
                        continue;
                    }

                    if (!pending.TryGetValue(texture.StreamFileName,
                        out List<(string, UnityTexture)>? list))
                    {
                        pending[texture.StreamFileName] = list = new List<(string, UnityTexture)>();
                    }

                    list.Add((path, texture));
                    owed++;
                }
            }
            else if (pending.TryGetValue(LastSegment(node.Path), out List<(string Path, UnityTexture Texture)>? wanted))
            {
                var regions = new List<ForwardRegions.Region>(wanted.Count);
                for (int i = 0; i < wanted.Count; i++)
                    regions.Add(new ForwardRegions.Region(wanted[i].Texture.StreamOffset,
                        wanted[i].Texture.StreamSize, i));

                ForwardRegions.Read(stream.Read, node.Size, regions, (index, pixels) =>
                {
                    result[wanted[index].Path] = CachedTexture.From(wanted[index].Texture, pixels);
                    owed--;
                });
            }

            if (owed == 0 && result.Count >= containerPaths.Count)
                break; // everything asked for is in hand: stop decoding here
        }

        return result;
    }

    // Everything, including .resS-backed pixels, at the cost of decoding the full bundle.
    public static Dictionary<string, CachedTexture> ExtractAll(string bundlePath,
        IReadOnlyCollection<string> containerPaths)
    {
        var result = new Dictionary<string, CachedTexture>();
        if (containerPaths.Count == 0)
            return result;

        UnityBundle bundle = UnityBundle.Read(File.ReadAllBytes(bundlePath));
        foreach (KeyValuePair<string, byte[]> file in bundle.Files)
        {
            if (IsStreamNode(file.Key))
                continue;

            foreach ((string path, UnityTexture texture) in Locate(SerializedFile.Read(file.Value), containerPaths))
            {
                if (result.ContainsKey(path))
                    continue;
                byte[]? pixels = texture.GetPixels(name => ResolveStream(bundle.Files, name));
                if (pixels is { Length: > 0 })
                    result[path] = CachedTexture.From(texture, pixels);
            }
        }

        return result;
    }

    // The Texture2D behind each wanted container path, read from the AssetBundle object that names every
    // asset in the file. Pixels are not touched here: where they live decides what the caller pays.
    internal static IEnumerable<(string Path, UnityTexture Texture)> Locate(SerializedFile file,
        IReadOnlyCollection<string> containerPaths)
    {
        var wanted = new HashSet<string>(containerPaths);
        var byId = new Dictionary<long, SerializedObject>();
        foreach (SerializedObject o in file.Objects)
            byId[o.PathId] = o;

        var seen = new HashSet<string>();
        foreach (SerializedObject o in file.Objects)
        {
            if (o.ClassId != AssetBundleClassId)
                continue;

            Dictionary<string, object> bundle = TypeTreeReader.Read(o.TypeTree, file.ReaderFor(o));
            foreach (object entry in (List<object>)bundle["m_Container"])
            {
                var pair = (Dictionary<string, object>)entry;
                string path = (string)pair["first"];
                if (!wanted.Contains(path) || !seen.Add(path))
                    continue;

                var info = (Dictionary<string, object>)pair["second"];
                long assetId = Convert.ToInt64(((Dictionary<string, object>)info["asset"])["m_PathID"]);
                if (!byId.TryGetValue(assetId, out SerializedObject? texObj)
                    || texObj.ClassId != Texture2DClassId)
                {
                    continue;
                }

                yield return (path, UnityTexture.Read(TypeTreeReader.Read(texObj.TypeTree,
                    file.ReaderFor(texObj))));
            }
        }
    }

    private const int Texture2DClassId = 28;
    private const int AssetBundleClassId = 142; // the container that names every asset

    private static bool IsStreamNode(string path) =>
        path.EndsWith(".resS", StringComparison.Ordinal)
        || path.EndsWith(".resource", StringComparison.Ordinal);

    private static string LastSegment(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    // A texture names its stream file by path; inside a bundle that entry sits next to the SerializedFile,
    // so match on the file name alone.
    private static byte[]? ResolveStream(IReadOnlyDictionary<string, byte[]> files, string streamPath)
    {
        if (files.TryGetValue(streamPath, out byte[]? exact))
            return exact;

        string name = Path.GetFileName(streamPath);
        foreach (KeyValuePair<string, byte[]> file in files)
            if (Path.GetFileName(file.Key).Equals(name, StringComparison.OrdinalIgnoreCase))
                return file.Value;

        return null;
    }
}
