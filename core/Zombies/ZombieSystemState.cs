using System;
using System.Collections.Generic;
using UnturnedGodot.Data;
using UnturnedGodot.Repro;

namespace UnturnedGodot.Zombies;

// Everything the brain carries between ticks, lifted out as data.
//
// A bug report that says "the zombie spins at this pole" is only reproducible if the simulation can be
// put back exactly where it was, and "exactly" here is more than the zombie's position: the route it is
// following, how long it has been failing to make progress on that route, whose turn it is in the
// repath queue, how much of the detection interval has elapsed, and how many zombies each player has
// on them (which decides whether the next one to aggro rushes or flanks). Miss any of those and the
// replay is a similar situation rather than the same one.
public sealed class ZombieSystemState
{
    public float DetectTimer;
    public int RepathCursor;

    // The generator's state, when it is one we can read (see ReproRandom). A session running on a plain
    // System.Random cannot hand its sequence over, and a replay of that dump re-rolls from the seed
    // below instead — the flag says which of the two you are looking at.
    public ulong RandomState;
    public bool RandomIsReproducible;

    // Order matters and is part of the state: Tick walks this list in order, and zombie-zombie
    // separation resolves against whoever moved first.
    public List<ZombieInstance> Zombies { get; } = new();

    // Player.agro per player id.
    public Dictionary<byte, int> Agro { get; } = new();

    public static ZombieInstance Clone(ZombieInstance source)
    {
        var copy = new ZombieInstance();
        CopyInto(source, copy);
        return copy;
    }

    // Field-by-field into an EXISTING instance. The recorder re-snapshots the whole population every
    // few seconds while a session runs, and a population is hundreds of zombies; copying into buffers
    // it already owns is what keeps that from being an allocation the game has to pay for.
    public static void CopyInto(ZombieInstance source, ZombieInstance destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        destination.Id = source.Id;
        destination.Bound = source.Bound;
        destination.Type = source.Type;
        destination.Speciality = source.Speciality;
        destination.Shirt = source.Shirt;
        destination.Pants = source.Pants;
        destination.Hat = source.Hat;
        destination.Gear = source.Gear;
        destination.Move = source.Move;
        destination.Idle = source.Idle;
        destination.IsHyper = source.IsHyper;
        destination.Home = source.Home;
        destination.Position = source.Position;
        destination.Yaw = source.Yaw;
        destination.State = source.State;
        destination.TargetPlayer = source.TargetPlayer;
        destination.Path = source.Path;
        destination.SinceSwing = source.SinceSwing;
        destination.PendingHit = source.PendingHit;
        destination.LeaveDelay = source.LeaveDelay;
        destination.LeaveTo = source.LeaveTo;
        destination.RepathGranted = source.RepathGranted;
        destination.CurrentWaypointIndex = source.CurrentWaypointIndex;
        destination.TargetReached = source.TargetReached;
        destination.PathIsPartial = source.PathIsPartial;
        destination.SteerDirection = source.SteerDirection;
        destination.RepathTimer = source.RepathTimer;
        destination.BlockedRouteTime = source.BlockedRouteTime;
        destination.RouteServedAnotherTarget = source.RouteServedAnotherTarget;
        destination.PathPoints.Clear();
        destination.PathPoints.AddRange(source.PathPoints);
    }
}

// A seam for watching the authoritative tick from outside the brain. The recorder uses it to bracket
// each tick — snapshot the state before, the resulting motion after — without ZombieHost, the network
// layer or the Godot host having to know that a recording is happening at all.
public interface IZombieTickObserver
{
    void BeginTick(ZombieSystem system, IReadOnlyList<ZombiePlayerView> players, float dt);

    void EndTick(ZombieSystem system);
}

public sealed partial class ZombieSystem
{
    // Set while Detect/Behave work on one zombie, so a recorder can attribute the world queries those
    // make (path, collision, ground, vision) to whoever asked. Null outside the per-zombie work.
    public ZombieInstance? CurrentZombie { get; private set; }

    public IZombieTickObserver? Observer;

    public ZombieSystemState CaptureState() => CaptureStateInto(new ZombieSystemState());

    // Snapshots into a buffer the caller keeps. Steady-state recording reuses one, so a session that
    // is armed for a bug report allocates nothing per snapshot after the first.
    public ZombieSystemState CaptureStateInto(ZombieSystemState into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.DetectTimer = _detectTimer;
        into.RepathCursor = _repathCursor;
        into.RandomIsReproducible = _random is ReproRandom;
        into.RandomState = _random is ReproRandom reproducible ? reproducible.State : 0UL;

        List<ZombieInstance> buffer = into.Zombies;
        for (int i = 0; i < _zombies.Count; i++)
        {
            if (i < buffer.Count)
                ZombieSystemState.CopyInto(_zombies[i], buffer[i]);
            else
                buffer.Add(ZombieSystemState.Clone(_zombies[i]));
        }
        if (buffer.Count > _zombies.Count)
            buffer.RemoveRange(_zombies.Count, buffer.Count - _zombies.Count);

        into.Agro.Clear();
        foreach (KeyValuePair<byte, int> entry in _agro)
            into.Agro[entry.Key] = entry.Value;
        return into;
    }

    // What a dump needs to rebuild this level's brain: the nav world it was constructed with.
    public IReadOnlyList<NavBound> Bounds => _bounds;

    public IReadOnlyList<ZombieTable> Tables => _tables;

    // Replaces the population wholesale. The zombies handed in are adopted as-is (clone first if the
    // caller means to keep them), which is what lets a replay hold the dump's own instances and read
    // their fields as the simulation moves them.
    public void RestoreState(ZombieSystemState state, ulong randomSeed = 0UL)
    {
        ArgumentNullException.ThrowIfNull(state);
        _zombies.Clear();
        foreach (List<ZombieInstance> bound in _byBound)
            bound.Clear();
        _agro.Clear();
        foreach (ZombieInstance zombie in state.Zombies)
        {
            if (zombie.Bound >= _byBound.Length)
                throw new ArgumentOutOfRangeException(nameof(state),
                    $"zombie {zombie.Id} belongs to nav bound {zombie.Bound}, and this level has "
                    + $"{_byBound.Length}: the dump was taken on another map.");
            _zombies.Add(zombie);
            _byBound[zombie.Bound].Add(zombie);
        }
        foreach (KeyValuePair<byte, int> entry in state.Agro)
            _agro[entry.Key] = entry.Value;
        _detectTimer = state.DetectTimer;
        _repathCursor = state.RepathCursor;
        // A dump from a session that was not running a readable generator still replays; its rolls
        // simply start from the seed the caller picked instead of continuing the recorded sequence.
        var random = new ReproRandom(randomSeed);
        if (state.RandomIsReproducible)
            random.State = state.RandomState;
        _random = random;
    }
}
