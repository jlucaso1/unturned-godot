using Godot;

namespace UnturnedGodot;

// The boot menu's hover hint, drawn as a plain Control instead of an engine tooltip.
//
// Godot's tooltips are Popups, and an embedded Popup in a release export never undoes the
// focus_entered/tree_exited connections it makes on the root window: hiding one prints a pair of
// "Attempt to disconnect a nonexistent connection" errors and leaks the connections until the popup is
// freed (godotengine/godot#87626, #89657 -- the fix, PR #95100, is still open). Setting TooltipText
// anywhere in the menu was enough to spam every hover, so the menu carries its own hint instead.
//
// It is top-level so no container lays it out and no ScrollContainer clips it, and it ignores the mouse
// so it never steals the hover it is describing.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class HoverTooltip : PanelContainer
{
    // Past this the hint is ellipsized rather than allowed to span the window; long workshop paths are
    // the only thing that gets near it.
    private const float MaxWidthFraction = 0.5f;

    private Label _label = null!;

    public static HoverTooltip Create()
    {
        var tip = new HoverTooltip
        {
            Name = "HoverTooltip",
            TopLevel = true,
            Visible = false,
            MouseFilter = MouseFilterEnum.Ignore,
            ZIndex = 100,
        };
        return tip;
    }

    public override void _Ready()
    {
        AddThemeStyleboxOverride("panel", UnturnedUi.Panel(new Color(0.05f, 0.06f, 0.04f, 0.94f)));

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        foreach (string side in new[] { "left", "right" })
            margin.AddThemeConstantOverride($"margin_{side}", 8);
        foreach (string side in new[] { "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 4);
        AddChild(margin);

        _label = new Label
        {
            Text = "",
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _label.AddThemeFontSizeOverride("font_size", 12);
        margin.AddChild(_label);

        SetProcess(false);
    }

    // Shows `text` and starts following the cursor. Empty text dismisses, so callers can pass a hint
    // straight through without checking it.
    public void ShowHint(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            Dismiss();
            return;
        }

        // Measured untrimmed, because a Label that is allowed to ellipsize reports a minimum width of
        // about one character and ResetSize would collapse the panel onto it.
        _label.TextOverrunBehavior = TextServer.OverrunBehavior.NoTrimming;
        _label.Text = text;
        ResetSize();

        float max = GetViewportRect().Size.X * MaxWidthFraction;
        if (Size.X > max)
        {
            _label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            Size = new Vector2(max, Size.Y);
        }

        Visible = true;
        SetProcess(true);
        Follow();
    }

    public void Dismiss()
    {
        Visible = false;
        SetProcess(false);
    }

    public override void _Process(double delta) => Follow();

    private void Follow() =>
        Position = UI.HoverTooltipPlacement.Place(GetGlobalMousePosition(), Size, GetViewportRect());
}
