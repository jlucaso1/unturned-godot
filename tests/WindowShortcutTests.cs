using System;
using System.IO;
using Xunit;

namespace UnturnedGodot.Tests;

public class WindowShortcutTests
{
    [Fact]
    public void Main_TogglesFullscreenOnAltEnterWithoutKeyRepeat()
    {
        string? path = FindRepositoryFile(Path.Combine("src", "Main.cs"));
        if (path == null)
            return;

        string source = File.ReadAllText(path);
        Assert.Contains("public override void _UnhandledInput(InputEvent @event)", source);
        Assert.Contains("Keycode: Key.Enter, AltPressed: true", source);
        Assert.Contains("Echo: false", source);
        Assert.Contains("DisplayServer.WindowGetMode()", source);
        Assert.Contains("DisplayServer.WindowMode.Fullscreen", source);
        Assert.Contains("DisplayServer.WindowMode.Windowed", source);
        Assert.Contains("GetViewport().SetInputAsHandled()", source);
    }

    [Fact]
    public void Main_AutoStartsWhenMapIsExplicitlySelected()
    {
        string? path = FindRepositoryFile(Path.Combine("src", "Main.cs"));
        if (path == null)
            return;

        string source = File.ReadAllText(path);
        int autoStart = source.IndexOf("bool autoStart", StringComparison.Ordinal);
        int startWorld = source.IndexOf("StartInteractiveWorld", autoStart, StringComparison.Ordinal);
        Assert.True(autoStart >= 0 && startWorld > autoStart);
        Assert.Contains("OS.GetEnvironment(\"MAP\") is { Length: > 0 }", source.Substring(autoStart,
            startWorld - autoStart));
    }

    // Null means the repository root was not found, and nothing else — see the copy in
    // PhysicsBodyOrderTests for why a file missing under a located root must fail instead.
    private static string? FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "unturned-godot.sln")))
            {
                string candidate = Path.Combine(directory.FullName, relativePath);
                Assert.True(File.Exists(candidate),
                    $"'{relativePath}' does not exist. This test reads that file and asserts on its "
                    + "contents, so it cannot pass without it: either update the path to where the code "
                    + "moved, or delete this test along with the thing it was guarding.");
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
