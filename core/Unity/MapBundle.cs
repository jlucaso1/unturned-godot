using System;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot.Unity;

// One of the small bundles a map ships beside its own files: Environment/Roads.unity3d,
// Terrain/Materials.unity3d, Terrain/Details.unity3d, Charts.unity3d.
//
// Two container formats are in the wild and the maps mix them freely — the legacy uncompressed UnityRaw
// (PEI, Alpha Valley, Russia) and UnityFS (Germany's roads, Washington's and Yukon's terrain layers, most
// workshop maps) — and inside them a texture's pixels either sit inline or in a .resS entry beside the
// SerializedFile. Both differences are settled here so the callers only see "the objects, and their
// pixels".
public sealed class MapBundle
{
    private readonly IReadOnlyDictionary<string, byte[]> _files;

    public SerializedFile File { get; }

    private MapBundle(IReadOnlyDictionary<string, byte[]> files, SerializedFile file)
    {
        _files = files;
        File = file;
    }

    public IReadOnlyList<SerializedObject> Objects => File.Objects;

    // Null when the bundle holds no SerializedFile (nothing but stream entries). Throws the way the
    // underlying readers do when the container or the SerializedFile is one this build cannot decode;
    // callers treat that as "this map keeps its flat fallback material".
    public static MapBundle? Read(byte[] data)
    {
        IReadOnlyDictionary<string, byte[]> files = UnityRawBundle.IsRaw(data)
            ? UnityRawBundle.Read(data).Files
            : UnityBundle.Read(data).Files;

        byte[]? serialized = null;
        foreach (KeyValuePair<string, byte[]> f in files)
            if (!IsStream(f.Key))
                serialized = f.Value;

        return serialized == null ? null : new MapBundle(files, SerializedFile.Read(serialized));
    }

    public static MapBundle? ReadFile(string path) =>
        System.IO.File.Exists(path) ? Read(System.IO.File.ReadAllBytes(path)) : null;

    // The Texture2D at this object, with its pixels resolved. False for anything that is not a texture or
    // whose pixels are missing — a texture Unturned never fills in is not an error, it just has nothing to
    // draw with.
    public bool TryReadTexture(SerializedObject obj, out UnityTexture texture, out byte[] pixels)
    {
        texture = null!;
        pixels = Array.Empty<byte>();
        if (obj.ClassId != 28) // Texture2D
            return false;

        texture = UnityTexture.Read(TypeTreeReader.Read(obj.TypeTree, File.ReaderFor(obj)));
        byte[]? resolved = texture.GetPixels(ResolveStream);
        if (resolved == null || resolved.Length == 0)
            return false;

        pixels = resolved;
        return true;
    }

    // A UnityFS texture names its stream by archive path ("archive:/CAB-x/CAB-x.resS"); inside a map
    // bundle that entry sits right next to the SerializedFile, so the file name alone identifies it.
    private byte[]? ResolveStream(string streamPath)
    {
        if (_files.TryGetValue(streamPath, out byte[]? exact))
            return exact;

        string name = Path.GetFileName(streamPath);
        foreach (KeyValuePair<string, byte[]> f in _files)
            if (Path.GetFileName(f.Key).Equals(name, StringComparison.OrdinalIgnoreCase))
                return f.Value;

        return null;
    }

    private static bool IsStream(string name) =>
        name.EndsWith(".resS", StringComparison.Ordinal) || name.EndsWith(".resource", StringComparison.Ordinal);
}
