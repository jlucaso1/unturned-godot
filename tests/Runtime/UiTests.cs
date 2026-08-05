using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The menu's shared widgets and its hover hint.
//
// HoverTooltipPlacement — where the hint goes relative to the cursor — is pure and unit tested in core/.
// What is only testable here is everything around it: a PanelContainer that has to size itself to its
// text, cap that against the current viewport, and stop processing when it is dismissed.
//
// The hint exists at all because Godot's own tooltips are Popups, and an embedded Popup in a release
// export leaks the connections it makes on the root window and prints a pair of disconnect errors on
// every hide (godotengine/godot#87626). Setting TooltipText anywhere in the menu was enough to spam
// every hover, so the menu draws its own.
public class UiTests : TestClass
{
    public UiTests(Node testScene) : base(testScene) { }

    // --- shared widgets ------------------------------------------------------------------------------

    // A bar's text lives in a child label rather than in Button.Text, because the glyph plate and the
    // centring padding are laid out around it. That is exactly why retitling one needs its own helper:
    // setting Button.Text would put a second, differently-placed caption on the same button.
    [Test]
    public void ABarCanBeRetitledAfterItIsBuilt()
    {
        Button bar = UnturnedUi.MakeBar("»", "Play", UnturnedUi.Olive, () => { });

        Assert.Equal("", bar.Text); // deliberately not where the caption lives
        Assert.Contains(Labels(bar), l => l.Text == "Play");

        UnturnedUi.SetBarText(bar, "Resume");

        Assert.Contains(Labels(bar), l => l.Text == "Resume");
        Assert.DoesNotContain(Labels(bar), l => l.Text == "Play");

        bar.Free();
    }

    // Retitling something that is not one of these bars must not throw: callers reach for it generically,
    // and a plain Button simply has no label to rename.
    [Test]
    public void RetitlingAPlainButtonIsANoOp()
    {
        var plain = new Button { Text = "untouched" };

        UnturnedUi.SetBarText(plain, "ignored");

        Assert.Equal("untouched", plain.Text);
        plain.Free();
    }

    // The bar's action is wired to Pressed, which is the whole reason the helper takes one.
    [Test]
    public void PressingABarRunsItsAction()
    {
        int presses = 0;
        Button bar = UnturnedUi.MakeBar("»", "Play", UnturnedUi.Olive, () => presses++);
        TestScene.AddChild(bar);

        bar.EmitSignal(BaseButton.SignalName.Pressed);

        Assert.Equal(1, presses);
        bar.QueueFree();
    }

    // Hover and pressed are tinted from the base colour rather than being separate constants, so a bar
    // in a new colour cannot end up with a hover state from a different palette.
    [Test]
    public void ABarTintsItsOwnHoverAndPressedStates()
    {
        Button bar = UnturnedUi.MakeBar("»", "Play", UnturnedUi.Brown, () => { });

        var normal = (StyleBoxFlat)bar.GetThemeStylebox("normal");
        var hover = (StyleBoxFlat)bar.GetThemeStylebox("hover");
        var pressed = (StyleBoxFlat)bar.GetThemeStylebox("pressed");

        Assert.Equal(UnturnedUi.Brown, normal.BgColor);
        Assert.True(hover.BgColor.Luminance > normal.BgColor.Luminance, "hover was not lightened");
        Assert.True(pressed.BgColor.Luminance < normal.BgColor.Luminance, "pressed was not darkened");
        // The border is the shared one, so bars of different colours still read as one family.
        Assert.Equal(UnturnedUi.BorderColor, normal.BorderColor);

        bar.Free();
    }

    // A panel is a bar with softer corners: same fill, same border, rounder.
    [Test]
    public void APanelIsABarWithSofterCorners()
    {
        StyleBoxFlat bar = UnturnedUi.Bar(UnturnedUi.Olive);
        StyleBoxFlat panel = UnturnedUi.Panel(UnturnedUi.Olive);

        Assert.Equal(bar.BgColor, panel.BgColor);
        Assert.Equal(bar.BorderColor, panel.BorderColor);
        Assert.True(panel.CornerRadiusTopLeft > bar.CornerRadiusTopLeft);
    }

    [Test]
    public void ACaptionIsMutedAndSized()
    {
        Label caption = UnturnedUi.Caption("14 maps", size: 11);

        Assert.Equal("14 maps", caption.Text);
        Assert.True(caption.Modulate.A < 1f, "the caption was not muted");
        Assert.Equal(11, caption.GetThemeFontSize("font_size"));

        caption.Free();
    }

