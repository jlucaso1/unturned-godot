using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Repro;

public sealed class ReproRecorderOptions
{
    // How much history is kept, in authoritative ticks (0.08 s each). 64 is a little over five
    // seconds: long enough that "it just did the thing" is inside the window when you reach for the
    // key, short enough that the whole ring is a couple of megabytes of live objects.
    public int WindowTicks { get; init; } = 64;

    // A hard ceiling on world queries kept for one tick, so a pathological frame cannot grow the ring
    // without bound. Anything past it is counted (see DroppedCalls) rather than silently lost.
    public int MaxCallsPerTick { get; init; } = 8192;
}

// Keeps the last few seconds of the zombie simulation in memory so that a key press can turn them into
// a dump. Nothing is written to disk, or even converted to dump records, until someone captures.
//
// Cost, because a recorder that makes the game stutter would never be left armed and a recorder that is
// not armed never catches anything:
//   - Attaching wraps the four world delegates. That is one extra delegate hop per query, against a
//     physics raycast — unmeasurable.
//   - Per tick it appends one small struct per query into lists it already owns, and copies the awake
//     zombies' motion. No allocation once the ring's lists have grown.
//   - The FULL brain state (every zombie, its route, its timers) is snapshotted only twice per window,
//     into two buffers that are reused forever, and the capture emits from the older of the two. So a
//     capture always has at least half a window of history, and the steady cost of carrying it is a
//     population copy every ~2.5 s rather than one every tick.
// Not creating a recorder at all costs exactly nothing: the system runs its own delegates.
public sealed class ReproRecorder : IZombieTickObserver
{
    private readonly ZombieSystem _system;
    private readonly ReproRecorderOptions _options;
    private readonly TickBuffer[] _ring;
    private readonly ZombieSystemState[] _anchors = { new(), new() };
    private readonly uint[] _anchorTicks = new uint[2];
    private readonly bool[] _anchorValid = new bool[2];
    private int _nextAnchor;

    private ZombieMoveResolver? _innerMove;
    private ZombieGroundSnap? _innerGround;
    private VisionBlocked? _innerVision;
    private ZombiePathQuery? _innerPath;
    private Func<bool>? _innerReady;
    private IZombieTickObserver? _innerObserver;
    private bool _attached;

    private uint _tick;         // the recorder's own monotonic tick counter
    private int _cursor = -1;   // ring slot of the tick being recorded
    private int _recorded;      // how many ticks are in the ring (saturates at WindowTicks)

    public ReproRecorder(ZombieSystem system, ReproRecorderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        _system = system;
        _options = options ?? new ReproRecorderOptions();
        int capacity = Math.Max(2, _options.WindowTicks);
        _ring = new TickBuffer[capacity];
        for (int i = 0; i < capacity; i++)
            _ring[i] = new TickBuffer();
    }

    // Whether each zombie was worth a motion sample last tick, indexed by id. A zombie that SETTLES
    // during the window has to be sampled the tick it settles, or a replay of changed code that keeps
    // chasing has nothing to disagree with and reports an exact reproduction.
    private bool[] _sampled = Array.Empty<bool>();

    // World queries dropped by the per-tick ceiling, over the RETAINED window only. A lifetime counter
    // would let one pathological tick taint every dump taken afterwards, long after the tick that
    // overflowed had rotated out of the ring.
    public long DroppedCalls
    {
        get
        {
            long dropped = 0;
            uint first = _tick - (uint)_recorded;
            for (uint t = first; t < _tick; t++)
                dropped += _ring[(int)(t % (uint)_ring.Length)].Dropped;
            return dropped;
        }
    }

    public int RecordedTicks => _recorded;

    // Which of the world's questions the session was actually able to ask. A dedicated server installs
    // only a pathfinder — no collision, no ground, no vision — and a replay that helpfully installs all
    // four is not a more faithful reproduction, it is a different simulation: ZombieSystem branches on
    // each of them being null.
    public ReproWorldSeams Seams => new()
    {
        MoveResolver = _innerMove != null,
        GroundSnap = _innerGround != null,
        VisionBlocked = _innerVision != null,
        PathQuery = _innerPath != null,
    };

