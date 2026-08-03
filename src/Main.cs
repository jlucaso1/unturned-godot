using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Net;

namespace UnturnedGodot;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class Main : Node3D
{
    // The map folder under Maps/ (or a workshop item) that this run loads. The menu picks it; MAP=
    // overrides for automation, and the last pick is remembered between sessions.
    private const string DefaultMapName = "PEI";
    private const float DefaultMeshLodThreshold = 2f;
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
        // Godot's own automatic mesh LOD, in pixels of screen-space error. 0 disables it outright, which
        // is the only way to A/B what the meshoptimizer chain ModelLibrary already generates is worth.
        //
        // Above the engine default because the engine's is chosen to be lossless on any content, and
        // this content is far more forgiving than that: its meshes are low-poly and untextured-flat, so
        // the levels differ mostly in silhouette. A step up sheds a useful share of the geometry a
        // wooded map submits at eye level with no difference the eye finds at the horizon, where the
        // levels actually swap. Further steps stop being free — the distant tree canopies visibly thin
        // — and buy progressively less, so this sits at the last value that still costs nothing.
        float thresholdPixels = DefaultMeshLodThreshold;
        if (OS.GetEnvironment("UG_MESH_LOD_THRESHOLD") is { Length: > 0 } lodThreshold
            && float.TryParse(lodThreshold, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float requestedThreshold)
            && float.IsFinite(requestedThreshold)) // TryParse accepts "NaN"/"Infinity", Clamp keeps NaN
        {
            thresholdPixels = requestedThreshold;
        }
        if (GetViewport() is { } viewport)
            viewport.MeshLodThreshold = Mathf.Clamp(thresholdPixels, 0f, 1024f);

