using Godot;

namespace UnturnedGodot;

// Baseline performance HUD (FPS, frame time, memory, draw calls). On by default because the
// project targets parity first, then performance — the numbers must always be in view. Toggle with F3.
public partial class DebugOverlay : CanvasLayer
{
    [Export] public Key ToggleKey = Key.F3;
    [Export] public double UpdateInterval = 0.2;

    private Label _label = null!;
    private double _accum;

    public override void _Ready()
    {
        Layer = 128; // stay above the 3D viewport

        var panel = new PanelContainer { Position = new Vector2(8, 8) };
        var style = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.55f) };
        style.SetContentMarginAll(8);
        panel.AddThemeStyleboxOverride("panel", style);
        AddChild(panel);

        _label = new Label();
        _label.AddThemeConstantOverride("outline_size", 3);
        _label.AddThemeColorOverride("font_outline_color", Colors.Black);
        panel.AddChild(_label);

        UpdateText();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        if (key.Keycode == ToggleKey)
            Visible = !Visible;
        else if (key.Keycode == Key.F4)
            CopyCameraShotCam();
    }

    // Copies whatever camera is currently rendering (free-fly OR the player's) as a SHOT_CAM value
    // ("px,py,pz,pitch,yaw") so any view can be reproduced headless with `SHOT_CAM=... godot ...`. Global
    // here (not on the free camera) so it works in player mode too. Prints as well, since the Wayland
    // clipboard is unreliable — read it from the console if paste comes up empty.
    private void CopyCameraShotCam()
    {
        Camera3D? cam = GetViewport().GetCamera3D();
        if (cam == null)
            return;
        Vector3 p = cam.GlobalPosition;
        Vector3 r = cam.GlobalRotationDegrees;
        var c = System.Globalization.CultureInfo.InvariantCulture;
        string shotCam = string.Format(c, "{0:0.##},{1:0.##},{2:0.##},{3:0.##},{4:0.##}",
            p.X, p.Y, p.Z, r.X, r.Y);
        DisplayServer.ClipboardSet(shotCam);
        Log.Print($"[camera] SHOT_CAM={shotCam}");
    }

    public override void _Process(double delta)
    {
        _accum += delta;
        if (_accum < UpdateInterval)
            return;
        _accum = 0;
        UpdateText();
    }

    private static double Mon(Performance.Monitor monitor) => Performance.GetMonitor(monitor);

    private void UpdateText()
    {
        double fps = Mon(Performance.Monitor.TimeFps);
        double cpuMs = Mon(Performance.Monitor.TimeProcess) * 1000.0;
        double physMs = Mon(Performance.Monitor.TimePhysicsProcess) * 1000.0;

        // Godot exposes no GPU-time monitor. What we CAN derive is GPU-wait: wall-clock frame time
        // (1000/fps) minus the CPU main-thread work. Since rendering is submitted on the main thread and
        // does not block on the GPU, this is only the time the CPU actually stalls waiting for the GPU —
        // it stays ~0 while CPU-bound (even at 100% GPU utilization) and only lights up once the GPU is the
        // real bottleneck (or under VSync). It is NOT a measure of GPU cost; use Draw calls / Primitives
        // for GPU workload, or an external tool (MangoHud/radeontop) for true GPU frame time.
        double frameMs = fps > 0.0 ? 1000.0 / fps : 0.0;
        double gpuWaitMs = Mathf.Max(0.0, frameMs - cpuMs);
        double memMb = Benchmark.ProcessMemory.RssBytes() / (1024.0 * 1024.0);
        double drawCalls = Mon(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double primitives = Mon(Performance.Monitor.RenderTotalPrimitivesInFrame);
        double renderObjects = Mon(Performance.Monitor.RenderTotalObjectsInFrame);
        double nodes = Mon(Performance.Monitor.ObjectNodeCount);

        _label.Text =
            $"FPS {fps:0}   Frame {frameMs:0.00} ms\n" +
            $"CPU {cpuMs:0.00} ms   GPU-wait ~{gpuWaitMs:0.00} ms   Phys {physMs:0.0} ms\n" +
            $"Process RSS {memMb:0.0} MB\n" +
            $"Draw calls {drawCalls:0}   Primitives {primitives:0}\n" +
            $"Render objects {renderObjects:0}   Nodes {nodes:0}\n" +
            NetLine() + "\n" +
            "F3 toggle HUD   F4 copy camera (SHOT_CAM)";
    }

    // The netcode's line, in the same place as everything else the project measures.
    //
    // Client-side numbers, because this HUD is drawn in a client's frame — the round trip and what this
    // machine is sending and receiving. A host wanting its SERVER's totals types `net.stats`, which
    // prints both halves; a line narrow enough to sit beside the frame time cannot carry four.
    //
    // Resolved through the group every time rather than held: the session is rebuilt on every map load,
    // and it does not exist at all in the menu or in a free-camera walkthrough.
    private string NetLine()
    {
        if (!IsInsideTree() || GetTree().GetFirstNodeInGroup(SceneGroups.Network) is not NetworkManager net
            || net.Client is not { } client)
        {
            return "Net  offline";
        }
        return UnturnedGodot.Net.NetReport.HudLine(client.Traffic, client.RoundTripSeconds);
    }
}