    // Throws away the history and starts again from here. Loading a dump into a running session
    // replaces the population wholesale, and a capture taken straight afterwards would otherwise
    // splice a pre-load anchor onto post-load ticks and replay as neither situation.
    public void Restart()
    {
        _tick = 0;
        _recorded = 0;
        _cursor = -1;
        _nextAnchor = 0;
        Array.Clear(_anchorValid);
        Array.Clear(_anchorTicks);
        Array.Clear(_sampled);
        foreach (TickBuffer buffer in _ring)
        {
            buffer.Reset();
            buffer.Tick = 0;
        }
    }

    // Wraps the system's world delegates and takes the tick seam. Idempotent.
    public void Attach()
    {
        if (_attached)
            return;
        _attached = true;
        _innerMove = _system.MoveResolver;
        _innerGround = _system.GroundSnap;
        _innerVision = _system.VisionBlocked;
        _innerPath = _system.PathQuery;
        _innerReady = _system.PathReady;
        _innerObserver = _system.Observer;

        if (_innerMove != null)
            _system.MoveResolver = RecordMove;
        if (_innerGround != null)
            _system.GroundSnap = RecordGround;
        if (_innerVision != null)
            _system.VisionBlocked = RecordVision;
        if (_innerPath != null)
            _system.PathQuery = RecordPath;
        if (_innerReady != null)
            _system.PathReady = RecordReady;
        _system.Observer = this;
    }

    public void Detach()
    {
        if (!_attached)
            return;
        _attached = false;
        _system.MoveResolver = _innerMove;
        _system.GroundSnap = _innerGround;
        _system.VisionBlocked = _innerVision;
        _system.PathQuery = _innerPath;
        _system.PathReady = _innerReady;
        _system.Observer = _innerObserver;
    }

    public void BeginTick(ZombieSystem system, IReadOnlyList<ZombiePlayerView> players, float dt)
    {
        ArgumentNullException.ThrowIfNull(players);
        // Re-anchor on the half-window boundary, alternating buffers: the older anchor is then always
        // between half a window and a full window old, and always still covered by the ring.
        int half = Math.Max(1, _ring.Length / 2);
        if (_tick % (uint)half == 0)
        {
            _system.CaptureStateInto(_anchors[_nextAnchor]);
            _anchorTicks[_nextAnchor] = _tick;
            _anchorValid[_nextAnchor] = true;
            _nextAnchor ^= 1;
        }

        _cursor = (int)(_tick % (uint)_ring.Length);
        TickBuffer buffer = _ring[_cursor];
        buffer.Reset();
        buffer.Tick = _tick;
        buffer.ServerTick = system.AuthoritativeTick;
        buffer.Dt = dt;
        for (int i = 0; i < players.Count; i++)
            buffer.Players.Add(players[i]);
        _innerObserver?.BeginTick(system, players, dt);
    }

    public void EndTick(ZombieSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (_cursor >= 0)
        {
            TickBuffer buffer = _ring[_cursor];
            IReadOnlyList<ZombieInstance> zombies = system.Zombies;
            for (int i = 0; i < zombies.Count; i++)
            {
                ZombieInstance zombie = zombies[i];
                if (zombie.Id >= _sampled.Length)
                    Array.Resize(ref _sampled, Math.Max(zombie.Id + 1, _sampled.Length * 2));
                // Idle zombies without a target do not move, so their positions are already in the
                // anchor state. Skipping them is what keeps a frame proportional to what is HAPPENING
                // rather than to how many zombies the map spawned — but the tick one SETTLES on is
                // part of what happened, so the first idle frame after activity is kept.
                bool awake = zombie.State != EZombieState.Idle || zombie.TargetPlayer != byte.MaxValue;
                if (!awake && !_sampled[zombie.Id])
                    continue;
                _sampled[zombie.Id] = awake;
                buffer.Motions.Add(new Motion(zombie.Id, zombie.Position, zombie.Yaw, zombie.State,
                    zombie.TargetPlayer));
            }
        }
        _tick++;
        if (_recorded < _ring.Length)
            _recorded++;
        // The tick is over: anything the world is asked from here until the next one is somebody
        // else's question (a probe, a diagnostic, the host's own lookup) and does not belong in the
        // frame that just closed.
        _cursor = -1;
        _innerObserver?.EndTick(system);
    }

