using Godot;
using UnturnedGodot.UI;
using Xunit;

namespace UnturnedGodot.Tests;

public class HoverTooltipPlacementTests
{
    private static readonly Rect2 Screen = new(0, 0, 1920, 1080);
    private static readonly Vector2 Offset = new(10, 20);

    [Fact]
    public void Place_SitsPastTheCursorWhenThereIsRoom()
    {
        Vector2 at = HoverTooltipPlacement.Place(new Vector2(400, 300), new Vector2(200, 40), Screen, Offset);

        Assert.Equal(new Vector2(410, 320), at);
    }

    [Fact]
    public void Place_FlipsToTheNearSideWhenItWouldOverflowTheFarEdge()
    {
        // 1850 + 10 + 200 runs past the right edge, so the box goes to the left of the cursor instead.
        Vector2 at = HoverTooltipPlacement.Place(new Vector2(1850, 1040), new Vector2(200, 40), Screen, Offset);

        Assert.Equal(new Vector2(1640, 980), at);
    }

    [Fact]
    public void Place_HugsTheFarEdgeWhenNeitherSideOfTheCursorFits()
    {
        // The cursor is near the left edge and the box is wider than the space on either side of it.
        Vector2 at = HoverTooltipPlacement.Place(new Vector2(100, 300), new Vector2(1850, 40), Screen, Offset);

        Assert.Equal(70, at.X); // 1920 - 1850
        Assert.Equal(320, at.Y);
    }

    [Fact]
    public void Place_KeepsTheStartVisibleWhenTheTooltipIsBiggerThanTheBounds()
    {
        Vector2 at = HoverTooltipPlacement.Place(new Vector2(960, 540), new Vector2(2400, 1200), Screen, Offset);

        Assert.Equal(Vector2.Zero, at);
    }

    [Fact]
    public void Place_IsRelativeToTheBoundsOrigin()
    {
        var panel = new Rect2(500, 200, 400, 300);

        Vector2 inside = HoverTooltipPlacement.Place(new Vector2(520, 220), new Vector2(100, 30), panel, Offset);
        Assert.Equal(new Vector2(530, 240), inside);

        // Past the panel's own right/bottom edge, not the screen's.
        Vector2 flipped = HoverTooltipPlacement.Place(new Vector2(880, 480), new Vector2(100, 30), panel, Offset);
        Assert.Equal(new Vector2(770, 430), flipped);
    }

    [Fact]
    public void Place_AppliesTheDefaultOffsetWhenNoneIsGiven()
    {
        Vector2 at = HoverTooltipPlacement.Place(new Vector2(100, 100), new Vector2(50, 20), Screen);

        Assert.Equal(new Vector2(100, 100) + HoverTooltipPlacement.DefaultOffset, at);
    }
}
