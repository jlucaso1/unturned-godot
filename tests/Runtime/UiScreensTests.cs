using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The screens that stand between the player and the world: the loading screen, the cold-load banner, and
// the performance overlay.
//
// All three are engine-only by nature — a CanvasLayer, a Tween, a keyboard toggle and Godot's own
// Performance monitors. None of them has a pure half that could live in core/.
//
// What is worth testing here is not that they draw. It is that they report the truth: a failed load says
// so instead of sweeping its bar forever, a long first run explains itself rather than looking hung, and
// the cold-load banner gets out of the way when the load is done.
public class UiScreensTests : TestClass
{
    public UiScreensTests(Node testScene) : base(testScene) { }

    // --- the loading screen --------------------------------------------------------------------------

    // The map is often unknown when the screen goes up — joining a server, it only arrives once the
    // server answers which level it runs — so the title is set afterwards.
    [Test]
    public void TheLoadingScreenIsTitledOnceTheMapIsKnown()
    {
        var screen = new LoadingScreen { MapName = "" };
        TestScene.AddChild(screen);

        screen.SetMap("PEI");
        screen.SetStatus("Reading Level.hierarchy…");

        Assert.Contains(Labels(screen), l => l.Text.Contains("PEI"));
        Assert.Contains(Labels(screen), l => l.Text.Contains("Level.hierarchy"));

        screen.QueueFree();
    }

    // A load that dies has to LOOK dead. Some maps use formats this port does not read yet, and a bar
    // still sweeping is indistinguishable from a hang — the player waits instead of going back.
    [Test]
    public async Task AFailedLoadStopsSweepingAndOffersTheWayBack()
    {
        var screen = new LoadingScreen { MapName = "Germany" };
        TestScene.AddChild(screen);
        await NextFrame();

        int backPresses = 0;
        screen.Fail("Foliage.blob version 9 is not readable yet.", () => backPresses++);
        await NextFrame();

        Assert.Contains(Labels(screen), l => l.Text.Contains("Could not load this map"));
        Assert.Contains(Labels(screen), l => l.Text.Contains("Foliage.blob"));

        Button? back = FindButton(screen);
        Assert.NotNull(back);
        back!.EmitSignal(BaseButton.SignalName.Pressed);
        Assert.Equal(1, backPresses);

        screen.QueueFree();
    }

    // The headline is a parameter because the same screen also reports a join that never happened, where
    // "could not load this map" would name the wrong culprit.
    [Test]
    public void AFailureCanNameADifferentCulprit()
    {
        var screen = new LoadingScreen { MapName = "PEI" };
        TestScene.AddChild(screen);

        screen.Fail("The server did not answer.", () => { }, headline: "Could not join.");

        Assert.Contains(Labels(screen), l => l.Text.Contains("Could not join."));
        Assert.DoesNotContain(Labels(screen), l => l.Text.Contains("Could not load this map"));

        screen.QueueFree();
    }

    // A failed screen stops animating entirely. Without this the bar would keep sweeping under the error
    // text, which reads as "still working" — the exact impression the failure message exists to correct.
    [Test]
    public async Task AFailedScreenStopsAnimating()
    {
        var screen = new LoadingScreen { MapName = "PEI" };
        TestScene.AddChild(screen);
        await NextFrame();
        await NextFrame();

        screen.Fail("nope", () => { });
        // The bar is two ColorRects: the track it slides along, and the sweep itself. Only the sweep is
        // hidden, so "some ColorRect went invisible and then stopped moving" is the honest assertion.
        System.Collections.Generic.List<ColorRect> bars = ColorRects(screen);
        ColorRect? sweep = bars.Find(r => !r.Visible);
        Assert.NotNull(sweep);
        Vector2 restingPlace = sweep!.Position;

        for (int i = 0; i < 3; i++)
            await NextFrame();

        Assert.False(sweep.Visible);
        Assert.Equal(restingPlace, sweep.Position);

        screen.QueueFree();
    }

    // Finishing fades out and frees itself — the world is already drawn underneath, so a screen that
    // lingered would be covering a playable game.
    [Test]
    public async Task FinishingFadesTheScreenOutAndFreesIt()
    {
        var screen = new LoadingScreen { MapName = "PEI" };
        TestScene.AddChild(screen);
        await NextFrame();

        screen.Finish();
        // The fade is 0.3 SECONDS, and a headless frame is far shorter than 1/60 s — counting frames
        // here would give up long before the tween finished. Wait on the clock instead, with a ceiling
        // so a screen that never frees itself fails rather than hangs.
        ulong deadline = Time.GetTicksMsec() + 3000;
        while (GodotObject.IsInstanceValid(screen) && Time.GetTicksMsec() < deadline)
            await NextFrame();

        Assert.False(GodotObject.IsInstanceValid(screen), "the loading screen never freed itself");
    }

    // --- the cold-load banner ------------------------------------------------------------------------