    private Vector3 RecordMove(Vector3 from, Vector3 to, float radius)
    {
        Vector3 result = _innerMove!(from, to, radius);
        if (TryRecord(out TickBuffer? buffer))
            buffer!.Moves.Add(new MoveCall(Asking, from, to, radius, result));
        return result;
    }

    private bool RecordGround(Vector3 position, out float y)
    {
        bool hit = _innerGround!(position, out y);
        if (TryRecord(out TickBuffer? buffer))
            buffer!.Grounds.Add(new GroundCall(Asking, position, hit, y));
        return hit;
    }

    private bool RecordVision(Vector3 from, Vector3 to)
    {
        bool blocked = _innerVision!(from, to);
        if (TryRecord(out TickBuffer? buffer))
            buffer!.Visions.Add(new VisionCall(Asking, from, to, blocked));
        return blocked;
    }

    private bool RecordPath(Vector3 from, Vector3 to, List<Vector3> path, float radius)
    {
        bool found = _innerPath!(from, to, path, radius);
        if (TryRecord(out TickBuffer? buffer))
        {
            int start = buffer!.PathPoints.Count;
            for (int i = 0; i < path.Count; i++)
                buffer.PathPoints.Add(path[i]);
            buffer.Paths.Add(new PathCall(Asking, from, to, radius, found, start, path.Count));
        }
        return found;
    }

    private bool RecordReady()
    {
        bool ready = _innerReady!();
        if (_cursor >= 0)
            _ring[_cursor].Ready = ready;
        return ready;
    }

    // Whoever the brain is working on right now, or -1 between zombies (the readiness poll).
    private int Asking => _system.CurrentZombie?.Id ?? -1;

    private bool TryRecord(out TickBuffer? buffer)
    {
        buffer = null;
        if (_cursor < 0)
            return false; // a query outside any tick (a probe, a host's own lookup): not our business
        TickBuffer candidate = _ring[_cursor];
        if (candidate.CallCount >= _options.MaxCallsPerTick)
        {
            candidate.Dropped++;
            return false;
        }
        buffer = candidate;
        return true;
    }

    // Turns the history into a section. This is the only place that allocates dump records, and it runs
    // once per bug report.
    public ReproZombieSection Build(out uint startTick, out int tickCount)
    {
        int anchor = OldestAnchor();
        if (anchor < 0)
        {
            startTick = 0;
            tickCount = 0;
            return new ReproZombieSection(); // nothing ticked yet: an empty window, honestly reported
        }

        var section = new ReproZombieSection
        {
            State = ReproZombieState.ToRecord(_anchors[anchor]),
        };
        uint from = _anchorTicks[anchor];

        // The ring holds exactly the last _recorded ticks, and an anchor is at most half a window old,
        // so every tick from here on is still in it — no slot has to be checked for having been
        // overwritten, and the frame index the oracle is written against cannot develop a gap.
        uint first = Math.Max(from, _tick - (uint)_recorded);

        int index = 0;
        for (uint t = first; t < _tick; t++, index++)
        {
            TickBuffer buffer = _ring[(int)(t % (uint)_ring.Length)];
            section.Frames.Add(buffer.ToFrame());
            buffer.WriteOracle(section.Oracle, index);
        }
        startTick = _anchorTicks[anchor];
        tickCount = index;
        return section;
    }

    private int OldestAnchor()
    {
        int best = -1;
        for (int i = 0; i < _anchors.Length; i++)
        {
            if (!_anchorValid[i])
                continue;
            if (best < 0 || _anchorTicks[i] < _anchorTicks[best])
                best = i;
        }
        return best;
    }

    // The server tick the emitted window starts at, for a dump's metadata.
    public uint ServerTickOfWindowStart
    {
        get
        {
            int anchor = OldestAnchor();
            if (anchor < 0)
                return 0u;
            uint tick = _anchorTicks[anchor];
            return _ring[(int)(tick % (uint)_ring.Length)].ServerTick;
        }
    }

