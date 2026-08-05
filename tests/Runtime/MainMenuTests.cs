using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The boot menu: pick a map, or type a server address.
//
// The rule with a bug in its history is the one about JOINING. When a player connects to a server, no map
// is passed and none has to be picked — the server is asked which level it runs and that is what gets
// built. Sending the browser's current selection along is what let a player join a PEI host and land in
// California 2: two different worlds, one session, and the player walking through geometry nobody else
// could see.
//
// So the address path deliberately passes an EMPTY map, and that is asserted rather than left to the
// reader. Everything else here is about not stranding the player: an empty address says so instead of
// starting nothing, and the cursor is released so the menu can be clicked at all.
public class MainMenuTests : TestClass
{
    public MainMenuTests(Node testScene) : base(testScene) { }

    // The menu releases the cursor. It is the first thing on screen, and a captured cursor there means a
    // player who cannot click anything and has no way to find out why.
    [Test]
    public async Task TheMenuReleasesTheCursor()
    {
        Input.MouseModeEnum previous = Input.MouseMode;
        var menu = new MainMenu { UnturnedPath = "" };
        TestScene.AddChild(menu);
        await NextFrame();

        try
        {
            Assert.Equal(Input.MouseModeEnum.Visible, Input.MouseMode);
        }
        finally
        {
            Input.MouseMode = previous;
            menu.QueueFree();
        }
    }

    // Connecting with no address typed says so rather than starting nothing. A button that appears to do
    // nothing is indistinguishable from one that is broken.
    [Test]
    public async Task ConnectingWithNoAddressSaysSo()
    {
        var menu = new MainMenu { UnturnedPath = "" };
        int starts = 0;
        menu.OnStart = (_, _) => starts++;
        TestScene.AddChild(menu);
        await NextFrame();

        Press(menu, "Connect");   // reveals the address row
        LineEdit? field = FindLineEdit(menu);
        Assert.NotNull(field);
        field!.Text = "   ";      // whitespace only, which is what an empty box amounts to
        Press(menu, "Join server");

        Assert.Equal(0, starts);
        Assert.Contains(Labels(menu), l => l.Text.Contains("address", System.StringComparison.OrdinalIgnoreCase));

        menu.QueueFree();
    }

    // The bug this exists to prevent: joining passes NO map. The server is asked which level it runs, and
    // sending the browser's selection along is what let a player join a PEI host and land in California 2.
    [Test]
    public async Task JoiningAServerPassesNoMapAtAll()
    {
        var menu = new MainMenu { UnturnedPath = "" };
        string? startedMap = null;
        string? startedAddress = null;
        menu.OnStart = (map, address) => (startedMap, startedAddress) = (map, address);
        TestScene.AddChild(menu);
        await NextFrame();

        Press(menu, "Connect"); // reveal the row
        LineEdit? field = FindLineEdit(menu);
        Assert.NotNull(field);
        field!.Text = "  198.51.100.7:27015  "; // with the whitespace a player would leave
        Press(menu, "Join server");

        Assert.Equal("", startedMap); // NOT the browser's selection
        Assert.Equal("198.51.100.7:27015", startedAddress); // and trimmed

        menu.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static void Press(Node parent, string labelled)
    {
        Button? button = FindButton(parent, labelled);
        Assert.True(button != null, $"no button labelled '{labelled}'");
        button!.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private static Button? FindButton(Node parent, string containing)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is Button button)
            {
                if (button.Text.Contains(containing, System.StringComparison.OrdinalIgnoreCase))
                    return button;
                foreach (Label label in Labels(button))
                    if (label.Text.Contains(containing, System.StringComparison.OrdinalIgnoreCase))
                        return button;
            }
            if (FindButton(child, containing) is { } nested)
                return nested;
        }
        return null;
    }

    private static LineEdit? FindLineEdit(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is LineEdit field)
                return field;
            if (FindLineEdit(child) is { } nested)
                return nested;
        }
        return null;
    }

    private static List<Label> Labels(Node parent)
    {
        var found = new List<Label>();
        Collect(parent, found);
        return found;

        static void Collect(Node node, List<Label> into)
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
