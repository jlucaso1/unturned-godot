using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// The map browser on the boot menu: every installed map on the left (official and workshop), the one
// under the cursor detailed on the right, and Play acting on the selection. Artwork, names and blurbs
// are the map's own (Icon.png, Preview.png, English.dat), so the list reads like the game's own picker.
//
// Maps whose terrain this port cannot read yet (pre-Landscape "legacy ground") are listed but not
// playable: hiding them would just raise the question of where they went.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class MapPicker : PanelContainer
{
    private const int ListWidth = 360;
    private const int MinListHeight = 260;
    private const int DetailWidth = 400;
    private const int ThumbWidth = 76;
    private const int ThumbHeight = 38;

    public System.Action<MapEntry>? OnPlay { get; set; }
    public MapEntry? Selected { get; private set; }

    private readonly List<MapEntry> _maps = new();
    private readonly List<Button> _items = new();
    private readonly ButtonGroup _group = new();

    private ScrollContainer _scroll = null!;
    private VBoxContainer _detail = null!;
    private TextureRect _preview = null!;
    private Label _title = null!;
    private Label _facts = null!;
    private RichTextLabel _description = null!;
    private Button _play = null!;

    public static MapPicker Create(IReadOnlyList<MapEntry> maps, string? initialSelection)
    {
        var picker = new MapPicker { Name = "MapPicker" };
        picker._maps.AddRange(maps);
        picker._initialSelection = initialSelection;
        return picker;
    }

    private string? _initialSelection;

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", UnturnedUi.Panel(new Color(0.09f, 0.10f, 0.08f, 0.86f)));

        var margin = new MarginContainer();
        foreach (string side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 14);
        AddChild(margin);

        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 14);
        margin.AddChild(columns);

        columns.AddChild(BuildList());
        columns.AddChild(BuildDetail());

        SelectInitial();
    }

    private Control BuildList()
    {
        var column = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(ListWidth, MinListHeight),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        column.AddThemeConstantOverride("separation", 6);

        int playable = 0;
        foreach (MapEntry map in _maps)
            if (map.IsSupported)
                playable++;

        column.AddChild(UnturnedUi.Caption(_maps.Count == 0
            ? "No maps found in this install"
            : $"{playable} playable · {_maps.Count} installed"));

        _scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        column.AddChild(_scroll);
        ScrollContainer scroll = _scroll;

        var items = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        items.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(items);

        foreach (MapEntry map in _maps)
        {
            Button item = MakeItem(map);
            _items.Add(item);
            items.AddChild(item);
        }

        return column;
    }

    // One row: the map's own Icon.png thumbnail, its name, and a subtitle saying where it came from and
    // how big it is.
    private Button MakeItem(MapEntry map)
    {
        Color tint = map.IsSupported ? UnturnedUi.Olive : UnturnedUi.Brown.Darkened(0.25f);
        var button = new Button
        {
            ToggleMode = true,
            ButtonGroup = _group,
            CustomMinimumSize = new Vector2(0, 54),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = map.IsSupported
                ? map.Path
                : "Legacy terrain: this map predates Landscape tiles, which this port does not read yet.",
        };
        button.AddThemeStyleboxOverride("normal", UnturnedUi.Bar(tint.Darkened(0.35f)));
        button.AddThemeStyleboxOverride("hover", UnturnedUi.Bar(tint.Darkened(0.15f)));
        button.AddThemeStyleboxOverride("pressed", UnturnedUi.Bar(tint));
        button.AddThemeStyleboxOverride("focus", UnturnedUi.Bar(tint.Darkened(0.35f)));
        button.Toggled += pressed =>
        {
            if (pressed)
                Show(map);
        };

        var row = new HBoxContainer
        {
            AnchorRight = 1,
            AnchorBottom = 1,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        row.AddThemeConstantOverride("separation", 10);
        button.AddChild(row);
        row.AddChild(new Control { CustomMinimumSize = new Vector2(4, 0) });

        // Icon.png is a 380x80 landscape crop: a thumbnail, so it goes to the left of the name. A fixed
        // box is what makes it visible at all, since an expanding TextureRect collapses to zero width.
        row.AddChild(new TextureRect
        {
            Texture = LoadTexture(map.IconPath),
            CustomMinimumSize = new Vector2(ThumbWidth, ThumbHeight),
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            ClipContents = true,
            Modulate = map.IsSupported ? Colors.White : new Color(1, 1, 1, 0.4f),
        });

        var text = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        text.AddThemeConstantOverride("separation", 1);
        row.AddChild(text);

        var name = new Label
        {
            Text = map.DisplayName,
            Modulate = map.IsSupported ? Colors.White : new Color(1, 1, 1, 0.6f),
        };
        name.AddThemeFontSizeOverride("font_size", 15);
        text.AddChild(name);
        text.AddChild(UnturnedUi.Caption(Subtitle(map), size: 11));

        row.AddChild(new Control { CustomMinimumSize = new Vector2(8, 0) });
        return button;
    }

    private static string Subtitle(MapEntry map)
    {
        if (!map.IsSupported)
            return "legacy terrain · not loadable yet";

        string origin = map.Source == MapSource.Workshop ? "Workshop" : map.Category ?? "Official";
        return $"{origin} · {Kilometres(map)} km · {map.TileCount} tiles";
    }

    // The edge of the square the map spans, in km ("4.1" for PEI's 4x4 tiles).
    private static string Kilometres(MapEntry map) =>
        (map.SizeMetres / 1000f).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private Control BuildDetail()
    {
        _detail = new VBoxContainer { CustomMinimumSize = new Vector2(DetailWidth, 0) };
        _detail.AddThemeConstantOverride("separation", 8);

        _preview = new TextureRect
        {
            CustomMinimumSize = new Vector2(DetailWidth, DetailWidth * 9 / 16),
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            ClipContents = true,
        };
        _detail.AddChild(_preview);

        _title = new Label { Text = "" };
        _title.AddThemeFontSizeOverride("font_size", 22);
        _detail.AddChild(_title);

        _facts = UnturnedUi.Caption("");
        _detail.AddChild(_facts);

        _description = new RichTextLabel
        {
            BbcodeEnabled = false,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(DetailWidth, 96),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            Modulate = new Color(1, 1, 1, 0.85f),
        };
        _description.AddThemeFontSizeOverride("normal_font_size", 13);
        _detail.AddChild(_description);

        _play = UnturnedUi.MakeBar("▶", "Play", UnturnedUi.Olive, () =>
        {
            if (Selected is { IsSupported: true } map)
                OnPlay?.Invoke(map);
        });
        _play.CustomMinimumSize = new Vector2(DetailWidth, 44);
        _detail.AddChild(_play);

        return _detail;
    }

    private void SelectInitial()
    {
        if (_items.Count == 0)
        {
            _play.Disabled = true;
            UnturnedUi.SetBarText(_play, "No map to play");
            return;
        }

        int index = 0;
        for (int i = 0; i < _maps.Count; i++)
            if (string.Equals(_maps[i].FolderName, _initialSelection, System.StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }

        Button item = _items[index];
        item.ButtonPressed = true; // fires Toggled, which fills the detail panel

        // Scroll the remembered map into view once the list has been laid out.
        Callable.From(() => _scroll.EnsureControlVisible(item)).CallDeferred();
    }

    private void Show(MapEntry map)
    {
        Selected = map;
        _title.Text = map.DisplayName;

        string origin = map.Source == MapSource.Workshop ? "Steam Workshop" : map.Category ?? "Official";
        _facts.Text = map.IsSupported
            ? $"{origin} · {Kilometres(map)} km across · {map.TileCount} landscape tiles"
            : $"{origin} · legacy terrain";

        _description.Text = map.IsSupported
            ? map.Description ?? ""
            : "This map stores its terrain in the pre-Landscape format, which this port does not read "
              + "yet, so it cannot be loaded.\n\n" + (map.Description ?? "");

        // Preview.png is the 16:9 thumbnail; workshop maps often ship only the chart.
        _preview.Texture = LoadTexture(map.PreviewPath) ?? LoadTexture(map.ChartPath);
        _preview.Modulate = map.IsSupported ? Colors.White : new Color(0.6f, 0.6f, 0.6f, 0.7f);

        _play.Disabled = !map.IsSupported;
        UnturnedUi.SetBarText(_play, map.IsSupported ? $"Play {map.DisplayName}" : "Not loadable");
    }

    // The menu owns its decoded artwork. Keeping this process-global made every map browsed during a
    // session retain its full RGBA texture throughout gameplay (hundreds of MiB on a large library).
    // The static cache remains available only as an explicit A/B control.
    private static readonly Dictionary<string, ImageTexture?> LegacyTextureCache = new();
    private readonly Dictionary<string, ImageTexture?> _textureCache = new();

    private Dictionary<string, ImageTexture?> TextureCache =>
        OS.GetEnvironment("UG_STATIC_MAP_PREVIEW_CACHE") == "1" ? LegacyTextureCache : _textureCache;

    // Map artwork lives outside res://, so it is loaded from disk and cached per path (the same icon is
    // re-read every time the panel rebuilds otherwise).
    private ImageTexture? LoadTexture(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (TextureCache.TryGetValue(path, out ImageTexture? cached))
            return cached;

        ImageTexture? texture = null;
        Image image = Image.LoadFromFile(path);
        if (image != null && image.GetWidth() > 0)
            texture = ImageTexture.CreateFromImage(image);

        TextureCache[path] = texture;
        return texture;
    }

    public override void _ExitTree()
    {
        // Child TextureRects release their last references as the menu subtree is destroyed. Dropping
        // ours here makes the ownership boundary explicit and prevents a later menu from inheriting a
        // session-sized cache. Godot frees the corresponding GPU resources after the render fence.
        if (OS.GetEnvironment("UG_STATIC_MAP_PREVIEW_CACHE") != "1")
            _textureCache.Clear();
    }
}
