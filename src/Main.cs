using Godot;

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

        if (System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--benchmark") >= 0)
        {
            Benchmark.BenchmarkRunner.Run(this, unturnedPath, MapName);
            GetTree().Quit();
            return;
        }

        WorldBuildResult world = WorldBuilder.Build(unturnedPath, MapName);
        AddChild(world.Terrain);
        AddChild(world.Objects);
        SetupEnvironment();

        if (DisplayServer.GetName() == "headless")
        {
            GD.Print("[unturned-godot] Headless: data loaded, quitting.");
            GetTree().Quit();
            return;
        }

        string shot = OS.GetEnvironment("SCREENSHOT_PATH");
        if (!string.IsNullOrEmpty(shot))
            _ = CaptureAndQuit(shot);
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

    private void SetupEnvironment()
    {
        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-50, -30, 0),
            ShadowEnabled = true,
        };
        AddChild(sun);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
        };
        AddChild(new WorldEnvironment { Environment = env, Name = "WorldEnvironment" });

        var camera = new FreeCamera { Name = "FreeCamera" };
        AddChild(camera);
        camera.Position = new Vector3(0, 300, 0); // above map center, looking down
        camera.RotationDegrees = new Vector3(-60, 0, 0);

        if (DisplayServer.GetName() != "headless")
            AddChild(new DebugOverlay { Name = "DebugOverlay" });
    }
}
