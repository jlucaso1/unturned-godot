using Godot;

namespace UnturnedGodot;

// The boot scene: no map loaded yet, just the Unturned-style menu over a sky gradient.
// Play starts singleplayer, Connect reveals a host:port field and joins, Quit exits.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class MainMenu : CanvasLayer
{
    // Called with null to play singleplayer, or "host[:port]" to join a server.
    public System.Action<string?>? OnStart { get; set; }

    private VBoxContainer _connectRow = null!;
    private LineEdit _address = null!;
    private Label _status = null!;

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;

        var root = new Control { AnchorRight = 1, AnchorBottom = 1 };
        AddChild(root);

        // PEI's midday sky colors as a simple backdrop gradient.
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
                        new Color(0.4f, 0.627f, 0.808f),   // SKY_SKY
                        new Color(0.784f, 0.784f, 0.784f), // SKY_EQUATOR haze
                        new Color(0.329f, 0.518f, 0.78f),  // SKY_GROUND
                    },
                },
            },
        };
        root.AddChild(sky);

        var center = new CenterContainer { AnchorRight = 1, AnchorBottom = 1 };
        root.AddChild(center);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        center.AddChild(column);

        var title = new Label
        {
            Text = "UNTURNED · GODOT",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 34);
        title.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
        title.AddThemeConstantOverride("shadow_offset_y", 2);
        column.AddChild(title);
        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 18) }); // spacing under the title

        column.AddChild(UnturnedUi.MakeBar("▶", "Play", UnturnedUi.Olive, () => OnStart?.Invoke(null)));
        column.AddChild(UnturnedUi.MakeBar("⇄", "Connect", UnturnedUi.Olive, ToggleConnectRow));
        column.AddChild(UnturnedUi.MakeBar("✕", "Quit", UnturnedUi.Brown, () => GetTree().Quit()));

        // Hidden until Connect: address field + confirm, in the same visual language.
        _connectRow = new VBoxContainer { Visible = false };
        _connectRow.AddThemeConstantOverride("separation", 8);
        _address = new LineEdit
        {
            Text = "127.0.0.1:27015",
            PlaceholderText = "host:port",
            CustomMinimumSize = new Vector2(320, 36),
            Alignment = HorizontalAlignment.Center,
        };
        _address.TextSubmitted += _ => Connect();
        _connectRow.AddChild(_address);
        _connectRow.AddChild(UnturnedUi.MakeBar("→", "Join server", UnturnedUi.Olive, Connect));
        column.AddChild(_connectRow);

        _status = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(1, 1, 1, 0.8f),
        };
        _status.AddThemeFontSizeOverride("font_size", 13);
        column.AddChild(_status);
    }

    private void ToggleConnectRow()
    {
        _connectRow.Visible = !_connectRow.Visible;
        if (_connectRow.Visible)
            _address.GrabFocus();
    }

    private void Connect()
    {
        string address = _address.Text.Trim();
        if (address.Length == 0)
        {
            _status.Text = "Enter an address (host:port).";
            return;
        }
        _status.Text = $"Connecting to {address}…";
        OnStart?.Invoke(address);
    }
}