        if (Benchmark.MemoryTrace.Create() is { } memoryTrace)
            AddChild(memoryTrace);

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
        if (EnvFlag.IsOn(OS.GetEnvironment("CHAR_ONLY"), whenUnset: false) && !string.IsNullOrEmpty(shotOnly))
        {
            if (CharacterModel.Build(unturnedPath) is { } model)
            {
                if (model is CharacterSkeleton rig && OS.GetEnvironment("CHAR_STANCE") is { Length: > 0 } s)
                {
                    rig.SetState(System.Enum.Parse<Player.EPlayerStance>(s, ignoreCase: true),
                        EnvFlag.IsOn(OS.GetEnvironment("CHAR_MOVING"), whenUnset: false));
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
            float side = EnvFlag.IsOn(OS.GetEnvironment("CHAR_BACK"), whenUnset: false) ? 3.2f : -3.2f; // -Z is the front
            cam.Position = EnvFlag.IsOn(OS.GetEnvironment("CHAR_SIDE"), whenUnset: false)
                ? new Vector3(4.0f, 1.0f, 0f)  // side profile, for reading prone/lie-down poses
                : new Vector3(0, 1.1f, side);  // full-body 3/4-front, so any stance is framed
            cam.LookAt(new Vector3(0, 0.5f, 0));
            int settle = OS.GetEnvironment("CHAR_SETTLE") is { Length: > 0 } sf ? int.Parse(sf) : 5;
            _ = CaptureAndQuit(shotOnly, settle); // more settle frames -> the animation advances further
            return;
        }

        string[] userArgs = OS.GetCmdlineUserArgs();
        UncapFramesForMeasurement(userArgs);

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

            _mapName = serverMap.SelectionKey;
            (Vector3 serverSpawn, _) = ResolveSpawn(unturnedPath, _mapName, heights: null);
            AddChild(DedicatedServer.Create(unturnedPath, _mapName, serverMap.FolderName, serverSpawn,
                serverPort));
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

        // Memory attribution: without this, the only session that runs streaming, navigation, physics and
        // the netcode is also the only one that runs a rendering driver, so their costs cannot be told
        // apart. UG_HEADLESS_INTERACTIVE=1 runs the normal interactive path with no driver at all, and
        // differencing the two answers how much of RSS is the renderer. A screenshot still wins, since it
        // needs something drawn.
        bool headlessInteractive = headless && EnvFlag.IsOn(OS.GetEnvironment("UG_HEADLESS_INTERACTIVE"), whenUnset: false)
            && string.IsNullOrEmpty(shot);
        _headlessInteractive = headlessInteractive;
        if (headlessInteractive)
            Log.Print("[unturned-godot] Headless interactive: no rendering driver; "
                + "view-dependent metrics (draw calls, primitives, frame time) do not apply.");

        if (!string.IsNullOrEmpty(shot) && EnvFlag.IsOn(OS.GetEnvironment("MENU_SHOT"), whenUnset: false))
        {
            // Screenshot of the boot menu, no world.
            AddChild(new MainMenu { Name = "MainMenu", UnturnedPath = unturnedPath, InitialMap = _mapName });
            _ = CaptureAndQuit(shot, settleFrames: 10);
            return;
        }

        string environmentDir = EnvironmentDir(unturnedPath, _mapName);
        LevelLighting? lighting = LevelLighting.Load(System.IO.Path.Combine(environmentDir, "Lighting.dat"));

        if ((headless && !headlessInteractive) || !string.IsNullOrEmpty(shot))
        {
            // Complete synchronous build — headless validation, or a screenshot that must be finished.
            WorldBuildResult world = WorldBuilder.Build(unturnedPath, _mapName);
            AddChild(world.Terrain);
            AddChild(world.Objects);
            AddChild(world.Vehicles);
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
            if (EnvFlag.IsOn(OS.GetEnvironment("PLAYER"), whenUnset: false))
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
        // A headless interactive session has no menu to click, so it always boots into the world.
        bool autoStart = headlessInteractive
            || EnvFlag.IsOn(OS.GetEnvironment("SOLO"), whenUnset: false)
            || EnvFlag.IsOn(OS.GetEnvironment("FREECAM"), whenUnset: false) || EnvFlag.IsOn(OS.GetEnvironment("OPEN_LAN"), whenUnset: false)
            || OS.GetEnvironment("OPEN_LAN_AFTER") is { Length: > 0 }
            || OS.GetEnvironment("JOIN") is { Length: > 0 }
            || OS.GetEnvironment("MAP") is { Length: > 0 };
        if (autoStart)
        {
            _ = StartInteractiveWorld(unturnedPath,
                OS.GetEnvironment("JOIN") is { Length: > 0 } join ? join : null);
            return;
        }

        var menu = new MainMenu { Name = "MainMenu", UnturnedPath = unturnedPath, InitialMap = _mapName };
        menu.OnStart = (mapName, joinTarget) =>
        {
            menu.QueueFree();
            // Joining carries no map: the server's answer decides it, so the remembered pick stays
            // untouched — including when the join never gets off the ground and we come back here.
            if (joinTarget == null)
            {
                _mapName = mapName;
                SaveLastMap(mapName);
            }
            _ = StartInteractiveWorld(unturnedPath, joinTarget);
        };
        AddChild(menu);
    }

    // STEP_PROBE="x,y,z>x,y,z[,jump]": drive a player-shaped body from one point toward another using the
    // real movement mechanics (MoveAndSlide + PlayerStep) and report where it ends up. This is how "can the
    // player get over that sill" is answered without a human at the keyboard — the capsule, gravity, jump
    // speed and step offset are all the ported constants.
    private async System.Threading.Tasks.Task RunStepProbe(string spec)
    {
        // The caller starts this from ObjectStreamer.MeshesReady, after BuildObjects attached every
        // collider. Cross one physics boundary so PhysicsServer has incorporated those new bodies before
        // the first MoveAndSlide; a fixed frame delay raced cold/large bundles and measured bare terrain.
        await NextPhysicsFrame();

        string[] ends = spec.Split('>');
        string[] a = ends[0].Split(',');
        string[] b = ends[1].Split(',');
        bool jump = b.Length > 3 && b[3].Trim() == "jump";
        var start = new Vector3(a[0].ToFloat(), a[1].ToFloat(), a[2].ToFloat());
        var goal = new Vector3(b[0].ToFloat(), b[1].ToFloat(), b[2].ToFloat());

        var body = new CharacterBody3D
        {
            CollisionLayer = CollisionLayers.Player,
            CollisionMask = CollisionLayers.CharacterMask,
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
        AppShutdown.RequestQuit(GetTree());
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

    // How this map is named ON THE WIRE: its folder name, the one identity two machines can agree on.
    // A selection key can be a workshop path that exists nowhere else, and a display name is localized.
    //
    // Folder names are not globally unique — two workshop items can ship "Ireland" — so this cannot
    // prove the two ends hold the SAME content, only that they agree on which map they mean. A
    // stronger identity would have to be a content signature: the workshop item id is not it, since
    // the same map reached through a subscription on one machine and the game's bundled copy on the
    // other carries different ids and would refuse a join that is perfectly fine. Unturned itself
    // keys servers by map name for the same reason. What this does close is the reported failure —
    // two different maps, silently played at once.
    private static string LevelIdentity(string unturnedPath, string mapName) =>
        MapCatalog.Find(unturnedPath, mapName)?.FolderName ?? mapName;

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
    private async System.Threading.Tasks.Task StartInteractiveWorld(string unturnedPath, string? joinTarget)
    {
        _pendingJoin = joinTarget;

        var loading = new LoadingScreen
        {
            Name = "LoadingScreen",
            MapName = joinTarget == null ? MapDisplayName(unturnedPath, _mapName) : "",
        };
        AddChild(loading);
        await NextFrame(); // paint the loading screen before any heavy work
        long loadStarted = System.Diagnostics.Stopwatch.GetTimestamp();

        // Joining: the SERVER decides which map is played, so ask it before building anything. The
        // client used to build whatever the map browser had selected and connect regardless, which is
        // how a player joined a PEI host and spawned into California 2 — same session, two worlds.
        if (joinTarget != null && !await AdoptServerMap(unturnedPath, joinTarget, loading))
            return;

        string environmentDir = EnvironmentDir(unturnedPath, _mapName);
        LevelLighting? lighting = LevelLighting.Load(System.IO.Path.Combine(environmentDir, "Lighting.dat"));

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
            if (_headlessInteractive)
            {
                // The way back is a button, and this session has neither a display to draw it on nor an
                // input to press it with. Reporting on screen would leave a benchmark waiting on a
                // loading screen that can never resolve, so fail the process instead.
                GetTree().Quit(1);
                return;
            }
            loading.Fail($"{e.GetType().Name}: {e.Message}", BackToMenu);
        }
    }

    // Asks the server which level it runs and points this session at that map. False means the join is
    // over before it started (nobody answered, or the map is not installed here) — the loading screen
    // already says why and offers the way back.
    private async System.Threading.Tasks.Task<bool> AdoptServerMap(string unturnedPath, string joinTarget,
        LoadingScreen loading)
    {
        (string host, ushort port) = ParseJoinTarget(joinTarget);
        loading.SetStatus($"Asking {host}:{port} which map it is running…");

        ServerInfo? info;
        try
        {
            info = await QueryServer(host, port);
        }
        catch (System.Exception e) when (e is System.Net.Sockets.SocketException or System.ArgumentException)
        {
            // An unresolvable host never gets as far as a datagram.
            return FailJoin(loading, $"Could not reach {host}:{port} — {e.Message}");
        }

        if (info == null)
            return FailJoin(loading, $"No server answered at {host}:{port}.");

        // Everything the handshake would refuse us for is already knowable here, and finding out now
        // costs seconds instead of a full world build followed by a kick.
        if (info.ProtocolVersion != NetMessages.ProtocolVersion)
        {
            return FailJoin(loading, $"That server speaks protocol {info.ProtocolVersion} and this "
                + $"build speaks {NetMessages.ProtocolVersion}: one of them needs updating.");
        }
        if (info.FreeSlots == 0)
            return FailJoin(loading, $"That server is full ({info.PlayerCount} players).");

        // The wire carries the map's FOLDER name; resolve it against what is installed here, exactly
        // as the map browser and the command line do.
        MapEntry? map = MapCatalog.Find(unturnedPath, info.Level);
        if (map == null)
        {
            return FailJoin(loading,
                $"That server is running '{info.Level}', which is not installed on this machine.");
        }
        if (!map.IsSupported)
        {
            return FailJoin(loading, $"That server is running '{map.DisplayName}', whose terrain format "
                + "this port cannot read yet.");
        }

        _mapName = map.SelectionKey;
        loading.SetMap(map.DisplayName);
        Log.Print($"[net] {host}:{port} is running '{info.Level}' ({info.PlayerCount} online); loading it");
        return true;
    }

    // Always false, so the callers can `return FailJoin(...)`: the join is over either way.
    private bool FailJoin(LoadingScreen loading, string message)
    {
        Log.PrintErr($"[net] could not join: {message}");
        if (_headlessInteractive)
        {
            // No display to show the screen on and no input to press its button with; the same reason
            // a failed world build ends the process in this mode.
            GetTree().Quit(1);
            return false;
        }
        loading.Fail(message, BackToMenu, "Could not join.");
        return false;
    }

    // Runs the pre-join query on its own short-lived socket, pumped from the frame loop. The session's
    // real connection is opened later, once the world is built: holding this one open through a load
    // that takes minutes would only have the server time it out.
    private async System.Threading.Tasks.Task<ServerInfo?> QueryServer(string host, ushort port)
    {
        var transport = new UdpClientTransport(host, port);
        try
        {
            var query = new ServerQuery(transport);
            while (query.State == EServerQueryState.Pending)
            {
                if (AppShutdown.IsShuttingDown)
                    return null;
                query.Update(NetworkManager.Now);
                await NextFrame();
            }
            return query.Info;
        }
        finally
        {
            transport.Close();
        }
    }

    // "host", "host:port" or an IPv4 address with either shape.
    private static (string Host, ushort Port) ParseJoinTarget(string target)
    {
        string[] parts = target.Split(':');
        return (parts[0],
            parts.Length > 1 && ushort.TryParse(parts[1], out ushort port) ? port : NetworkManager.DefaultPort);
    }

    // The server hung up on us. The join flow asks which map to build before connecting, so reaching
    // here means something changed underneath (the host switched maps, filled up, or runs another
    // build): say so and offer the way back, rather than leaving the player alone in a loaded world.
    private void ShowJoinRefused(JoinRejection rejection)
    {
        if (_joinRefused)
            return;
        _joinRefused = true; // one screen per session; BackToMenu clears it for the next attempt

        if (_headlessInteractive)
        {
            // Same reason FailJoin quits: no display for the screen, no input for its button. A
            // benchmark would otherwise wait on an overlay nothing can ever dismiss.
            Log.PrintErr($"[net] the server refused the join: {NetworkManager.Describe(rejection)}");
            GetTree().Quit(1);
            return;
        }

        // The player was walking when this arrived, so the cursor is captured and the button under it
        // is unclickable. Release it, exactly as the menu and the pause screen do.
        Input.MouseMode = Input.MouseModeEnum.Visible;
        var screen = new LoadingScreen { Name = "JoinRefused" };
        AddChild(screen);
        screen.Fail(NetworkManager.Describe(rejection), BackToMenu, "The server refused the join:");
    }

    // Returns to the map browser after a failed load, clearing whatever the attempt already built.
    private ObjectStreamer? _activeLoadStreamer;
    private bool _returningToMenu;
    private bool _joinRefused;
    private bool _headlessInteractive;

    private async void BackToMenu()
    {
        if (_returningToMenu)
            return;
        _returningToMenu = true;

        ObjectStreamer? failedStreamer = _activeLoadStreamer;
        _activeLoadStreamer = null;
        _joinRefused = false; // the next attempt gets its own refusal screen
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
            if (joinTarget == null)
            {
                _mapName = mapName;
                SaveLastMap(mapName);
            }
            _ = StartInteractiveWorld(_unturnedPath, joinTarget);
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
        string? stepProbe = OS.GetEnvironment("STEP_PROBE") is { Length: > 0 } probe ? probe : null;
        // STEP_PROBE is a diagnostic run with no player/input. It starts from MeshesReady below rather
        // than from a guessed delay, so it always sees the object collision world it measures.
        if (stepProbe == null && EnvFlag.IsOn(OS.GetEnvironment("FREECAM"), whenUnset: false))
            AddFreeCamera();
        else if (stepProbe == null)
            _player = SpawnPlayer(terrain, thirdPerson: false, unturnedPath, heights);

        loading.SetStatus("World objects…");
        await NextFrame();
        var overlay = new LoadingOverlay { Name = "LoadingOverlay" };
        AddChild(streamer);
        AddChild(overlay);
        overlay.Track(streamer); // connect before Begin so a warm cache's instant signals are caught
        streamer.Finished += loading.Finish; // fade out once the scene (and warm textures) are in
        streamer.Finished += () => _player?.MarkWorldReady();
        // ExtractionFinished, not Finished: this may open a bundle, and a warm mesh cache with cold
        // terrain layers finishes the scene while its pass is still decoding the very bundle this would
        // reach for. Waiting also means the pass has had its chance to extract these definitions itself,
        // which on the cold path leaves this with nothing to do at all.
        streamer.ExtractionFinished += RunPendingAudioExtraction;
        if (stepProbe != null)
            streamer.MeshesReady += elapsedMs => _ = RunStepProbe(stepProbe);
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

        var network = new NetworkManager { Name = "Network", LevelName = LevelIdentity(unturnedPath, _mapName) };
        _network = network;
        network.OnRejected += ShowJoinRefused;
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
            (string host, ushort port) = ParseJoinTarget(join);
            network.JoinServer(host, port, playerName);
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

        if (EnvFlag.IsOn(OS.GetEnvironment("OPEN_LAN"), whenUnset: false))
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

        PhysicsMaterialBank bank = MovementAudioRequests.ScanPhysicsMaterials(sources);
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

        // What each bundle owes the audio cache, planned exactly as the object streamer plans it for its
        // decode pass — the cold load extracts these while it is already walking the bundle, so on that
        // path this fallback finds every definition cached and opens nothing.
        System.Collections.Generic.List<AudioExtractor.Request> audioRequests =
            MovementAudioRequests.For(sources, bank, unturnedPath, audioCacheDir);

        // Deferred until the world streamer finishes: what the pass did not cover (an unstreamable bundle,
        // a pass that was cancelled, or a load whose meshes were already cached and therefore ran no pass)
        // still has to be fetched, and racing a second whole-bundle decode against the first doubles peak
        // CPU/memory and can stall weak machines. Whole-bundle passes remain serial for the same reason.
        _pendingAudioExtraction = () => AppShutdown.Track(System.Threading.Tasks.Task.Run(() =>
        {
            bool decoded = false;
            foreach (AudioExtractor.Request request in audioRequests)
            {
                if (AppShutdown.IsShuttingDown)
                    break;
                if (request.BundlePath.Length == 0 || AudioExtractor.IsSatisfied(request))
                    continue;

                // Marked before the attempt, not after it: a decode that threw part-way still grew the
                // heap, so the reclaim below is owed either way.
                decoded = true;
                try
                {
                    AudioExtractor.Extract(request.BundlePath, request.BundleTag, request.DefPaths,
                        request.AudioCacheDir, request.ClipGroups);
                }
                catch (System.Exception e) when (e is System.IO.IOException
                    or System.UnauthorizedAccessException or System.IO.InvalidDataException)
                {
                    // Per bundle. The order here comes from a dictionary, so one truncated workshop
                    // bundle used to be able to end the loop before the game's own footsteps and zombie
                    // clips were ever extracted.
                    Log.PrintErr($"[audio] {request.BundlePath} could not be extracted: {e.Message}");
                }
            }

            // A whole-bundle decode here lands AFTER the streamer has already compacted the load's heap,
            // so nothing else ever gives its transient back: the collector keeps the segments it grew for
            // it, and RSS stays hundreds of megabytes above a warm session's for the rest of the game.
            // Compact once when this one-time work is done, off the frame loop, like the loader does.
            if (decoded && !AppShutdown.IsShuttingDown)
                LoadMemory.Reclaim("audio extraction");
        }));

        Log.Print($"[audio] footsteps ready: {bank.Count} physics materials, {landscape.Count} landscape " +
            $"materials, {splat.TileCount} splat tiles");

        var oneShot = OneShotAudio.Create(new AudioDefLibrary(audioCacheDir));
        AddChild(oneShot);
        _oneShotAudio = oneShot;
        string BundleTagOfDirectory(string directory) =>
            SourceForAssetDirectory(sources, directory)?.CacheTag
            ?? UnturnedGodot.Unity.TextureKey.TagFor(System.IO.Path.GetFileNameWithoutExtension(bundlePath));

        MovementAudio Factory(bool startGrounded) =>
            new(bank, landscape, splat, oneShot, startGrounded, BundleTagOfDirectory);
        return (Factory(startGrounded: false), () => Factory(startGrounded: true));
    }

    // The content source whose assets folder holds `directory`, which is how a scanned asset is traced
    // back to the bundle its content lives in. Shared with the audio planning, so a definition and the
    // clips extracted for it are always attributed to the same bundle.
    private static ContentSource? SourceForAssetDirectory(
        System.Collections.Generic.IReadOnlyList<ContentSource> sources, string directory)
        => MovementAudioPlan.SourceForAssetDirectory(sources, directory);

    // The day/night cycle owns the sun-shaft pass, which has to render in front of whichever camera is
    // live; both camera paths hand theirs over here.
    private DayNightController? _dayNight;

    // Measurement runs need frames as fast as the machine can produce them; players do not. project.godot
    // used to ship vsync OFF so the benchmarks would be uncapped, which meant every player also got an
    // uncapped renderer — a menu spinning the GPU at several hundred FPS, and tearing, as the default.
    //
    // The requirement belongs to the benchmark, so the benchmark asks for it. Turning it off here rather
    // than in the config keeps the shipped default honest without making the numbers in bench/ and
    // docs/PROFILING.md quietly refresh-rate-limited instead — which is the failure that would have made
    // this change worse than leaving it alone: not a crash, just numbers that stop meaning anything.
    //
    // Headless has no window to set a mode on, and no frames to pace either, so it is left alone.
    private static void UncapFramesForMeasurement(string[] userArgs)
    {
        bool measuring = System.Array.IndexOf(userArgs, "--benchmark") >= 0
            || OS.GetEnvironment("UG_RUNTIME_BENCH_SECS") is { Length: > 0 };
        if (!measuring || DisplayServer.GetName() == "headless")
            return;

        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
    }

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
        if (EnvFlag.IsOn(OS.GetEnvironment("PLAYER_FRONT"), whenUnset: false) && GetNodeOrNull<Node3D>("Player") is { } player)
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
        AppShutdown.RequestQuit(GetTree());
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