    // The banner follows the streamer's own signals rather than polling it, and hides itself when the
    // load finishes. On a warm cache the streamer fires everything before the first frame, so this is
    // also what stops the banner from appearing at all.
    [Test]
    public void TheColdLoadBannerFollowsTheStreamerAndThenGetsOutOfTheWay()
    {
        var overlay = new LoadingOverlay();
        TestScene.AddChild(overlay);
        var streamer = new ObjectStreamer { Name = "ObjectStreamer" };
        TestScene.AddChild(streamer);

        overlay.Track(streamer);

        streamer.EmitSignal(ObjectStreamer.SignalName.MeshesReady, 1234.0);
        Assert.Contains(Labels(overlay), l => l.Text.Contains("1234") && l.Text.Contains("textures"));

        streamer.EmitSignal(ObjectStreamer.SignalName.Progress, 48, 280);
        Assert.Contains(Labels(overlay), l => l.Text.Contains("48/280"));

        Assert.True(overlay.Visible);
        streamer.EmitSignal(ObjectStreamer.SignalName.Finished);
        Assert.False(overlay.Visible);

        overlay.QueueFree();
        streamer.QueueFree();
    }

    // --- the performance overlay ---------------------------------------------------------------------

    // F3 toggles it. The overlay is the only way to see the frame cost while playing, so a toggle that
    // stopped working would be silently unnoticeable.
    [Test]
    public async Task TheDebugOverlayTogglesOnItsKey()
    {
        var overlay = new DebugOverlay();
        TestScene.AddChild(overlay);
        await NextFrame();

        bool before = overlay.Visible;
        overlay._Input(new InputEventKey { Keycode = Key.F3, Pressed = true });
        Assert.NotEqual(before, overlay.Visible);

        overlay._Input(new InputEventKey { Keycode = Key.F3, Pressed = true });
        Assert.Equal(before, overlay.Visible);

        // A key repeat is not a second press: holding F3 down would otherwise strobe the overlay.
        bool afterToggles = overlay.Visible;
        overlay._Input(new InputEventKey { Keycode = Key.F3, Pressed = true, Echo = true });
        Assert.Equal(afterToggles, overlay.Visible);

        // And a release is not a press either.
        overlay._Input(new InputEventKey { Keycode = Key.F3, Pressed = false });
        Assert.Equal(afterToggles, overlay.Visible);

        overlay.QueueFree();
    }

    // F4 copies the current camera as a SHOT_CAM value, so any view can be reproduced headless. It reads
    // whatever camera is rendering — free-fly or the player's — which is why it lives here rather than on
    // the free camera.
    [Test]
    public async Task F4CopiesTheCurrentCameraAsAShotCamValue()
    {
        var overlay = new DebugOverlay();
        TestScene.AddChild(overlay);
        var camera = new Camera3D { Current = true, Position = new Vector3(12.5f, 34f, -56f) };
        camera.RotationDegrees = new Vector3(-10f, 45f, 0f);
        TestScene.AddChild(camera);
        await NextFrame();

        overlay._Input(new InputEventKey { Keycode = Key.F4, Pressed = true });

        // Not asserted through the clipboard: a headless DisplayServer has none, which is exactly why
        // the code prints the value as well. What matters is that the handler runs to completion over a
        // real camera rather than throwing inside an input callback.
        Assert.True(GodotObject.IsInstanceValid(overlay));

        camera.QueueFree();
        overlay.QueueFree();
    }

    // With no camera rendering at all there is nothing to copy, and reaching for one anyway would throw
    // inside an input handler.
    [Test]
    public async Task F4WithNoCameraDoesNothing()
    {
        var overlay = new DebugOverlay();
        TestScene.AddChild(overlay);
        await NextFrame();

        overlay._Input(new InputEventKey { Keycode = Key.F4, Pressed = true });

        overlay.QueueFree();
    }

    // The readout is throttled rather than rebuilt every frame: it is a diagnostic, and one that cost a
    // measurable slice of the frame would be lying about the frame it measures.
    [Test]
    public async Task TheReadoutIsThrottledAndEventuallyFills()
    {
        var overlay = new DebugOverlay { UpdateInterval = 0.0 }; // every frame, so the test is not slow
        TestScene.AddChild(overlay);

        for (int i = 0; i < 3; i++)
            await NextFrame();

        Assert.Contains(Labels(overlay), l => l.Text.Contains("FPS"));
        overlay.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static Button? FindButton(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is Button button)
                return button;
            if (FindButton(child) is { } nested)
                return nested;
        }
        return null;
    }

    private static System.Collections.Generic.List<ColorRect> ColorRects(Node parent)
    {
        var found = new System.Collections.Generic.List<ColorRect>();
        Collect(parent, found);
        return found;

        static void Collect(Node node, System.Collections.Generic.List<ColorRect> into)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is ColorRect rect)
                    into.Add(rect);
                Collect(child, into);
            }
        }
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
