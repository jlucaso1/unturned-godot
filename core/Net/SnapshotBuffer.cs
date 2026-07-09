using Godot;

namespace UnturnedGodot.Net;

// A remote player's pose at one instant, interpolated between server states (Unturned's
// PitchYawSnapshotInfo: position lerps, pitch/yaw lerp as angles).
public readonly struct PoseSnapshot
{
    public readonly Vector3 Position;
    public readonly float Pitch;
    public readonly float Yaw;

    public PoseSnapshot(Vector3 position, float pitch, float yaw)
    {
        Position = position;
        Pitch = pitch;
        Yaw = yaw;
    }

    public PoseSnapshot Lerp(in PoseSnapshot target, float delta) => new(
        Position.Lerp(target.Position, delta),
        Mathf.LerpAngle(Mathf.DegToRad(Pitch), Mathf.DegToRad(target.Pitch), delta) * (180f / Mathf.Pi),
        Mathf.LerpAngle(Mathf.DegToRad(Yaw), Mathf.DegToRad(target.Yaw), delta) * (180f / Mathf.Pi));
}

// Faithful port of Unturned's NetworkSnapshotBuffer<T>: server states arrive at readDuration intervals
// (Provider.UPDATE_TIME = 0.08 s) and are read readDelay behind arrival (UPDATE_DELAY = 0.1 s) so there is
// usually a next snapshot to interpolate toward. Falling more than 4 snapshots behind snaps to the newest
// (the catch-up branch). Time is an explicit parameter, so tests are deterministic.
public sealed class SnapshotBuffer
{
    public const float UpdateTime = 0.08f; // Provider.UPDATE_TIME
    public const float UpdateDelay = 0.1f; // Provider.UPDATE_DELAY
    private const int Capacity = 16;

    private readonly (PoseSnapshot Info, double Timestamp)[] _snapshots = new (PoseSnapshot, double)[Capacity];
    private readonly float _readDuration;
    private readonly float _readDelay;

    private int _readIndex;
    private int _readCount;
    private int _writeIndex;
    private int _writeCount;
    private PoseSnapshot _lastInfo;
    private double _readLast;

    public SnapshotBuffer(float readDuration = UpdateTime, float readDelay = UpdateDelay)
    {
        _readDuration = readDuration;
        _readDelay = readDelay;
    }

    // Resets interpolation to this pose (spawn, teleport, large skips — tellState's LARGE_DISTANCE path).
    public void UpdateLastSnapshot(in PoseSnapshot info, double now)
    {
        _readIndex = 0;
        _readCount = 0;
        _writeIndex = 0;
        _writeCount = 0;
        _lastInfo = info;
        _readLast = now;
    }

    public void AddNewSnapshot(in PoseSnapshot info, double now)
    {
        _snapshots[_writeIndex] = (info, now);
        _writeIndex = (_writeIndex + 1) % Capacity;
        _writeCount++;
    }

    public PoseSnapshot GetCurrentSnapshot(double now)
    {
        int space = _writeCount - _readCount; // how far ahead the writing is from the reading

        if (space <= 0) // nothing to lerp toward
        {
            _readLast = now;
            return _lastInfo;
        }

        if (space > 4) // lagging far behind: catch up to the newest snapshot
        {
            _readIndex = _writeIndex == 0 ? Capacity - 1 : _writeIndex - 1;
            _readCount = _writeCount - 1;
            _lastInfo = _snapshots[_readIndex].Info;
            _readLast = now;
            return _lastInfo;
        }

        if (now - _readLast > _readDuration && space > 1) // finished interpolating this snapshot
        {
            _lastInfo = _snapshots[_readIndex].Info;
            _readLast += _readDuration;
            _readIndex = (_readIndex + 1) % Capacity;
            _readCount++;
        }

        if (now - _snapshots[_readIndex].Timestamp < _readDelay) // wait for the arrival delay to elapse
        {
            _readLast = now;
            return _lastInfo;
        }

        float delta = Mathf.Clamp((float)((now - _readLast) / _readDuration), 0f, 1f);
        return _lastInfo.Lerp(_snapshots[_readIndex].Info, delta);
    }
}
