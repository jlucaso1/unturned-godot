using Godot;

namespace UnturnedGodot;

// The shared visual language of our menus, following Unturned's: flat olive/brown bars with a white
// glyph plate on the left, a centered label, thin dark borders and subtle hover/pressed tints.
public static class UnturnedUi
{
    public static readonly Color Olive = new(0.31f, 0.36f, 0.20f);
    public static readonly Color Brown = new(0.37f, 0.29f, 0.20f);
    public static readonly Color BorderColor = new(0.16f, 0.14f, 0.10f);

    // A muted one-line label, for counts and captions under a heading.
    public static Label Caption(string text, int size = 12)
    {
        var label = new Label { Text = text, Modulate = new Color(1, 1, 1, 0.72f) };
        label.AddThemeFontSizeOverride("font_size", size);
        return label;
    }

    // Retitles a bar built by MakeBar (the text lives in a child label, not Button.Text).
    public static void SetBarText(Button bar, string text)
    {
        if (bar.GetMeta(BarLabelMeta, default(Variant)).As<Label>() is { } label)
            label.Text = text;
    }

    private const string BarLabelMeta = "unturned_ui_bar_label";

    public static Button MakeBar(string glyph, string text, Color tint, System.Action onPressed)
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
        button.SetMeta(BarLabelMeta, label);

        // Re-center the text over the full bar (the glyph plate offsets the HBox): pad the right side.
        content.AddChild(new Control { CustomMinimumSize = new Vector2(48, 0) });

        return button;
    }

    // The backdrop behind a menu panel: darker, softer corners, same border.
    public static StyleBoxFlat Panel(Color fill)
    {
        StyleBoxFlat box = Bar(fill);
        box.CornerRadiusBottomLeft = 6;
        box.CornerRadiusBottomRight = 6;
        box.CornerRadiusTopLeft = 6;
        box.CornerRadiusTopRight = 6;
        return box;
    }

    public static StyleBoxFlat Bar(Color tint) => new()
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
}
