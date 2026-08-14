using System.Collections.Generic;
using Godot;
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
    public void HostZombies(string levelDir)
    {
        if (_server == null)
            return;
        // A generator whose whole state is one integer, so a bug-repro dump can carry the sequence the
        // session was on rather than re-rolling from scratch (Repro.ReproRandom). ZOMBIE_SEED pins it.
        var random = Repro.ReproRandom.ForSession(OS.GetEnvironment("ZOMBIE_SEED"), out ulong seed);
        UnturnedGodot.Zombies.ZombieSystem? zombies =
            UnturnedGodot.Zombies.ZombieWorld.Load(levelDir, _ground, random);
        if (zombies == null)
        {
            Log.PushWarning("[zombies] level ships no zombie data; skipping");
            // The punch host is NOT skipped with them. A level with no zombie population still has
            // trees and rubble standing on it and a player whose fists work, and the Damageable handoff
            // the streamer makes later needs a host to hand it to.
            PunchDamage = AttachPunchDamage(zombies: null, host: null);
            return;
        }
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

        // PATH_PROBE="x,y,z>x,y,z": log the navmesh route between two points — the exact tool for
        // "which opening does the zombie leave the house through" investigations.
        if (OS.GetEnvironment("PATH_PROBE") is { Length: > 0 } pathProbe && zombies.PathQuery != null)
        {
            string[] ends = pathProbe.Split('>');
            string[] a = ends[0].Split(',');
            string[] b = ends[1].Split(',');
            var from = new Vector3(a[0].ToFloat(), a[1].ToFloat(), a[2].ToFloat());
            var to = new Vector3(b[0].ToFloat(), b[1].ToFloat(), b[2].ToFloat());
            double startedAt = Now;
            async void Probe()
            {
                // A previously unseen stitched portal asks MoveResolver to validate its opening. The
                // resolver owns DirectSpaceState, so even this diagnostic must enter a physics
                // notification before it can safely populate the portal cache.
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                var waypoints = new System.Collections.Generic.List<Vector3>();
                bool ok = zombies.PathQuery!(from, to, waypoints, BakedNavGraph.AgentRadius);
                Log.Print($"[nav] path probe t={Now - startedAt:F1}s {from} -> {to}: " +
                    $"found={ok} waypoints={waypoints.Count}");
                foreach (Vector3 w in waypoints)
                    Log.Print($"[nav]   wp {w}");
                if (!ok && Now - startedAt < 15)
                    GetTree().CreateTimer(1.0).Timeout += Probe;
            }
            GetTree().CreateTimer(1.0).Timeout += Probe;
        }

        // HUNT_PROBE="zx,zy,zz>px,py,pz": run the COMPLETE zombie brain (detection, pathfinding,
        // carrot following, physics) with one synthetic zombie hunting a stationary player, and
        // log its trajectory — the definitive end-to-end check for "does the zombie reach me here".
        if (OS.GetEnvironment("HUNT_PROBE") is { Length: > 0 } huntProbe)
        {
            // 25 s: the navmesh is reconciled against collision once the world finishes streaming, and
            // that rebuild re-syncs the map for a few seconds. Probing earlier catches the gap and reads
            // as "the zombie never moved" when it simply had no graph yet.
            GetTree().CreateTimer(25.0).Timeout += async () => // after the nav map sync AND the reconcile
            {
                string[] ends = huntProbe.Split('>');
                string[] a = ends[0].Split(',');
                string[] b = ends[1].Split(',');
                var zombieAt = new UnturnedGodot.Data.ZombieSpawnpointData(0,
                    new Vector3(a[0].ToFloat(), a[1].ToFloat(), -a[2].ToFloat())); // unity z-flip
                var playerAt = new Vector3(b[0].ToFloat(), b[1].ToFloat(), b[2].ToFloat());
                // Optional third point: after 3 s the player "runs" there (aggro out in the open,
                // then dive into the house/garage — the reported scenario shape).
                Vector3? playerLater = null;
                if (ends.Length > 2)
                {
                    string[] c = ends[2].Split(',');
                    playerLater = new Vector3(c[0].ToFloat(), c[1].ToFloat(), c[2].ToFloat());
                }

                var probe = new UnturnedGodot.Zombies.ZombieSystem(
                    new[] { new UnturnedGodot.Data.ZombieTable { Name = "Probe", Damage = 10 } },
                    UnturnedGodot.Data.LevelNavigationData.Load(
                        System.IO.Path.Combine(levelDir, "Environment")),
                    _ground,
                    zombies.Navmesh)
                {
                    PathQuery = zombies.PathQuery,
                    PathReady = zombies.PathReady,
                    NavmeshProject = zombies.NavmeshProject,
                    NavmeshSupportsSegment = zombies.NavmeshSupportsSegment,
                    MoveResolver = zombies.MoveResolver,
                    GroundSnap = zombies.GroundSnap,
                    VisionBlocked = zombies.VisionBlocked,
                    PhysicalLineBlocked = zombies.PhysicalLineBlocked,
                };
                probe.Spawn(new[] { zombieAt }, new System.Random(1));
                if (probe.Zombies.Count == 0)
                {
                    Log.Print("[nav] hunt probe: spawnpoint rejected (outside bounds/navmesh)");
                    return;
                }
                UnturnedGodot.Zombies.ZombieInstance z = probe.Zombies[0];
                z.Speciality = UnturnedGodot.Zombies.EZombieSpeciality.Normal;
                Vector3 last = z.Position;
                for (int tick = 0; tick < 500; tick++) // 40 s of hunting
                {
                    // DirectSpaceState is legal only during a physics notification. Running this whole
                    // loop in the timer callback made every collision query fail and then advertised the
                    // resulting no-collision ATTACK as a successful end-to-end probe. Advance one real
                    // physics frame per simulated server tick so the diagnostic exercises the authority
                    // seams it claims to measure.
                    await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                    Vector3 where = playerLater != null && tick > 37 ? playerLater.Value : playerAt;
                    var views = new[]
                    {
                        new UnturnedGodot.Zombies.ZombiePlayerView(1, where,
                            UnturnedGodot.Player.EPlayerStance.Sprint, false),
                    };
                    probe.Tick(views, UnturnedGodot.Net.ServerSimulation.TickRate);
                    if (tick % 25 == 0)
                        Log.Print($"[nav] hunt t={tick * 0.08f:F1}s pos={z.Position} state={z.State}");
                    bool phaseTwo = playerLater == null || tick > 37;
                    if (z.State == UnturnedGodot.Zombies.EZombieState.Attack && phaseTwo)
                    {
                        Log.Print($"[nav] hunt probe: ATTACK reached at t={tick * 0.08f:F1}s pos={z.Position}");
                        return;
                    }
                    last = z.Position;
                }
                Log.Print($"[nav] hunt probe: never reached attack; final pos={last} state={z.State}");
            };
        }

        // WALK_PROBE="x,y,z>x,y,z": march the zombie movement mechanics (MoveResolver + GroundSnap)
        // in a straight line between two points and log where they end up — the tool for "can a
        // zombie physically climb this stair/step" questions.
        if (OS.GetEnvironment("WALK_PROBE") is { Length: > 0 } walkProbe)
        {
            // The navigation map answers only after its async sync settles (~5 s on PEI); probing at
            // 2 s reported "no route" for crossings that resolve fine once it is up.
            GetTree().CreateTimer(10.0).Timeout += () =>
            {
                string[] ends = walkProbe.Split('>');
                string[] a = ends[0].Split(',');
                string[] b = ends[1].Split(',');
                var at = new Vector3(a[0].ToFloat(), a[1].ToFloat(), a[2].ToFloat());
                var goal = new Vector3(b[0].ToFloat(), b[1].ToFloat(), b[2].ToFloat());

                // What the GAME's own pre-baked navmesh thinks of this crossing. If it routes
                // straight through (a ratio near 1), the original expects a zombie to walk it and
                // our collision refusing is the divergence. If it routes the long way round, the
                // original sends its zombies round too and the collision is right.
                var route = new List<Vector3>();
                if (zombies.PathQuery != null
                    && zombies.PathQuery(at, goal, route, BakedNavGraph.AgentRadius))
                {
                    float walked = 0f;
                    for (int i = 1; i < route.Count; i++)
                        walked += route[i - 1].DistanceTo(route[i]);
                    float direct = at.DistanceTo(goal);
                    Log.Print($"[nav] walk probe: navmesh route {route.Count} waypoints, " +
                        $"{walked:0.#} m vs {direct:0.#} m direct (ratio {walked / direct:0.##})");
                    foreach (Vector3 w in route)
                        Log.Print($"[nav]   waypoint {w.X:0.##},{w.Y:0.##},{w.Z:0.##}");
                }
                else
                {
                    Log.Print("[nav] walk probe: navmesh has NO route between these points");
                }

                for (int i = 0; i < 120; i++)
                {
                    Vector3 flat = new(goal.X - at.X, 0, goal.Z - at.Z);
                    if (flat.Length() < 0.2f)
                        break;
                    Vector3 next = at + (flat.Normalized() * 0.44f); // one 5.5 m/s tick
                    next = zombies.MoveResolver!(at, next, 0.4f);
                    if (zombies.GroundSnap!(next, out float gy))
                        next.Y = gy;
                    if ((next - at).Length() < 0.02f)
                    {
                        Log.Print($"[nav] walk probe STUCK at {at} (step {i})");
                        return;
                    }
                    at = next;
                }
                Log.Print($"[nav] walk probe reached {at} (goal {goal})");
            };
        }

        // GROUND_PROBE="x,y,z;x,y,z": log the sampled ground at reference points (the pre-baked
        // navmesh heights are the ground truth of the ORIGINAL geometry, so this diagnoses any
        // placement drift in our world). Deferred a second so the physics world has colliders.
        if (OS.GetEnvironment("GROUND_PROBE") is { Length: > 0 } probe)
        {
            GetTree().CreateTimer(2.0).Timeout += () =>
            {
                foreach (string point in probe.Split(';'))
                {
                    string[] parts = point.Split(',');
                    var at = new Vector3(parts[0].ToFloat(), parts[1].ToFloat(), parts[2].ToFloat());
                    bool ok = zombies.GroundSnap!(at, out float gy);
                    // Also report WHO the surface belongs to, and what a mask-1-only ray (the one the
                    // move resolver uses) finds: a floor missing from mask 1 drops the walker onto the
                    // terrain underneath, which silently turns a low sill into a tall step.
                    PhysicsDirectSpaceState3D? sp = GetViewport()?.World3D?.DirectSpaceState;
                    string who = "?", who1 = "?";
                    float y1 = float.NaN;
                    if (sp != null)
                    {
                        var probeRay = new PhysicsRayQueryParameters3D
                        {
                            From = new Vector3(at.X, at.Y + 3f, at.Z),
                            To = new Vector3(at.X, at.Y - 3f, at.Z),
                            CollisionMask = 1 | ObjectsBuilder.MediumFurnitureLayer,
                        };
                        if (sp.IntersectRay(probeRay) is { Count: > 0 } anyHit)
                            who = InstancedStaticBodies.ColliderName(anyHit);
                        probeRay.CollisionMask = 1;
                        if (sp.IntersectRay(probeRay) is { Count: > 0 } worldHit)
                        {
                            who1 = InstancedStaticBodies.ColliderName(worldHit);
                            y1 = ((Vector3)worldHit["position"]).Y;
                        }
                    }
                    Log.Print($"[nav] ground probe at {at}: hit={ok} y={gy:F2} (reference {at.Y:F2}, " +
                        $"delta {gy - at.Y:+0.00;-0.00}) owner={who} | mask1: y={y1:F2} owner={who1}");
                }
            };
        }

        // NAV_AUDIT=<stride>: compare the pre-baked navmesh against the collision world we built.
        // The navmesh is the ORIGINAL geometry's walkable surface, so raycasting our world down onto
        // each navmesh vertex measures placement drift directly: a systematic offset means the whole
        // world sits wrong, isolated spikes mean specific colliders are too tall (which is what stops
        // a zombie stepping over a sill the game walks across).
        if (OS.GetEnvironment("NAV_AUDIT") is { Length: > 0 } auditStride)
        {
            GetTree().CreateTimer(10.0).Timeout += () => AuditNavHeights(zombies, auditStride.ToInt());
        }

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

    // Raycasts our collision world down onto every Nth navmesh vertex and reports the height error.
    private void AuditNavHeights(UnturnedGodot.Zombies.ZombieSystem zombies, int stride)
    {
        if (zombies.Navmesh is not { Count: > 0 } flags)
        {
            Log.Print("[nav] audit: this map ships no navmesh");
            return;
        }
        PhysicsDirectSpaceState3D? space = GetViewport()?.World3D?.DirectSpaceState;
        if (space == null)
            return;

        stride = System.Math.Max(1, stride);
        var ray = new PhysicsRayQueryParameters3D { CollisionMask = 1 };
        var errors = new List<float>();
        var tall = new List<(float Error, Vector3 At, string Owner)>();
        int missed = 0;

        foreach (NavFlag flag in flags)
            for (int i = 0; i < flag.Vertices.Length; i += stride)
            {
                Vector3 v = flag.Vertices[i];
                // Start above and end below the navmesh point: whatever surface our world puts there
                // is what a zombie would actually stand on.
                ray.From = new Vector3(v.X, v.Y + 2f, v.Z);
                ray.To = new Vector3(v.X, v.Y - 2f, v.Z);
                Godot.Collections.Dictionary hit = space.IntersectRay(ray);
                if (hit.Count == 0)
                {
                    missed++;
                    continue;
                }
                float error = ((Vector3)hit["position"]).Y - v.Y;
                errors.Add(error);

                // Only a POSITIVE error blocks anything: our surface standing above the walkable one
                // is what a zombie has to climb. (Negative is the recast offset — the navmesh floats
                // a little over the ground it was baked from, and nothing trips on that.) Record who
                // owns the collider so the culprit is a named object, not a coordinate.
                if (error > 0.25f && tall.Count < 4000)
                    tall.Add((error, v, InstancedStaticBodies.ColliderName(hit)));
            }

        if (errors.Count == 0)
        {
            Log.Print($"[nav] audit: no hits ({missed} misses)");
            return;
        }

        errors.Sort();
        float mean = 0f;
        foreach (float e in errors)
            mean += e;
        mean /= errors.Count;
        int over25 = 0;
        foreach (float e in errors)
            if (System.MathF.Abs(e) > 0.25f)
                over25++;

        Log.Print($"[nav] audit over {errors.Count} navmesh vertices ({missed} with no collider under them):");
        Log.Print($"[nav]   mean {mean:+0.000;-0.000}  median {errors[errors.Count / 2]:+0.000;-0.000}  " +
            $"p5 {errors[errors.Count / 20]:+0.000;-0.000}  p95 {errors[errors.Count * 19 / 20]:+0.000;-0.000}");
        Log.Print($"[nav]   min {errors[0]:+0.000;-0.000}  max {errors[^1]:+0.000;-0.000}  " +
            $"|error|>0.25 m: {over25} ({100.0 * over25 / errors.Count:0.#}%)");

        // The blocking tail, grouped by whoever owns the collider.
        Log.Print($"[nav]   OUR SURFACE ABOVE THE WALKABLE ONE by >0.25 m: {tall.Count} " +
            $"({100.0 * tall.Count / errors.Count:0.#}% of vertices)");
        var byOwner = new Dictionary<string, (int Count, float Worst)>();
        foreach ((float error, Vector3 _, string owner) in tall)
        {
            byOwner.TryGetValue(owner, out (int Count, float Worst) acc);
            byOwner[owner] = (acc.Count + 1, System.MathF.Max(acc.Worst, error));
        }
        var ranked = new List<KeyValuePair<string, (int Count, float Worst)>>(byOwner);
        ranked.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
        for (int i = 0; i < ranked.Count && i < 12; i++)
            Log.Print($"[nav]     {ranked[i].Key}: {ranked[i].Value.Count} vertices, worst " +
                $"{ranked[i].Value.Worst:+0.00}");
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

    public override void _PhysicsProcess(double delta)
    {
        double now = Now;
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
