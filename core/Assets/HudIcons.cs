using System.Collections.Generic;

namespace UnturnedGodot;

// The crosshair and hitmarker icons, out of the game's own core bundle.
//
// StaticIconRef<Texture2D>("UI/Player/Icons/PlayerLife", "Hit_Entity") in the original, which resolves
// through an IconsBundle to exactly these files. They are drawn rather than generated because they ARE
// the game's crosshair: a dot and a wedge someone drew, and any stand-in would read as a different game.
//
// Naming them is string work over a bundle's container table, so it sits here; HudIconSet, which turns
// the extracted bytes into a Texture2D the HUD can draw, is the half that needs the engine and stays in
// src/UI. The split matters because the extraction PLAN is built well before any of that — the object
// streamer asks for these paths during its cold-load pass, from a worker with no HUD anywhere near it.
public static class HudIcons
{
    // Where the icons sit inside the core master bundle, as container paths (lowercase, as the container
    // table keys them).
    public const string Directory = "ui/player/icons/playerlife";

    public const string Dot = "dot.png";
    public const string HitEntity = "hit_entity.png";
    public const string HitBuild = "hit_build.png";
    public const string HitGhost = "hit_ghost.png";

    public static IReadOnlyList<string> Files { get; } = new[] { Dot, HitEntity, HitBuild, HitGhost };

    // The container path of one icon, under the bundle's own asset prefix.
    public static string ContainerPath(string assetPrefix, string file) =>
        $"{assetPrefix.ToLowerInvariant()}/{Directory}/{file}";

    public static List<string> ContainerPaths(string assetPrefix)
    {
        var paths = new List<string>(Files.Count);
        foreach (string file in Files)
            paths.Add(ContainerPath(assetPrefix, file));
        return paths;
    }

    // The extraction request for a bundle's icons. Shares ImpactDecalExtractor because the work is
    // identical — named textures out of a bundle, into a .tex cache under a key both ends agree on.
    public static ImpactDecalExtractor.Request RequestFor(string bundlePath, string bundleTag,
        string assetPrefix, string cacheDirectory) =>
        new(bundlePath, bundleTag, ContainerPaths(assetPrefix), cacheDirectory);
}
