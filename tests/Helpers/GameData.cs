using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;

namespace UnturnedGodot.Tests.Helpers;

// Locates the real game content for the end-to-end tests. Every one of them self-skips when the
// lookup returns null, so the suite is fully green on a machine without Unturned installed (CI) and
// gains the extra real-data assertions on a machine that has it.
public static class GameData
{
    // The Unturned install (UNTURNED_PATH, else the Steam libraries for this OS), or null.
    public static string? Install { get; } = UnturnedInstall.Find();

    // <install>/Maps/<name>, or null when the game or that map is missing.
    public static string? Map(string name)
    {
        if (Install == null)
            return null;
        string path = Path.Combine(Install, "Maps", name);
        return Directory.Exists(path) ? path : null;
    }

    // The platform's core masterbundle inside the install, or null.
    public static string? MasterBundle =>
        Install == null ? null : UnturnedInstall.FindMasterBundle(Install);

    // The core masterbundle's prefab graph. Reading it costs a ~170 MB LZMA decode, so it is done once for
    // the whole run and shared by every test that asks — the same thing ModelExtractor does per load.
    public static PrefabGraph Prefabs => PrefabsLazy.Value;

    private static readonly Lazy<PrefabGraph> PrefabsLazy = new(ReadPrefabs);

    private static PrefabGraph ReadPrefabs()
    {
        byte[] raw = File.ReadAllBytes(MasterBundle!);
        UnityBundle bundle = UnityBundle.Read(raw, SerializedFileCap(raw));
        foreach (KeyValuePair<string, byte[]> file in bundle.Files)
            if (!IsStream(file.Key))
                return PrefabGraph.Read(SerializedFile.Read(file.Value));
        throw new InvalidDataException($"{MasterBundle} carries no SerializedFile.");
    }

    // Stop the decode at the end of the SerializedFile: the .resS pixel stream after it is several times
    // larger and holds nothing the prefab graph reads.
    private static long SerializedFileCap(byte[] bundle)
    {
        using MasterBundleStream? stream = MasterBundleStream.Open(bundle);
        if (stream == null)
            return long.MaxValue;

        long end = 0;
        foreach (MasterBundleStream.Node node in stream.Nodes)
            if (!IsStream(node.Path))
                end = Math.Max(end, node.Offset + node.Size);
        return end > 0 ? end : long.MaxValue;
    }

    private static bool IsStream(string path) =>
        path.EndsWith(".resS", StringComparison.Ordinal)
        || path.EndsWith(".resource", StringComparison.Ordinal);
}
