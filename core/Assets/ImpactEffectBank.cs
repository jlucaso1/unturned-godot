using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;

namespace UnturnedGodot.Assets;

// One EffectAsset, reduced to the mark it leaves behind.
//
// SDG.Unturned.EffectAsset carries a prefab of particles, a decal, splatters, audio and a lifetime; this
// port draws the DECAL and nothing else, so what survives is which textures the mark can be drawn with and
// where in the bundle they live.
//
// Which textures those are is a convention of the shipped content rather than a field in the .dat, and it
// is a consistent one. A hard-surface impact folder carries a single Texture.png beside a Material_Decal
// (Concrete_WithDecal_NoAudio, Wood_..., Metal_..., Gravel_...). A gore one carries Texture_0..3.png, one
// per splatter prefab, and the game picks between them. A folder with neither — Foliage, Snow, Water — is
// particles only, and leaves no mark in the game either.
public sealed class ImpactEffectAsset
{
    public Guid Guid { get; }

    // The folder the .dat was found in, relative to the content source's Bundles directory, in the form
    // the master bundle keys its container table by. Empty when it could not be expressed that way.
    public string BundleDirectory { get; }

    // Where the mark's textures WOULD be, as container paths into the bundle: the single Texture.png and
    // the gore Texture_0..N.png, all of them, in that order.
    //
    // Candidates rather than an answer, because the folder on disk holds only the .dat — the textures live
    // inside the master bundle, and the cheap way to learn which of these exist is to ask the extractor
    // for all of them and keep what comes back. A folder that carries neither shape hands back nothing and
    // leaves no mark, which is what Foliage, Snow and Water do in the game.
    public IReadOnlyList<string> DecalTextureCandidates { get; }

    public ImpactEffectAsset(Guid guid, string bundleDirectory, IReadOnlyList<string> decalTextures)
    {
        Guid = guid;
        BundleDirectory = bundleDirectory;
        DecalTextureCandidates = decalTextures;
    }
}

// Every effect a surface can name, by GUID.
//
// Scanned from the .dat files under Bundles/Effects rather than from the master bundle: the .dat is what
// carries the GUID a PhysicsMaterialAsset points at, and its folder is what names the textures inside the
// bundle. Reading the bundle to find that out would mean decoding 1.4 GB to learn a dozen paths.
public sealed class ImpactEffectBank
{
    // The single decal a hard-surface impact folder carries.
    public const string SingleDecalFile = "Texture.png";

    // How many Texture_N.png a gore folder is checked for. The shipped ones carry four, matching their
    // four splatter prefabs; the loop stops at the first gap, so a folder with fewer is not a problem.
    public const int MaxGoreTextures = 8;

    private readonly Dictionary<Guid, ImpactEffectAsset> _byGuid = new();

    public int Count => _byGuid.Count;

    public ImpactEffectAsset? Find(Guid guid) =>
        guid != Guid.Empty && _byGuid.TryGetValue(guid, out ImpactEffectAsset? asset) ? asset : null;

    public void Add(ImpactEffectAsset asset) => _byGuid.TryAdd(asset.Guid, asset);

    // Scans one content source's effects. `bundlesDirectory` is the source's Bundles folder and
    // `assetPrefix` its MasterBundle.dat's Asset_Prefix, which together turn a folder on disk into the
    // container path its textures are keyed by inside the bundle.
    public static ImpactEffectBank ScanDirectory(string bundlesDirectory, string assetPrefix)
    {
        var bank = new ImpactEffectBank();
        string effects = Path.Combine(bundlesDirectory, "Effects");
        if (!Directory.Exists(effects))
            return bank;

        try
        {
            foreach (string file in Directory.EnumerateFiles(effects, "*.dat", SearchOption.AllDirectories))
            {
                DatDictionary parsed;
                try { parsed = DatParser.Parse(TextFile.ReadAllText(file)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

                if (TryRead(parsed, file, bundlesDirectory, assetPrefix) is { } asset)
                    bank.Add(asset);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        return bank;
    }

    // Merges several sources, the game's own first. First claimant wins, the same rule the object database
    // and the physics-material bank follow: a workshop item is free to reuse an official GUID, and letting
    // it take the registration would send an official map's impacts through the mod's bundle.
    public static ImpactEffectBank ScanSources(IEnumerable<(string BundlesDirectory, string AssetPrefix)> sources)
    {
        var merged = new ImpactEffectBank();
        foreach ((string directory, string prefix) in sources)
            foreach (ImpactEffectAsset asset in ScanDirectory(directory, prefix)._byGuid.Values)
                merged.Add(asset);
        return merged;
    }

    private static ImpactEffectAsset? TryRead(DatDictionary root, string datPath, string bundlesDirectory,
        string assetPrefix)
    {
        // An effect .dat is flat — GUID and Type at the top level — unlike the Metadata/Asset shape the
        // newer assets use.
        if (!root.TryGetGuid("GUID", out Guid guid))
            return null;
        if (!string.Equals(root.GetString("Type"), "Effect", StringComparison.OrdinalIgnoreCase))
            return null;

        string directory = Path.GetDirectoryName(datPath) ?? string.Empty;
        string bundleDirectory = ContainerDirectory(directory, bundlesDirectory, assetPrefix);
        return new ImpactEffectAsset(guid, bundleDirectory, DecalCandidates(bundleDirectory));
    }

    // "<install>/Bundles/Effects/Impacts/Wood_WithDecal_NoAudio" ->
    // "assets/coremasterbundle/effects/impacts/wood_withdecal_noaudio". The container table is keyed by
    // the lowercase Unity path, and the Unity project mirrors the Bundles folder under the asset prefix.
    public static string ContainerDirectory(string directory, string bundlesDirectory, string assetPrefix)
    {
        if (assetPrefix.Length == 0)
            return string.Empty;
        string relative;
        try { relative = Path.GetRelativePath(bundlesDirectory, directory); }
        catch (ArgumentException) { return string.Empty; }
        if (relative.StartsWith("..", StringComparison.Ordinal) || relative == ".")
            return string.Empty;

        // The prefix comes out of MasterBundle.dat, so it is whatever an author typed: a trailing slash
        // or a Windows separator would produce a container path with a double slash in it, which matches
        // nothing in the bundle's table and silently loses the mark.
        string prefix = assetPrefix.Replace('\\', '/').TrimEnd('/');
        return $"{prefix}/{relative.Replace(Path.DirectorySeparatorChar, '/')}".ToLowerInvariant();
    }

    // Every container path the folder's mark could be at. Both shapes are offered for every effect: only
    // the bundle knows which exist, and asking for a path that is not there costs the extractor nothing.
    private static List<string> DecalCandidates(string bundleDirectory)
    {
        var textures = new List<string>();
        if (bundleDirectory.Length == 0)
            return textures;

        textures.Add($"{bundleDirectory}/{SingleDecalFile.ToLowerInvariant()}");
        for (int i = 0; i < MaxGoreTextures; i++)
            textures.Add($"{bundleDirectory}/texture_{i}.png");
        return textures;
    }
}
