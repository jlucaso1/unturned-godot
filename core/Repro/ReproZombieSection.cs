using System;
using System.Collections.Generic;
using UnturnedGodot.Player;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Repro;

// The zombie AI's slice of a dump: the complete brain state at the start of the window, the world's
// answers to every question it asked during the window, and the motion that came out.
//
// Two things make it reproducible rather than merely detailed. The oracle replays the exact answers the
// engine gave, so an UNCHANGED brain reproduces the window bit for bit; and the world geometry in the
// dump answers the queries a CHANGED brain makes, which is what anyone fixing the bug will be issuing.
// A replay reports which of the two served each query, so "it stopped reproducing" can never be a
// silent consequence of the harness making the world up.
public sealed record ReproZombieSection
{
    public ReproSystemState State { get; init; } = new();

    // Which world delegates the recording session had installed. Null in a dump written before this
    // was recorded, which a replay reads as "install whatever you can answer".
    public ReproWorldSeams? Seams { get; init; }
    public List<ReproFrame> Frames { get; init; } = new();
    public ReproOracleData Oracle { get; init; } = new();

    public string Describe()
    {
        int awake = 0, hunting = 0;
        foreach (ReproZombieRecord zombie in State.Zombies)
        {
            if (zombie.State != EZombieState.Idle)
                awake++;
            if (zombie.Target != byte.MaxValue)
                hunting++;
        }
        var text = new System.Text.StringBuilder();
        text.Append("  zombies     ").Append(State.Zombies.Count).Append(" (").Append(awake)
            .Append(" awake, ").Append(hunting).Append(" hunting)\n");
        text.Append("  oracle      ").Append(Oracle.MoveCount).Append(" move, ")
            .Append(Oracle.GroundCount).Append(" ground, ").Append(Oracle.VisionCount)
            .Append(" vision, ").Append(Oracle.PathCount).Append(" path\n");
        if (!State.RandomIsReproducible)
            text.Append("  ! the recording session used a non-reproducible RNG; rolls will differ\n");
        return text.ToString();
    }
}

// The seams between the simulation and the world it runs in. Which ones EXIST is part of the
// simulation: ZombieSystem takes a different branch for each one that is null, so a replay that
// installs a resolver the session did not have is not being more thorough, it is running something
// else. A dedicated server, for instance, has a pathfinder and no physics at all.
public sealed record ReproWorldSeams
{
    public bool MoveResolver { get; init; }
    public bool GroundSnap { get; init; }
    public bool VisionBlocked { get; init; }
    public bool PathQuery { get; init; }
}

public sealed record ReproSystemState
{
    public float DetectTimer { get; init; }
    public int RepathCursor { get; init; }
    public ulong RandomState { get; init; }
    public bool RandomIsReproducible { get; init; }
    public List<ReproAgro> Agro { get; init; } = new();
    public List<ReproZombieRecord> Zombies { get; init; } = new();
}

public sealed record ReproAgro
{
    public byte Player { get; init; }
    public int Count { get; init; }
}

// One zombie, complete. Anything left out here is a detail of the bug the replay would have to guess
// at, so nothing is left out — including the route in hand and the timers that judge it.
public sealed record ReproZombieRecord
{
    public int Id { get; init; }
    public byte Bound { get; init; }
    public byte Type { get; init; }
    public EZombieSpeciality Speciality { get; init; }
    public bool IsHyper { get; init; }
    public byte Shirt { get; init; }
    public byte Pants { get; init; }
    public byte Hat { get; init; }
    public byte Gear { get; init; }
    public byte Move { get; init; }
    public byte Idle { get; init; }
    public float[] Home { get; init; } = Array.Empty<float>();
    public float[] Position { get; init; } = Array.Empty<float>();
    public float Yaw { get; init; }
    public EZombieState State { get; init; }
    public byte Target { get; init; } = byte.MaxValue;
    public EZombiePath Approach { get; init; }
    public float SinceSwing { get; init; }
    public float PendingHit { get; init; }
    public float LeaveDelay { get; init; }
    public float[] LeaveTo { get; init; } = Array.Empty<float>();
    public float RepathTimer { get; init; }
    public float BlockedRouteTime { get; init; }
    public int WaypointIndex { get; init; }
    public bool TargetReached { get; init; }
    public bool PathIsPartial { get; init; }
    public bool RouteServedAnotherTarget { get; init; }
    public float[] Steer { get; init; } = Array.Empty<float>();
    public float[] Route { get; init; } = Array.Empty<float>(); // flat xyz waypoints
}

// One authoritative tick's input and outcome. The players are the simulation's INPUT (a replay feeds
// them back verbatim, so the reporter's own movement is part of the reproduction); the zombies are the
// OUTPUT a replay is scored against.
public sealed record ReproFrame
{
    public uint Tick { get; init; }
    public float Dt { get; init; }
    public List<ReproPlayerSample> Players { get; init; } = new();
    public List<ReproMotionSample> Zombies { get; init; } = new();
}

public sealed record ReproPlayerSample
{
    public byte Id { get; init; }
    public float[] Position { get; init; } = Array.Empty<float>();
    public EPlayerStance Stance { get; init; }
    public bool Moving { get; init; }
}

public sealed record ReproMotionSample
{
    public int Id { get; init; }
    public float[] Position { get; init; } = Array.Empty<float>();
    public float Yaw { get; init; }
    public EZombieState State { get; init; }
    public byte Target { get; init; } = byte.MaxValue;
}

// Every answer the world gave the brain, in call order, stored as flat columns. One JSON object per
// call costs about three times as many bytes for the same numbers, and a busy window is tens of
// thousands of calls — the difference is a file you can open against one you cannot.
public sealed record ReproOracleData
{
    public const int MoveStride = 12;   // tick, zombie, from xyz, to xyz, radius, result xyz
    public const int GroundStride = 7;  // tick, zombie, position xyz, hit, y
    public const int VisionStride = 9;  // tick, zombie, from xyz, to xyz, blocked
    public const int PathStride = 12;   // tick, zombie, from xyz, to xyz, radius, found, start, count
    public const int ReadyStride = 2;   // tick, ready

    // Ticks are indices into Frames, not server ticks: a window is at most a few hundred, which keeps
    // every number in these arrays short.
    public string Columns { get; init; } =
        "move: tick,zombie,fromXYZ,toXYZ,radius,resultXYZ | "
        + "ground: tick,zombie,positionXYZ,hit,y | "
        + "vision: tick,zombie,fromXYZ,toXYZ,blocked | "
        + "path: tick,zombie,fromXYZ,toXYZ,radius,found,pointStart,pointCount | "
        + "ready: tick,ready";

    public List<float> Move { get; init; } = new();
    public List<float> Ground { get; init; } = new();
    public List<float> Vision { get; init; } = new();
    public List<float> Path { get; init; } = new();
    public List<float> PathPoints { get; init; } = new();
    public List<float> Ready { get; init; } = new();

    public int MoveCount => Move.Count / MoveStride;
    public int GroundCount => Ground.Count / GroundStride;
    public int VisionCount => Vision.Count / VisionStride;
    public int PathCount => Path.Count / PathStride;
}
