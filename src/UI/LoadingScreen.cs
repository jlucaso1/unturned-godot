using Godot;

namespace UnturnedGodot;

// The boot loading screen: same sky backdrop as the main menu, a status line and a slim indeterminate
// bar that keeps sweeping as long as frames render — the visible proof the app isn't frozen. Fades in
// on show and out on Finish; the world builds behind it in stages that yield to the render loop.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class LoadingScreen : CanvasLayer
{
    private Control _root = null!;
    private Label _status = null!;
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

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 14);
        center.AddChild(column);

        var title = new Label { Text = "Loading PEI", HorizontalAlignment = HorizontalAlignment.Center };
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

    public override void _Process(double delta)
    {
        _sweepPhase = Mathf.PosMod(_sweepPhase + (float)delta * 1.4f, 1.3f);
        _sweep.Position = new Vector2((_sweepPhase - 0.15f) * 340f / 1.0f, 0);
    }

    public void SetStatus(string text) => _status.Text = text;

    // Fades out and frees itself; the world is already visible underneath.
    public void Finish()
    {
        Tween tween = CreateTween();
        tween.TweenProperty(_root, "modulate:a", 0.0f, 0.3f);
        tween.TweenCallback(Callable.From(QueueFree));
    }
}
