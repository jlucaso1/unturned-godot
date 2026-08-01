using System;
using System.IO;
using System.Runtime.Versioning;

namespace UnturnedGodot.Tests.Helpers;

// Tests that seal a directory off to prove a parser survives an unreadable path only mean something
// where the mode bits are actually enforced: not on Windows, and not for a privileged user (root, or
// anyone holding CAP_DAC_OVERRIDE), which is what CI containers and dev containers usually run as.
// Probing beats reading $USER: the variable is unset in most containers even when the shell is root.
public static class PosixPermissions
{
    private static readonly Lazy<bool> AreEnforcedProbe = new(Probe);

    // The guard attributes let a caller behind `if (PosixPermissions.AreEnforced)` call the Unix-only
    // file-mode APIs without CA1416 firing, exactly as `OperatingSystem.IsLinux()` would.
    [SupportedOSPlatformGuard("linux")]
    [SupportedOSPlatformGuard("macos")]
    public static bool AreEnforced => AreEnforcedProbe.Value;

    private static bool Probe()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;

        using var dir = new TempDir();
        string sealedOff = Path.Combine(dir.Path, "sealed");
        Directory.CreateDirectory(sealedOff);
        File.WriteAllBytes(Path.Combine(sealedOff, "probe"), Array.Empty<byte>());
        try
        {
            File.SetUnixFileMode(sealedOff, UnixFileMode.None);
            Directory.GetFiles(sealedOff);
            return false;                       // the listing went through, so the mode bits do not bite
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        finally
        {
            File.SetUnixFileMode(sealedOff, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
    }
}
