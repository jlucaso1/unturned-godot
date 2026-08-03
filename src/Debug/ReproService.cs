using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Repro;
using UnturnedGodot.Zombies;

namespace UnturnedGodot;

// The bug-report key.
//
// A session that hosts a simulation keeps the last few seconds of it in memory (ReproRecorder). Press
// the capture key — F7 by default — and that history, the world around you, the players and the camera
// you were looking through become one file that replays headless, in a test, or back inside the game.
//
// It exists because "I saw a zombie spinning at a pole at these coordinates" is not reproducible, and
// everything anyone would need in order to make it reproducible is already in memory when it happens.
// Nothing here is specific to that bug or to zombies: the capture asks each subsystem for its section,
// and the zombie brain is simply the first subsystem that has one.
//
// Environment:
//   REPRO=0                 turn the recorder off entirely (it is on while hosting by default)
//   REPRO_KEY=F7            capture key
//   REPRO_DIR=user://repro  where dumps are written
//   REPRO_WINDOW=64         ticks of history kept (0.08 s each)
//   REPRO_AUTO=1            capture by itself when a zombie starts behaving like a bug report
//   REPRO_CAPTURE_AT=30     capture once, N seconds in, and keep playing (for headless runs)
//   REPRO_NOTE="..."        a line of description carried into the dump
//   REPRO_GEOMETRY_RADIUS=16, REPRO_NAV_RADIUS=48, REPRO_GROUND_RADIUS=32
//   REPRO_LOAD=path         load a dump into this session instead of capturing one
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed partial class ReproService : Node
{
    private ZombieSystem _zombies = null!;
    private NetServer? _server;
    private GroundSampler? _ground;
    private ReproRecorder? _recorder;
    private ReproWatcher? _watcher;
    private Key _key = Key.F7;
    private string _directory = "user://repro";
    private string _note = "";
    private float _geometryRadius = 16f;
    private float _navRadius = 48f;
    private float _groundRadius = 32f;
    private double _captureAt = -1.0;
    private double _elapsed;
    private int _written;

    public string LevelName { get; set; } = "";

    public string Map { get; set; } = "";

    // Builds the service for a hosted session, or nothing at all when REPRO=0 — in which case the
    // simulation runs with its own delegates and this file costs the session exactly nothing.
    public static ReproService? Create(ZombieSystem zombies, NetServer? server, GroundSampler? ground)
    {
        ArgumentNullException.ThrowIfNull(zombies);
        if (!EnvFlag.IsOn(OS.GetEnvironment("REPRO"), whenUnset: true))
            return null;
        var service = new ReproService
        {
            Name = "Repro",
            _zombies = zombies,
            _server = server,
            _ground = ground,
        };
        return service;
    }

    public override void _Ready()
    {
        if (OS.GetEnvironment("REPRO_KEY") is { Length: > 0 } key
            && Enum.TryParse(key, ignoreCase: true, out Key parsed))
        {
            _key = parsed;
        }
        if (OS.GetEnvironment("REPRO_DIR") is { Length: > 0 } directory)
            _directory = directory;
        if (OS.GetEnvironment("REPRO_NOTE") is { Length: > 0 } note)
            _note = note;
        _geometryRadius = Radius("REPRO_GEOMETRY_RADIUS", _geometryRadius);
        _navRadius = Radius("REPRO_NAV_RADIUS", _navRadius);
        _groundRadius = Radius("REPRO_GROUND_RADIUS", _groundRadius);
        if (OS.GetEnvironment("REPRO_CAPTURE_AT") is { Length: > 0 } at)
            _captureAt = at.ToFloat();

        var options = new ReproRecorderOptions
        {
            WindowTicks = OS.GetEnvironment("REPRO_WINDOW") is { Length: > 0 } window
                ? Math.Clamp(window.ToInt(), 2, 2048)
                : 64,
        };
        _recorder = new ReproRecorder(_zombies, options);
        _recorder.Attach();
        if (EnvFlag.IsOn(OS.GetEnvironment("REPRO_AUTO"), whenUnset: false))
            _watcher = new ReproWatcher();

        if (OS.GetEnvironment("REPRO_LOAD") is { Length: > 0 } load)
            CallDeferred(MethodName.Load, load);
        else
            Log.Print($"[repro] armed: {options.WindowTicks} ticks of history, "
                + $"press {_key} to write a dump to {_directory}");
    }

    private static float Radius(string variable, float fallback) =>
        OS.GetEnvironment(variable) is { Length: > 0 } value ? value.ToFloat() : fallback;

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key && key.Keycode == _key)
            Capture("manual", Focus(), _note);
    }

    public override void _PhysicsProcess(double delta)
    {
        _elapsed += delta;
        if (_recorder != null && _server != null)
            _recorder.ExternalTick = _server.Tick;
        if (_captureAt >= 0.0 && _elapsed >= _captureAt)
        {
            _captureAt = -1.0;
            Capture("timer", Focus(), _note);
        }
        if (_watcher != null
            && _watcher.Poll(_zombies, (float)delta, out string reason, out Vector3 focus))
        {
            Capture(reason, focus, _note);
        }
    }

    // Where the interesting thing is: the local player if there is one, else the camera, else the
    // origin. Everything the dump slices — geometry, navmesh, terrain — is cut around this.
    private Vector3 Focus()
    {
        Vector3 focus = Vector3.Zero;
        bool found = false;
        _server?.ForEachJoinedConnection((_, state, _) =>
        {
            if (found)
                return;
            focus = state.Position;
            found = true;
        });
        if (!found && GetViewport()?.GetCamera3D() is { } camera)
            focus = camera.GlobalPosition;
        return focus;
    }

    public string? Capture(string reason, Vector3 focus, string note)
    {
        var warnings = new List<string>();
        var request = new ReproCaptureRequest
        {
            Reason = reason,
            Note = note,
            Map = Map,
            LevelName = LevelName,
            Engine = (string)Engine.GetVersionInfo()["string"],
            Build = ProjectSettings.GetSetting("application/config/version", "dev").AsString(),
            Headless = DisplayServer.GetName() == "headless",
            CapturedAtUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ShotCam = ShotCam(),
            Focus = focus,
            NavmeshRadius = _navRadius,
            GroundRadius = _groundRadius,
            Session = Session(),
            Geometry = ReproGeometryCapture.Capture(GetViewport()?.World3D?.DirectSpaceState, focus,
                _geometryRadius, warnings),
            Log = Log.Tail(),
            Warnings = warnings,
        };
        ReproDump dump = ReproCapture.Build(request, _zombies, _recorder, _ground);

        string path = $"{_directory.TrimEnd('/')}/{DateTime.UtcNow:yyyyMMdd-HHmmss}-{_written++}.json";
        try
        {
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(_directory));
            dump.Write(ProjectSettings.GlobalizePath(path));
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            Log.PrintErr($"[repro] could not write {path}: {e.Message}");
            return null;
        }
        Log.Print($"[repro] {reason} -> {ProjectSettings.GlobalizePath(path)}");
        foreach (string line in dump.Describe().Split('\n'))
            if (line.Length > 0)
                Log.Print($"[repro] {line}");
        return ProjectSettings.GlobalizePath(path);
    }

    private ReproSessionData Session()
    {
        var session = new ReproSessionData
        {
            ServerTick = _server?.Tick ?? 0,
            TimeOfDay = Find<DayNightController>(GetTree().Root)?.TimeOfDay ?? -1f,
        };
        _server?.ForEachJoinedConnection((id, state, _) => session.Players.Add(new ReproPlayerState
        {
            Id = id,
            Position = ReproVector.From(state.Position),
            Velocity = ReproVector.From(state.Velocity),
            Grounded = state.Grounded,
            Yaw = ReproVector.Round(state.Yaw),
            Pitch = ReproVector.Round(state.Pitch),
            Stance = state.Stance,
            Moving = state.Moving,
        }));
        return session;
    }

    // The same string F4 copies, so a dump renders back through SHOT_CAM without any extra plumbing.
    private string ShotCam()
    {
        if (GetViewport()?.GetCamera3D() is not { } camera)
            return "";
        Vector3 p = camera.GlobalPosition;
        Vector3 r = camera.GlobalRotationDegrees;
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:0.##},{1:0.##},{2:0.##},{3:0.##},{4:0.##}", p.X, p.Y, p.Z, r.X, r.Y);
    }

    // Puts a dump back into THIS running session: the zombies get their recorded state, and the local
    // player is stood where the reporter was. From there the session simply carries on, which is the
    // point — you watch the bug happen in the real renderer instead of reading about it.
    public void Load(string path)
    {
        ReproDump dump;
        try
        {
            dump = ReproDump.Read(ProjectSettings.GlobalizePath(path));
        }
        catch (Exception e) when (e is System.IO.IOException or System.Text.Json.JsonException
            or InvalidOperationException)
        {
            Log.PrintErr($"[repro] could not read {path}: {e.Message}");
            return;
        }
        foreach (string line in dump.Describe().Split('\n'))
            if (line.Length > 0)
                Log.Print($"[repro] {line}");

        if (dump.Zombies is { } section)
        {
            try
            {
                _zombies.RestoreState(ReproZombieState.FromRecord(section.State));
            }
            catch (ArgumentOutOfRangeException e)
            {
                Log.PrintErr($"[repro] {e.Message}");
                return;
            }
        }

        Vector3 focus = ReproVector.To(dump.Meta.FocusPoint);
        foreach (ReproPlayerState player in dump.Session.Players)
        {
            Vector3 position = ReproVector.To(player.Position);
            _server?.Teleport(player.Id, position);
            focus = position;
        }
        if (Find<PlayerController>(GetTree().Root) is { } controller)
            controller.GlobalPosition = focus;
        Log.Print($"[repro] loaded; stand at {focus}. Camera: SHOT_CAM={dump.Meta.ShotCam}");
    }

    // The debug tool's way of reaching another subsystem without every subsystem having to know about
    // the debug tool. Runs once per capture, over a tree of a few hundred nodes.
    private static T? Find<T>(Node node) where T : Node
    {
        if (node is T match)
            return match;
        foreach (Node child in node.GetChildren())
            if (Find<T>(child) is { } found)
                return found;
        return null;
    }

    public override void _ExitTree() => _recorder?.Detach();
}
