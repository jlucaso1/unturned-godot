using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnturnedGodot.Data;

namespace UnturnedGodot.Assets;

// Where the game content lives on this machine. Nothing here ships with the project: every path
// points into the user's own Steam copy of Unturned, on whichever OS they run.
//
// The lookup order is UNTURNED_PATH first (an explicit override always wins), then the Steam
// libraries: the default one for the platform plus any extra drives registered in
// steamapps/libraryfolders.vdf, which is how Steam records installs on a second disk.
//
// The OS-dependent members (CurrentPlatform, DefaultSteamRoots, Find) are excluded from coverage:
// their branches select on the running OS, so no single test run can reach all of them. The logic
// they select *between* is pure and fully covered.
public static class UnturnedInstall
{
    public const string PathEnvironmentVariable = "UNTURNED_PATH";

    // Steam's own name for the game directory, identical on every platform.
    private const string GameFolderName = "Unturned";

    public enum Platform
    {
        Windows,
        Linux,
        Mac,
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // selects on the running OS
    public static Platform CurrentPlatform =>
        OperatingSystem.IsWindows() ? Platform.Windows
        : OperatingSystem.IsMacOS() ? Platform.Mac
        : Platform.Linux;

    // The install to use, or null when the game is not installed (or is somewhere unusual, in which
    // case UNTURNED_PATH is the escape hatch).
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // reads the real environment
    public static string? Find()
    {
        string? overridePath = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (!string.IsNullOrEmpty(overridePath))
            return Directory.Exists(overridePath) ? overridePath : null;

        return FindInstall(DefaultSteamRoots());
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // reads the real environment
    public static IReadOnlyList<string> DefaultSteamRoots() => DefaultSteamRoots(
        CurrentPlatform,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    // Default Steam installation roots for a platform, most likely first. Callers pass the folders
    // in so this stays a pure function; empty entries (a SpecialFolder that does not resolve off
    // Windows) are dropped.
    public static IReadOnlyList<string> DefaultSteamRoots(Platform platform, string home,
        string programFilesX86, string programFiles)
    {
        var roots = new List<string>();
        switch (platform)
        {
            case Platform.Windows:
                Add(programFilesX86, "Steam");
                Add(programFiles, "Steam");
                break;
            case Platform.Mac:
                Add(home, "Library", "Application Support", "Steam");
                break;
            default:
                Add(home, ".local", "share", "Steam");
                Add(home, ".steam", "steam");
                Add(home, ".steam", "root");
                // Flatpak keeps its own sandboxed Steam data directory.
                Add(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
                break;
        }

        return roots;

        void Add(string baseFolder, params string[] parts)
        {
            // An unresolved SpecialFolder comes back as "", and Path.Combine("", "Steam") would yield
            // a bare relative "Steam", which must never be probed.
            if (string.IsNullOrEmpty(baseFolder))
                return;

            var segments = new string[parts.Length + 1];
            segments[0] = baseFolder;
            parts.CopyTo(segments, 1);

            string path = Path.Combine(segments);
            if (!roots.Contains(path))
                roots.Add(path);
        }
    }

    // First existing <root>/steamapps/common/Unturned across the given Steam roots and every extra
    // library folder they register, or null when none of them has the game.
    public static string? FindInstall(IEnumerable<string> steamRoots)
    {
        ArgumentNullException.ThrowIfNull(steamRoots);

        foreach (string library in EnumerateLibraries(steamRoots))
        {
            string candidate = Path.Combine(library, "steamapps", "common", GameFolderName);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    // Every Steam library to probe: the roots themselves, then the drives listed in each root's
    // steamapps/libraryfolders.vdf (Steam's registry of installs on other disks). Duplicates are
    // dropped, since libraryfolders.vdf normally lists the root it lives in as well.
    public static IReadOnlyList<string> EnumerateLibraries(IEnumerable<string> steamRoots)
    {
        ArgumentNullException.ThrowIfNull(steamRoots);

        var libraries = new List<string>();
        foreach (string root in steamRoots)
        {
            if (string.IsNullOrEmpty(root))
                continue;
            if (!libraries.Contains(root))
                libraries.Add(root);

            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            string? text = TryReadAllText(vdf);
            if (text == null)
                continue;

            foreach (string extra in ParseLibraryFolders(text))
                if (!libraries.Contains(extra))
                    libraries.Add(extra);
        }

        return libraries;
    }

    // Library paths out of a libraryfolders.vdf. Two shapes exist in the wild: the current one, where
    // each numbered block carries a "path" key, and the pre-2021 one, where the numbered key holds
    // the path directly. Both are handled.
    //
    // Numbered keys only count at depth 1 (directly inside the root block), because the current format
    // nests an "apps" block whose keys are numeric app ids mapped to sizes, and those are not paths.
    public static IReadOnlyList<string> ParseLibraryFolders(string vdf)
    {
        var paths = new List<string>();
        string? key = null;
        int depth = 0;

        foreach (string token in TokenizeVdf(vdf))
        {
            if (token is "{" or "}")
            {
                depth += token == "{" ? 1 : -1;
                key = null;
                continue;
            }

            if (key == null)
            {
                key = token;
                continue;
            }

            bool isPathKey = string.Equals(key, "path", StringComparison.OrdinalIgnoreCase)
                || (depth <= 1 && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _));
            if (isPathKey && token.Length > 0 && !paths.Contains(token))
                paths.Add(token);

            key = null;
        }

        return paths;
    }

    // Unturned's Steam app id, whose workshop content directory holds subscribed maps and mods.
    private const string WorkshopAppId = "304930";

    // <library>/steamapps/workshop/content/304930 for this install, or null when the install is not laid
    // out like a Steam library (a copied folder, say).
    public static string? WorkshopContentDirectory(string installRoot)
    {
        // <library>/steamapps/common/Unturned -> <library>/steamapps
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installRoot)) is not { } common
            || Path.GetDirectoryName(common) is not { } steamapps)
        {
            return null;
        }

        return Path.Combine(steamapps, "workshop", "content", WorkshopAppId);
    }

    // The Unity data directories an Unturned install can keep resources.assets in. The retail client
    // builds to "Unturned", the dedicated server to "Unturned_Headless" — and it is the SERVER that
    // scripts/fetch-game-data.sh downloads, since that is the build Valve's anonymous account may have.
    // Both carry the same prefab graph (Player_Client, its Viewmodel subtree, the zombies), so a check-out
    // that only ever fetched the server can still draw the real character; looking under the client name
    // alone is what silently left it on the placeholder figure.
    private static readonly string[] DataDirectoryNames = { "Unturned_Data", "Unturned_Headless_Data" };

    // Where this install keeps `name` (e.g. "resources.assets"), or null when no layout has it.
    public static string? FindDataFile(string installRoot, string name)
    {
        foreach (string directory in DataDirectoryNames)
        {
            string candidate = Path.Combine(installRoot, directory, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // The masterbundle for this install, or null when it is missing (a partial/corrupt download, or
    // a path that is not an Unturned install at all).
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // selects on the running OS
    public static string? FindMasterBundle(string installRoot) =>
        FindMasterBundle(installRoot, CurrentPlatform);

    // Same, but never null: callers that report their own "missing content" error want a concrete
    // path to name. Falls back to this platform's expected file name inside the install.
    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // selects on the running OS
    public static string MasterBundlePath(string installRoot) =>
        FindMasterBundle(installRoot, CurrentPlatform)
        ?? Path.Combine(installRoot, "Bundles", MasterBundleFileNames(CurrentPlatform)[0]);

    public static string? FindMasterBundle(string installRoot, Platform platform) =>
        FindBundle(Path.Combine(installRoot, "Bundles"), "core", platform);

    // A bundle file in a directory, given its base name ("core", "california2"): the variant for this
    // platform first, then the others. Workshop mods ship the same three variants as the game.
    public static string? FindBundle(string directory, string baseName, Platform platform)
    {
        foreach (string suffix in BundleSuffixes(platform))
        {
            string candidate = Path.Combine(directory, baseName + suffix + ".masterbundle");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    // Unturned ships the Windows bundle unsuffixed and gives Linux/macOS their own copies (they
    // differ only in the shader variants baked in). Steam installs just the one for the running OS,
    // so the native name is tried first and the others accepted as a fallback, so pointing
    // UNTURNED_PATH at a copy of another platform's install still loads, since the meshes,
    // materials and textures this project reads are identical across all three.
    public static IReadOnlyList<string> MasterBundleFileNames(Platform platform)
    {
        var names = new List<string>();
        foreach (string suffix in BundleSuffixes(platform))
            names.Add("core" + suffix + ".masterbundle");
        return names;
    }

    // The platform suffix a bundle file name carries, native first: Windows builds are unsuffixed.
    private static IReadOnlyList<string> BundleSuffixes(Platform platform) => platform switch
    {
        Platform.Windows => new[] { "", "_linux", "_mac" },
        Platform.Mac => new[] { "_mac", "", "_linux" },
        _ => new[] { "_linux", "", "_mac" },
    };

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? TextFile.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null; // unreadable library registry: fall back to the roots we already have
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Minimal VDF lexer: quoted strings (with \\ and \" escapes), bare words, {} block markers, and
    // // line comments. Enough for libraryfolders.vdf; this is not a general KeyValues parser.
    private static IEnumerable<string> TokenizeVdf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                continue;
            }

            if (c is '{' or '}')
            {
                i++;
                yield return c == '{' ? "{" : "}";
                continue;
            }

            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        sb.Append(text[i] switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            _ => text[i], // \\ and \", and anything else, taken literally
                        });
                    }
                    else
                    {
                        sb.Append(text[i]);
                    }

                    i++;
                }

                i++; // closing quote (or end of input on a truncated file)
                yield return sb.ToString();
                continue;
            }

            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}'))
                i++;
            yield return text[start..i];
        }
    }
}
