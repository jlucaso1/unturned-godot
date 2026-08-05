using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The in-game pause menu.
//
// Small, and worth testing for one reason above the others: it owns the MOUSE. Opening releases the
// cursor so the menu can be clicked, and closing captures it again so the camera resumes. Getting that
// pair wrong strands a player either unable to click the menu they just opened, or unable to look around
// after they closed it — and both look like the game froze.
//
// Escape toggles it, and the toggle marks the event handled so the same keypress does not also reach
// whatever else is listening.
public class PauseMenuTests : TestClass
{
    public PauseMenuTests(Node testScene) : base(testScene) { }

    // Closed on arrival: the menu appears because the player asked, never because the scene loaded.
    [Test]
    public async Task ItStartsClosed()
    {
        var menu = new PauseMenu();
        TestScene.AddChild(menu);
        await NextFrame();

        Assert.False(menu.IsOpen);
        menu.QueueFree();
    }

    // Escape opens it, and Escape closes it again. One key, both ways — a player who opened it with
    // Escape reaches for Escape to leave.
    [Test]
    public async Task EscapeTogglesItBothWays()
    {
        var menu = new PauseMenu();
        TestScene.AddChild(menu);
        await NextFrame();

        menu._Input(Escape());
        Assert.True(menu.IsOpen);

        menu._Input(Escape());
        Assert.False(menu.IsOpen);

        menu.QueueFree();
    }

    // A key repeat is not a second press. Holding Escape would otherwise strobe the menu open and shut.
    [Test]
    public async Task HoldingEscapeDoesNotStrobeIt()
    {
        var menu = new PauseMenu();
        TestScene.AddChild(menu);
        await NextFrame();
        menu._Input(Escape());
        Assert.True(menu.IsOpen);

        menu._Input(new InputEventKey { Keycode = Key.Escape, Pressed = true, Echo = true });
        Assert.True(menu.IsOpen, "a key repeat closed the menu");

        // And a release is not a press either.
        menu._Input(new InputEventKey { Keycode = Key.Escape, Pressed = false });
        Assert.True(menu.IsOpen);

        menu.QueueFree();
    }

    // Other keys are left alone, so the menu does not swallow input meant for the game.
    [Test]
    public async Task OtherKeysAreLeftAlone()
    {
        var menu = new PauseMenu();
        TestScene.AddChild(menu);
        await NextFrame();

        menu._Input(new InputEventKey { Keycode = Key.W, Pressed = true });
        menu._Input(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Left });

        Assert.False(menu.IsOpen);
        menu.QueueFree();
    }

    // The mouse. Opening releases the cursor so the menu can be clicked; closing captures it so the
    // camera resumes. Either half missing strands the player — unable to click the menu, or unable to
    // look around after leaving it.
    [Test]
    public async Task OpeningReleasesTheCursorAndClosingTakesItBack()
    {
        Input.MouseModeEnum previous = Input.MouseMode;
        var menu = new PauseMenu();
        TestScene.AddChild(menu);
        await NextFrame();
        try
        {
            menu._Input(Escape());
            Assert.Equal(Input.MouseModeEnum.Visible, Input.MouseMode);
            Assert.True(menu.IsOpen);

            menu._Input(Escape());
            // Only the release is asserted directly. A headless DisplayServer has no window to capture
            // a cursor into, so it does not hold the Captured mode a real one does — asserting it here
            // would be asserting the display server rather than this menu.
            Assert.False(menu.IsOpen);
        }
        finally
        {
            Input.MouseMode = previous;
            menu.QueueFree();
        }
    }

    // Opening to LAN without a host says so rather than failing quietly. A player who clicked it and saw
    // nothing happen has no way to tell whether it worked.
    [Test]
    public async Task OpeningToLanWithNoHostReportsWhyNot()
    {
        var menu = new PauseMenu();
        TestScene.AddChild(menu);
        await NextFrame();
        menu._Input(Escape());

        Button? lan = FindButton(menu, "LAN");
        Assert.NotNull(lan);
        lan!.EmitSignal(BaseButton.SignalName.Pressed);

        Assert.Contains(Labels(menu), l => l.Text.Length > 0);

        Input.MouseMode = Input.MouseModeEnum.Visible;
        menu.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static InputEventKey Escape() =>
        new() { Keycode = Key.Escape, Pressed = true, Echo = false };

    private static Button? FindButton(Node parent, string containing)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is Button button)
                foreach (Label label in Labels(button))
                    if (label.Text.Contains(containing, System.StringComparison.OrdinalIgnoreCase))
                        return button;
            if (FindButton(child, containing) is { } nested)
                return nested;
        }
        return null;
    }

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

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
