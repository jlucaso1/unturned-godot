using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot;
using UnturnedGodot.Assets;
using UnturnedGodot.DevConsole;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The in-game console, and the half of it that only an engine can answer: the key that opens it, the
// cursor it takes and gives back, the log arriving from worker threads, and the bindings actually
// reaching the world through the scene tree.
//
// The parsing, the ranges and the wording of every refusal are covered without an engine, in
// tests/Console. What is here is everything those tests structurally cannot see.
//
// The registry is process-wide by design — a configuration has to survive a return to the menu and the
// load of another map — so every test that changes a value puts it back. A leak here would not fail this
// suite; it would quietly change the frame every later test measures.
public class ConsoleTests : TestClass
{
    public ConsoleTests(Node testScene) : base(testScene) { }

    [Setup]
    public void EveryVariableStartsAtItsDefault() => RenderConsole.Console.Execute("reset all");

    // --- the pane ------------------------------------------------------------------------------------

    [Test]
    public async Task TheToggleKeyOpensAndClosesItAndTheCursorIsGivenBack()
    {
        ConsoleOverlay console = await Open();
        Assert.True(console.IsOpen);
        Assert.Equal(Input.MouseModeEnum.Visible, Input.MouseMode);

        console._Input(Press(Key.F1));
        Assert.False(console.IsOpen);

        // The backtick is the key muscle memory reaches for, and it opens the same pane.
        console._Input(Press(Key.Quoteleft));
        Assert.True(console.IsOpen);

        Release(console);
    }

    // Escape closes the console rather than reaching the pause menu behind it, and the keystroke stops
    // here: the menu and the player both read Escape, and both would act on it otherwise.
    [Test]
    public async Task EscapeClosesItAndGoesNoFurther()
    {
        ConsoleOverlay console = await Open();

        // Pushed through the viewport rather than called directly: that is what resets the handled flag
        // before dispatch, so the assertion below is about THIS keystroke and not a leftover one.
        TestScene.GetViewport().PushInput(Press(Key.Escape));

        Assert.False(console.IsOpen);
        Assert.True(TestScene.GetViewport().IsInputHandled());
        Release(console);
    }

    // A key that means nothing to the console while it is closed must reach whatever is behind it — the
    // pane is not a modal, it is a pane that is not on screen.
    [Test]
    public async Task AClosedConsoleSwallowsNothing()
    {
        ConsoleOverlay console = await Attached();

        TestScene.GetViewport().PushInput(Press(Key.W));

        Assert.False(TestScene.GetViewport().IsInputHandled());
        Release(console);
    }

    [Test]
    public async Task TheScrollbackStartsWithWhatAlreadyHappened()
    {
        Log.Print("[test] this happened before the console existed");

        ConsoleOverlay console = await Attached();

        Assert.Contains("this happened before the console existed", console.Output.GetParsedText());
        Release(console);
    }

    // Log lines are written from the bundle decoders, the extraction pass and the foliage streamer, none
    // of which run on the main thread — and a node may only be touched from that one. The handler
    // enqueues; the frame loop is where they become text.
    [Test]
    public async Task LinesLoggedFromAWorkerThreadReachTheScrollback()
    {
        ConsoleOverlay console = await Attached();

        await Task.Run(() => Log.Print("[test] written from a worker"));
        await NextFrame();

        Assert.Contains("written from a worker", console.Output.GetParsedText());
        Release(console);
    }

    // The log is full of bracketed tags and the pane reads BBCode, so an unescaped "[stream]" would be
    // eaten as markup — the line would still be there, minus the part that says what wrote it.
    [Test]
    public async Task BracketsInTheLogSurviveTheMarkup()
    {
        ConsoleOverlay console = await Attached();

        Log.Print("[stream] scene built: 157 ms");
        await NextFrame();

        Assert.Contains("[stream] scene built: 157 ms", console.Output.GetParsedText());
        Release(console);
    }

    [Test]
    public async Task ClearEmptiesTheScrollbackWithoutStoppingTheLog()
    {
        ConsoleOverlay console = await Attached();
        Log.Print("[test] before the clear");
        await NextFrame();

        console.Submit("clear", echo: false);
        Assert.Empty(console.Output.GetParsedText());

        Log.Print("[test] after the clear");
        await NextFrame();
        Assert.Contains("after the clear", console.Output.GetParsedText());
        Release(console);
    }

