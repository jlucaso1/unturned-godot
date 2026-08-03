using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace UnturnedGodot.Tests;

// project.godot is the one file that configures the running game and that nothing else asserts on. It
// is also the file the Godot editor rewrites wholesale whenever anyone opens the project settings UI,
// which drops comments — so the reasons these two values are what they are have to live somewhere the
// editor cannot erase, and that is here.
public class ProjectSettingsTests
{
    private static Dictionary<string, string> LoadProjectSettings()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "unturned-godot.sln")))
            directory = directory.Parent;

        Assert.True(directory != null, "repository root (the folder holding unturned-godot.sln) not found");
        string path = Path.Combine(directory!.FullName, "project.godot");
        Assert.True(File.Exists(path), $"'{path}' does not exist");

        // Flattened to "section/key" so a value cannot be matched against the wrong section, and parsed
        // rather than grepped so whitespace and key order are free to change.
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        string section = "";
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == ';')
                continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                continue;
            }
            int equals = line.IndexOf('=');
            if (equals > 0)
                settings[$"{section}/{line[..equals].Trim()}"] = line[(equals + 1)..].Trim();
        }
        return settings;
    }

    // Shipped vsync belongs to the player. It was 0 (Disabled) so the benchmarks would render as fast as
    // the machine allows, which handed every player an uncapped renderer as the default: a menu spinning
    // the GPU at several hundred FPS, and tearing. The benchmark now asks for that itself, at runtime, in
    // Main.UncapFramesForMeasurement.
    [Fact]
    public void VSyncIsEnabledForPlayers()
    {
        Dictionary<string, string> settings = LoadProjectSettings();

        Assert.True(settings.TryGetValue("display/window/vsync/vsync_mode", out string? mode),
            "window/vsync/vsync_mode is not set; the engine default is Enabled, but leaving it implicit "
            + "is what let a benchmarking value ship to players unnoticed in the first place.");
        Assert.Equal("1", mode); // DisplayServer.VSyncMode.Enabled
    }

    // The netcode's cadence IS the physics rate: NetworkManager, DedicatedServer and BotClient all run in
    // _PhysicsProcess(delta), so the server's tick loop and the client's send rate are both driven by it.
    // #36 then sized the server's input buffer at 4 frames — 0.32 s of jitter — against a client that
    // produces one input per physics tick. Changing this number silently re-tunes that buffer and the
    // client/server tick relationship together, which is not a thing that should be possible by opening
    // the project settings UI and not noticing.
    [Fact]
    public void PhysicsTickRateIsPinned()
    {
        Dictionary<string, string> settings = LoadProjectSettings();

        Assert.True(settings.TryGetValue("physics/common/physics_ticks_per_second", out string? ticks),
            "physics/common/physics_ticks_per_second is not pinned, so the netcode's cadence is whatever "
            + "the engine happens to default to.");
        Assert.Equal("60", ticks);
    }
}