    private readonly record struct MoveCall(int Zombie, Vector3 From, Vector3 To, float Radius,
        Vector3 Result);

    private readonly record struct GroundCall(int Zombie, Vector3 Position, bool Hit, float Y);

    private readonly record struct VisionCall(int Zombie, Vector3 From, Vector3 To, bool Blocked);

    private readonly record struct PathCall(int Zombie, Vector3 From, Vector3 To, float Radius,
        bool Found, int Start, int Count);

    private readonly record struct Motion(int Id, Vector3 Position, float Yaw, EZombieState State,
        byte Target);

    // One tick's worth of history. Every list here is cleared and refilled rather than replaced, which
    // is what makes an armed session allocation-free after its first few seconds.
    private sealed class TickBuffer
    {
        public uint Tick;
        public uint ServerTick;
        public float Dt;
        public bool Ready = true;
        public long Dropped;
        public readonly List<ZombiePlayerView> Players = new();
        public readonly List<Motion> Motions = new();
        public readonly List<MoveCall> Moves = new();
        public readonly List<GroundCall> Grounds = new();
        public readonly List<VisionCall> Visions = new();
        public readonly List<PathCall> Paths = new();
        public readonly List<Vector3> PathPoints = new();

        public int CallCount => Moves.Count + Grounds.Count + Visions.Count + Paths.Count;

        public void Reset()
        {
            Ready = true;
            Dropped = 0;
            Players.Clear();
            Motions.Clear();
            Moves.Clear();
            Grounds.Clear();
            Visions.Clear();
            Paths.Clear();
            PathPoints.Clear();
        }

        public ReproFrame ToFrame()
        {
            var frame = new ReproFrame { Tick = ServerTick, Dt = Dt };
            for (int i = 0; i < Players.Count; i++)
                frame.Players.Add(ReproZombieState.ToSample(Players[i]));
            for (int i = 0; i < Motions.Count; i++)
            {
                Motion motion = Motions[i];
                frame.Zombies.Add(new ReproMotionSample
                {
                    Id = motion.Id,
                    Position = ReproVector.From(motion.Position),
                    Yaw = ReproVector.Round(motion.Yaw),
                    State = motion.State,
                    Target = motion.Target,
                });
            }
            return frame;
        }

        public void WriteOracle(ReproOracleData oracle, int index)
        {
            foreach (MoveCall call in Moves)
            {
                oracle.Move.Add(index);
                oracle.Move.Add(call.Zombie);
                Add(oracle.Move, call.From);
                Add(oracle.Move, call.To);
                oracle.Move.Add(ReproVector.Round(call.Radius));
                Add(oracle.Move, call.Result);
            }
            foreach (GroundCall call in Grounds)
            {
                oracle.Ground.Add(index);
                oracle.Ground.Add(call.Zombie);
                Add(oracle.Ground, call.Position);
                oracle.Ground.Add(call.Hit ? 1f : 0f);
                oracle.Ground.Add(ReproVector.Round(call.Y));
            }
            foreach (VisionCall call in Visions)
            {
                oracle.Vision.Add(index);
                oracle.Vision.Add(call.Zombie);
                Add(oracle.Vision, call.From);
                Add(oracle.Vision, call.To);
                oracle.Vision.Add(call.Blocked ? 1f : 0f);
            }
            foreach (PathCall call in Paths)
            {
                oracle.Path.Add(index);
                oracle.Path.Add(call.Zombie);
                Add(oracle.Path, call.From);
                Add(oracle.Path, call.To);
                oracle.Path.Add(ReproVector.Round(call.Radius));
                oracle.Path.Add(call.Found ? 1f : 0f);
                oracle.Path.Add(oracle.PathPoints.Count / 3);
                oracle.Path.Add(call.Count);
                for (int i = 0; i < call.Count; i++)
                    Add(oracle.PathPoints, PathPoints[call.Start + i]);
            }
            oracle.Ready.Add(index);
            oracle.Ready.Add(Ready ? 1f : 0f);
        }

        private static void Add(List<float> into, Vector3 value)
        {
            into.Add(ReproVector.Round(value.X));
            into.Add(ReproVector.Round(value.Y));
            into.Add(ReproVector.Round(value.Z));
        }
    }
}