    // Tab completes to the longest head every candidate shares and then lists them, which is what makes
    // dotted names typeable without anybody memorising them.
    [Test]
    public async Task TabCompletesTheNameAndListsWhatIsLeft()
    {
        ConsoleOverlay console = await Open();

        console.Prompt.Text = "objects.";
        console._Input(Press(Key.Tab));

        Assert.Equal("objects.", console.Prompt.Text); // already the shared head; the list is the answer
        Assert.Contains("objects.trees.enabled", console.Output.GetParsedText());

        console.Prompt.Text = "objects.tr";
        console._Input(Press(Key.Tab));
        Assert.Equal("objects.trees.enabled", console.Prompt.Text);

        console.Prompt.Text = "zzz";
        console._Input(Press(Key.Tab));
        Assert.Contains("Nothing starts with 'zzz'", console.Output.GetParsedText());

        // A value is not a name, so a line that already has one is left alone.
        console.Prompt.Text = "objects.tr 0";
        console._Input(Press(Key.Tab));
        Assert.Equal("objects.tr 0", console.Prompt.Text);

        Release(console);
    }

    [Test]
    public async Task TheArrowsWalkWhatWasTyped()
    {
        ConsoleOverlay console = await Open();

        console.Prompt.EmitSignal(LineEdit.SignalName.TextSubmitted, "perf");
        console.Prompt.EmitSignal(LineEdit.SignalName.TextSubmitted, "list foliage");
        Assert.Empty(console.Prompt.Text); // submitting clears the box

        console._Input(Press(Key.Up));
        Assert.Equal("list foliage", console.Prompt.Text);
        console._Input(Press(Key.Up));
        Assert.Equal("perf", console.Prompt.Text);
        console._Input(Press(Key.Down));
        Assert.Equal("list foliage", console.Prompt.Text);

        Release(console);
    }

    // --- the bindings --------------------------------------------------------------------------------

    [Test]
    public async Task AToggleReachesTheWorldThroughItsGroup()
    {
        ConsoleOverlay console = await Attached();
        var terrain = new Node3D { Name = "Terrain" };
        terrain.AddToGroup(SceneGroups.Terrain);
        TestScene.AddChild(terrain);

        console.Submit("terrain.enabled 0", echo: false);
        Assert.False(terrain.Visible);

        console.Submit("terrain.enabled 1", echo: false);
        Assert.True(terrain.Visible);

        Discard(terrain);
        Release(console);
    }

    // Ctrl+C had to be taken from the LineEdit to reach the scrollback at all, and taking it must not
    // cost the prompt its own copy: with nothing selected in the log, the keystroke belongs to whatever
    // the person selected in the input box.
    //
    // Asserted on the decision rather than on the viewport's handled flag, because that flag cannot tell
    // the two outcomes apart: the focused LineEdit consumes Ctrl+C either way, and its doing so is the
    // fallback this is about. What the overlay controls is whether it takes the key first.
    [Test]
    public async Task CtrlCCopiesNothingWhenNothingInTheLogIsSelected()
    {
        ConsoleOverlay console = await Open();
        console.Submit("help", echo: true); // scrollback with text in it, but no selection

        Assert.False(console.CopySelection());
        // And the keystroke still reaches the prompt, which is where the caret is.
        TestScene.GetViewport().PushInput(Press(Key.C, control: true));
        Assert.True(console.Prompt.HasFocus());

        Release(console);
    }

    // A paste REPLACES what is selected, which InsertTextAtCaret does not do on its own: the LineEdit
    // deletes the selection first on its own paste path, and this one does not go through it. Pasting a
    // recipe over a selected command used to keep both and run the two welded together.
    [Test]
    public async Task PastingOverASelectionReplacesItRatherThanAddingToIt()
    {
        ConsoleOverlay console = await Open();
        console.Prompt.Text = "objects.trees.enabled 0";

        console.Prompt.SelectAll();
        console.InsertAtPrompt("terrain.enabled 0; perf");
        Assert.Equal("terrain.enabled 0; perf", console.Prompt.Text);

        // With nothing selected it is still an insert at the caret, not a replacement of the line.
        console.Prompt.Deselect();
        console.Prompt.CaretColumn = 0;
        console.InsertAtPrompt("help; ");
        Assert.Equal("help; terrain.enabled 0; perf", console.Prompt.Text);

        Release(console);
    }

