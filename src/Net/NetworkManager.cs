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
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
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

    private static bool FlatFallback(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    public static double Now => Time.GetTicksMsec() / 1000.0;

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
            return;
        }
        // AlertTool's BLOCK_VISION raycast: stealth detection fails when geometry hides the player.
        // BLOCK_VISION is exactly LARGE | MEDIUM (RayMasks.cs) — LARGE/MEDIUM object colliders and
        // nothing else: not the terrain, not resources, and crucially not the players themselves
        // (an unfiltered ray ends inside the target's own capsule at close range and blinds every
        // nearby zombie). Stops at 95% of the distance exactly like the original. The ZombieHost
        // ticks inside _PhysicsProcess, so querying the physics space here is safe.
        // Query-parameter objects are engine objects, so both closures reuse one instead of
        // allocating (and later finalizing) a fresh one per zombie step.
        var ray = new PhysicsRayQueryParameters3D { CollisionMask = ObjectsBuilder.VisionBlockerLayer };
        // The viewport's World3D never changes while this node is in the tree, so resolve it once and keep
        // it: these three closures run PER ZOMBIE PER TICK, and GetViewport() walks the node tree and
        // marshals across to the engine every single time. The space state itself is still fetched per
        // call, which is what has to stay fresh.
        World3D? world = null;
        World3D? World() => world ??= GetViewport()?.World3D;

        zombies.VisionBlocked = (from, to) =>
        {
            PhysicsDirectSpaceState3D? space = World()?.DirectSpaceState;
            if (space == null)
                return false;
            ray.From = from;
            ray.To = from + ((to - from) * Zombies.ZombieBody.VisionRayFraction);
            return space.IntersectRay(ray).Count > 0;
        };
        // The CharacterController's collide-and-slide against real world colliders: sweep a
        // capsule covering the zombie's body from the knees up (0.4..2.0 m — the full 2 m capsule
        // dragged its base along the terrain and jammed on every micro-slope; ground contact is
        // the ground snap's job here, like a CC's grounding pass). Catches knee-high furniture and
        // head-high overhangs that the old chest sphere missed.
        var sweep = new CapsuleShape3D { Height = Zombies.ZombieBody.CapsuleHeight };
        // Mask 1 only: LARGE world + terrain + resources. MEDIUM furniture lives on its own layer
        // (the navmesh ignores it; original zombies shove through it).
        var query = new PhysicsShapeQueryParameters3D { Shape = sweep, CollisionMask = 1 };
        var stepDown = new PhysicsRayQueryParameters3D { CollisionMask = 1 | ObjectsBuilder.MediumFurnitureLayer };
        float lastRadius = -1f;
        zombies.MoveResolver = (from, to, radius) =>
        {
            PhysicsDirectSpaceState3D? space = World()?.DirectSpaceState;
            if (space == null)
                return to;
            if (radius != lastRadius) // one capsule size per speciality; skip the redundant engine write
            {
                sweep.Radius = radius;
                lastRadius = radius;
            }
            // Capsule centre: covers ZombieBody.CapsuleBottom..CapsuleTop above the feet. The constants
            // live in core because the bug-repro replay sweeps the SAME body without an engine, and a
            // replay against a different capsule is not a reproduction of anything.
            var chest = new Vector3(0f, Zombies.ZombieBody.CapsuleCenter, 0f);
            Vector3 at = from;
            Vector3 motion = to - from;

            // Unity's CharacterController.Move is ITERATIVE: it advances to the first contact,
            // projects what is left of the motion onto that surface, and goes again. That loop is
            // the whole reason a zombie wedged nose-first into a corner in Unturned works itself
            // free over a frame or two — one pass leaves it stuck against the first surface, and a
            // sidestep heuristic bolted on top of a single pass re-decides every tick and visibly
            // shuffles left and right. Four passes is enough for a corner (two walls) plus slack;
            // Unity does not publish its own count.
            for (int pass = 0; pass < Zombies.ZombieBody.MaxSlides; pass++)
            {
                if (motion.LengthSquared() < 1e-8f)
                    break;

                query.Transform = new Transform3D(Basis.Identity, at + chest);
                query.Motion = motion;
                float[] cast = space.CastMotion(query);
                if (cast[0] >= 1f)
                {
                    at += motion; // rest of the motion is clear
                    break;
                }

                // Step-up with the zombie CharacterController's REAL stepOffset (0.5, read from the
                // prefab; slope limit is 75°): retry the sweep raised by it — steps pass, walls do
                // not. Only attempted on the first contact, like the CC's own step pass.
                if (pass == 0)
                {
                    query.Transform = new Transform3D(Basis.Identity,
                        at + chest + new Vector3(0f, Player.PlayerConfig.StepOffset, 0f));
                    float[] stepCast = space.CastMotion(query);
                    if (stepCast[0] >= 1f)
                    {
                        // Only a real step: the raised destination must have ground within the climb
                        // height, or this would float over gaps.
                        stepDown.From = new Vector3(to.X, at.Y + 1.5f, to.Z);
                        stepDown.To = new Vector3(to.X, at.Y + 0.05f, to.Z);
                        Godot.Collections.Dictionary ground = space.IntersectRay(stepDown);
                        if (ground.Count > 0)
                            return new Vector3(to.X, ((Vector3)ground["position"]).Y, to.Z);
                    }
                }

                Vector3 safe = at + (motion * cast[0]);

                // Contact normal at the blocked spot -> slide what remains along that surface, then
                // let the next pass resolve whatever the slide runs into.
                query.Transform = new Transform3D(Basis.Identity, safe + chest);
                query.Motion = Vector3.Zero;
                Vector3 normal = space.GetRestInfo(query) is { Count: > 0 } rest
                    ? (Vector3)rest["normal"]
                    : Vector3.Zero;
                if (normal == Vector3.Zero)
                    return safe;

                Vector3 remaining = motion * (1f - cast[0]);
                motion = remaining - (normal * remaining.Dot(normal));
                at = safe;
            }

            return at;
        };
        // Real ground: a short downward ray finds the surface actually underfoot — sidewalks,
        // house floors, stairs — with the zombie's current height as the reference so stacked
        // floors (basements, upper storeys) resolve correctly. Mask 1 = static world only (the
        // player's body lives on layer 2), falling back to the heightfield out in the open.
        var snapRay = new PhysicsRayQueryParameters3D { CollisionMask = 1 | ObjectsBuilder.MediumFurnitureLayer }; // reused: one per zombie step otherwise
        zombies.GroundSnap = (Vector3 position, out float y) =>
        {
            PhysicsDirectSpaceState3D? space = World()?.DirectSpaceState;
            if (space == null)
                return _ground(position.X, position.Z, out y);
            // Start just above the CC's stepOffset (0.5): steps resolve, railings above don't.
            snapRay.From = position + new Vector3(0f, Zombies.ZombieBody.GroundProbeUp, 0f);
            snapRay.To = position + new Vector3(0f, -Zombies.ZombieBody.GroundProbeDown, 0f);
            Godot.Collections.Dictionary hit = space.IntersectRay(snapRay);
            if (hit.Count > 0)
            {
                y = ((Vector3)hit["position"]).Y;
                return true;
            }
            return _ground(position.X, position.Z, out y);
        };
        // The pre-baked navmesh drives the Seeker port: zombies path around buildings and props
        // exactly over the triangles the original game baked. Prefer the data parsed at the start of the
        // world load; until collision reconciliation publishes it, PathReady selects direct movement.
        _zombieNavigation = ZombieNavigation.TakePreloaded() ?? ZombieNavigation.Build(zombies.Navmesh);
        if (_zombieNavigation != null)
        {
            zombies.PathQuery = _zombieNavigation.Query;
            zombies.PathReady = () => _zombieNavigation?.IsReady == true;
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
            void Probe()
            {
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
            GetTree().CreateTimer(25.0).Timeout += () => // after the nav map sync AND the reconcile
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
                    MoveResolver = zombies.MoveResolver,
                    GroundSnap = zombies.GroundSnap,
                    VisionBlocked = zombies.VisionBlocked,
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

        _ = new UnturnedGodot.Zombies.ZombieHost(zombies, _server);

        // The bug-report key (F7): keeps the last few seconds of the simulation in memory so a session
        // that just did something wrong can be written out and replayed. Off with REPRO=0.
        if (ReproService.Create(zombies, _server, _ground) is { } repro)
        {
            repro.LevelName = LevelName;
            repro.Map = System.IO.Path.GetFileName(levelDir.TrimEnd('/', '\\'));
            AddChild(repro);
        }

        Log.Print($"[zombies] {zombies.Zombies.Count} zombies spawned from the level's spawnpoints "
            + $"(ZOMBIE_SEED={seed})");
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
