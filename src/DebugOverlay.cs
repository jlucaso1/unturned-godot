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
        double frameMs = Mon(Performance.Monitor.TimeProcess) * 1000.0;
        double physMs = Mon(Performance.Monitor.TimePhysicsProcess) * 1000.0;
        double memMb = Mon(Performance.Monitor.MemoryStatic) / (1024.0 * 1024.0);
        double drawCalls = Mon(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double primitives = Mon(Performance.Monitor.RenderTotalPrimitivesInFrame);
        double renderObjects = Mon(Performance.Monitor.RenderTotalObjectsInFrame);
        double nodes = Mon(Performance.Monitor.ObjectNodeCount);

        _label.Text =
            $"FPS {fps:0}   ({frameMs:0.0} ms cpu / {physMs:0.0} ms phys)\n" +
            $"Static memory {memMb:0.0} MB\n" +
            $"Draw calls {drawCalls:0}   Primitives {primitives:0}\n" +
            $"Render objects {renderObjects:0}   Nodes {nodes:0}";
    }
}
