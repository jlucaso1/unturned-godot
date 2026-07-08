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

        WorldBuildResult world = WorldBuilder.Build(unturnedPath, MapName);
        AddChild(world.Terrain);
        AddChild(world.Objects);

        string environmentDir = System.IO.Path.Combine(unturnedPath, "Maps", MapName, "Environment");
        AddChild(RoadsBuilder.Build(environmentDir));

        LevelLighting? lighting = LevelLighting.Load(System.IO.Path.Combine(environmentDir, "Lighting.dat"));
        AddChild(WaterBuilder.Build(lighting));
        SetupEnvironment(lighting);

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

    // PEI's real midday values, used as a fallback when a map ships no Lighting.dat.
    private static readonly LightingKeyframe DefaultMidday = new(
        sun: new Color(0.933f, 0.863f, 0.757f),
        sea: new Color(0.482f, 0.608f, 0.792f),
        fog: new Color(0.784f, 0.784f, 0.784f),
        ambientSky: new Color(0.8f, 0.627f, 0.388f),
        ambientEquator: new Color(0.722f, 0.62f, 0.467f),
        ambientGround: new Color(0.682f, 0.627f, 0.545f),
        intensity: 1f, fogDensity: 0.147f, clouds: 0.087f, shadows: 1f, rays: 0.25f);

    private const float DefaultAzimuth = 281.74f;

    private void SetupEnvironment(LevelLighting? lighting)
    {
        // Replicates Unturned's lighting model (LevelLighting.cs): a flat ambient plus one directional
        // sun, driven by the map's real midday keyframe decoded from Environment/Lighting.dat. The strong
        // warm ambient is what fills shadowed/enclosed surfaces so they aren't black.
        LightingKeyframe day = lighting?.Midday ?? DefaultMidday;
        float azimuth = lighting?.Azimuth ?? DefaultAzimuth;

        var sun = new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-50, -azimuth, 0),
            LightColor = day.Sun,
            LightEnergy = day.Intensity,
            ShadowEnabled = true,
            // 4 cascades is overkill for the ~100 m default shadow range; 2 is visually identical here
            // (shadows are subtle in Unturned's ambient-filled look) and ~10% cheaper on frame time (#6).
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            // The default normal bias (2 m) pushes the shadow test so far off large flat surfaces that
            // the terrain stopped catching tree shadows while the raised roads still did; lower it and
            // extend the range so shadows land on the ground too.
            ShadowNormalBias = 0.3f,
            ShadowBias = 0.03f,
            DirectionalShadowMaxDistance = 300f,
        };
        AddChild(sun);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            // Godot has no trilight ambient, so average Unturned's sky/equator/ground into one flat color.
            AmbientLightColor = AverageAmbient(day),
            AmbientLightEnergy = 1.0f,
        };
        AddChild(new WorldEnvironment { Environment = env, Name = "WorldEnvironment" });

        var camera = new FreeCamera { Name = "FreeCamera" };
        AddChild(camera);
        camera.Position = new Vector3(0, 300, 0); // above map center, looking down
        camera.RotationDegrees = new Vector3(-60, 0, 0);

        if (DisplayServer.GetName() != "headless")
            AddChild(new DebugOverlay { Name = "DebugOverlay" });
    }

    private static Color AverageAmbient(LightingKeyframe day) => new(
        (day.AmbientSky.R + day.AmbientEquator.R + day.AmbientGround.R) / 3f,
        (day.AmbientSky.G + day.AmbientEquator.G + day.AmbientGround.G) / 3f,
        (day.AmbientSky.B + day.AmbientEquator.B + day.AmbientGround.B) / 3f);
}
