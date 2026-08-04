using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Repro;

public sealed class ReproScenarioOptions
{
    // Serve the session's own recorded answers where the replay asks the same questions. Turning this
    // off replays the window against the dump's geometry alone, which is the honest way to ask "is the
    // recorded outcome a property of the world, or of the engine's exact answers that day".
    public bool UseRecordedAnswers { get; init; } = true;

    // Answer whatever the recording does not cover from the dump's collision slice.
    public bool UseGeometry { get; init; } = true;

    // The generator a replay rolls from when the dump could not carry the session's own state.
    public ulong RandomSeed { get; init; }

    // The full navmesh, when the replay has the real map available. Without it the dump's local slice
    // is used, which routes correctly near the incident and nowhere else.
    public IReadOnlyList<NavFlag>? NavFlags { get; init; }

    // A pathfinder to use instead of the one built from the flags above (the Godot host's, say).
    public ZombiePathQuery? PathQuery { get; init; }
}

// Rebuilds the zombie simulation from a dump and steps it. This is what "reproducible" means here: the
// same brain, the same population, the same routes and timers, the same player movement, and the same
// world answers — in a plain unit test, in the standalone harness, or inside the running game.
public sealed class ReproScenario
{
    private readonly ReproDump _dump;
    private readonly ReproZombieSection _section;
    private readonly ReproScenarioOptions _options;
    private readonly List<ZombiePlayerView> _players = new();
    private readonly Dictionary<int, ZombieInstance> _byId = new();
    private readonly ReproHeightSampler? _heights;
    private readonly ZombiePathQuery? _pathQuery;
    private readonly bool _routesAreSliced;
    private readonly float _navmeshRadius;
    private readonly Vector3 _navmeshCentre;

    public ZombieSystem System { get; }

    public ReproOracle? Oracle { get; }

    public ReproCollisionWorld? Collision { get; }

    // Index into the window's frames — 0 is the state the dump was anchored at.
    public int Tick { get; private set; }

    public int WindowTicks => _section.Frames.Count;

    // Queries the dump's geometry answered because the recording had nothing matching. In a replay of
    // unmodified code this stays at zero; once it moves, the replay has left the recorded world and is
    // being simulated, which is a different (and still useful) claim.
    public int GeometryAnswers { get; private set; }

    // Queries nothing could answer: no recording, no geometry. The world was treated as empty for
    // these, and a replay with a high count here is not evidence of anything.
    public int UnansweredQueries { get; private set; }

    // ...of which these happened inside the recorded window. Only those bear on whether the window
    // reproduced: a tail simulated past the recording is expected to ask questions nobody recorded.
    public int UnansweredInWindow { get; private set; }

    // Routes recomputed rather than replayed, called out separately because they are the one world
    // answer a replay cannot reproduce faithfully: a map inside Godot's polygon budget paths through
    // the engine's NavigationServer, and a replay has no engine, so it paths over the baked graph.
    // The two agree most of the time and then disagree about which side of a building to pass, after
    // which the trajectories have nothing left to say to each other. A run with this above zero is
    // answering "does the body still hit the same walls", not "does this build still do it".
    public int RoutesRecomputed { get; private set; }

    public ReproScenario(ReproDump dump, ReproScenarioOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dump);
        _dump = dump;
        _options = options ?? new ReproScenarioOptions();
        _section = dump.Zombies ?? new ReproZombieSection();

        List<NavBound> bounds = ReproZombieState.ToBounds(dump.World);
        List<ZombieTable> tables = ReproZombieState.ToTables(dump.World);
        IReadOnlyList<NavFlag>? flags = _options.NavFlags
            ?? (dump.World.HasNavmesh ? ReproZombieState.ToNavFlags(dump.World) : null);

        _heights = ReproHeightSampler.From(dump.World.Ground);
        _navmeshCentre = ReproVector.To(dump.Meta.FocusPoint);
        _navmeshRadius = dump.Meta.FocusRadius > 0f ? dump.Meta.FocusRadius : float.PositiveInfinity;
        Collision = _options.UseGeometry ? ReproCollisionWorld.From(dump.World) : null;
        Oracle = _options.UseRecordedAnswers
            ? new ReproOracle(_section.Oracle, Math.Max(1, _section.Frames.Count))
            : null;

        System = new ZombieSystem(tables, bounds, SampleGround, flags);
        System.RestoreState(ReproZombieState.FromRecord(_section.State), _options.RandomSeed);

