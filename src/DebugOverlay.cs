using Godot;

namespace UnturnedGodot;

// Baseline performance HUD (FPS, frame time, memory, draw calls). On by default because the
// project targets parity first, then performance — the numbers must always be in view. Toggle with F3.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
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
        if (@event is InputEventKey { Pressed: true } key && key.Keycode == ToggleKey)
            Visible = !Visible;
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
        double memMb = Mon(Performance.Monitor.MemoryStatic) / (1024.0 * 1024.0);
        double drawCalls = Mon(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double primitives = Mon(Performance.Monitor.RenderTotalPrimitivesInFrame);
        double renderObjects = Mon(Performance.Monitor.RenderTotalObjectsInFrame);
        double nodes = Mon(Performance.Monitor.ObjectNodeCount);

        _label.Text =
            $"FPS {fps:0}   Frame {frameMs:0.00} ms\n" +
            $"CPU {cpuMs:0.00} ms   GPU-wait ~{gpuWaitMs:0.00} ms   Phys {physMs:0.0} ms\n" +
            $"Static memory {memMb:0.0} MB\n" +
            $"Draw calls {drawCalls:0}   Primitives {primitives:0}\n" +
            $"Render objects {renderObjects:0}   Nodes {nodes:0}\n" +
            "F3 toggle HUD   F4 copy camera (SHOT_CAM)";
    }
}
