using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Repro;

public sealed class ReproWatcherOptions
{
    // How long a zombie has to keep it up before it counts. Shorter windows catch the ordinary
    // half-second of turning at a corner, which is not a bug.
    public float WindowSeconds { get; init; } = 3f;

    // Turning a full circle and a half inside that window.
    public float MinYawChurnDegrees { get; init; } = 540f;

    // ...while ending up within this of where it started. A zombie chasing someone covers ~16 m in
    // three seconds, so anything under a couple of metres is not a chase.
    public float MaxDisplacementMetres { get; init; } = 1.5f;

    // A zombie can also be plainly wedged: a route it has failed to make progress on for nearly as
    // long as the brain's own patience with it.
    //
    // NOT the timeout itself. ApplyStep accumulates BlockedRouteTime in tick-sized steps and zeroes it
    // the instant it reaches BlockedRouteTimeout, inside the same tick — so a watcher looking after
    // the tick, as this one does, can never see the timeout value. The largest it can ever observe is
    // one tick below it, which is what this is.
    public float MaxBlockedRouteTime { get; init; } =
        ZombieSystem.BlockedRouteTimeout - ServerSimulation.TickRate;

    // One capture per incident, not one per tick.
    public float CooldownSeconds { get; init; } = 30f;
}

// Notices a zombie behaving like a bug report and says where to point the capture.
//
// It is deliberately about SYMPTOMS rather than about any one cause: turning without going anywhere,
// or failing to make progress on a route it keeps. Both are things a player would describe as "it is
// stuck doing something weird", and neither assumes what is wrong. New symptoms belong here as new
// rules; nothing else in the harness knows what a bug looks like.
public sealed class ReproWatcher
{
    private readonly ReproWatcherOptions _options;
    private readonly Dictionary<int, Track> _tracks = new();
    private readonly List<int> _stale = new();
    private float _cooldown;

    public ReproWatcher(ReproWatcherOptions? options = null) =>
        _options = options ?? new ReproWatcherOptions();

    // Forgets everything it has been watching. Loading a dump replaces the population but reuses its
    // ids, so a track left over from the session before would blend two different simulations into one
    // window — and the dump most likely to be loaded is the one whose incident filled that track with
    // exactly the churn or blockage this looks for.
    public void Forget()
    {
        _tracks.Clear();
        _cooldown = 0f;
    }

    // Feeds one tick in and reports the first zombie that qualifies. Cheap by construction: a handful
    // of floats per AWAKE zombie, and idle ones are dropped as soon as they settle.
    public bool Poll(ZombieSystem system, float dt, out string reason, out Vector3 focus)
    {
        ArgumentNullException.ThrowIfNull(system);
        reason = "";
        focus = Vector3.Zero;
        if (_cooldown > 0f)
            _cooldown = MathF.Max(0f, _cooldown - dt);

        bool fired = false;
        foreach (ZombieInstance zombie in system.Zombies)
        {
            if (zombie.State == EZombieState.Idle && zombie.TargetPlayer == byte.MaxValue)
            {
                _tracks.Remove(zombie.Id);
                continue;
            }
            if (!_tracks.TryGetValue(zombie.Id, out Track? track))
                _tracks[zombie.Id] = track = new Track(zombie);

            track.Elapsed += dt;
            track.Churn += MathF.Abs(Mathf.Wrap(zombie.Yaw - track.LastYaw, -180f, 180f));
            track.LastYaw = zombie.Yaw;
            track.WorstBlocked = MathF.Max(track.WorstBlocked, zombie.BlockedRouteTime);
            if (track.Elapsed < _options.WindowSeconds)
                continue;

            float displacement = (zombie.Position - track.Start).Length();
            if (!fired && _cooldown <= 0f)
            {
                if (track.Churn >= _options.MinYawChurnDegrees
                    && displacement <= _options.MaxDisplacementMetres)
                {
                    reason = $"auto:spin zombie {zombie.Id} turned {track.Churn:0}° in "
                        + $"{track.Elapsed:0.#} s and moved {displacement:0.##} m";
                    focus = zombie.Position;
                    fired = true;
                }
                else if (track.WorstBlocked >= _options.MaxBlockedRouteTime
                    && displacement <= _options.MaxDisplacementMetres)
                {
                    reason = $"auto:stuck zombie {zombie.Id} made no route progress for "
                        + $"{track.WorstBlocked:0.##} s and moved {displacement:0.##} m";
                    focus = zombie.Position;
                    fired = true;
                }
            }
            track.Restart(zombie);
        }

        // Tracks for zombies that vanished (a level change, a rebuilt population) go with them.
        if (_tracks.Count > system.Zombies.Count)
        {
            _stale.Clear();
            foreach (int id in _tracks.Keys)
            {
                bool present = false;
                foreach (ZombieInstance zombie in system.Zombies)
                {
                    present = zombie.Id == id;
                    if (present)
                        break;
                }
                if (!present)
                    _stale.Add(id);
            }
            foreach (int id in _stale)
                _tracks.Remove(id);
        }

        if (fired)
            _cooldown = _options.CooldownSeconds;
        return fired;
    }

    private sealed class Track
    {
        public Vector3 Start;
        public float LastYaw;
        public float Churn;
        public float Elapsed;
        public float WorstBlocked;

        public Track(ZombieInstance zombie) => Restart(zombie);

        public void Restart(ZombieInstance zombie)
        {
            Start = zombie.Position;
            LastYaw = zombie.Yaw;
            Churn = 0f;
            Elapsed = 0f;
            WorstBlocked = 0f;
        }
    }
}
