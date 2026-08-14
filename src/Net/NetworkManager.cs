using System.Collections.Generic;
using Godot;
using UnturnedGodot.Config;
using UnturnedGodot.Data;
using UnturnedGodot.Net;

namespace UnturnedGodot;

// The Godot-side owner of the multiplayer session. Three shapes, all over the same core stack:
//  - Listen server ("open to LAN"): NetServer over loopback+UDP composite, the host joins via loopback.
//  - Client: NetClient over UDP to someone else's server (JOIN=host:port).
//  - Dedicated: see DedicatedServer (no local player at all).
// Pumps everything on the physics tick and forwards the local player's 12.5 Hz inputs.
public partial class NetworkManager : Node
{
    public const ushort DefaultPort = 27015;

    private NetServer? _server;
    private CompositeServerTransport? _serverTransport;
    private NetClient? _client;
    private IClientTransport? _clientTransport;

    public NetClient? Client => _client;
    public NetServer? Server => _server; // extension seam owner: future systems hook OnTick/Broadcast
    public bool IsHosting => _server != null;
    public bool IsLanOpen { get; private set; }
    public bool IsActive => _client != null || _server != null;

    private GroundSampler _ground = FlatFallback;
    private Vector3 _spawn;

    // The map folder this session's world was built from — the identity both ends of the handshake
    // agree on, so nobody plays on a map the server is not running. Main sets it before any session
    // starts (see Main.LevelIdentity).
    public string LevelName { get; set; } = "";

    // Raised when the server refuses our join (wrong map, wrong build, full). Main turns it into a
    // message and the way back to the menu, instead of a session that silently never starts.
    public System.Action<JoinRejection>? OnRejected;

    // The server half of PlayerEquipment.punch. Created whenever this session hosts a world, with or
    // without a zombie population in it. Null on a pure client, which decides no damage at all.
    public UnturnedGodot.Damage.PunchDamageHost? PunchDamage { get; private set; }

    // The level's breakable placements, handed over by the object streamer once it has read the map.
    // Assigning it late is the normal case: the session is up and hosting long before the world has
    // finished streaming, and until then a punch can still hit a zombie, just not a tree.
    public UnturnedGodot.Damage.DamageableWorld? Damageable
    {
        get => PunchDamage?.World;
        set
        {
            if (PunchDamage != null)
                PunchDamage.World = value;
        }
    }

