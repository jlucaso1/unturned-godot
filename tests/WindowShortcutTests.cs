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

    private static string? FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "unturned-godot.sln")))
            {
                string candidate = Path.Combine(directory.FullName, relativePath);
                return File.Exists(candidate) ? candidate : null;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
