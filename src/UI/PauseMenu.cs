using Godot;

namespace UnturnedGodot;

// The in-game ESC menu, styled after Unturned's: a floating stack of flat olive/brown bars with a
// white glyph plate on the left, over the blurred live world (no panel, no pause — like Unturned,
// the escape menu never stops the simulation). Escape toggles it; while open the mouse is released
// and the player controller treats "mouse not captured" as no input, so movement and look stop.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class PauseMenu : CanvasLayer
{
    // Unturned's menu palette: the first actions sit on olive green bars, the rest on brown.
    private static readonly Color Olive = new(0.31f, 0.36f, 0.20f);
    private static readonly Color Brown = new(0.37f, 0.29f, 0.20f);
    private static readonly Color BorderColor = new(0.16f, 0.14f, 0.10f);

    private Control _root = null!;
    private Label _status = null!;
    private Button _lanButton = null!;

    public bool IsOpen => _root.Visible;

    public override void _Ready()
    {
        Layer = 20; // above the debug overlay

        _root = new Control
        {
            Visible = false,
            AnchorRight = 1,
            AnchorBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Stop, // swallow clicks under the menu
        };
        AddChild(_root);

        // Blur the running world behind the menu (Unturned blurs its pause background).
        var blur = new ColorRect { AnchorRight = 1, AnchorBottom = 1 };
        blur.Material = new ShaderMaterial
        {
            Shader = new Shader
            {
                Code = """
                shader_type canvas_item;
                uniform sampler2D screen : hint_screen_texture, filter_linear_mipmap;
                void fragment() {
                    COLOR = vec4(textureLod(screen, SCREEN_UV, 2.5).rgb, 1.0);
                }
                """,
            },
        };
        _root.AddChild(blur);

        var center = new CenterContainer { AnchorRight = 1, AnchorBottom = 1 };
        _root.AddChild(center);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        center.AddChild(column);

        column.AddChild(MakeBar("▶", "Continuar", Olive, Close));
        _lanButton = MakeBar("⇄", "Abrir para LAN", Olive, OpenToLan);
        column.AddChild(_lanButton);
        column.AddChild(MakeBar("◀", "Sair do jogo", Brown, () => GetTree().Quit()));

        _status = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(1, 1, 1, 0.75f),
        };
        _status.AddThemeFontSizeOverride("font_size", 13);
        column.AddChild(_status);

        if (OS.GetEnvironment("SHOW_PAUSE_MENU") == "1") // screenshot/debug aid
            Open();
    }

    // One Unturned-style bar: flat tinted body, thin dark border, white glyph plate + centered label.
    private static Button MakeBar(string glyph, string text, Color tint, System.Action onPressed)
    {
        var button = new Button { CustomMinimumSize = new Vector2(320, 40) };
        button.AddThemeStyleboxOverride("normal", Bar(tint));
        button.AddThemeStyleboxOverride("hover", Bar(tint.Lightened(0.12f)));
        button.AddThemeStyleboxOverride("pressed", Bar(tint.Darkened(0.15f)));
        button.AddThemeStyleboxOverride("focus", Bar(tint));
        button.Pressed += onPressed;

        var content = new HBoxContainer
        {
            AnchorRight = 1,
            AnchorBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        content.AddThemeConstantOverride("separation", 0);
        button.AddChild(content);

        var plate = new Label
        {
            Text = glyph,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(48, 0),
        };
        plate.AddThemeFontSizeOverride("font_size", 18);
        content.AddChild(plate);

        var label = new Label
        {
            Text = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        label.AddThemeFontSizeOverride("font_size", 14);
        content.AddChild(label);

        // Re-center the text over the full bar (the glyph plate offsets the HBox): pad the right side.
        var pad = new Control { CustomMinimumSize = new Vector2(48, 0) };
        content.AddChild(pad);

        return button;
    }

    private static StyleBoxFlat Bar(Color tint) => new()
    {
        BgColor = tint,
        BorderColor = BorderColor,
        BorderWidthBottom = 1,
        BorderWidthTop = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusBottomLeft = 3,
        CornerRadiusBottomRight = 3,
        CornerRadiusTopLeft = 3,
        CornerRadiusTopRight = 3,
    };

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            if (IsOpen)
                Close();
            else
                Open();
            GetViewport().SetInputAsHandled();
        }
    }

    private void Open()
    {
        _root.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    private void Close()
    {
        _root.Visible = false;
        _status.Text = "";
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    // Placeholder until the UDP transport phase: the composite listen-server (host over loopback +
    // LAN listener) is already built and tested in core/Net; this button will start it.
    private void OpenToLan()
    {
        _lanButton.Disabled = true;
        _status.Text = "Rede LAN chega na próxima fase (transporte UDP).";
        GD.Print("[net] Open to LAN requested — UDP transport lands in the next phase.");
    }
}