        // Only install what the session actually had. A world delegate that exists where the original
        // had none is not a more complete replay, it is a different simulation: ZombieSystem branches
        // on each of these being null. A dedicated server runs with a pathfinder and no physics, and a
        // replay of one of its dumps has to run the same way or every step it takes is a different
        // step. Dumps written before the seams were recorded fall back to "install what can answer".
        ReproWorldSeams seams = _section.Seams
            ?? new ReproWorldSeams
            {
                MoveResolver = true,
                GroundSnap = true,
                VisionBlocked = true,
                PathQuery = flags != null,
            };
        if (Oracle != null || Collision != null)
        {
            if (seams.MoveResolver)
                System.MoveResolver = ResolveMove;
            if (seams.GroundSnap)
                System.GroundSnap = ResolveGround;
            if (seams.VisionBlocked)
                System.VisionBlocked = ResolveVision;
        }
        if (flags != null && seams.PathQuery)
        {
            // Without a pathfinder handed in, the dump's own navmesh slice becomes one. It routes
            // correctly around the incident and nowhere else, which is the trade a self-contained
            // bug report makes; pass the real map's flags (or a pathfinder) to lift it.
            _pathQuery = _options.PathQuery ?? BuildGraph(flags, dump.World, _options.NavFlags == null);
            // Whether that graph covers the whole map or only what the dump sliced out. A slice can
            // only answer near the incident, and a route asked for beyond it comes back "no route"
            // from a graph that simply has no triangles there — which is not the same claim.
            _routesAreSliced = _options.PathQuery == null && _options.NavFlags == null;
            System.PathQuery = ResolvePath;
            System.PathReady = () => Oracle?.Ready() ?? true;
        }
        foreach (ZombieInstance zombie in System.Zombies)
            _byId[zombie.Id] = zombie;
    }

    // The player views this tick feeds the brain. Past the recorded window the last frame's players are
    // held: a bug that needs the reporter to keep moving is not one you can extrapolate your way into.
    private IReadOnlyList<ZombiePlayerView> PlayersAt(int tick)
    {
        _players.Clear();
        if (_section.Frames.Count == 0)
            return _players;
        ReproFrame frame = _section.Frames[Math.Min(tick, _section.Frames.Count - 1)];
        foreach (ReproPlayerSample sample in frame.Players)
            _players.Add(ReproZombieState.FromSample(sample));
        return _players;
    }

    private float DtAt(int tick)
    {
        if (_section.Frames.Count == 0)
            return _dump.Meta.TickRate > 0f ? _dump.Meta.TickRate : ServerSimulation.TickRate;
        float dt = _section.Frames[Math.Min(tick, _section.Frames.Count - 1)].Dt;
        return dt > 0f ? dt : ServerSimulation.TickRate;
    }

    // The dump's own flags, with the faces reconciliation had already disabled put back out of use.
    // The navmesh is baked before the map's objects exist, so a graph rebuilt from the same triangles
    // routes through every building on it; restoring them is what makes a recorded route reproducible
    // rather than merely plausible.
    //
    // Only when the flags came from the dump. `--level` hands over the whole map, whose triangles are
    // numbered differently from the slice, and applying the slice's indices to it would take out faces
    // chosen at random — a worse lie than not applying them at all. A dump from before this was
    // recorded carries none, and behaves as it always did.
    private static ZombiePathQuery BuildGraph(IReadOnlyList<NavFlag> flags, ReproWorldData world,
        bool flagsAreTheDumps)
    {
        // Excluded at BUILD time, the way the session's own graph was built, rather than disabled
        // afterwards. The two are not equivalent: adjacency is derived once, and an edge only counts as
        // a border when no enabled face sits across it — so a face that is absent from the start can
        // change which edges are borders, and therefore which seams get stitched. Disabling later only
        // marks faces; it cannot add the connections the build would have made.
        var exclusions = new Dictionary<NavFlag, HashSet<int>>();
        if (flagsAreTheDumps && flags.Count == world.NavFlags.Count)
            for (int i = 0; i < flags.Count; i++)
            {
                // Null, not empty, on every dump written before this field existed: the reader leaves
                // an absent array alone rather than running the initializer.
                int[]? off = world.NavFlags[i].DisabledFaces;
                if (off is { Length: > 0 })
                    exclusions[flags[i]] = new HashSet<int>(off);
            }
        return BakedNavGraph.Build(flags, exclusions.Count > 0 ? exclusions : null).TryPath;
    }

    public void Step()
    {
        Oracle?.SeekTick(Tick);
        System.Tick(PlayersAt(Tick), DtAt(Tick));
        Tick++;
    }

    // Replays the recorded window and then, optionally, keeps going. The extra ticks are where a fix is
    // judged: the window shows the bug, the tail shows whether it ends.
    public ReproReplayReport Run(int extraTicks = 0)
    {
        var report = new ReproReplayReport();
        // Tracks start from the state the dump RESTORED, before a single tick has run. Created after
        // the first Step instead, a zombie's first tick of motion is measured against where that tick
        // already put it — which is precisely the tick a short window is about.
        var tracks = new Dictionary<int, MotionTrack>();
        foreach (ZombieInstance zombie in System.Zombies)
            tracks[zombie.Id] = new MotionTrack(zombie.Position, zombie.Yaw,
                zombie.State != EZombieState.Idle || zombie.TargetPlayer != byte.MaxValue);

        int total = WindowTicks + Math.Max(0, extraTicks);
        for (int i = 0; i < total; i++)
        {
            Step();
            if (Tick - 1 < WindowTicks)
                Compare(_section.Frames[Tick - 1], report);
            foreach (ZombieInstance zombie in System.Zombies)
            {
                if (!tracks.TryGetValue(zombie.Id, out MotionTrack? track))
                    tracks[zombie.Id] = track = new MotionTrack(zombie.Position, zombie.Yaw, false);
                bool awake = zombie.State != EZombieState.Idle || zombie.TargetPlayer != byte.MaxValue;
                // Once a zombie has done something — including having been mid-something when the
                // window opened — it keeps being measured. A zombie that reaches its retreat point on
                // the very first tick is the whole incident in some short windows, and measuring it
                // only from the tick AFTER it settles measures nothing at all.
                if (awake || track.StartedAwake || track.Samples > 0)
                    track.Add(zombie);
            }
        }

        report.Ticks = Tick;
        report.OracleHits = Oracle?.Hits ?? 0;
        report.OracleMisses = Oracle?.Misses ?? 0;
        report.GeometryAnswers = GeometryAnswers;
        report.UnansweredQueries = UnansweredQueries;
        report.UnansweredInWindow = UnansweredInWindow;
        report.RoutesRecomputed = RoutesRecomputed;
        foreach (KeyValuePair<int, MotionTrack> entry in tracks)
            if (entry.Value.Samples > 0)
                report.Motion.Add(entry.Value.ToSummary(entry.Key));
        report.Motion.Sort((a, b) => b.YawChurnDegrees.CompareTo(a.YawChurnDegrees));
        return report;
    }

    private readonly HashSet<int> _sampledThisFrame = new();

    private void Compare(ReproFrame frame, ReproReplayReport report)
    {
        _sampledThisFrame.Clear();
        foreach (ReproMotionSample expected in frame.Zombies)
        {
            _sampledThisFrame.Add(expected.Id);
            if (!_byId.TryGetValue(expected.Id, out ZombieInstance? live))
                continue;
            float error = (live.Position - ReproVector.To(expected.Position)).Length();
            float yawError = MathF.Abs(Mathf.Wrap(live.Yaw - expected.Yaw, -180f, 180f));
            report.ComparedSamples++;
            report.PositionSamples++;
            report.SumPositionError += error;
            report.MaxPositionError = MathF.Max(report.MaxPositionError, error);
            report.MaxYawError = MathF.Max(report.MaxYawError, yawError);
            if (live.State != expected.State)
                report.StateMismatches++;
            // Who a zombie is hunting is a behaviour a build can get wrong without moving an inch:
            // with two players in range, retargeting the other one looks identical in position and
            // state for the length of a short window.
            if (live.TargetPlayer != expected.Target)
                report.TargetMismatches++;
        }

        // A zombie the recording left out of this frame is a claim, not an absence: the recorder omits
        // exactly those that were idle with nobody to hunt, and therefore did not move. A build that
        // newly wakes one has nothing to contradict it unless that claim is checked.
        foreach (ZombieInstance live in System.Zombies)
        {
            if (_sampledThisFrame.Contains(live.Id))
                continue;
            report.ComparedSamples++;
            report.SilenceChecks++;
            if (live.State != EZombieState.Idle || live.TargetPlayer != byte.MaxValue)
                report.SilentWakeups++;
        }
    }

    private Vector3 ResolveMove(Vector3 from, Vector3 to, float radius)
    {
        if (Oracle != null && Oracle.TryMove(from, to, radius, out Vector3 recorded))
            return recorded;
        if (Collision != null)
        {
            // The capsule reaches `radius` sideways and its full height up from the feet.
            float reach = radius + ZombieBody.CapsuleTop;
            Recompute(Collision.Covers(from, reach) && Collision.Covers(to, reach));
            return Collision.Resolve(from, to, radius);
        }
        Unanswered();
        return to;
    }

    private bool ResolveGround(Vector3 position, out float y)
    {
        if (Oracle != null && Oracle.TryGround(position, out bool hit, out y))
            return hit;
        if (Collision != null)
        {
            Recompute(Collision.Covers(position, ZombieBody.GroundProbeDown));
            return Collision.GroundSnap(position, out y);
        }
        Unanswered();
        return SampleGround(position.X, position.Z, out y);
    }

    private bool ResolveVision(Vector3 from, Vector3 to)
    {
        if (Oracle != null && Oracle.TryVision(from, to, out bool blocked))
            return blocked;
        if (Collision != null)
        {
            Recompute(Collision.Covers(from) && Collision.Covers(to));
            return Collision.VisionBlocked(from, to);
        }
        Unanswered();
        return false;
    }

    private bool WithinNavmeshSlice(Vector3 point)
    {
        if (float.IsPositiveInfinity(_navmeshRadius))
            return true;
        float dx = point.X - _navmeshCentre.X, dz = point.Z - _navmeshCentre.Z;
        return (dx * dx) + (dz * dz) <= _navmeshRadius * _navmeshRadius;
    }

    // A query the geometry answered, and whether it was somewhere the capture actually looked. Outside
    // the slice the triangle soup is empty, so the answer is "open ground" whatever is really there —
    // the body still moves (a replay that froze would be worse), but the answer is booked as one
    // nobody could give rather than as evidence.
    private void Recompute(bool covered)
    {
        if (covered)
        {
            GeometryAnswers++;
            return;
        }
        Unanswered();
    }

    private bool ResolvePath(Vector3 from, Vector3 to, List<Vector3> path, float radius)
    {
        if (Oracle != null && Oracle.TryPath(from, to, radius, path, out bool found))
            return found;
        // Installed only alongside a pathfinder, so there is always one to fall through to. Whether it
        // can actually answer is another matter: the dump's own slice has triangles near the incident
        // and nowhere else, so a route asked for beyond it comes back "no route" from a graph that
        // never had that ground rather than from a map that has no way through.
        RoutesRecomputed++;
        if (_routesAreSliced && !(WithinNavmeshSlice(from) && WithinNavmeshSlice(to)))
        {
            Unanswered();
            return _pathQuery!(from, to, path, radius);
        }
        GeometryAnswers++;
        return _pathQuery!(from, to, path, radius);
    }

    private void Unanswered()
    {
        UnansweredQueries++;
        if (Tick < WindowTicks)
            UnansweredInWindow++;
    }

    // The dump's heightfield patch, as the brain's ground sampler. Outside the patch it answers false,
    // which is what the real sampler does off the edge of the map.
    private bool SampleGround(float x, float z, out float y)
    {
        y = 0f;
        return _heights != null && _heights.Sample(x, z, out y);
    }

    private sealed class MotionTrack
    {
        private Vector3 _first;
        private Vector3 _previous;
        private float _previousYaw;

        public MotionTrack(Vector3 position, float yaw, bool startedAwake)
        {
            _first = position;
            _previous = position;
            _previousYaw = yaw;
            StartedAwake = startedAwake;
        }

        public bool StartedAwake { get; }

        public float Travelled { get; private set; }

        public float YawChurn { get; private set; }

        public Vector3 Last { get; private set; }

        public EZombieState State { get; private set; }

        public float BlockedRouteTime { get; private set; }

        public int Samples { get; private set; }

        public void Add(ZombieInstance zombie)
        {
            Samples++;
            Travelled += (zombie.Position - _previous).Length();
            YawChurn += MathF.Abs(Mathf.Wrap(zombie.Yaw - _previousYaw, -180f, 180f));
            _previous = zombie.Position;
            _previousYaw = zombie.Yaw;
            Last = zombie.Position;
            State = zombie.State;
            BlockedRouteTime = MathF.Max(BlockedRouteTime, zombie.BlockedRouteTime);
        }

        public ReproMotionSummary ToSummary(int id) => new()
        {
            Id = id,
            TravelledMetres = Travelled,
            NetDisplacementMetres = (Last - _first).Length(),
            YawChurnDegrees = YawChurn,
            FinalState = State,
            WorstBlockedRouteTime = BlockedRouteTime,
        };
    }
}