    // A one-line clipboard is left to the LineEdit: its own paste already handles the selection, the
    // caret and the undo stack, and there is nothing about it to fix. Only a block it cannot hold —
    // one the newlines would be silently dropped from — is taken over.
    //
    // The clipboard is set rather than assumed, and put back afterwards. Read as it lies, this would
    // pass on a session that has no clipboard at all (the guard refuses first, for a different reason)
    // and fail on one whose clipboard happened to hold several lines.
    [Test]
    public async Task ASingleLineClipboardIsLeftToTheLineEdit()
    {
        ConsoleOverlay console = await Open();
        if (!DisplayServer.HasFeature(DisplayServer.Feature.Clipboard))
        {
            // Headless: refused at the guard, which is the honest answer here rather than a green tick
            // for a branch that never ran.
            Assert.False(console.PasteBlock());
            Release(console);
            return;
        }

        string previous = DisplayServer.ClipboardGet();
        try
        {
            DisplayServer.ClipboardSet("perf");
            Assert.False(console.PasteBlock());

            DisplayServer.ClipboardSet("terrain.enabled 0\nperf");
            Assert.True(console.PasteBlock());
            Assert.Equal("terrain.enabled 0; perf", console.Prompt.Text);
        }
        finally
        {
            DisplayServer.ClipboardSet(previous);
        }
        Release(console);
    }

    // A session with no clipboard — this one, and any headless run — says so rather than reporting a
    // copy that did not happen.
    [Test]
    public async Task TheCopyCommandSaysWhenThereIsNoClipboardToCopyTo()
    {
        ConsoleOverlay console = await Attached();

        console.Submit("copy", echo: false);

        Assert.Contains(DisplayServer.HasFeature(DisplayServer.Feature.Clipboard)
            ? "Scrollback copied" : "no clipboard", console.Output.GetParsedText());
        Release(console);
    }

    // The splat toggle does not hide anything — it reaches into the shading of the tiles that stay on
    // screen. It also has to tell "no terrain at all" apart from "terrain wearing the flat fallback
    // material", because on a map whose layer textures could not be read there is nothing to skip and a
    // silent no-op would read as a measurement.
    [Test]
    public async Task TheSplatToggleReachesTheTileMaterialsAndSaysWhenThereAreNone()
    {
        ConsoleOverlay console = await Attached();
        var terrain = new Node3D { Name = "Terrain" };
        terrain.AddToGroup(SceneGroups.Terrain);
        var flat = new MeshInstance3D { Name = "Tile_flat", Mesh = new BoxMesh() };
        flat.Mesh.SurfaceSetMaterial(0, new StandardMaterial3D());
        terrain.AddChild(flat);
        TestScene.AddChild(terrain);

        console.Submit("terrain.splat.unpainted.enabled 1", echo: false);
        Assert.Contains("flat-colour fallback material", console.Output.GetParsedText());

        var material = new ShaderMaterial
        {
            Shader = new Shader { Code = TerrainBuilder.SplatShaderCode(1) },
        };
        var splat = new MeshInstance3D { Name = "Tile_0_0", Mesh = new BoxMesh() };
        splat.Mesh.SurfaceSetMaterial(0, material);
        terrain.AddChild(splat);

        console.Submit("terrain.splat.unpainted.enabled 1", echo: false);
        Assert.True(material.GetShaderParameter("sample_unpainted").AsBool());
        console.Submit("terrain.splat.unpainted.enabled 0", echo: false);
        Assert.False(material.GetShaderParameter("sample_unpainted").AsBool());

        Discard(terrain);
        Release(console);
    }

    // The map-load contract, which is the reason the values live at process scope: a toggle set while
    // nothing is loaded is kept, said out loud, and pushed at the world that appears next.
    [Test]
    public async Task AValueSetAgainstNoWorldIsKeptAndAppliedToTheNextOne()
    {
        ConsoleOverlay console = await Attached();

        console.Submit("terrain.enabled 0", echo: false);
        Assert.Contains("No terrain is loaded", console.Output.GetParsedText());

        var terrain = new Node3D { Name = "Terrain" };
        terrain.AddToGroup(SceneGroups.Terrain);
        TestScene.AddChild(terrain);
        Assert.True(terrain.Visible);

        console.ReapplySettings();

        Assert.False(terrain.Visible);
        Discard(terrain);
        Release(console);
    }

