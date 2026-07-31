using System.IO;
using UnturnedGodot.Assets;

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
}
