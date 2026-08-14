using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;

namespace UnturnedGodot;

// Standalone dedicated server: `godot --headless -- --server [--port=27015]`. Loads only what the
// authoritative movement sim needs (heightfields plus placed-object collision) and runs NetServer over
// raw UDP — no rendering and no local player.
public partial class DedicatedServer : Node
{
    private NetServer _server = null!;
    private IServerTransport _transport = null!;

    // levelName is what the handshake compares (the map's folder name), which is not always mapName:
    // a workshop selection key carries an absolute path that means nothing on the joining machine.
    public static DedicatedServer Create(string unturnedPath, string mapName, string levelName, Vector3 spawn,
        ushort port)
    {
        // Through the catalog, not Maps/<name>: a workshop map lives in the workshop content folder, and
        // hard-coding the shipped location left the server advertising that map while loading no terrain
        // and no zombies from it. The client's spawn resolution already goes through the same lookup.
        var level = new LevelInfo(MapCatalog.ResolvePath(unturnedPath, mapName));
        var tiles = new List<HeightmapTile>();
        foreach ((int x, int y) in level.EnumerateTiles())
            tiles.Add(HeightmapTile.Read(level.HeightmapPath(x, y), x, y));
        var heights = new HeightmapSampler(tiles);
        GroundSampler ground = (float x, float z, out float y) => heights.TrySampleHeight(x, -z, out y);

        var transport = new UdpServerTransport(port);
        var node = new DedicatedServer
        {
            Name = "DedicatedServer",
            _transport = transport,
            _server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(ground)), spawn,
                levelName),
        };

        // Static bodies must exist in the same World3D the authoritative zombie queries use. The root is
        // attached before this node enters the tree; ZombiePhysics resolves the world lazily on first tick.
        var navigationField = new CollisionFieldBuilder();
        node.AddChild(WorldBuilder.BuildTerrainCollision(tiles, navigationField));
        node.AddChild(WorldBuilder.BuildObjectCollision(unturnedPath, level,
            out IReadOnlySet<System.Guid> selectedGuids,
            out UnturnedGodot.Damage.DamageableWorld damageable, navigationField));

        // LightingManager runs on a dedicated server too — it draws nothing, but isNighttime and
        // isFullMoon are GAMEPLAY (the night-only Dying Light volatiles, and the hyper population a full
        // moon makes). This path used to omit both, so the same map that spawned volatiles on a listen
        // server at dusk spawned none here, and a full-moon session was never hyper. The clock starts
        // where the map was saved and advances with the tick, exactly as the windowed session's does.
        node._clock = ServerClock(level);

        // The map's own ZombieDifficultyAssets, so its bounds and tables roll their own speciality
        // weights rather than the mode config's — plus the operator's Config.json, which decides the
        // population density, the fallback weights and the swing damage.
        System.Collections.Generic.IReadOnlyList<UnturnedGodot.Assets.ContentSource> sources =
            UnturnedGodot.Assets.ContentSource.Discover(unturnedPath);
        UnturnedGodot.Zombies.ZombieSystem? zombies = UnturnedGodot.Zombies.ZombieWorld.Load(
            level.Path, ground,
            UnturnedGodot.Repro.ReproRandom.ForSession(OS.GetEnvironment("ZOMBIE_SEED"), out _),
            difficulties: UnturnedGodot.Assets.ZombieDifficultyBank.ScanContentSources(sources),
            mode: NetworkManager.ServerModeConfig(unturnedPath),
            isNighttime: node._clock?.IsNighttime ?? false,
            isFullMoon: node._clock?.IsFullMoon ?? false,
            clothing: UnturnedGodot.Assets.ClothingArmorDatabase.ScanContentSources(sources),
            prioritization: UnturnedGodot.Assets.LevelDifficultyPrioritization.ForMap(level.Path, sources));
        UnturnedGodot.Zombies.ZombieHost? zombieHost = null;
        if (zombies != null)
        {
            ZombiePhysics.Attach(zombies, () => node.GetViewport()?.World3D, ground);
            node._zombieNavigation = ZombieNavigation.Build(zombies.Navmesh, zombies.MoveResolver, level.Path);
            if (node._zombieNavigation != null)
            {
                // Movement collision alone prevents crossing, but the graph must also learn about those
                // bodies or a hunter can keep selecting the route through a placed fence instead of the
                // route around its end. Retain the CPU mirror until the first safe physics-frame reconcile.
                node._navigationField = navigationField;
                node._selectedColliderGuids = selectedGuids;
                zombies.PathQuery = node._zombieNavigation.Query;
                zombies.PathReady = () => node._zombieNavigation?.IsReady == true;
                zombies.NavmeshProject = node._zombieNavigation.ProjectToSurface;
                zombies.NavmeshSupportsSegment = node._zombieNavigation.SupportsLocalSegment;
            }
            else
                navigationField.Release();
            node._zombies = zombies;
            var host = new UnturnedGodot.Zombies.ZombieHost(zombies, node._server);
            zombieHost = host;

            // The same bug-report recorder the windowed session arms. There is no key to press on a
            // dedicated server, which is the point: REPRO_AUTO=1 and REPRO_CAPTURE_AT are for exactly
            // this — an unattended soak run that writes a dump when the simulation misbehaves.
            if (ReproService.Create(zombies, node._server, ground, host) is { } repro)
            {
                repro.DisabledFaces = () => node._zombieNavigation?.DisabledFaces;
                repro.LevelName = levelName;
                repro.Map = mapName;
                node.AddChild(repro);
            }

            Log.Print($"[server] {zombies.Zombies.Count} zombies spawned");
        }
        else
            navigationField.Release();

        // Punches resolve against the same world the movement solver uses, whether or not this level
        // has zombies: the rubble and resources are there either way. Hooked after the zombie host so a
        // swing sees the population that tick already moved.
        var punches = new UnturnedGodot.Damage.PunchDamageHost(node._server, zombies, zombieHost, damageable);
        PunchPhysics.Attach(punches, () => node.GetViewport()?.World3D, node._server);
        node.PunchDamage = punches;

        Log.Print($"[server] dedicated server for {levelName} listening on UDP {port} ({tiles.Count} height tiles)");
        return node;
    }

    // The map's day/night cycle, read from its own Lighting.dat. Null when the map ships none, which is
    // the same static-midday answer DayNightController gives on that map: daytime, no moon.
    //
    // DAY_SPEED is honoured here as well as in the windowed session, because a soak run that has to
    // reach a full moon cannot wait for real hours of cycle time.
    private static DayNightClock? ServerClock(LevelInfo level)
    {
        LevelLighting? lighting = LevelLighting.Load(
            System.IO.Path.Combine(level.Path, "Environment", "Lighting.dat"));
        return lighting == null
            ? null
            : new DayNightClock(lighting.Bias, lighting.TimeOfDay, lighting.MoonPhase);
    }

    private DayNightClock? _clock;
    private UnturnedGodot.Zombies.ZombieSystem? _zombies;
    private readonly float _daySpeed =
        OS.GetEnvironment("DAY_SPEED") is { Length: > 0 } speed ? speed.ToFloat() : 1f;

    private double _lastStatus;

    public override void _PhysicsProcess(double delta)
    {
        StartNavigationReconciliation();
        // updateLighting's frame, then the population's hyper state off it. Assigning an unchanged value
        // costs one comparison; the population is only walked when the moon actually turns over.
        if (_clock != null)
        {
            _clock.Advance((float)delta, _daySpeed);
            if (_zombies != null)
            {
                _zombies.IsFullMoon = _clock.IsFullMoon;
                _zombies.IsNighttime = _clock.IsNighttime;
            }
        }
        _server.Update(NetworkManager.Now);
        if (NetworkManager.Now - _lastStatus >= 10.0)
        {
            _lastStatus = NetworkManager.Now;
            Log.Print($"[server] {_server.PlayerCount} player(s) online");
        }
    }

    // Held only so the host it hooks outlives the constructor: it drives itself off NetServer.OnTick.
    public UnturnedGodot.Damage.PunchDamageHost? PunchDamage { get; private set; }
    private ZombieNavigation? _zombieNavigation;
    private CollisionFieldBuilder? _navigationField;
    private IReadOnlySet<System.Guid>? _selectedColliderGuids;

    private void StartNavigationReconciliation()
    {
        if (_navigationField == null || _selectedColliderGuids == null || _zombieNavigation == null)
            return;

        CollisionFieldBuilder field = _navigationField;
        IReadOnlySet<System.Guid> selected = _selectedColliderGuids;
        _navigationField = null;
        _selectedColliderGuids = null;

        PhysicsDirectSpaceState3D? space = GetViewport()?.World3D?.DirectSpaceState;
        if (space == null)
        {
            Log.PushWarning("[server] physics space unavailable; navigation collision reconciliation skipped");
            field.Release();
            return;
        }
        _ = AppShutdown.Track(_zombieNavigation.PruneAgainstCollisionAsync(
            this, space, UnturnedGodot.Player.PlayerConfig.StepOffset, selected, field));
    }

    public override void _ExitTree()
    {
        _transport.Close();
        _navigationField?.Release();
        _zombieNavigation?.Free();
    }
}
