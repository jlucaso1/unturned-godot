using Godot;

namespace UnturnedGodot.UI;

// Where a hover tooltip sits, given the cursor and the rectangle it has to stay inside.
//
// The rules are Godot's own (Viewport::_gui_show_tooltip_at): offset down-right from the cursor, flip to
// the other side of the cursor when that would overflow, and hug the far edge when the flip does not fit
// either. The arithmetic is ours because the engine's tooltip is a Popup, and in a release export every
// embedded Popup fails to undo the focus_entered/tree_exited connections it makes on the root window when
// it hides -- two "Attempt to disconnect a nonexistent connection" errors per hover
// (godotengine/godot#87626, #89657; fix PR #95100 is still open). Placing our own Control instead keeps
// the behaviour and takes the engine's Popup out of the boot menu entirely.
public static class HoverTooltipPlacement
{
    // Clear of the cursor without being detached from it: to the right of the hotspot and below the
    // pointer's own height, which is roughly what the engine's default tooltip offset gives.
    public static readonly Vector2 DefaultOffset = new(12, 20);

    // The top-left corner for a tooltip of `size` next to `cursor`, kept within `bounds`.
    public static Vector2 Place(Vector2 cursor, Vector2 size, Rect2 bounds, Vector2 offset)
    {
        return new Vector2(
            Axis(cursor.X, size.X, bounds.Position.X, bounds.Size.X, offset.X),
            Axis(cursor.Y, size.Y, bounds.Position.Y, bounds.Size.Y, offset.Y));
    }

    public static Vector2 Place(Vector2 cursor, Vector2 size, Rect2 bounds) =>
        Place(cursor, size, bounds, DefaultOffset);

    // One axis of the same decision: past the cursor, else before it, else against the far edge -- and
    // never starting before the near edge, so a tooltip too big to fit shows its beginning rather than
    // its middle.
    private static float Axis(float cursor, float size, float boundsStart, float boundsSize, float offset)
    {
        float boundsEnd = boundsStart + boundsSize;
        float start = cursor + offset;

        if (start + size > boundsEnd)
        {
            start = cursor - size - offset;   // flip to the near side of the cursor
            if (start < boundsStart)
                start = boundsEnd - size;     // no room either way: sit against the far edge
        }

        return start < boundsStart ? boundsStart : start;
    }
}