// What a replay is worth, in numbers. Everything here is a claim someone can check rather than a
// verdict the harness hands down.
public sealed class ReproReplayReport
{
    public int Ticks { get; set; }
    public int ComparedSamples { get; set; }

    // Of those, the ones that carried a position to measure. A map with three hundred sleeping
    // zombies contributes three hundred checks per frame that no trajectory error can move, and
    // averaging the error over those would report a near-zero mean for a replay that is metres out
    // on every zombie that actually did something.
    public int PositionSamples { get; set; }
    public int SilenceChecks { get; set; }
    public float MaxPositionError { get; set; }
    public float SumPositionError { get; set; }
    public float MaxYawError { get; set; }
    public int StateMismatches { get; set; }
    public int TargetMismatches { get; set; }

    // Zombies the recording says stayed idle and this replay woke up. They contribute no position to
    // compare, so without this they diverge in silence.
    public int SilentWakeups { get; set; }
    public int OracleHits { get; set; }
    public int OracleMisses { get; set; }
    public int GeometryAnswers { get; set; }
    public int UnansweredQueries { get; set; }
    public int UnansweredInWindow { get; set; }
    public int RoutesRecomputed { get; set; }
    public List<ReproMotionSummary> Motion { get; } = new();

    public float MeanPositionError => PositionSamples > 0 ? SumPositionError / PositionSamples : 0f;

