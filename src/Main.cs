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
            AddChild(NodesBuilder.Build(environmentDir));
            SetupEnvironment(lighting);

            if (headless)
            {
                GD.Print("[unturned-godot] Headless: data loaded, quitting.");
                GetTree().Quit();
                return;
            }

            // A screenshot uses the free camera + SHOT_CAM by default; PLAYER=1 spawns the character and
            // shoots from its (third-person) camera instead.
            if (OS.GetEnvironment("PLAYER") == "1")
            {
                SpawnPlayer(world.Terrain, thirdPerson: true, unturnedPath);
                _ = CaptureAndQuit(shot, settleFrames: 40);
            }
            else
            {
                AddFreeCamera();
                _ = CaptureAndQuit(shot, settleFrames: 5);
            }
            return;
        }

        // Interactive: terrain, roads, water and environment up front; objects stream in (mesh-first,
        // textures hot-swapped as they decode) so a cold load is playable in ~3 s instead of ~10 s.
        var level = new LevelInfo(System.IO.Path.Combine(unturnedPath, "Maps", MapName));
        (Node3D terrain, _) = WorldBuilder.BuildTerrain(level);
        AddChild(terrain);
        AddChild(RoadsBuilder.Build(environmentDir));
        AddChild(WaterBuilder.Build(lighting));
        AddChild(NodesBuilder.Build(environmentDir));
        SetupEnvironment(lighting);

        // Feature flag: FREECAM=1 keeps the fly-through camera; otherwise the player character spawns and
        // walks the map (terrain collision is added on demand so free-cam runs don't pay for it).
        if (OS.GetEnvironment("FREECAM") == "1")
            AddFreeCamera();
        else
            SpawnPlayer(terrain, thirdPerson: false, unturnedPath);

        var streamer = new ObjectStreamer { Name = "ObjectStreamer" };
        var overlay = new LoadingOverlay { Name = "LoadingOverlay" };
        AddChild(streamer);
        AddChild(overlay);
        overlay.Track(streamer); // connect before Begin so a warm cache's instant signals are caught
        streamer.Begin(unturnedPath, level);
    }

    // Spawns the character over a town and gives each terrain tile a cheap heightfield collision so it can
    // stand on the ground (vs a 2.1M-triangle concave trimesh). Objects stay non-colliding for now (the
    // player clips buildings), which is fine for movement.
    private static readonly Vector3 PlayerSpawn = new(300, 60, 84); // above land near the central town

    private void SpawnPlayer(Node3D terrain, bool thirdPerson, string unturnedPath)
    {
        foreach (Node child in terrain.GetChildren())
            if (child is MeshInstance3D tile)
                TerrainBuilder.AddHeightfieldCollision(tile);

        AddChild(new PlayerController
        {
            Name = "Player",
            Position = PlayerSpawn,
            StartThirdPerson = thirdPerson,
            BodyModel = CharacterModel.Build(unturnedPath), // real Unturned body, or null -> placeholder
        });
    }

    private void AddFreeCamera()
    {
        var camera = new FreeCamera { Name = "FreeCamera" };
        AddChild(camera);
        camera.Position = new Vector3(0, 300, 0); // above map center, looking down
        camera.RotationDegrees = new Vector3(-60, 0, 0);
    }

    // Render a few frames before grabbing the framebuffer so meshes are drawn (and, in player mode, so the
    // character settles onto the terrain). SHOT_CAM only applies to the free camera.
    private async System.Threading.Tasks.Task CaptureAndQuit(string path, int settleFrames)
    {
        if (GetNodeOrNull<FreeCamera>("FreeCamera") is { } cam)
        {
            cam.Position = new Vector3(-256, 900, 700);
            cam.RotationDegrees = new Vector3(-55, -20, 0);

            string camEnv = OS.GetEnvironment("SHOT_CAM"); // "px,py,pz,rx,ry" to override
            if (!string.IsNullOrEmpty(camEnv))
            {
                string[] p = camEnv.Split(',');
                cam.Position = new Vector3(p[0].ToFloat(), p[1].ToFloat(), p[2].ToFloat());
                cam.RotationDegrees = new Vector3(p[3].ToFloat(), p[4].ToFloat(), 0);
            }
        }

        for (int i = 0; i < settleFrames; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        // PLAYER_FRONT=1 frames the character from the front (its face) instead of the over-the-shoulder view.
        if (OS.GetEnvironment("PLAYER_FRONT") == "1" && GetNodeOrNull<Node3D>("Player") is { } player)
        {
            Vector3 headTarget = player.GlobalPosition + new Vector3(0, 1.7f, 0);
            Vector3 forward = -player.GlobalTransform.Basis.Z; // the character's facing
            var front = new Camera3D { Name = "FrontCamera", Current = true };
            AddChild(front);
            front.GlobalPosition = headTarget + (forward * 2.2f);
            front.LookAt(headTarget);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        Image img = GetViewport().GetTexture().GetImage();
        img.SavePng(path);
        GD.Print($"[unturned-godot] Screenshot saved: {path}");
        GetTree().Quit();
    }

    // Sun + sky/ambient from the map lighting, plus the debug overlay (windowed only). The camera/player is
    // added separately by the caller so the free-cam and character paths can differ.
    private void SetupEnvironment(LevelLighting? lighting)
    {
        (DirectionalLight3D sun, WorldEnvironment world) = SceneEnvironment.Build(lighting);
        AddChild(sun);
        AddChild(world);

        if (DisplayServer.GetName() != "headless")
            AddChild(new DebugOverlay { Name = "DebugOverlay" });
    }
}
