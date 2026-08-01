using Godot;

namespace UnturnedGodot;

// The boot loading screen: same sky backdrop as the main menu, a status line and a slim indeterminate
// bar that keeps sweeping as long as frames render — the visible proof the app isn't frozen. Fades in
// on show and out on Finish; the world builds behind it in stages that yield to the render loop.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class LoadingScreen : CanvasLayer
{
    // The map being loaded, shown in the title. Set before the screen enters the tree.
    public string MapName { get; init; } = "";

    private Control _root = null!;
    private VBoxContainer _column = null!;
    private Label _status = null!;
    private bool _failed;
    private ColorRect _sweep = null!;
    private float _sweepPhase;

    public override void _Ready()
    {
        Layer = 30;

        _root = new Control { AnchorRight = 1, AnchorBottom = 1, Modulate = new Color(1, 1, 1, 0) };
        AddChild(_root);

        var sky = new TextureRect
        {
            AnchorRight = 1,
            AnchorBottom = 1,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            Texture = new GradientTexture2D
            {
                FillFrom = new Vector2(0, 0),
                FillTo = new Vector2(0, 1),
                Gradient = new Gradient
                {
                    Offsets = new float[] { 0f, 0.72f, 1f },
                    Colors = new[]
                    {
                        new Color(0.4f, 0.627f, 0.808f),
                        new Color(0.784f, 0.784f, 0.784f),
                        new Color(0.329f, 0.518f, 0.78f),
                    },
                },
            },
        };
        _root.AddChild(sky);

        var center = new CenterContainer { AnchorRight = 1, AnchorBottom = 1 };
        _root.AddChild(center);

        _column = new VBoxContainer();
        _column.AddThemeConstantOverride("separation", 14);
        center.AddChild(_column);
        VBoxContainer column = _column;

        var title = new Label
        {
            Text = string.IsNullOrEmpty(MapName) ? "Loading" : $"Loading {MapName}",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 26);
        title.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
        title.AddThemeConstantOverride("shadow_offset_y", 2);
        column.AddChild(title);

        // Indeterminate bar: an Unturned-olive track with a bright segment sweeping across.
        var track = new Control { CustomMinimumSize = new Vector2(340, 10), ClipContents = true };
        var trackBg = new ColorRect
        {
            AnchorRight = 1,
            AnchorBottom = 1,
            Color = UnturnedUi.BorderColor,
        };
        track.AddChild(trackBg);
        _sweep = new ColorRect
        {
            Color = UnturnedUi.Olive.Lightened(0.35f),
            Size = new Vector2(110, 10),
        };
        track.AddChild(_sweep);
        column.AddChild(track);

        _status = new Label { Text = "Preparing…", HorizontalAlignment = HorizontalAlignment.Center };
        _status.AddThemeFontSizeOverride("font_size", 14);
        _status.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
        column.AddChild(_status);

        CreateTween().TweenProperty(_root, "modulate:a", 1.0f, 0.15f);
    }

    private double _elapsed;
    private bool _longLoadNoted;

    public override void _Process(double delta)
    {
        if (_failed)
            return;

        _sweepPhase = Mathf.PosMod(_sweepPhase + (float)delta * 1.4f, 1.3f);
        _sweep.Position = new Vector2((_sweepPhase - 0.15f) * 340f / 1.0f, 0);

        _elapsed += delta;
        if (!_longLoadNoted && _elapsed > 45)
        {
            _longLoadNoted = true; // first run extracts game assets from the masterbundle: minutes, once
            _status.Text += "\nFirst run extracts game assets — this can take a few minutes, only once.";
        }
    }

    public void SetStatus(string text) => _status.Text = text;

    // The load died: say so and offer the way back instead of sweeping the bar forever. Some maps use
    // formats this port does not read yet, and that has to be visible rather than look like a hang.
    public void Fail(string message, System.Action onBack)
    {
        _failed = true;
        _sweep.Visible = false;
        _status.Text = $"Could not load this map.\n{message}";

        Button back = UnturnedUi.MakeBar("←", "Back to maps", UnturnedUi.Brown, onBack);
        back.CustomMinimumSize = new Vector2(320, 40);
        _column.AddChild(back);
    }

    // Fades out and frees itself; the world is already visible underneath.
    public void Finish()
    {
        Tween tween = CreateTween();
        tween.TweenProperty(_root, "modulate:a", 0.0f, 0.3f);
        tween.TweenCallback(Callable.From(QueueFree));
    }
}