    // --- the hover hint ------------------------------------------------------------------------------

    // It starts hidden and idle. A hint that processed while invisible would follow the cursor around
    // the menu for the whole session for nothing.
    [Test]
    public void TheHintStartsHiddenAndIdle()
    {
        HoverTooltip tip = HoverTooltip.Create();
        TestScene.AddChild(tip);

        Assert.False(tip.Visible);
        Assert.False(tip.IsProcessing());

        tip.QueueFree();
    }

    // Showing a hint makes it visible and starts it following the cursor; dismissing stops both. The
    // processing half is the one that costs something.
    [Test]
    public void ShowingAHintMakesItFollowTheCursorAndDismissingStopsIt()
    {
        HoverTooltip tip = HoverTooltip.Create();
        TestScene.AddChild(tip);

        tip.ShowHint("PEI");
        Assert.True(tip.Visible);
        Assert.True(tip.IsProcessing());

        tip.Dismiss();
        Assert.False(tip.Visible);
        Assert.False(tip.IsProcessing());

        tip.QueueFree();
    }

    // Empty text dismisses rather than showing an empty panel, so a caller can pass a row's hint straight
    // through without checking whether the row has one.
    [Test]
    public void AnEmptyHintDismissesInsteadOfShowingAnEmptyPanel()
    {
        HoverTooltip tip = HoverTooltip.Create();
        TestScene.AddChild(tip);
        tip.ShowHint("something");

        tip.ShowHint("");
        Assert.False(tip.Visible);

        tip.ShowHint("something");
        tip.ShowHint(null);
        Assert.False(tip.Visible);

        tip.QueueFree();
    }

    // The panel sizes itself to its text — and caps at half the viewport, ellipsizing past that. Long
    // workshop paths are the only thing that gets near the cap, and without it one would span the window.
    [Test]
    public void AVeryLongHintIsCappedAndEllipsized()
    {
        HoverTooltip tip = HoverTooltip.Create();
        TestScene.AddChild(tip);

        tip.ShowHint("PEI");
        float shortWidth = tip.Size.X;

        tip.ShowHint(new string('W', 4000));
        float cap = tip.GetViewportRect().Size.X * 0.5f;

        Assert.True(tip.Size.X > shortWidth, "the panel did not grow with its text");
        Assert.True(tip.Size.X <= cap + 0.5f, $"the panel ran to {tip.Size.X}, past the {cap} cap");

        tip.QueueFree();
    }

    // UG_UI_TRACE is what scripts/check-menu-popup-errors.sh reads: a gate that looks for the ABSENCE of
    // an error needs proof the hover it drove actually landed on a row, or a gate that never hovered
    // anything would pass.
    [Test]
    public void TheHoverTraceRunsWhenAskedFor()
    {
        HoverTooltip tip = HoverTooltip.Create();
        TestScene.AddChild(tip);

        string previous = OS.GetEnvironment("UG_UI_TRACE");
        OS.SetEnvironment("UG_UI_TRACE", "1");
        try
        {
            tip.ShowHint("PEI");
            Assert.True(tip.Visible);
        }
        finally
        {
            OS.SetEnvironment("UG_UI_TRACE", previous);
        }

        tip.QueueFree();
    }

    // The window is resizable and Alt+Enter toggles fullscreen, so the cap has to be redone against the
    // new viewport rather than the one the hint was first measured against. A hidden hint is left alone —
    // it will be re-fitted when it is next shown.
    [Test]
    public void ResizingTheWindowRefitsAVisibleHint()
    {
        HoverTooltip tip = HoverTooltip.Create();
        TestScene.AddChild(tip);

        tip.ShowHint(new string('W', 4000));
        tip.GetViewport().EmitSignal(Viewport.SignalName.SizeChanged);
        Assert.True(tip.Visible);

        tip.Dismiss();
        tip.GetViewport().EmitSignal(Viewport.SignalName.SizeChanged); // hidden: nothing to redo
        Assert.False(tip.Visible);

        tip.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static System.Collections.Generic.List<Label> Labels(Node parent)
    {
        var found = new System.Collections.Generic.List<Label>();
        Collect(parent, found);
        return found;

        static void Collect(Node node, System.Collections.Generic.List<Label> into)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is Label label)
                    into.Add(label);
                Collect(child, into);
            }
        }
    }
}
