using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// The boot scene: no map loaded yet, just the Unturned-style menu over a sky gradient. The map browser
// lists every map installed on this machine and Play loads the selected one; Connect reveals a host:port
// field and joins (loading the selected map locally, since the protocol does not name one); Quit exits.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class MainMenu : CanvasLayer
{
    // The install to list maps from, and the map to preselect (the previous session's, or MAP=).
    public string UnturnedPath { get; init; } = "";
    public string? InitialMap { get; init; }

    // Called with the map folder to load, and null for singleplayer or "host[:port]" to join a server.
    public System.Action<string, string?>? OnStart { get; set; }

    private MapPicker _picker = null!;
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

        // Fills the window and lets the map browser take whatever height is left, so the menu fits any
        // resolution instead of pushing the buttons off a short screen.
        var margin = new MarginContainer { AnchorRight = 1, AnchorBottom = 1 };
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        root.AddChild(margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 8);
        margin.AddChild(column);

        var title = new Label
        {
            Text = "UNTURNED · GODOT",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 34);
        title.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.5f));
        title.AddThemeConstantOverride("shadow_offset_y", 2);
        column.AddChild(title);
        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 12) }); // spacing under the title

        // The browser keeps its natural width, centered, and stretches vertically.
        var pickerRow = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        column.AddChild(pickerRow);
        pickerRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        _picker = MapPicker.Create(MapCatalog.Scan(UnturnedPath), InitialMap);
        _picker.OnPlay = map => OnStart?.Invoke(map.SelectionKey, null);
        _picker.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        pickerRow.AddChild(_picker);

        pickerRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

        var actions = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        actions.AddThemeConstantOverride("separation", 8);
        column.AddChild(actions);

        Button connect = UnturnedUi.MakeBar("⇄", "Connect", UnturnedUi.Olive, ToggleConnectRow);
        connect.CustomMinimumSize = new Vector2(240, 40);
        actions.AddChild(connect);

        Button quit = UnturnedUi.MakeBar("✕", "Quit", UnturnedUi.Brown, () => GetTree().Quit());
        quit.CustomMinimumSize = new Vector2(240, 40);
        actions.AddChild(quit);

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
        if (_picker.Selected is not { IsSupported: true } map)
        {
            _status.Text = "Pick a playable map first: the client builds the world locally.";
            return;
        }
        _status.Text = $"Connecting to {address} on {map.DisplayName}…";
        OnStart?.Invoke(map.SelectionKey, address);
    }
}
