using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Repro;

// What a capture is told about the session. Everything the engine knows and this assembly does not —
// the camera, the collision slice, the console tail, whatever a future subsystem wants to add — arrives
// here, so that building a dump stays a pure function and can be tested without an engine.
public sealed class ReproCaptureRequest
{
    public string Reason { get; init; } = "manual";
    public string Note { get; init; } = "";
    public string Map { get; init; } = "";
    public string LevelName { get; init; } = "";
    public string Engine { get; init; } = "";
    public string Build { get; init; } = "";
    public bool Headless { get; init; }
    public string ShotCam { get; init; } = "";
    public string CapturedAtUtc { get; init; } = "";

    // The point everything is sliced around: the reporter's own position, or whatever a trigger
    // pointed at.
    public Vector3 Focus { get; init; }

    // How much navmesh travels with the dump. A route only has to be right near the incident; the rest
    // of the map is a download away (fetch-game-data.sh) and can be passed to the replay instead.
    public float NavmeshRadius { get; init; } = 48f;

    // The heightfield patch's extent and resolution.
    public float GroundRadius { get; init; } = 32f;
    public float GroundCell { get; init; } = 1f;

    public ReproSessionData Session { get; init; } = new();
    public ReproGeometryData? Geometry { get; init; }
    public IReadOnlyList<string> Log { get; init; } = Array.Empty<string>();
    public Dictionary<string, JsonElement> Sections { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

// Assembles a dump out of a live session. The expensive parts (converting the ring buffer, slicing the
// navmesh, tessellating collision) all happen here, once, when someone actually presses the key — the
// recording itself stays cheap enough to leave armed.
public static class ReproCapture
{
    public static ReproDump Build(ReproCaptureRequest request, ZombieSystem? system,
        ReproRecorder? recorder, GroundSampler? ground = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ReproZombieSection? section = null;
        uint startTick = 0;
        int tickCount = 0;
        if (recorder != null)
        {
            section = recorder.Build(out startTick, out tickCount);
            startTick = recorder.ServerTickOfWindowStart;
            if (recorder.DroppedCalls > 0)
                request.Warnings.Add($"{recorder.DroppedCalls} world queries exceeded the per-tick "
                    + "recording ceiling and are missing from the window");
            if (tickCount == 0)
                request.Warnings.Add("the recorder had not seen a full tick yet: the window is empty");
        }
        else if (system != null)
        {
            // No recorder: still worth a dump. It carries the state and the world, just no history —
            // which reproduces the SITUATION but not the run-up to it.
            section = new ReproZombieSection
            {
                State = ReproZombieState.ToRecord(system.CaptureState()),
            };
            request.Warnings.Add("no recorder was armed: the dump has state but no recorded window");
        }

        var dump = new ReproDump
        {
            Meta = new ReproMeta
            {
                CapturedAtUtc = request.CapturedAtUtc,
                Reason = request.Reason,
                Note = request.Note,
                Map = request.Map,
                LevelName = request.LevelName,
                Engine = request.Engine,
                Build = request.Build,
                Headless = request.Headless,
                TickRate = ServerSimulation.TickRate,
                StartTick = startTick,
                TickCount = tickCount,
                FocusPoint = ReproVector.From(request.Focus),
                FocusRadius = request.NavmeshRadius,
                ShotCam = request.ShotCam,
                Warnings = request.Warnings,
            },
            Session = request.Session,
            World = BuildWorld(system, request, ground),
            Zombies = section,
            Sections = request.Sections,
        };
        foreach (string line in request.Log)
            dump.Log.Add(line);
        return dump;
    }

    public static ReproWorldData BuildWorld(ZombieSystem? system, ReproCaptureRequest request,
        GroundSampler? ground)
    {
        ArgumentNullException.ThrowIfNull(request);
        var world = new ReproWorldData
        {
            HasNavmesh = system?.Navmesh is { Count: > 0 },
            Geometry = request.Geometry,
            Ground = ground == null
                ? null
                : SampleGround(ground, request.Focus, request.GroundRadius, request.GroundCell),
        };
        if (system == null)
            return world;

        foreach (NavBound bound in system.Bounds)
            world.Bounds.Add(new ReproNavBound
            {
                Center = ReproVector.From(bound.Center),
                Size = ReproVector.From(bound.Size),
                MaxZombies = bound.MaxZombies,
                SpawnZombies = bound.SpawnZombies,
                HyperAgro = bound.HyperAgro,
            });
        foreach (ZombieTable table in system.Tables)
            world.Tables.Add(new ReproZombieTable
            {
                Name = table.Name,
                IsMega = table.IsMega,
                Health = table.Health,
                Damage = table.Damage,
            });
        if (system.Navmesh != null)
            foreach (NavFlag flag in system.Navmesh)
                world.NavFlags.Add(Slice(flag, request.Focus, request.NavmeshRadius));
        return world;
    }

    // One nav flag, keeping only the triangles near the focus and renumbering the vertices they use.
    // PEI's navmesh is 42k triangles; a 48 m disc around an incident is a few hundred, and the flag's
    // own box travels whole because checkNavigation and the player's nav region are decided by it.
    public static ReproNavFlag Slice(NavFlag flag, Vector3 focus, float radius)
    {
        ArgumentNullException.ThrowIfNull(flag);
        var remap = new Dictionary<int, int>();
        var vertices = new List<float>();
        var triangles = new List<int>();
        float radiusSquared = radius * radius;
        int[] source = flag.Triangles;
        for (int i = 0; i + 2 < source.Length; i += 3)
        {
            if (!Near(flag.Vertices[source[i]], focus, radiusSquared)
                && !Near(flag.Vertices[source[i + 1]], focus, radiusSquared)
                && !Near(flag.Vertices[source[i + 2]], focus, radiusSquared))
                continue;
            for (int corner = 0; corner < 3; corner++)
            {
                int index = source[i + corner];
                if (!remap.TryGetValue(index, out int mapped))
                {
                    mapped = vertices.Count / 3;
                    remap[index] = mapped;
                    Vector3 vertex = flag.Vertices[index];
                    vertices.Add(ReproVector.Round(vertex.X));
                    vertices.Add(ReproVector.Round(vertex.Y));
                    vertices.Add(ReproVector.Round(vertex.Z));
                }
                triangles.Add(mapped);
            }
        }
        return new ReproNavFlag
        {
            Center = ReproVector.From(flag.Center),
            Size = ReproVector.From(flag.Size),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
            Sliced = triangles.Count < source.Length,
        };
    }

    private static bool Near(Vector3 vertex, Vector3 focus, float radiusSquared)
    {
        float dx = vertex.X - focus.X, dz = vertex.Z - focus.Z;
        return (dx * dx) + (dz * dz) <= radiusSquared;
    }

    // The terrain under the incident, on a regular grid. NaN marks a cell the sampler had no answer
    // for, so a replay can tell "no ground here" from "ground at zero".
    public static ReproHeightPatch SampleGround(GroundSampler ground, Vector3 focus, float radius,
        float cell)
    {
        ArgumentNullException.ThrowIfNull(ground);
        cell = MathF.Max(0.25f, cell);
        var side = (int)MathF.Ceiling((radius * 2f) / cell) + 1;
        float originX = focus.X - radius, originZ = focus.Z - radius;
        var heights = new float[side * side];
        for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
                heights[(z * side) + x] =
                    ground(originX + (x * cell), originZ + (z * cell), out float y)
                        ? ReproVector.Round(y)
                        : float.NaN;
        return new ReproHeightPatch
        {
            Origin = new[] { ReproVector.Round(originX), 0f, ReproVector.Round(originZ) },
            Cell = cell,
            Width = side,
            Depth = side,
            Heights = heights,
        };
    }
}