    private static bool FlatFallback(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    public static double Now => Time.GetTicksMsec() / 1000.0;

    public override void _Ready() => AddToGroup(SceneGroups.Network);

    // A* search workspaces the baked graph is holding: three int/float arrays per triangle each, pooled
    // per flag and never drained, so this is the only reading of what pathfinding retains for the rest
    // of the session. Zero on a client, which has no graph.
    public int SearchWorkspaceCount => _zombieNavigation?.SearchWorkspaceCount ?? 0;

    public void Configure(HeightmapSampler heights, Vector3 spawn)
    {
        _spawn = spawn;
        _ground = (float x, float z, out float y) => heights.TrySampleHeight(x, -z, out y);
    }

    // The always-on session, Unturned's Provider shape: singleplayer IS a loopback server with the local
    // player as its first client. Every gameplay feature is then written once as server logic +
    // replication and works identically solo, LAN and dedicated.
    public void StartSingleplayer(string hostName)
    {
        if (IsActive)
            return;
        var loopback = new LoopbackServerTransport();
        _serverTransport = new CompositeServerTransport(loopback);
        _server = new NetServer(_serverTransport, new ServerSimulation(new HeightfieldMoveSolver(_ground)),
            _spawn, LevelName);
        _clientTransport = loopback.CreateClient();
        _client = new NetClient(_clientTransport, hostName, LevelName);
        WatchForRejection(_client);
        Log.Print($"[net] local session up on '{LevelName}'; '{hostName}' joined via loopback");
    }

    // Minecraft-style "open to LAN": attach a UDP listener to the ALREADY-RUNNING local server.
    public bool OpenToLan(ushort port)
    {
        if (_server == null || _serverTransport == null || IsLanOpen)
            return false;
        try
        {
            _serverTransport.Add(new UdpServerTransport(port));
        }
        catch (System.Net.Sockets.SocketException e)
        {
            Log.PushWarning($"[net] failed to bind UDP port {port}: {e.Message}");
            return false;
        }
        IsLanOpen = true;
        Log.Print($"[net] open to LAN on UDP {port}");
        return true;
    }

    // Brings the level's zombie population up on the hosted server (no-op for pure clients): the
    // ZombieHost hooks the NetServer extension seams, so solo, LAN and dedicated all share it.
    //
    // `unturnedPath` is the install the map's content sources come from, so the map's own
    // ZombieDifficultyAssets can be scanned; `dayNight` supplies LightingManager's time of day, which
    // the speciality roll reads and which keeps driving the population's hyper state afterwards. Both
    // are optional, and skipping them yields the mode config's own weights — which is exactly what a map
    // naming no difficulty asset gets anyway, PEI included.
    public void HostZombies(string levelDir, string? unturnedPath = null,
        DayNightController? dayNight = null)
    {
        if (_server == null)
            return;
        _dayNight = dayNight;
        // Provider.modeConfigData: the operator's own Config.json, if this install has one. It decides
        // the population's size, its speciality weights, its swing damage and its stagger — none of
        // which could be configured at all while this was pinned to the ported NORMAL block.
        ModeConfigData mode = ServerModeConfig(unturnedPath);
        // A generator whose whole state is one integer, so a bug-repro dump can carry the sequence the
        // session was on rather than re-rolling from scratch (Repro.ReproRandom). ZOMBIE_SEED pins it.
        var random = Repro.ReproRandom.ForSession(OS.GetEnvironment("ZOMBIE_SEED"), out ulong seed);
        // Discovered once. Three separate scans of the same install used to run here, and the difficulty
        // prioritization would have made it four.
        System.Collections.Generic.IReadOnlyList<UnturnedGodot.Assets.ContentSource> sources =
            unturnedPath is { Length: > 0 }
                ? UnturnedGodot.Assets.ContentSource.Discover(unturnedPath)
                : System.Array.Empty<UnturnedGodot.Assets.ContentSource>();
        UnturnedGodot.Zombies.ZombieSystem? zombies = UnturnedGodot.Zombies.ZombieWorld.Load(
            levelDir, _ground, random,
            difficulties: sources.Count > 0
                ? UnturnedGodot.Assets.ZombieDifficultyBank.ScanContentSources(sources)
                : null,
            mode: mode,
            isNighttime: dayNight?.IsNighttime ?? false,
            isFullMoon: dayNight?.IsFullMoon ?? false,
            clothing: sources.Count > 0
                ? UnturnedGodot.Assets.ClothingArmorDatabase.ScanContentSources(sources)
                : null,
            prioritization: UnturnedGodot.Assets.LevelDifficultyPrioritization.ForMap(levelDir, sources));
        if (zombies == null)
        {
            Log.PushWarning("[zombies] level ships no zombie data; skipping");
            // The punch host is NOT skipped with them. A level with no zombie population still has
            // trees and rubble standing on it and a player whose fists work, and the Damageable handoff
            // the streamer makes later needs a host to hand it to.
            PunchDamage = AttachPunchDamage(zombies: null, host: null);
            return;
        }
        _zombies = zombies;
        // Tier 3's split of this session's zombie CPU. Installed unconditionally: the counters behind it
        // early-out while they are off, so a production run pays two clock reads on the 12.5 Hz tick.
        zombies.Costs = Benchmark.RuntimeCounters.ZombieCosts;
        ZombiePhysics.Attach(zombies, () => GetViewport()?.World3D, _ground);
        // The pre-baked navmesh drives the Seeker port: zombies path around buildings and props
        // exactly over the triangles the original game baked. Prefer the data parsed at the start of the
        // world load; until collision reconciliation publishes it, PathReady selects direct movement.
        _zombieNavigation = ZombieNavigation.TakePreloaded(zombies.MoveResolver)
            ?? ZombieNavigation.Build(zombies.Navmesh, zombies.MoveResolver);
        if (_zombieNavigation != null)
        {
            zombies.PathQuery = _zombieNavigation.Query;
            zombies.PathReady = () => _zombieNavigation?.IsReady == true;
            zombies.NavmeshProject = _zombieNavigation.ProjectToSurface;
            zombies.NavmeshSupportsSegment = _zombieNavigation.SupportsLocalSegment;
        }

        // The env-var diagnostics: PATH_PROBE, HUNT_PROBE, WALK_PROBE, GROUND_PROBE and NAV_AUDIT.
        // They live in src/Diagnostics/NavProbes.cs rather than here — they are investigation tools that
        // happen to need a hosted session, not part of owning one.
        Diagnostics.NavProbes.AttachFromEnvironment(this, zombies, levelDir, _ground);

        var host = new UnturnedGodot.Zombies.ZombieHost(zombies, _server);
        PunchDamage = AttachPunchDamage(zombies, host);

        // The bug-report key (F7): keeps the last few seconds of the simulation in memory so a session
        // that just did something wrong can be written out and replayed. Off with REPRO=0.
        if (ReproService.Create(zombies, _server, _ground, host) is { } repro)
        {
            repro.DisabledFaces = () => _zombieNavigation?.DisabledFaces;
            repro.LevelName = LevelName;
            repro.Map = System.IO.Path.GetFileName(levelDir.TrimEnd('/', '\\'));
            AddChild(repro);
        }

        Log.Print($"[zombies] {zombies.Zombies.Count} zombies spawned from the level's spawnpoints "
            + $"(ZOMBIE_SEED={seed})");
    }

    // The punch host, cast against this session's own physics world. Hooked AFTER the zombie host so
    // damage resolves against the population the tick has already moved, and so a kill it reports is
    // broadcast on the next tick rather than racing that tick's snapshots.
    private UnturnedGodot.Damage.PunchDamageHost AttachPunchDamage(
        UnturnedGodot.Zombies.ZombieSystem? zombies, UnturnedGodot.Zombies.ZombieHost? host)
    {
        var punches = new UnturnedGodot.Damage.PunchDamageHost(_server!, zombies, host);
        PunchPhysics.Attach(punches, () => GetViewport()?.World3D, _server);
        return punches;
    }

    // Reconciles the navmesh with the collision world. Called once the object colliders are actually in
    // the physics space (ObjectStreamer.Finished) — earlier it measures bare terrain and prunes the wrong
    // triangles. The step allowance is the CharacterController's m_StepOffset from the game data.
    // `collision`, when the load recorded one, is the CPU copy of the solid world: reconciliation probes
    // it on workers and only asks the physics server about what it cannot settle itself.
    public void ReconcileNavigation(IReadOnlySet<System.Guid> colliderGuids,
        Data.CollisionFieldBuilder? collision = null)
    {
        if (_zombieNavigation == null || _navigationReconcile != null)
        {
            // Nothing will reconcile — this session joined someone else's server, or the map has no
            // navmesh to prune. What the builder holds is the whole map's collision geometry, recorded
            // during the load for this one pass, so on those sessions it would otherwise sit there for
            // the rest of the game with no consumer at all.
            collision?.Release();
            return;
        }
        // With the PhysicsServer on its own thread, DirectSpaceState is intentionally unavailable from
        // ObjectStreamer.Finished (an idle-frame signal). Enter the next physics notification first;
        // the same path also works in the default single-threaded mode.
        var selected = new HashSet<System.Guid>(colliderGuids);
        _navigationReconcile = AppShutdown.Track(ReconcileNavigationWhenSafeAsync(selected, collision));
    }

    private async System.Threading.Tasks.Task ReconcileNavigationWhenSafeAsync(
        IReadOnlySet<System.Guid> colliderGuids, Data.CollisionFieldBuilder? collision)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        if (AppShutdown.IsShuttingDown || _zombieNavigation == null)
        {
            collision?.Release();
            return;
        }
        PhysicsDirectSpaceState3D? space = GetViewport()?.World3D?.DirectSpaceState;
        if (space == null)
        {
            Log.PushWarning("[nav] physics space unavailable; collision reconciliation skipped");
            collision?.Release();
            return;
        }
        await _zombieNavigation.PruneAgainstCollisionAsync(
            this, space, Player.PlayerConfig.StepOffset, colliderGuids, collision);
    }

