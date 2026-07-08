using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class Main : Node3D
{
    // Overridable via UNTURNED_PATH env var.
    private const string DefaultUnturnedPath =
        "/home/jlucaso/.local/share/Steam/steamapps/common/Unturned";

    private const string MapName = "PEI";

    public override void _Ready()
    {
        string unturnedPath = OS.GetEnvironment("UNTURNED_PATH");
        if (string.IsNullOrEmpty(unturnedPath))
            unturnedPath = DefaultUnturnedPath;

        string[] userArgs = OS.GetCmdlineUserArgs();
        if (System.Array.IndexOf(userArgs, "--benchmark") >= 0)
        {
            if (System.Array.IndexOf(userArgs, "--gpu") >= 0)
            {
                // Tier 2 drives frames over time and quits itself when done.
                _ = Benchmark.GpuBenchmark.RunAsync(this, unturnedPath, MapName);
                return;
            }

            Benchmark.BenchmarkRunner.Run(this, unturnedPath, MapName);
            GetTree().Quit();
            return;
        }

        string environmentDir = System.IO.Path.Combine(unturnedPath, "Maps", MapName, "Environment");
        LevelLighting? lighting = LevelLighting.Load(System.IO.Path.Combine(environmentDir, "Lighting.dat"));
        bool headless = DisplayServer.GetName() == "headless";
        string shot = OS.GetEnvironment("SCREENSHOT_PATH");

        if (headless || !string.IsNullOrEmpty(shot))
        {
            // Complete synchronous build — headless validation, or a screenshot that must be finished.
            WorldBuildResult world = WorldBuilder.Build(unturnedPath, MapName);
            AddChild(world.Terrain);
            AddChild(world.Objects);
            AddChild(world.Foliage);
            AddChild(RoadsBuilder.Build(environmentDir));
            AddChild(WaterBuilder.Build(lighting));
            SetupEnvironment(lighting);

            if (headless)
            {
                GD.Print("[unturned-godot] Headless: data loaded, quitting.");
                GetTree().Quit();
                return;
            }
            _ = CaptureAndQuit(shot);
            return;
        }

        // Interactive: terrain, roads, water and environment up front; objects stream in (mesh-first,
        // textures hot-swapped as they decode) so a cold load is playable in ~3 s instead of ~10 s.
        var level = new LevelInfo(System.IO.Path.Combine(unturnedPath, "Maps", MapName));
        (Node3D terrain, _) = WorldBuilder.BuildTerrain(level);
        AddChild(terrain);
        AddChild(RoadsBuilder.Build(environmentDir));
        AddChild(WaterBuilder.Build(lighting));
        SetupEnvironment(lighting);

        var streamer = new ObjectStreamer { Name = "ObjectStreamer" };
        var overlay = new LoadingOverlay { Name = "LoadingOverlay" };
        AddChild(streamer);
        AddChild(overlay);
        overlay.Track(streamer); // connect before Begin so a warm cache's instant signals are caught
        streamer.Begin(unturnedPath, level);
    }

    // Render a few frames before grabbing the framebuffer so meshes are actually drawn.
    private async System.Threading.Tasks.Task CaptureAndQuit(string path)
    {
        var cam = GetNode<FreeCamera>("FreeCamera");
        cam.Position = new Vector3(-256, 900, 700);
        cam.RotationDegrees = new Vector3(-55, -20, 0);

        string camEnv = OS.GetEnvironment("SHOT_CAM"); // "px,py,pz,rx,ry" to override
        if (!string.IsNullOrEmpty(camEnv))
        {
            string[] p = camEnv.Split(',');
            cam.Position = new Vector3(p[0].ToFloat(), p[1].ToFloat(), p[2].ToFloat());
            cam.RotationDegrees = new Vector3(p[3].ToFloat(), p[4].ToFloat(), 0);
        }

        for (int i = 0; i < 5; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Image img = GetViewport().GetTexture().GetImage();
        img.SavePng(path);
        GD.Print($"[unturned-godot] Screenshot saved: {path}");
        GetTree().Quit();
    }

    // Sun + sky/ambient from the map lighting, then the free camera and (windowed only) the debug overlay.
    private void SetupEnvironment(LevelLighting? lighting)
    {
        (DirectionalLight3D sun, WorldEnvironment world) = SceneEnvironment.Build(lighting);
        AddChild(sun);
        AddChild(world);

        var camera = new FreeCamera { Name = "FreeCamera" };
        AddChild(camera);
        camera.Position = new Vector3(0, 300, 0); // above map center, looking down
        camera.RotationDegrees = new Vector3(-60, 0, 0);

        if (DisplayServer.GetName() != "headless")
            AddChild(new DebugOverlay { Name = "DebugOverlay" });
    }
}
