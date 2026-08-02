using Godot;

namespace UnturnedGodot;

// The in-game ESC menu, styled after Unturned's: a floating stack of flat olive/brown bars with a
// white glyph plate on the left, over the blurred live world (no panel, no pause — like Unturned,
// the escape menu never stops the simulation). Escape toggles it; while open the mouse is released
// and the player controller treats "mouse not captured" as no input, so movement and look stop.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class PauseMenu : CanvasLayer
{
    private Control _root = null!;
    private Label _status = null!;
    private Button _lanButton = null!;

    // Wired by Main: the session owner, and a callback to attach the input sender + remote view.
    public NetworkManager? Network { get; set; }
    public System.Action? OnSessionStarted { get; set; }

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

        column.AddChild(UnturnedUi.MakeBar("▶", "Resume", UnturnedUi.Olive, Close));
        _lanButton = UnturnedUi.MakeBar("⇄", "Open to LAN", UnturnedUi.Olive, OpenToLan);
        column.AddChild(_lanButton);
        column.AddChild(UnturnedUi.MakeBar("◀", "Quit game", UnturnedUi.Brown, () => AppShutdown.RequestQuit(GetTree())));

        _status = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(1, 1, 1, 0.75f),
        };
        _status.AddThemeFontSizeOverride("font_size", 13);
        column.AddChild(_status);

        if (EnvFlag.IsOn(OS.GetEnvironment("SHOW_PAUSE_MENU"), whenUnset: false)) // screenshot/debug aid
            Open();
    }

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

    // Minecraft-style: the always-on local session simply gains a UDP listener.
    private void OpenToLan()
    {
        if (Network == null || !Network.IsHosting)
        {
            _status.Text = "Networking is unavailable in this mode.";
            return;
        }
        if (Network.IsLanOpen)
        {
            _status.Text = "Already open to LAN.";
            return;
        }

        if (Network.OpenToLan(NetworkManager.DefaultPort))
        {
            _lanButton.Disabled = true;
            _status.Text = $"Open to LAN on UDP port {NetworkManager.DefaultPort}.";
            OnSessionStarted?.Invoke();
        }
        else
        {
            _status.Text = $"Failed to bind UDP port {NetworkManager.DefaultPort}.";
        }
    }
}