    public void JoinServer(string host, ushort port, string name)
    {
        if (IsActive)
            return;
        _clientTransport = new UdpClientTransport(host, port);
        _client = new NetClient(_clientTransport, name, LevelName);
        WatchForRejection(_client);
        Log.Print($"[net] joining {host}:{port} as '{name}' on '{LevelName}'");
    }

    // The join flow asks the server which level it runs and builds that one, so a refusal here means
    // something changed under us (the host switched maps, filled up, or updated). Say so out loud.
    private void WatchForRejection(NetClient client) =>
        client.OnRejected += rejection =>
        {
            Log.PushWarning($"[net] the server refused the join: {Describe(rejection)}");
            OnRejected?.Invoke(rejection);
        };

    public static string Describe(JoinRejection rejection) => rejection.Reason switch
    {
        EJoinRejection.LevelMismatch =>
            $"it is running '{rejection.ServerLevel}', and this session built another map.",
        EJoinRejection.ProtocolMismatch =>
            $"it speaks protocol {rejection.ServerProtocolVersion}, this build speaks "
            + $"{NetMessages.ProtocolVersion}.",
        EJoinRejection.ServerFull => "it is full.",
        _ => "no reason given.",
    };

    // The server's own gameplay config: Provider.modeConfigData, read from
    // <install>/Servers/<id>/Config.json. `UG_SERVER_ID` names the savedata entry (Provider.serverID)
    // and `UG_GAME_MODE` picks which of the file's three mode sections applies (Provider.mode). Both
    // default to what an operator who has configured nothing gets, which is the ported NORMAL block.
    //
    // The same file for a listen server as for a dedicated one — see ModeConfigData.ServerConfigPath for
    // why the retail client's own Worlds/Singleplayer_<n> save is deliberately not the fallback.
    public static ModeConfigData ServerModeConfig(string? unturnedPath)
    {
        string serverId = OS.GetEnvironment("UG_SERVER_ID") is { Length: > 0 } id ? id : DefaultServerId;
        EGameMode mode =
            System.Enum.TryParse(OS.GetEnvironment("UG_GAME_MODE"), ignoreCase: true, out EGameMode parsed)
            && System.Enum.IsDefined(parsed)
                ? parsed
                : EGameMode.Normal;
        ModeConfigData config = ModeConfigData.ForServer(unturnedPath, serverId, mode);
        // Said out loud only when it CHANGES something. An unconfigured NORMAL host is every default
        // run, and a line on every start is a line nobody reads.
        if (config != ModeConfigData.Normal)
            Log.Print($"[net] gameplay config: {mode} mode from "
                + $"{ModeConfigData.ServerConfigPath(unturnedPath ?? "", serverId)}");
        return config;
    }