    // Did the replay reproduce the RECORDING, rather than merely something plausible? Only meaningful
    // for unmodified code; a millimetre is well inside the rounding a dump stores positions at.
    //
    // A query nobody could answer disqualifies the claim outright, however well the positions line up.
    // Part of the world was treated as empty for it, and a body that happened not to need that answer
    // this time is not evidence that the window reproduced — it is evidence that the window was lucky.
    public bool ReproducesRecording => ComparedSamples > 0 && MaxPositionError <= 0.01f
        && MaxYawError <= 0.5f && StateMismatches == 0 && TargetMismatches == 0
        && SilentWakeups == 0 && UnansweredInWindow == 0;

    public string Describe()
    {
        var text = new System.Text.StringBuilder();
        text.Append("replayed ").Append(Ticks).Append(" ticks; ")
            .Append(PositionSamples).Append(" motion samples compared, ")
            .Append(SilenceChecks).Append(" idle zombies checked for staying idle\n");
        text.Append("  position error   max ").Append(Format(MaxPositionError))
            .Append(" m, mean ").Append(Format(MeanPositionError)).Append(" m\n");
        text.Append("  yaw error        max ").Append(Format(MaxYawError)).Append("°\n");
        text.Append("  state mismatches ").Append(StateMismatches)
            .Append(", target mismatches ").Append(TargetMismatches)
            .Append(", woke up unexpectedly ").Append(SilentWakeups).Append('\n');
        text.Append("  answers          ").Append(OracleHits).Append(" recorded, ")
            .Append(GeometryAnswers).Append(" from geometry, ")
            .Append(UnansweredQueries).Append(" unanswered")
            .Append(UnansweredInWindow > 0 ? $" ({UnansweredInWindow} inside the window)" : "")
            .Append('\n');
        if (RoutesRecomputed > 0)
            text.Append("  routes           ").Append(RoutesRecomputed)
                .Append(" recomputed over the baked graph rather than replayed; the live map may have "
                    + "used the engine's pathfinder, so a divergence here is expected\n");
        text.Append("  verdict          ").Append(ReproducesRecording
            ? "reproduces the recording"
            : "diverges from the recording (expected if the code under replay changed)").Append('\n');
        foreach (ReproMotionSummary summary in Motion)
            text.Append("  zombie ").Append(summary.Id).Append("  travelled ")
                .Append(Format(summary.TravelledMetres)).Append(" m, net ")
                .Append(Format(summary.NetDisplacementMetres)).Append(" m, turned ")
                .Append(Format(summary.YawChurnDegrees)).Append("°, ")
                .Append(summary.FinalState)
                .Append(summary.IsSpinningInPlace ? "  <- spinning in place" : "")
                .Append('\n');
        return text.ToString();
    }

    private static string Format(float value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

// Per-zombie motion over a replay. Distance travelled against distance actually covered is the number
// that tells a stuck zombie from a walking one, whatever the cause turns out to be.
public sealed record ReproMotionSummary
{
    public int Id { get; init; }
    public float TravelledMetres { get; init; }
    public float NetDisplacementMetres { get; init; }
    public float YawChurnDegrees { get; init; }
    public float WorstBlockedRouteTime { get; init; }
    public EZombieState FinalState { get; init; }

    // Turned more than a full circle while getting nowhere: the shape of the report this harness was
    // built for, and a useful default for "did my fix work".
    public bool IsSpinningInPlace => YawChurnDegrees > 360f && NetDisplacementMetres < 1f;
}
