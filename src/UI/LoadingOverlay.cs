using Godot;

namespace UnturnedGodot;

// A small load-status banner driven by ObjectStreamer's signals: shows while the cold-load streams the
// object meshes and then the textures, and hides itself once everything is applied. On a warm cache the
// streamer fires MeshesReady + Finished before the first frame, so the banner never appears.
public partial class LoadingOverlay : CanvasLayer
{
    private Label _label = null!;
    private PanelContainer _panel = null!;

    public override void _Ready()
    {
        Layer = 100;

        _panel = new PanelContainer { Position = new Vector2(8, 8) };
        var style = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.6f) };
        style.SetContentMarginAll(10);
        _panel.AddThemeStyleboxOverride("panel", style);
        AddChild(_panel);

        _label = new Label { Text = "Loading world…" };
        _label.AddThemeConstantOverride("outline_size", 3);
        _label.AddThemeColorOverride("font_outline_color", Colors.Black);
        _panel.AddChild(_label);
    }

    // Wire this to a streamer BEFORE the streamer's Begin() runs, so the synchronous warm-cache signals
    // are caught (and the banner hides immediately).
    public void Track(ObjectStreamer streamer)
    {
        streamer.MeshesReady += OnMeshesReady;
        streamer.Progress += OnProgress;
        streamer.Finished += OnFinished;
    }

    private void OnMeshesReady(double elapsedMs) =>
        _label.Text = $"World ready in {elapsedMs:0} ms · loading textures…";

    private void OnProgress(int applied, int total) =>
        _label.Text = $"Loading textures… {applied}/{total}";

    private void OnFinished() => Visible = false;
}
