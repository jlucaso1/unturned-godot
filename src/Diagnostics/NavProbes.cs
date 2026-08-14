using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Diagnostics;

// The navigation diagnostics a session can be started with, all five of them driven by an environment
// variable and all five off unless one is set.
//
// They used to live inside NetworkManager, which owns the multiplayer session, and being there cost
// something in both directions. Two hundred lines of investigation tooling made the session owner hard
// to read and harder to test — the only assertions anything could make about the probes were made by
// reading NetworkManager.cs as TEXT and searching it (tests/PhysicsBodyOrderTests.cs), because there
// was no seam to reach them through. And the probes themselves are worth more than that: they are the
// tools for "which opening does the zombie leave the house through" and "can a zombie physically climb
// this step", questions that come back on every map.
//
// What they need is a hosted session, a live physics space and the level's zombie brain. What they are
// not is part of owning one, which is why they are here and reached through a single call.
//
// One rule runs through all of them, and it is the reason several are written as timers followed by an
// await rather than as plain callbacks: Godot's DirectSpaceState may only be queried inside a physics
// notification. A probe that ignores that does not fail loudly — every collision query simply answers
// "nothing there", and the probe then reports a clean run over a world it never actually touched.
public static class NavProbes
{
    // Reads the environment once and arms whatever it names. Nothing is armed and nothing is allocated
    // when none of the variables is set, which is every session that is not being investigated.
    public static void AttachFromEnvironment(Node owner, ZombieSystem zombies, string levelDir,
        GroundSampler ground)
    {
        System.ArgumentNullException.ThrowIfNull(owner);
        System.ArgumentNullException.ThrowIfNull(zombies);

        // PATH_PROBE="x,y,z>x,y,z": log the navmesh route between two points — the exact tool for
        // "which opening does the zombie leave the house through" investigations.
        if (OS.GetEnvironment("PATH_PROBE") is { Length: > 0 } pathProbe && zombies.PathQuery != null)
        {
            string[] ends = pathProbe.Split('>');
            string[] a = ends[0].Split(',');
            string[] b = ends[1].Split(',');
            var from = new Vector3(a[0].ToFloat(), a[1].ToFloat(), a[2].ToFloat());
            var to = new Vector3(b[0].ToFloat(), b[1].ToFloat(), b[2].ToFloat());
            double startedAt = NetworkManager.Now;
            async void Probe()
            {
                // A previously unseen stitched portal asks MoveResolver to validate its opening. The
                // resolver owns DirectSpaceState, so even this diagnostic must enter a physics
                // notification before it can safely populate the portal cache.
                await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.PhysicsFrame);
                var waypoints = new List<Vector3>();
                bool ok = zombies.PathQuery!(from, to, waypoints, BakedNavGraph.AgentRadius);
                Log.Print($"[nav] path probe t={NetworkManager.Now - startedAt:F1}s {from} -> {to}: " +
                    $"found={ok} waypoints={waypoints.Count}");
                foreach (Vector3 w in waypoints)
                    Log.Print($"[nav]   wp {w}");
                if (!ok && NetworkManager.Now - startedAt < 15)
                    owner.GetTree().CreateTimer(1.0).Timeout += Probe;
            }
            owner.GetTree().CreateTimer(1.0).Timeout += Probe;
        }

        // HUNT_PROBE="zx,zy,zz>px,py,pz": run the COMPLETE zombie brain (detection, pathfinding,
        // carrot following, physics) with one synthetic zombie hunting a stationary player, and
        // log its trajectory — the definitive end-to-end check for "does the zombie reach me here".
        if (OS.GetEnvironment("HUNT_PROBE") is { Length: > 0 } huntProbe)
        {
            // 25 s: the navmesh is reconciled against collision once the world finishes streaming, and
            // that rebuild re-syncs the map for a few seconds. Probing earlier catches the gap and reads
            // as "the zombie never moved" when it simply had no graph yet.
            owner.GetTree().CreateTimer(25.0).Timeout += async () => // after the nav map sync AND the reconcile
            {
                string[] ends = huntProbe.Split('>');
                string[] a = ends[0].Split(',');
                string[] b = ends[1].Split(',');
                var zombieAt = new ZombieSpawnpointData(0,
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

                var probe = new ZombieSystem(
                    new[] { new ZombieTable { Name = "Probe", Damage = 10 } },
                    LevelNavigationData.Load(System.IO.Path.Combine(levelDir, "Environment")),
                    ground,
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
                ZombieInstance z = probe.Zombies[0];
                z.Speciality = EZombieSpeciality.Normal;
                Vector3 last = z.Position;
                for (int tick = 0; tick < 500; tick++) // 40 s of hunting
                {
                    // DirectSpaceState is legal only during a physics notification. Running this whole
                    // loop in the timer callback made every collision query fail and then advertised the
                    // resulting no-collision ATTACK as a successful end-to-end probe. Advance one real
                    // physics frame per simulated server tick so the diagnostic exercises the authority
                    // seams it claims to measure.
                    await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.PhysicsFrame);
                    Vector3 where = playerLater != null && tick > 37 ? playerLater.Value : playerAt;
                    var views = new[]
                    {
                        new ZombiePlayerView(1, where, Player.EPlayerStance.Sprint, false),
                    };
                    probe.Tick(views, ServerSimulation.TickRate);
                    if (tick % 25 == 0)
                        Log.Print($"[nav] hunt t={tick * 0.08f:F1}s pos={z.Position} state={z.State}");
                    bool phaseTwo = playerLater == null || tick > 37;
                    if (z.State == EZombieState.Attack && phaseTwo)
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
            owner.GetTree().CreateTimer(10.0).Timeout += () =>
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
            owner.GetTree().CreateTimer(2.0).Timeout += () =>
            {
                foreach (string point in probe.Split(';'))
                {
                    string[] parts = point.Split(',');
                    var at = new Vector3(parts[0].ToFloat(), parts[1].ToFloat(), parts[2].ToFloat());
                    bool ok = zombies.GroundSnap!(at, out float gy);
                    // Also report WHO the surface belongs to, and what a mask-1-only ray (the one the
                    // move resolver uses) finds: a floor missing from mask 1 drops the walker onto the
                    // terrain underneath, which silently turns a low sill into a tall step.
                    PhysicsDirectSpaceState3D? sp = owner.GetViewport()?.World3D?.DirectSpaceState;
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
            owner.GetTree().CreateTimer(10.0).Timeout += () => AuditNavHeights(owner, zombies, auditStride.ToInt());
        }
    }

    // Raycasts our collision world down onto every Nth navmesh vertex and reports the height error.
    private static void AuditNavHeights(Node owner, ZombieSystem zombies, int stride)
    {
        if (zombies.Navmesh is not { Count: > 0 } flags)
        {
            Log.Print("[nav] audit: this map ships no navmesh");
            return;
        }
        PhysicsDirectSpaceState3D? space = owner.GetViewport()?.World3D?.DirectSpaceState;
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
        foreach ((float error, Vector3 _, string collider) in tall)
        {
            byOwner.TryGetValue(collider, out (int Count, float Worst) acc);
            byOwner[collider] = (acc.Count + 1, System.MathF.Max(acc.Worst, error));
        }
        var ranked = new List<KeyValuePair<string, (int Count, float Worst)>>(byOwner);
        ranked.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
        for (int i = 0; i < ranked.Count && i < 12; i++)
            Log.Print($"[nav]     {ranked[i].Key}: {ranked[i].Value.Count} vertices, worst " +
                $"{ranked[i].Value.Worst:+0.00}");
    }
}
