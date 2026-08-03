using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Repro;

// Replays the answers the live world gave, in the order it gave them.
//
// A query is matched inside its own tick, scanning forward from wherever the last match left off. That
// ordering matters: the resolver is called twice for the same body in one tick (once for the step, once
// after zombie-zombie separation), and serving the second call the first one's answer would quietly
// change the simulation.
//
// A miss is not an error and not hidden: it means the code being replayed asked something the session
// never asked, which is exactly what a CHANGED brain does. The caller falls back to the dump's geometry
// and the counts here say how much of the replay was recorded and how much was recomputed.
public sealed class ReproOracle
{
    // Recorded arguments are rounded to ReproVector.Digits, so a match has to tolerate at least that.
    private const float Tolerance = 1e-3f;
    private const float ToleranceSquared = Tolerance * Tolerance;

    // Which columns of an entry take part in the key. Every column layout puts the first point at
    // offset 2, the second at 5 and the radius at 8, so one matcher serves all four.
    private const int KeyPoint = 1;
    private const int KeySegment = 2;
    private const int KeySegmentRadius = 3;

    private readonly ReproOracleData _data;
    private readonly Column _move;
    private readonly Column _ground;
    private readonly Column _vision;
    private readonly Column _path;
    private int _tick;

    public int Hits { get; private set; }

    public int Misses { get; private set; }

    public ReproOracle(ReproOracleData data, int tickCount)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _move = new Column(data.Move, ReproOracleData.MoveStride, tickCount);
        _ground = new Column(data.Ground, ReproOracleData.GroundStride, tickCount);
        _vision = new Column(data.Vision, ReproOracleData.VisionStride, tickCount);
        _path = new Column(data.Path, ReproOracleData.PathStride, tickCount);
    }

    // Moves the replay onto a tick. Each tick is served from its own recording, and re-seeking a tick
    // hands its entries out again — so a scenario can be replayed more than once from the same dump.
    public void SeekTick(int tick)
    {
        _tick = Math.Max(0, tick);
        _move.Rewind(_tick);
        _ground.Rewind(_tick);
        _vision.Rewind(_tick);
        _path.Rewind(_tick);
    }

    public bool TryMove(Vector3 from, Vector3 to, float radius, out Vector3 result)
    {
        result = to;
        int match = _move.Find(_tick, KeySegmentRadius, from, to, radius);
        if (match < 0)
            return Miss();
        int offset = match * ReproOracleData.MoveStride;
        result = new Vector3(_data.Move[offset + 9], _data.Move[offset + 10], _data.Move[offset + 11]);
        return Hit();
    }

    public bool TryGround(Vector3 position, out bool hit, out float y)
    {
        hit = false;
        y = position.Y;
        int match = _ground.Find(_tick, KeyPoint, position, default, 0f);
        if (match < 0)
            return Miss();
        int offset = match * ReproOracleData.GroundStride;
        hit = _data.Ground[offset + 5] != 0f;
        y = _data.Ground[offset + 6];
        return Hit();
    }

    public bool TryVision(Vector3 from, Vector3 to, out bool blocked)
    {
        blocked = false;
        int match = _vision.Find(_tick, KeySegment, from, to, 0f);
        if (match < 0)
            return Miss();
        blocked = _data.Vision[(match * ReproOracleData.VisionStride) + 8] != 0f;
        return Hit();
    }

    public bool TryPath(Vector3 from, Vector3 to, float radius, List<Vector3> path, out bool found)
    {
        ArgumentNullException.ThrowIfNull(path);
        found = false;
        int match = _path.Find(_tick, KeySegmentRadius, from, to, radius);
        if (match < 0)
            return Miss();
        int offset = match * ReproOracleData.PathStride;
        found = _data.Path[offset + 9] != 0f;
        var pointStart = (int)_data.Path[offset + 10];
        var pointCount = (int)_data.Path[offset + 11];
        path.Clear();
        for (int i = 0; i < pointCount; i++)
        {
            int at = (pointStart + i) * 3;
            if (at + 2 < _data.PathPoints.Count)
                path.Add(new Vector3(_data.PathPoints[at], _data.PathPoints[at + 1],
                    _data.PathPoints[at + 2]));
        }
        return Hit();
    }

    // The pathfinder's readiness for this tick. Absent (a window with no recording) reads as ready,
    // which is what ZombieSystem itself does with no PathReady delegate installed.
    public bool Ready()
    {
        for (int i = 0; i + 1 < _data.Ready.Count; i += ReproOracleData.ReadyStride)
            if ((int)_data.Ready[i] == _tick)
                return _data.Ready[i + 1] != 0f;
        return true;
    }

    private bool Hit()
    {
        Hits++;
        return true;
    }

    private bool Miss()
    {
        Misses++;
        return false;
    }

    // One recorded column: the flat numbers, where each tick's entries begin, and which entries this
    // pass has already handed out.
    private sealed class Column
    {
        private readonly List<float> _values;
        private readonly int _stride;
        private readonly int[] _start;
        private readonly bool[] _used;
        private int _cursor;

        public Column(List<float> values, int stride, int tickCount)
        {
            _values = values;
            _stride = stride;
            int entries = values.Count / stride;
            _used = new bool[entries];
            // Entries are written in tick order, so counting them indexes the column in one pass.
            _start = new int[tickCount + 2];
            for (int i = 0; i < entries; i++)
                _start[Math.Clamp((int)values[i * stride], 0, tickCount) + 1]++;
            for (int i = 1; i < _start.Length; i++)
                _start[i] += _start[i - 1];
        }

        public void Rewind(int tick)
        {
            _cursor = 0;
            if (tick + 1 >= _start.Length)
                return;
            for (int i = _start[tick]; i < _start[tick + 1]; i++)
                _used[i] = false;
        }

        public int Find(int tick, int key, Vector3 a, Vector3 b, float radius)
        {
            if (tick + 1 >= _start.Length)
                return -1;
            int start = _start[tick], end = _start[tick + 1];
            // Forward from the cursor first (the common case: calls arrive in the recorded order),
            // then wrap to the start of the tick so a reordered but identical query still matches.
            for (int pass = 0; pass < 2; pass++)
            {
                int from = pass == 0 ? start + _cursor : start;
                int to = pass == 0 ? end : Math.Min(end, start + _cursor);
                for (int i = from; i < to; i++)
                {
                    if (_used[i] || !Matches(i * _stride, key, a, b, radius))
                        continue;
                    _used[i] = true;
                    _cursor = i - start + 1;
                    return i;
                }
            }
            return -1;
        }

        private bool Matches(int at, int key, Vector3 a, Vector3 b, float radius)
        {
            if (!Near(at + 2, a))
                return false;
            if (key >= KeySegment && !Near(at + 5, b))
                return false;
            return key < KeySegmentRadius || MathF.Abs(_values[at + 8] - radius) <= Tolerance;
        }

        private bool Near(int at, Vector3 value)
        {
            float dx = _values[at] - value.X;
            float dy = _values[at + 1] - value.Y;
            float dz = _values[at + 2] - value.Z;
            return (dx * dx) + (dy * dy) + (dz * dz) <= ToleranceSquared;
        }
    }
}