    // Provider.serverID's own default for a host that names none.
    public const string DefaultServerId = "unturned";

    // The clock the hosted population's hyper state follows. Held rather than sampled once at spawn:
    // LightingManager broadcasts onMoonUpdated whenever the moon changes and ZombieRegion turns that
    // into onHyperUpdated, so a session that started in daylight still gets a hyper population when the
    // full moon comes up. Nothing here respawns a zombie, so without this the moon never reached one.
    private DayNightController? _dayNight;
    private UnturnedGodot.Zombies.ZombieSystem? _zombies;

    // This session's zombie population, or null on a client and on a map that ships none. Handed out for
    // the same reason `Server` is: it is the session's own state, and the things that want to look at it
    // (a diagnostic, a test asking whether the moon reached the horde) are not worth a callback each.
    public UnturnedGodot.Zombies.ZombieSystem? Zombies => _zombies;

    public override void _PhysicsProcess(double delta)
    {
        double now = Now;
        // Assigning an unchanged value costs one comparison; the population is only walked on the edge.
        if (_zombies != null && _dayNight != null)
        {
            _zombies.IsFullMoon = _dayNight.IsFullMoon;
            _zombies.IsNighttime = _dayNight.IsNighttime;
        }
        long serverStarted = Benchmark.RuntimeCounters.Start();
        _server?.Update(now);
        Benchmark.RuntimeCounters.Record(Benchmark.RuntimeCounters.Counter.NetworkServer, serverStarted);
        long clientStarted = Benchmark.RuntimeCounters.Start();
        _client?.Update(now);
        Benchmark.RuntimeCounters.Record(Benchmark.RuntimeCounters.Counter.NetworkClient, clientStarted);
    }

    private ZombieNavigation? _zombieNavigation;
    private System.Threading.Tasks.Task? _navigationReconcile;

    public override void _ExitTree()
    {
        _clientTransport?.Close();
        _serverTransport?.Close();
        _zombieNavigation?.Free();
    }
}
