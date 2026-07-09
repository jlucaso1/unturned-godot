using Godot;
using UnturnedGodot.Assets;
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

        // CHAR_ONLY=1: just the character + a light + a front camera, no world build. For fast iteration on
        // the character material/shader (renders in seconds instead of the ~2 min full-world build).
        string shotOnly = OS.GetEnvironment("SCREENSHOT_PATH");
        if (OS.GetEnvironment("CHAR_ONLY") == "1" && !string.IsNullOrEmpty(shotOnly))
        {
            if (CharacterModel.Build(unturnedPath) is { } model)
            {
                if (model is CharacterSkeleton rig && OS.GetEnvironment("CHAR_STANCE") is { Length: > 0 } s)
                {
                    rig.SetState(System.Enum.Parse<Player.EPlayerStance>(s, ignoreCase: true),
                        OS.GetEnvironment("CHAR_MOVING") == "1");
                    if (OS.GetEnvironment("CHAR_PITCH") is { Length: > 0 } cp)
                        rig.SetPitch(cp.ToFloat()); // look pitch -> spine/skull bend
                    rig.Seek(OS.GetEnvironment("CHAR_ANIM_TIME") is { Length: > 0 } at ? at.ToFloat() : 0f);
                }
                AddChild(model);
            }
            AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-50, -140, 0) });
            AddChild(new WorldEnvironment
            {
                Environment = new Godot.Environment
                {
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = new Color(0.7f, 0.7f, 0.75f),
                    AmbientLightEnergy = 1.0f,
                },
            });
            var cam = new Camera3D { Current = true };
            AddChild(cam);
            float side = OS.GetEnvironment("CHAR_BACK") == "1" ? 3.2f : -3.2f; // -Z is the front
            cam.Position = OS.GetEnvironment("CHAR_SIDE") == "1"
                ? new Vector3(4.0f, 1.0f, 0f)  // side profile, for reading prone/lie-down poses
                : new Vector3(0, 1.1f, side);  // full-body 3/4-front, so any stance is framed
            cam.LookAt(new Vector3(0, 0.5f, 0));
            int settle = OS.GetEnvironment("CHAR_SETTLE") is { Length: > 0 } sf ? int.Parse(sf) : 5;
            _ = CaptureAndQuit(shotOnly, settle); // more settle frames -> the animation advances further
            return;
        }

        string[] userArgs = OS.GetCmdlineUserArgs();

        // Dedicated server: godot --headless -- --server [--port=27015]. Movement-sim only, raw UDP.
        if (System.Array.IndexOf(userArgs, "--server") >= 0)
        {
            ushort serverPort = NetworkManager.DefaultPort;
            foreach (string arg in userArgs)
                if (arg.StartsWith("--port=") && ushort.TryParse(arg[7..], out ushort parsed))
                    serverPort = parsed;
            AddChild(DedicatedServer.Create(unturnedPath, MapName, PlayerSpawn, serverPort));
            return;
        }

        // Scripted headless client for multiplayer verification: BOT_JOIN=host:port [BOT_SECONDS=20].
        if (OS.GetEnvironment("BOT_JOIN") is { Length: > 0 } botTarget)
        {
            string[] parts = botTarget.Split(':');
            float lifetime = OS.GetEnvironment("BOT_SECONDS") is { Length: > 0 } bs ? bs.ToFloat() : 20f;
            AddChild(BotClient.Create(parts[0], parts.Length > 1 ? ushort.Parse(parts[1]) : NetworkManager.DefaultPort,
                OS.GetEnvironment("BOT_NAME") is { Length: > 0 } bn ? bn : "Bot", lifetime));
            return;
        }

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

        if (!string.IsNullOrEmpty(shot) && OS.GetEnvironment("MENU_SHOT") == "1")
        {
            AddChild(new MainMenu { Name = "MainMenu" }); // screenshot of the boot menu, no world
            _ = CaptureAndQuit(shot, settleFrames: 10);
            return;
        }

        if (headless || !string.IsNullOrEmpty(shot))
        {
            // Complete synchronous build — headless validation, or a screenshot that must be finished.
            WorldBuildResult world = WorldBuilder.Build(unturnedPath, MapName);
            AddChild(world.Terrain);
            AddChild(world.Objects);
            AddChild(world.Foliage);
            AddChild(RoadsBuilder.Build(environmentDir, world.Heights));
            AddChild(WaterBuilder.Build(lighting, out StandardMaterial3D water));
            AddChild(NodesBuilder.Build(environmentDir));
            SetupEnvironment(lighting, water, unturnedPath);

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
                SpawnPlayer(world.Terrain, thirdPerson: true, unturnedPath, world.Heights);
                RunPendingAudioExtraction(); // no streamer on this path; extract right away
                int settle = OS.GetEnvironment("SETTLE") is { Length: > 0 } sv ? int.Parse(sv) : 40;
                _ = CaptureAndQuit(shot, settle);
            }
            else
            {
                AddFreeCamera();
                _ = CaptureAndQuit(shot, settleFrames: 5);
            }
            return;
        }

        // Interactive: automation env flags (SOLO/FREECAM/JOIN/OPEN_LAN) boot straight into the world; a
        // normal launch lands on the main menu first — no map is loaded until the player picks an option.
        // SOLO=1 is the local session WITHOUT the UDP listener — the right flag for single-player
        // automation; OPEN_LAN is only for tests where a second client actually joins.
        bool autoStart = OS.GetEnvironment("SOLO") == "1"
            || OS.GetEnvironment("FREECAM") == "1" || OS.GetEnvironment("OPEN_LAN") == "1"
            || OS.GetEnvironment("OPEN_LAN_AFTER") is { Length: > 0 }
            || OS.GetEnvironment("JOIN") is { Length: > 0 };
        if (autoStart)
        {
            _ = StartInteractiveWorld(unturnedPath, environmentDir, lighting,
                OS.GetEnvironment("JOIN") is { Length: > 0 } join ? join : null);
            return;
        }

        var menu = new MainMenu { Name = "MainMenu" };
        menu.OnStart = joinTarget =>
        {
            menu.QueueFree();
            _ = StartInteractiveWorld(unturnedPath, environmentDir, lighting, joinTarget);
        };
        AddChild(menu);
    }

    // Builds the streamed interactive world behind a loading screen, yielding to the render loop between
    // stages (and inside the heavy ones) so the UI never freezes; joinTarget != null also connects there.
    private async System.Threading.Tasks.Task StartInteractiveWorld(string unturnedPath, string environmentDir,
        LevelLighting? lighting, string? joinTarget)
    {
        // Kick the zombie navigation map off first: its NavigationServer sync runs async over a
        // few seconds and finishes behind the world build, so pathfinding is ready on first aggro.
        ZombieNavigation.Preload(System.IO.Path.Combine(unturnedPath, "Maps", MapName));

        _pendingJoin = joinTarget;

        var loading = new LoadingScreen { Name = "LoadingScreen" };
        AddChild(loading);
        await NextFrame(); // paint the loading screen before any heavy work

        var level = new LevelInfo(System.IO.Path.Combine(unturnedPath, "Maps", MapName));

        // Start the object placement/asset IO now so it runs on a worker while the terrain builds; the
        // streamer joins the tree later, just before Begin().
        var streamer = new ObjectStreamer { Name = "ObjectStreamer" };
        streamer.StartPrepare(unturnedPath, level);

        loading.SetStatus("Building terrain…");
        (Node3D terrain, _, HeightmapSampler heights) = await WorldBuilder.BuildTerrainAsync(level, this);
        AddChild(terrain);

        loading.SetStatus("Roads and water…");
        await NextFrame();
        AddChild(RoadsBuilder.Build(environmentDir, heights));
        AddChild(WaterBuilder.Build(lighting, out StandardMaterial3D waterMat));
        AddChild(NodesBuilder.Build(environmentDir));
        SetupEnvironment(lighting, waterMat, unturnedPath);

        loading.SetStatus("Character…");
        await NextFrame();
        // Feature flag: FREECAM=1 keeps the fly-through camera; otherwise the player character spawns and
        // walks the map (terrain collision is added on demand so free-cam runs don't pay for it).
        if (OS.GetEnvironment("FREECAM") == "1")
            AddFreeCamera();
        else
            SpawnPlayer(terrain, thirdPerson: false, unturnedPath, heights);

        loading.SetStatus("World objects…");
        await NextFrame();
        var overlay = new LoadingOverlay { Name = "LoadingOverlay" };
        AddChild(streamer);
        AddChild(overlay);
        overlay.Track(streamer); // connect before Begin so a warm cache's instant signals are caught
        streamer.Finished += loading.Finish; // fade out once the scene (and warm textures) are in
        streamer.Finished += RunPendingAudioExtraction;
        streamer.Begin();
    }

    private async System.Threading.Tasks.Task NextFrame() =>
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    // Set by the main menu's Connect flow (or the JOIN env), consumed by SpawnPlayer.
    private string? _pendingJoin;

    // One-shot movement-audio extraction, deferred behind the world streamer (see BuildFootsteps).
    private System.Action? _pendingAudioExtraction;

    private void RunPendingAudioExtraction()
    {
        _pendingAudioExtraction?.Invoke();
        _pendingAudioExtraction = null;
    }

    // Spawns the character over a town and gives each terrain tile a cheap heightfield collision so it can
    // stand on the ground (vs a 2.1M-triangle concave trimesh). Objects stay non-colliding for now (the
    // player clips buildings), which is fine for movement.
    private static readonly Vector3 PlayerSpawn = new(300, 60, 84); // above land near the central town

    private void SpawnPlayer(Node3D terrain, bool thirdPerson, string unturnedPath, HeightmapSampler? heights)
    {
        foreach (Node child in terrain.GetChildren())
            if (child is MeshInstance3D tile)
                TerrainBuilder.AddHeightfieldCollision(tile);

        var network = new NetworkManager { Name = "Network" };
        if (heights != null)
            network.Configure(heights, PlayerSpawn);
        AddChild(network);

        var player = new PlayerController
        {
            Name = "Player",
            Position = PlayerSpawn,
            StartThirdPerson = thirdPerson,
            BodyModel = CharacterModel.Build(unturnedPath), // real Unturned body, or null -> placeholder
        };
        (player.Footsteps, _movementAudioFactory) = BuildMovementAudio(unturnedPath);
        AddChild(player);

        string playerName = OS.GetEnvironment("PLAYER_NAME") is { Length: > 0 } pn ? pn : "Player";

        // Connect from the main menu (or JOIN=host[:port]) joins someone else's server; otherwise the
        // always-on local session starts — singleplayer IS a loopback server (Unturned's Provider shape),
        // so every gameplay feature is written once as server logic + replication and works identically
        // solo, LAN and dedicated. Solo cost is microseconds per tick: one in-memory queue exchange and a
        // one-player simulation step; the lone StateUpdate doubles as the self-healing keepalive.
        if (_pendingJoin is { Length: > 0 } join)
        {
            string[] parts = join.Split(':');
            network.JoinServer(parts[0],
                parts.Length > 1 && ushort.TryParse(parts[1], out ushort p) ? p : NetworkManager.DefaultPort,
                playerName);
        }
        else
        {
            network.StartSingleplayer(playerName);
            network.HostZombies(System.IO.Path.Combine(unturnedPath, "Maps", MapName));
        }

        // OPEN_LAN=1 opens the UDP listener immediately; OPEN_LAN_AFTER=seconds opens it mid-game — the
        // timing of a player pressing the pause-menu button after already moving (e2e scripts use both).
        if (OS.GetEnvironment("OPEN_LAN") == "1")
            network.OpenToLan(NetworkManager.DefaultPort);
        else if (OS.GetEnvironment("OPEN_LAN_AFTER") is { Length: > 0 } delay)
            GetTree().CreateTimer(delay.ToFloat()).Timeout += () => network.OpenToLan(NetworkManager.DefaultPort);
        AttachSession(network, player, unturnedPath);

        if (DisplayServer.GetName() != "headless")
            AddChild(new PauseMenu { Name = "PauseMenu", Network = network, OnSessionStarted = () => AttachSession(network, player, unturnedPath) });
    }

    // Once a session exists (hosted or joined), wire the input sender and the remote-player view.
    private void AttachSession(NetworkManager network, PlayerController player, string unturnedPath)
    {
        if (network.Client == null || player.Net != null)
            return;
        player.Net = network.Client;
        AddChild(RemotePlayersView.Create(network.Client, unturnedPath, _movementAudioFactory));
        AddChild(ZombiesView.Create(network.Client, unturnedPath, _oneShotAudio));
    }

    // One MovementAudio per character; remote avatars get theirs from this factory (RemotePlayersView).
    private System.Func<MovementAudio>? _movementAudioFactory;
    private OneShotAudio? _oneShotAudio; // the shared positional voice pool (zombie roars use it too)

    // Movement audio infrastructure: the physics-material bank + terrain splat sampler resolve WHICH
    // definition a step plays; the shared AudioDefLibrary + positional OneShotAudio pool play it. The
    // referenced OneShotAudioDefinitions extract from the masterbundle in the background on first run.
    private (MovementAudio Local, System.Func<MovementAudio> Factory) BuildMovementAudio(string unturnedPath)
    {
        string bundlesAssets = System.IO.Path.Combine(unturnedPath, "Bundles", "Assets");
        PhysicsMaterialBank bank =
            PhysicsMaterialBank.ScanDirectory(System.IO.Path.Combine(bundlesAssets, "PhysicsMaterials"));
        LandscapePhysics landscape =
            LandscapePhysics.ScanDirectory(System.IO.Path.Combine(bundlesAssets, "Landscapes"));

        var level = new LevelInfo(System.IO.Path.Combine(unturnedPath, "Maps", MapName));
        var splat = new SplatSampler();
        System.Collections.Generic.Dictionary<(int x, int y), System.Guid[]> tileMaterials =
            LevelHierarchy.ReadTileMaterials(System.IO.Path.Combine(level.Path, "Level.hierarchy"));
        foreach (((int x, int y), System.Guid[] materials) in tileMaterials)
        {
            // Only the dominant layer index survives (64 KB/tile); the full float tile (2 MB) is
            // never materialized — the audio sampler used to retain ~32 MB of those.
            string path = level.SplatmapPath(x, y);
            if (System.IO.File.Exists(path))
                splat.Add(x, y, SplatmapTile.DominantLayers(System.IO.File.ReadAllBytes(path)), materials);
        }

        string audioCacheDir = ProjectSettings.GlobalizePath("user://audio_cache");
        string bundlePath = System.IO.Path.Combine(unturnedPath, "Bundles", "core_linux.masterbundle");
        var defPaths = new System.Collections.Generic.HashSet<string>();
        foreach (string key in new[] { "FootstepWalk", "FootstepRun", "BipedLand" })
            foreach (string name in new[]
                { "Foliage", "Concrete", "Gravel", "Sand", "Tile", "Metal", "Wood", "Cloth", "Snow", "Ice" })
                if (bank.FindAudioDefPath(name, key) is { } path)
                    defPaths.Add(path);
        // ZombieManager's raw clip arrays (played directly, not via OneShotAudioDefinitions): the 16
        // roars and 5 groans, packaged as synthetic definitions with Zombie.PlayOneShot's envelope
        // (pitch 0.9-1.1 for a normal zombie; megas override at play time).
        var roarPaths = new string[16];
        for (int i = 0; i < roarPaths.Length; i++)
            roarPaths[i] = $"Sounds/Zombies/Roars/Roar_{i}.mp3";
        var groanPaths = new string[5];
        for (int i = 0; i < groanPaths.Length; i++)
            groanPaths[i] = $"Sounds/Zombies/Groans/Groan_{i}.mp3";
        var clipGroups = new System.Collections.Generic.List<AudioExtractor.RawClipGroup>
        {
            new("ZombieRoars", roarPaths, Volume: 1f, MinPitch: 0.9f, MaxPitch: 1.1f),
            new("ZombieGroans", groanPaths, Volume: 1f, MinPitch: 0.9f, MaxPitch: 1.1f),
        };
        // Deferred until the world streamer finishes: a cold load already runs one full 1.4 GB bundle
        // decode, and racing a second one for audio doubles peak CPU/memory and can stall weak machines.
        _pendingAudioExtraction = () =>
            _ = System.Threading.Tasks.Task.Run(
                () => AudioExtractor.Extract(bundlePath, defPaths, audioCacheDir, clipGroups));

        GD.Print($"[audio] footsteps ready: {bank.Count} physics materials, {landscape.Count} landscape " +
            $"materials, {splat.TileCount} splat tiles");

        var oneShot = OneShotAudio.Create(new AudioDefLibrary(audioCacheDir));
        AddChild(oneShot);
        _oneShotAudio = oneShot;
        MovementAudio Factory(bool startGrounded) => new(bank, landscape, splat, oneShot, startGrounded);
        return (Factory(startGrounded: false), () => Factory(startGrounded: true));
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
    private void SetupEnvironment(LevelLighting? lighting, StandardMaterial3D waterMaterial, string unturnedPath)
    {
        AddChild(DayNightController.Build(lighting, waterMaterial, SkyboxAssets.Load(unturnedPath)));

        if (DisplayServer.GetName() != "headless")
            AddChild(new DebugOverlay { Name = "DebugOverlay" });
    }
}
