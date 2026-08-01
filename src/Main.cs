using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;

namespace UnturnedGodot;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class Main : Node3D
{
    // The map folder under Maps/ (or a workshop item) that this run loads. The menu picks it; MAP=
    // overrides for automation, and the last pick is remembered between sessions.
    private const string DefaultMapName = "PEI";
    private const string MenuConfigPath = "user://menu.cfg";

    private string _mapName = DefaultMapName;
    private string _unturnedPath = "";

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Enter, AltPressed: true })
            return;

        DisplayServer.WindowMode mode = DisplayServer.WindowGetMode();
        DisplayServer.WindowSetMode(mode == DisplayServer.WindowMode.Fullscreen
            ? DisplayServer.WindowMode.Windowed
            : DisplayServer.WindowMode.Fullscreen);
        GetViewport().SetInputAsHandled();
    }

    public override void _Ready()
    {
        if (OS.GetEnvironment("MAP") is { Length: > 0 } mapOverride)
            _mapName = mapOverride;
        else if (LoadLastMap() is { Length: > 0 } remembered)
            _mapName = remembered;

        // Nothing here ships with the project: the map, models, textures and audio are all read from the
        // player's own Steam copy of Unturned. UNTURNED_PATH overrides the Steam library autodetection.
        string unturnedPath = OS.GetEnvironment(UnturnedInstall.PathEnvironmentVariable);
        if (string.IsNullOrEmpty(unturnedPath))
            unturnedPath = UnturnedInstall.FindInstall(UnturnedInstall.DefaultSteamRoots()) ?? "";

        _unturnedPath = unturnedPath;

        if (!System.IO.Directory.Exists(unturnedPath))
        {
            Log.PrintErr("[unturned-godot] Unturned install not found. Install it through Steam, or point "
                + $"{UnturnedInstall.PathEnvironmentVariable} at the game directory (the one containing "
                + "Bundles/ and Maps/).");
            GetTree().Quit(1);
            return;
        }

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

        // Dedicated server: godot --headless -- --server [--port=27015] [--map=Washington]. Movement-sim
        // only, raw UDP.
        if (System.Array.IndexOf(userArgs, "--server") >= 0)
        {
            ushort serverPort = NetworkManager.DefaultPort;
            foreach (string arg in userArgs)
            {
                if (arg.StartsWith("--port=") && ushort.TryParse(arg[7..], out ushort parsed))
                    serverPort = parsed;
                else if (arg.StartsWith("--map=") && arg.Length > 6)
                    _mapName = arg[6..];
            }

            MapEntry? serverMap = MapCatalog.Find(unturnedPath, _mapName);
            if (serverMap is not { IsSupported: true })
            {
                string reason = serverMap == null ? "was not found" : "uses an unsupported terrain format";
                Log.PrintErr($"[server] Map '{_mapName}' {reason}; the listener was not started.");
                GetTree().Quit(1);
                return;
            }

            _mapName = serverMap.FolderName;
            (Vector3 serverSpawn, _) = ResolveSpawn(unturnedPath, _mapName, heights: null);
            AddChild(DedicatedServer.Create(unturnedPath, _mapName, serverSpawn, serverPort));
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
                _ = Benchmark.GpuBenchmark.RunAsync(this, unturnedPath, _mapName);
                return;
            }

            Benchmark.BenchmarkRunner.Run(this, unturnedPath, _mapName);
            GetTree().Quit();
            return;
        }

        bool headless = DisplayServer.GetName() == "headless";
        string shot = OS.GetEnvironment("SCREENSHOT_PATH");

        if (!string.IsNullOrEmpty(shot) && OS.GetEnvironment("MENU_SHOT") == "1")
        {
            // Screenshot of the boot menu, no world.
            AddChild(new MainMenu { Name = "MainMenu", UnturnedPath = unturnedPath, InitialMap = _mapName });
            _ = CaptureAndQuit(shot, settleFrames: 10);
            return;
        }

        string environmentDir = EnvironmentDir(unturnedPath, _mapName);
        LevelLighting? lighting = LevelLighting.Load(System.IO.Path.Combine(environmentDir, "Lighting.dat"));

        if (headless || !string.IsNullOrEmpty(shot))
        {
            // Complete synchronous build — headless validation, or a screenshot that must be finished.
            WorldBuildResult world = WorldBuilder.Build(unturnedPath, _mapName);
            AddChild(world.Terrain);
            AddChild(world.Objects);
            AddChild(world.Foliage);
            AddSubsystem("roads", () => RoadsBuilder.Build(environmentDir, world.Heights));
            StandardMaterial3D water = AddWater(lighting);
            AddSubsystem("nodes", () => NodesBuilder.Build(environmentDir));
            RunSubsystem("environment", () => SetupEnvironment(lighting, water, unturnedPath));

            if (headless)
            {
                Log.Print("[unturned-godot] Headless: data loaded, quitting.");
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
            || OS.GetEnvironment("JOIN") is { Length: > 0 }
            || OS.GetEnvironment("MAP") is { Length: > 0 };
        if (autoStart)
        {
            _ = StartInteractiveWorld(unturnedPath, environmentDir, lighting,
                OS.GetEnvironment("JOIN") is { Length: > 0 } join ? join : null);
            return;
        }

        var menu = new MainMenu { Name = "MainMenu", UnturnedPath = unturnedPath, InitialMap = _mapName };
        menu.OnStart = (mapName, joinTarget) =>
        {
            menu.QueueFree();
            _mapName = mapName;
            SaveLastMap(mapName);

            // The map is only known now, so its lighting is read here rather than at boot.
            string mapEnvironment = EnvironmentDir(unturnedPath, mapName);
            LevelLighting? mapLighting =
                LevelLighting.Load(System.IO.Path.Combine(mapEnvironment, "Lighting.dat"));
            _ = StartInteractiveWorld(unturnedPath, mapEnvironment, mapLighting, joinTarget);
        };
        AddChild(menu);
    }

    // STEP_PROBE="x,y,z>x,y,z[,jump]": drive a player-shaped body from one point toward another using the
    // real movement mechanics (MoveAndSlide + PlayerStep) and report where it ends up. This is how "can the
    // player get over that sill" is answered without a human at the keyboard — the capsule, gravity, jump
    // speed and step offset are all the ported constants.
    private async System.Threading.Tasks.Task RunStepProbe(string spec)
    {
        for (int i = 0; i < 120; i++) // let the world finish streaming in
            await NextPhysicsFrame();

        string[] ends = spec.Split('>');
        string[] a = ends[0].Split(',');
        string[] b = ends[1].Split(',');
        bool jump = b.Length > 3 && b[3].Trim() == "jump";
        var start = new Vector3(a[0].ToFloat(), a[1].ToFloat(), a[2].ToFloat());
        var goal = new Vector3(b[0].ToFloat(), b[1].ToFloat(), b[2].ToFloat());

        var body = new CharacterBody3D
        {
            CollisionLayer = 2,
            CollisionMask = 1 | ObjectsBuilder.MediumFurnitureLayer,
            FloorMaxAngle = Mathf.DegToRad(Player.PlayerConfig.MaxWalkableSlopeDegrees),
            FloorSnapLength = 0.5f,
            FloorStopOnSlope = true,
            Position = start,
        };
        body.AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = Player.PlayerConfig.Radius, Height = Player.PlayerConfig.HeightStand },
            Position = Vector3.Up * (Player.PlayerConfig.HeightStand * 0.5f),
        });
        AddChild(body);
        await NextPhysicsFrame();

        float best = Horizontal(start, goal);
        int steps = 0;
        for (; steps < 400; steps++)
        {
            Vector3 flat = new(goal.X - body.GlobalPosition.X, 0f, goal.Z - body.GlobalPosition.Z);
            if (flat.Length() < 0.3f)
                break;

            Vector3 wish = flat.Normalized();
            Vector3 velocity = body.Velocity;
            Vector3 ground = Player.PlayerMovement.GroundVelocity(wish, Player.PlayerConfig.SpeedFor(Player.EPlayerStance.Stand));
            float dt = (float)GetPhysicsProcessDeltaTime();
            if (body.IsOnFloor())
            {
                velocity.X = ground.X;
                velocity.Z = ground.Z;
                velocity.Y = jump ? Player.PlayerConfig.JumpSpeed : -2f;
            }
            else
            {
                velocity = Player.PlayerMovement.AirVelocity(velocity, wish,
                    Player.PlayerConfig.SpeedFor(Player.EPlayerStance.Stand), dt);
            }

            body.Velocity = velocity;
            Vector3 before = body.GlobalPosition;
            body.MoveAndSlide();
            PlayerStep.TryStepUp(body, before, new Vector3(velocity.X, 0f, velocity.Z) * dt);
            best = Mathf.Min(best, Horizontal(body.GlobalPosition, goal));
            await NextPhysicsFrame();
        }

        Vector3 end = body.GlobalPosition;
        Log.Print($"[step] probe {(jump ? "with jump" : "walking")}: ended at " +
            $"({end.X:0.##},{end.Y:0.##},{end.Z:0.##}) after {steps} steps; " +
            $"goal ({goal.X:0.##},{goal.Y:0.##},{goal.Z:0.##}); closest approach {best:0.##} m; " +
            $"{(best < 0.5f ? "REACHED" : "BLOCKED")}");
        // Horizontal only: the walker can be mid-fall when it arrives, and a height difference is not
        // a failure to get there.
        body.QueueFree();
        GetTree().Quit();
    }

    private static float Horizontal(Vector3 a, Vector3 b) =>
        new Vector2(a.X - b.X, a.Z - b.Z).Length();

    private static string EnvironmentDir(string unturnedPath, string mapName) =>
        System.IO.Path.Combine(MapCatalog.ResolvePath(unturnedPath, mapName), "Environment");

    // The edge of the square the map's landscape tiles span, in metres (4096 for PEI's 4x4 tiles).
    private static float MapSpanMetres(string unturnedPath, string mapName)
    {
        MapEntry? map = MapCatalog.Read(MapCatalog.ResolvePath(unturnedPath, mapName), MapSource.Official);
        return map is { SizeMetres: > 0f } ? map.SizeMetres : 4 * Landscape.TILE_SIZE;
    }

    // The map's localized name for the UI, falling back to the folder name.
    private static string MapDisplayName(string unturnedPath, string mapName) =>
        MapCatalog.Read(MapCatalog.ResolvePath(unturnedPath, mapName), MapSource.Official)?.DisplayName
        ?? mapName;

    // Where the player starts on this map: one of its own spawnpoints, else the middle of the terrain.
    // The heightmap, when the terrain is already built, keeps the character above ground either way.
    private static (Vector3 Position, float Yaw) ResolveSpawn(string unturnedPath, string mapName,
        HeightmapSampler? heights)
    {
        string mapDir = MapCatalog.ResolvePath(unturnedPath, mapName);
        Vector3 position;
        float yaw = 0f;

        if (LevelPlayers.Choose(LevelPlayers.Load(mapDir)) is { } spawn)
        {
            position = spawn.Position;
            yaw = spawn.YawDegrees;
        }
        else
        {
            Log.Print($"[unturned-godot] {mapName} ships no player spawnpoints; starting at the centre.");
            position = new Vector3(0, 60, 0);
        }

        if (heights != null && TerrainCoordinates.TrySampleGodotHeight(heights, position.X, position.Z,
            out float ground))
            position.Y = Mathf.Max(position.Y, ground + 0.5f);

        return (position, yaw);
    }

    private static string? LoadLastMap()
    {
        var config = new ConfigFile();
        return config.Load(MenuConfigPath) == Error.Ok
            ? config.GetValue("menu", "map", "").AsString()
            : null;
    }

    private static void SaveLastMap(string mapName)
    {
        var config = new ConfigFile();
        config.Load(MenuConfigPath); // keep whatever else the file holds
        config.SetValue("menu", "map", mapName);
        config.Save(MenuConfigPath);
    }

    // Builds the streamed interactive world behind a loading screen, yielding to the render loop between
    // stages (and inside the heavy ones) so the UI never freezes; joinTarget != null also connects there.
    private async System.Threading.Tasks.Task StartInteractiveWorld(string unturnedPath, string environmentDir,
        LevelLighting? lighting, string? joinTarget)
    {
        _pendingJoin = joinTarget;

        var loading = new LoadingScreen { Name = "LoadingScreen", MapName = MapDisplayName(unturnedPath, _mapName) };
        AddChild(loading);
        await NextFrame(); // paint the loading screen before any heavy work
        long loadStarted = System.Diagnostics.Stopwatch.GetTimestamp();

        // The navigation map goes up next, not before the screen: parsing the pre-baked navmesh and handing
        // it to the NavigationServer takes a couple of seconds on a large map, and doing that first meant
        // the window stayed black for all of it. Its own sync still runs async and finishes behind the
        // world build, so pathfinding is ready on first aggro either way.
        ZombieNavigation.Preload(MapCatalog.ResolvePath(unturnedPath, _mapName));

        // Nothing above the await chain observes this task, so an exception here would otherwise vanish
        // and leave the loading screen spinning forever. Report it on screen and offer the way back.
        try
        {
            await BuildInteractiveWorld(unturnedPath, environmentDir, lighting, loading, loadStarted);
        }
        catch (System.Exception e)
        {
            Log.PrintErr($"[unturned-godot] Failed to load {_mapName}: {e}");
            loading.Fail($"{e.GetType().Name}: {e.Message}", BackToMenu);
        }
    }

    // Returns to the map browser after a failed load, clearing whatever the attempt already built.
    private ObjectStreamer? _activeLoadStreamer;
    private bool _returningToMenu;

    private async void BackToMenu()
    {
        if (_returningToMenu)
            return;
        _returningToMenu = true;

        ObjectStreamer? failedStreamer = _activeLoadStreamer;
        _activeLoadStreamer = null;
        if (failedStreamer != null)
            await failedStreamer.CancelAsync();

        foreach (Node child in GetChildren())
            child.QueueFree();

        // The preloaded navmesh is static, not a child, so freeing the tree does not touch it. Left in
        // place it would be handed to whatever map the player picks next.
        ZombieNavigation.DiscardPreloaded();

        var menu = new MainMenu { Name = "MainMenu", UnturnedPath = _unturnedPath, InitialMap = _mapName };
        menu.OnStart = (mapName, joinTarget) =>
        {
            menu.QueueFree();
            _mapName = mapName;
            SaveLastMap(mapName);
            string mapEnvironment = EnvironmentDir(_unturnedPath, mapName);
            _ = StartInteractiveWorld(_unturnedPath, mapEnvironment,
                LevelLighting.Load(System.IO.Path.Combine(mapEnvironment, "Lighting.dat")), joinTarget);
        };
        AddChild(menu);
        _returningToMenu = false;
    }

    private async System.Threading.Tasks.Task BuildInteractiveWorld(string unturnedPath,
        string environmentDir, LevelLighting? lighting, LoadingScreen loading, long loadStarted)
    {

        var level = new LevelInfo(MapCatalog.ResolvePath(unturnedPath, _mapName));

        // Start the object placement/asset IO now so it runs on a worker while the terrain builds; the
        // streamer joins the tree later, just before Begin().
        var streamer = new ObjectStreamer { Name = "ObjectStreamer" };
        _activeLoadStreamer = streamer;
        streamer.StartPrepare(unturnedPath, level);

        loading.SetStatus("Building terrain…");
        (Node3D terrain, _, HeightmapSampler heights) =
            await WorldBuilder.BuildTerrainAsync(unturnedPath, level, this, streamer.LayerTextures);
        AddChild(terrain);

        loading.SetStatus("Roads and water…");
        await NextFrame();
        AddSubsystem("roads", () => RoadsBuilder.Build(environmentDir, heights));
        StandardMaterial3D waterMat = AddWater(lighting);
        AddSubsystem("nodes", () => NodesBuilder.Build(environmentDir));
        RunSubsystem("environment", () => SetupEnvironment(lighting, waterMat, unturnedPath));

        loading.SetStatus("Character…");
        await NextFrame();
        // Feature flag: FREECAM=1 keeps the fly-through camera; otherwise the player character spawns and
        // walks the map (terrain collision is added on demand so free-cam runs don't pay for it).
        if (OS.GetEnvironment("STEP_PROBE") is { Length: > 0 } stepProbe)
        {
            _ = RunStepProbe(stepProbe); // diagnostic run: no player, no input
        }
        else if (OS.GetEnvironment("FREECAM") == "1")
            AddFreeCamera();
        else
            _player = SpawnPlayer(terrain, thirdPerson: false, unturnedPath, heights);

        loading.SetStatus("World objects…");
        await NextFrame();
        var overlay = new LoadingOverlay { Name = "LoadingOverlay" };
        AddChild(streamer);
        AddChild(overlay);
        overlay.Track(streamer); // connect before Begin so a warm cache's instant signals are caught
        streamer.Finished += loading.Finish; // fade out once the scene (and warm textures) are in
        streamer.Finished += () => _player?.MarkWorldReady();
        streamer.Finished += RunPendingAudioExtraction;
        // Only now do the object colliders exist, so only now can the navmesh be checked against them.
        streamer.Finished += () => _network?.ReconcileNavigation(streamer.NeededGuids);
        if (OS.GetEnvironment("UG_RUNTIME_BENCH_SECS") is { Length: > 0 } duration
            && double.TryParse(duration, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seconds))
        {
            streamer.Finished += () =>
            {
                double loadMs = System.Diagnostics.Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds;
                _ = Benchmark.RuntimeBenchmark.RunAsync(this, _mapName, seconds, loadMs);
            };
        }
        await streamer.BeginAsync();

        // Stays with the load until the world is actually finished, so a failure while realising meshes or
        // building the scene reaches the handler above instead of leaving the screen up for good.
        await streamer.Completion;
        if (ReferenceEquals(_activeLoadStreamer, streamer))
            _activeLoadStreamer = null;
    }

    private async System.Threading.Tasks.Task NextFrame() =>
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    // A kinematic body may only be moved inside the physics step; yielding on the render frame instead
    // makes MoveAndSlide integrate against a stale world and swallow the jump entirely.
    private async System.Threading.Tasks.Task NextPhysicsFrame() =>
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);

    // Set by the main menu's Connect flow (or the JOIN env), consumed by SpawnPlayer.
    private string? _pendingJoin;
    private NetworkManager? _network;

    // One-shot movement-audio extraction, deferred behind the world streamer (see BuildFootsteps).
    private System.Action? _pendingAudioExtraction;
    private PlayerController? _player;

    private void RunPendingAudioExtraction()
    {
        _pendingAudioExtraction?.Invoke();
        _pendingAudioExtraction = null;
    }

    // Spawns the character on one of the map's own player spawnpoints (Spawns/Players.dat) and gives each
    // terrain tile a cheap heightfield collision so it can stand on the ground (vs a 2.1M-triangle concave
    // trimesh). Objects stay non-colliding for now (the player clips buildings), which is fine for movement.
    private PlayerController SpawnPlayer(Node3D terrain, bool thirdPerson, string unturnedPath,
        HeightmapSampler? heights)
    {
        foreach (Node child in terrain.GetChildren())
            if (child is MeshInstance3D tile)
                TerrainBuilder.AddHeightfieldCollision(tile);

        (Vector3 spawnPosition, float spawnYaw) = ResolveSpawn(unturnedPath, _mapName, heights);

        var network = new NetworkManager { Name = "Network" };
        _network = network;
        if (heights != null)
            network.Configure(heights, spawnPosition);
        AddChild(network);

        var player = new PlayerController
        {
            Name = "Player",
            Position = spawnPosition,
            RotationDegrees = new Vector3(0, spawnYaw, 0),
            StartThirdPerson = thirdPerson,
            BodyModel = CharacterModel.Build(unturnedPath), // real Unturned body, or null -> placeholder
        };
        (player.Footsteps, _movementAudioFactory) = BuildMovementAudio(unturnedPath);
        AddChild(player);
        _dayNight?.AttachCamera(player.Camera);

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
            network.HostZombies(MapCatalog.ResolvePath(unturnedPath, _mapName));
        }

        // OPEN_LAN=1 opens the UDP listener immediately; OPEN_LAN_AFTER=seconds opens it mid-game — the
        // timing of a player pressing the pause-menu button after already moving (e2e scripts use both).
        // QUIT_AFTER=seconds: exercises the same GetTree().Quit() the pause menu's button calls, from the
        // full gameplay path (player, session, zombies, background extraction), so shutdown can be timed
        // and its console output inspected without a human at the keyboard.
        if (OS.GetEnvironment("QUIT_AFTER") is { Length: > 0 } quitAfter)
            GetTree().CreateTimer(quitAfter.ToFloat()).Timeout += () =>
            {
                Log.Print("[shutdown] quit requested");
                AppShutdown.RequestQuit(GetTree());
            };

        if (OS.GetEnvironment("OPEN_LAN") == "1")
            network.OpenToLan(NetworkManager.DefaultPort);
        else if (OS.GetEnvironment("OPEN_LAN_AFTER") is { Length: > 0 } delay)
            GetTree().CreateTimer(delay.ToFloat()).Timeout += () => network.OpenToLan(NetworkManager.DefaultPort);
        AttachSession(network, player, unturnedPath);

        if (DisplayServer.GetName() != "headless")
            AddChild(new PauseMenu { Name = "PauseMenu", Network = network, OnSessionStarted = () => AttachSession(network, player, unturnedPath) });
        return player;
    }

    // Once a session exists (hosted or joined), wire the input sender and the remote-player view.
    private void AttachSession(NetworkManager network, PlayerController player, string unturnedPath)
    {
        if (network.Client == null || player.Net != null)
            return;
        player.Net = network.Client;
        AddChild(RemotePlayersView.Create(network.Client, unturnedPath, _movementAudioFactory,
            player.BodyModel));
        // The zombies view tracks the LOCAL player's nav bound (Player.PlayerMovement.updateBounds runs client-
        // side in the original too) to drop the avatars of a region it leaves.
        var navBounds = LevelNavigationData.Load(
            EnvironmentDir(unturnedPath, _mapName));
        var zombiesView = ZombiesView.Create(network.Client, unturnedPath, _oneShotAudio,
            navBounds, () => player.GlobalPosition);
        AddChild(zombiesView);
        zombiesView.WarmupTemplates(); // still behind LoadingScreen; never import on a city-entry packet
    }

    // One MovementAudio per character; remote avatars get theirs from this factory (RemotePlayersView).
    private System.Func<MovementAudio>? _movementAudioFactory;
    private OneShotAudio? _oneShotAudio; // the shared positional voice pool (zombie roars use it too)

    // Movement audio infrastructure: the physics-material bank + terrain splat sampler resolve WHICH
    // definition a step plays; the shared AudioDefLibrary + positional OneShotAudio pool play it. The
    // referenced OneShotAudioDefinitions extract from the masterbundle in the background on first run.
    private (MovementAudio Local, System.Func<MovementAudio> Factory) BuildMovementAudio(string unturnedPath)
    {
        // The game's assets and every installed workshop mod's: a workshop map's terrain layers and
        // surfaces are defined by its own mod, and without them its ground has no footstep sound at all.
        System.Collections.Generic.IReadOnlyList<ContentSource> sources =
            ContentSource.Discover(unturnedPath);
        var assetRoots = new System.Collections.Generic.List<string>();
        foreach (ContentSource source in sources)
            assetRoots.Add(source.AssetsDir);

        PhysicsMaterialBank bank = PhysicsMaterialBank.ScanDirectories(
            assetRoots.ConvertAll(r => System.IO.Path.Combine(r, "PhysicsMaterials")));
        LandscapePhysics landscape = LandscapePhysics.ScanDirectories(
            assetRoots.ConvertAll(r => System.IO.Path.Combine(r, "Landscapes")));
        string bundlesAssets = System.IO.Path.Combine(unturnedPath, "Bundles", "Assets");

        var level = new LevelInfo(MapCatalog.ResolvePath(unturnedPath, _mapName));
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
        string bundlePath = UnturnedInstall.MasterBundlePath(unturnedPath);

        // Grouped by the bundle that carries them: a workshop map can define its own surfaces, and their
        // definitions are packaged in the mod's bundle. Asking only the game's bundle for those left the
        // new surfaces silent — a material that falls back to a core one still resolves to the core
        // bundle, because the fallback asset is the one that defines the event.
        var defPathsByBundle =
            new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>>(
                System.StringComparer.Ordinal);
        // Every name the bank knows, not the game's ten base surfaces: a workshop landscape can name its
        // own material, and one the extraction never visited was resolvable at runtime but absent from
        // the audio cache, so that ground was silent. Definitions are shared between materials, so the
        // set of paths this produces is barely larger than the built-in one.
        foreach (string key in new[] { "FootstepWalk", "FootstepRun", "BipedLand" })
            foreach (string name in bank.Names)
            {
                if (bank.FindAudioDef(name, key) is not { } def)
                    continue;

                string owner = SourceForAssetDirectory(sources, def.Owner.Directory)?.BundlePath
                    ?? bundlePath;
                if (owner.Length == 0)
                    continue;

                if (!defPathsByBundle.TryGetValue(owner,
                    out System.Collections.Generic.HashSet<string>? paths))
                {
                    defPathsByBundle[owner] = paths = new System.Collections.Generic.HashSet<string>();
                }

                paths.Add(def.Path);
            }

        if (!defPathsByBundle.ContainsKey(bundlePath))
            defPathsByBundle[bundlePath] = new System.Collections.Generic.HashSet<string>();
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
        // Each bundle's definitions are cached under that bundle's tag, so two bundles naming one the same
        // thing stay apart — and the parallel passes never write each other's directory. The zombie clip
        // groups only exist in the game's own bundle, so they ride along with its pass.
        string TagOf(string bundle) => UnturnedGodot.Unity.TextureKey.TagFor(
            BundleNameOf(sources, bundle) ?? System.IO.Path.GetFileNameWithoutExtension(bundle));

        _pendingAudioExtraction = () =>
        {
            foreach ((string bundle, System.Collections.Generic.HashSet<string> paths) in defPathsByBundle)
            {
                System.Collections.Generic.List<AudioExtractor.RawClipGroup>? groups =
                    bundle == bundlePath ? clipGroups : null;
                string tag = TagOf(bundle);
                AppShutdown.Track(System.Threading.Tasks.Task.Run(
                    () => AudioExtractor.Extract(bundle, tag, paths, audioCacheDir, groups)));
            }
        };

        Log.Print($"[audio] footsteps ready: {bank.Count} physics materials, {landscape.Count} landscape " +
            $"materials, {splat.TileCount} splat tiles");

        var oneShot = OneShotAudio.Create(new AudioDefLibrary(audioCacheDir));
        AddChild(oneShot);
        _oneShotAudio = oneShot;
        string BundleTagOfDirectory(string directory) =>
            UnturnedGodot.Unity.TextureKey.TagFor(SourceForAssetDirectory(sources, directory)?.Name
                ?? System.IO.Path.GetFileNameWithoutExtension(bundlePath));

        MovementAudio Factory(bool startGrounded) =>
            new(bank, landscape, splat, oneShot, startGrounded, BundleTagOfDirectory);
        return (Factory(startGrounded: false), () => Factory(startGrounded: true));
    }

    // The masterbundle of the content source whose assets folder holds `directory`, falling back to the
    // game's own bundle for anything unattributed. A source that ships no bundle has nothing to extract
    // from, and returning "" drops those definitions rather than looking for them in the wrong file.
    // The name a bundle's own MasterBundle.dat gives it, which is what the cache tag is derived from: the
    // FILE name carries a platform suffix and would key the same content differently per platform.
    private static string? BundleNameOf(
        System.Collections.Generic.IReadOnlyList<ContentSource> sources, string bundlePath)
    {
        foreach (ContentSource source in sources)
            if (string.Equals(source.BundlePath, bundlePath, System.StringComparison.Ordinal))
                return source.Name;

        return null;
    }

    private static ContentSource? SourceForAssetDirectory(
        System.Collections.Generic.IReadOnlyList<ContentSource> sources, string directory)
    {
        if (directory.Length == 0)
            return null;

        foreach (ContentSource source in sources)
            if (source.Owns(directory))
                return source;

        return null;
    }

    // The day/night cycle owns the sun-shaft pass, which has to render in front of whichever camera is
    // live; both camera paths hand theirs over here.
    private DayNightController? _dayNight;

    private void AddFreeCamera()
    {
        var camera = new FreeCamera { Name = "FreeCamera" };
        AddChild(camera);
        _dayNight?.AttachCamera(camera);
        camera.Position = new Vector3(0, 300, 0); // above map center, looking down
        camera.RotationDegrees = new Vector3(-60, 0, 0);
    }

    // Render a few frames before grabbing the framebuffer so meshes are drawn (and, in player mode, so the
    // character settles onto the terrain). SHOT_CAM only applies to the free camera.
    private async System.Threading.Tasks.Task CaptureAndQuit(string path, int settleFrames)
    {
        if (GetNodeOrNull<FreeCamera>("FreeCamera") is { } cam)
        {
            // A high three-quarter view of the whole map. The offsets are PEI's framing expressed as
            // fractions of its 4 km span, so every map is framed the same way at its own scale.
            float span = MapSpanMetres(_unturnedPath, _mapName);
            cam.Position = new Vector3(-0.0625f * span, 0.22f * span, 0.171f * span);
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
        Log.Print($"[unturned-godot] Screenshot saved: {path}");
        GetTree().Quit();
    }

    // Sun + sky/ambient from the map lighting, plus the debug overlay (windowed only). The camera/player is
    // added separately by the caller so the free-cam and character paths can differ.
    private void SetupEnvironment(LevelLighting? lighting, StandardMaterial3D waterMaterial, string unturnedPath)
    {
        _dayNight = DayNightController.Build(lighting, waterMaterial, SkyboxAssets.Load(unturnedPath));
        AddChild(_dayNight);

        if (DisplayServer.GetName() != "headless")
            AddChild(new DebugOverlay { Name = "DebugOverlay" });
    }

    // Builds one optional part of the world and attaches it, or logs why it could not be built and carries
    // on. Roads, water, nodes and the sky are independent of each other and of the terrain: a map whose
    // per-map bundle uses a format this project cannot read yet (some official maps ship a SerializedFile
    // version the parser rejects) used to lose the whole rest of the boot to that one throw.
    private void AddSubsystem(string what, System.Func<Node> build)
    {
        try
        {
            AddChild(build());
        }
        catch (System.Exception e)
        {
            Log.PrintErr($"[unturned-godot] {what} unavailable, continuing without it " +
                $"({e.GetType().Name}: {e.Message})");
        }
    }

    // Same, for a step that attaches its own nodes rather than returning one.
    private void RunSubsystem(string what, System.Action build)
    {
        try
        {
            build();
        }
        catch (System.Exception e)
        {
            Log.PrintErr($"[unturned-godot] {what} unavailable, continuing without it " +
                $"({e.GetType().Name}: {e.Message})");
        }
    }

    // Water is the one subsystem another one reads back from: the day/night cycle tints its material as the
    // sun moves. On failure the cycle still gets a material to drive, it just is not attached to anything.
    private StandardMaterial3D AddWater(LevelLighting? lighting)
    {
        var material = new StandardMaterial3D();
        AddSubsystem("water", () =>
        {
            Node water = WaterBuilder.Build(lighting, out StandardMaterial3D built);
            material = built;
            return water;
        });
        return material;
    }
}