    [Test]
    public async Task OneKindOfObjectCanBeSwitchedOffWithoutTheRest()
    {
        ConsoleOverlay console = await Attached();
        var batches = new MultiMeshRidRenderer { Name = "ObjectsBatches" };
        batches.Add(Batch(), Transform3D.Identity, category: EObjectType.Resource);
        batches.Add(Batch(), Transform3D.Identity, category: EObjectType.Large);
        batches.AddToGroup(SceneGroups.ObjectBatches);
        TestScene.AddChild(batches);
        await NextFrame();

        console.Submit("objects.trees.enabled 0", echo: false);
        // Found the batches: no notice. The trees are still submitted — the renderer still owns them —
        // they are simply not drawn, which is what makes the difference measurable and reversible.
        Assert.DoesNotContain("No object batches", console.Output.GetParsedText());
        Assert.Equal(1, batches.CountOf(EObjectType.Resource));

        // A category this map does not place is reported rather than silently doing nothing — otherwise
        // "I switched the NPCs off and nothing changed" reads as a broken console.
        console.Submit("npcs.enabled 0", echo: false);
        Assert.Contains("No NPC set is loaded", console.Output.GetParsedText());

        console.Submit("objects.shadows.enabled 0", echo: false);
        Assert.True(batches.ShadowsSuppressed);

        console.Submit("reset all", echo: false);
        Assert.False(batches.ShadowsSuppressed);

        Discard(batches);
        Release(console);
    }

    [Test]
    public async Task ARendererSettingReachesTheViewport()
    {
        ConsoleOverlay console = await Attached();
        float before = TestScene.GetViewport().Scaling3DScale;
        try
        {
            console.Submit("r.scale 0.5", echo: false);
            Assert.Equal(0.5f, TestScene.GetViewport().Scaling3DScale);

            // Out of range is refused, and the refusal leaves the frame exactly as it was — which is the
            // whole reason it is refused rather than clamped.
            console.Submit("r.scale 9", echo: false);
            Assert.Equal(0.5f, TestScene.GetViewport().Scaling3DScale);
            Assert.Contains("outside r.scale's range", console.Output.GetParsedText());
        }
        finally
        {
            console.Submit("reset r.scale", echo: false);
            TestScene.GetViewport().Scaling3DScale = before;
        }
        Release(console);
    }

    // The foliage draw distance can only be moved on the streamed renderer, which owns the visibility
    // ranges. Any other shape says so instead of appearing to work.
    [Test]
    public async Task TheFoliageRangeSaysWhenThisSessionsFoliageCannotMove()
    {
        ConsoleOverlay console = await Attached();
        var foliage = new Node3D { Name = "Foliage" };
        foliage.AddToGroup(SceneGroups.Foliage);
        TestScene.AddChild(foliage);

        console.Submit("foliage.range 0.5", echo: false);

        Assert.Contains("not the streamed renderer", console.Output.GetParsedText());
        Discard(foliage);
        Release(console);
    }

    [Test]
    public async Task PerfWritesTheNumbersIntoTheScrollback()
    {
        ConsoleOverlay console = await Attached();

        console.Submit("perf", echo: false);

        Assert.Contains("draw calls", console.Output.GetParsedText());
        Release(console);
    }

    // UG_CONSOLE is how a benchmark, a screenshot or a bug report makes the same change a person would
    // have made by hand, with nobody there to make it.
    [Test]
    public async Task TheEnvironmentCanConfigureASessionBeforeAnybodyTypes()
    {
        string previous = OS.GetEnvironment("UG_CONSOLE");
        OS.SetEnvironment("UG_CONSOLE", "terrain.enabled 0");
        try
        {
            ConsoleOverlay console = await Attached();

            Assert.False(RenderConsole.Console.Variable("terrain.enabled")!.AsBool);
            Assert.Contains("> terrain.enabled 0", console.Output.GetParsedText());
            Release(console);
        }
        finally
        {
            OS.SetEnvironment("UG_CONSOLE", previous);
            RenderConsole.Console.Execute("reset all");
        }
    }

    private async Task<ConsoleOverlay> Attached()
    {
        var console = new ConsoleOverlay { Name = "Console" };
        TestScene.AddChild(console);
        await NextFrame();
        return console;
    }

    private async Task<ConsoleOverlay> Open()
    {
        ConsoleOverlay console = await Attached();
        console._Input(Press(Key.F1));
        return console;
    }

    // Freed rather than queued: the overlay unsubscribes from the log in _ExitTree, and a queued free
    // lands at the end of the frame — long enough for the next test's lines to arrive in this one's
    // scrollback.
    private static void Release(ConsoleOverlay console) => Discard(console);

    // Freed rather than queued for the same reason: a queued free lands at the end of the frame, and a
    // node that is still in its group when the next test runs is a world that test never built.
    private static void Discard(Node node)
    {
        node.GetParent()?.RemoveChild(node);
        node.Free();
    }

    private static InputEventKey Press(Key key, bool control = false) =>
        new() { Keycode = key, Pressed = true, CtrlPressed = control };

    private static MultiMesh Batch()
    {
        var mesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new BoxMesh(),
            InstanceCount = 1,
        };
        mesh.SetInstanceTransform(0, Transform3D.Identity);
        return mesh;
    }

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
