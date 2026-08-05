using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.DevConsole;

namespace UnturnedGodot;

// The drop-down console: F1 (or the backtick, as every game that has one of these uses) opens a pane over
// the top of the screen carrying the log and a prompt.
//
// Why it exists is measurement. This project's order is parity first, then performance, and the F3 HUD
// already keeps the numbers in view — but a number is only worth something next to a second number taken
// under one deliberate difference. Restarting the game with a different environment variable gives you
// that difference across two loads, two shader caches and two thermal states; typing `objects.trees.enabled 0`
// gives it to you between two frames of the same session. Everything the prompt can reach is listed in
// RenderConsole.
//
// The scrollback is the game's own log, live: the same lines the terminal gets, arriving as they are
// written, so what a load did and what you then asked it to do read as one transcript. That is also why
// the pane is bound to a key rather than a debug build — a player who hits a bad frame can open it, see
// the last few hundred lines, and read them back without a terminal at all.
public partial class ConsoleOverlay : CanvasLayer
{
    // How much scrollback is kept. Beyond this the oldest paragraphs are dropped: a session that runs for
    // hours writes tens of thousands of log lines, and a RichTextLabel keeps every one of them laid out.
    private const int MaximumParagraphs = 1000;
    // Log lines moved onto the screen per frame. The queue is drained, not sampled — this only stops a
    // burst (a cold load reports thousands of extraction lines) from being paid for in one frame.
    private const int LinesPerFrame = 200;

    [Export] public Key ToggleKey = Key.F1;

    private Control _root = null!;
    private RichTextLabel _output = null!;
    private LineEdit _prompt = null!;
    private readonly ConsoleHistory _history = new();
    // Log lines are written from worker threads — the bundle decoders, the extraction pass, the foliage
    // streamer — and a Godot node may only be touched from the main one. The handler does nothing but
    // enqueue; _Process is where they become text.
    private readonly ConcurrentQueue<(LogSeverity Severity, string Text)> _pending = new();
    private Input.MouseModeEnum _mouseBeforeOpen = Input.MouseModeEnum.Captured;

    public bool IsOpen => _root.Visible;

    // The two controls the runtime suite drives and reads back. Internal rather than public: they are the
    // pane's own widgets, and nothing outside this assembly has any business holding them.
    internal RichTextLabel Output => _output;

    internal LineEdit Prompt => _prompt;

    // The registry outlives this node, and has to: the overlay is built with the world and freed with
    // it, while the whole point of a toggle is to carry it from one map to the next and compare. It
    // lives at process scope in RenderConsole, and each new overlay adopts it (see ReapplySettings).
    public static ConsoleRegistry Console => RenderConsole.Console;

    public override void _Ready()
    {
        Layer = 200; // above the pause menu (20) and the performance HUD (128)
        // Every binding resolves the world through whichever node is the host, so a command typed now
        // reaches the world that exists now rather than one that was freed two maps ago.
        RenderConsole.Host = this;
        BuildInterface();

        foreach (string line in Log.Tail())
            Append(ConsoleSeverity.Reply, line);
        Log.LineWritten += Remember;

        // UG_CONSOLE="foliage.enabled 0; sun.shadows.enabled 0" configures a session before anyone can
        // type: it is how a screenshot or a bug report measures the same difference a person would have
        // made by hand, without one being there to make it. Taken rather than read, because a run with
        // no overlay — the GPU benchmark tier — applies the same line from its own world build.
        if (RenderConsole.TryTakeStartupLine(out string startup))
            Submit(startup, echo: true);

        // The same screenshot aid the pause menu carries (SHOW_PAUSE_MENU): a capture run never presses a
        // key, so this is the only way to photograph the pane over a real world.
        if (EnvFlag.IsOn(OS.GetEnvironment("SHOW_CONSOLE"), whenUnset: false))
            Open();
    }

    public override void _ExitTree()
    {
        Log.LineWritten -= Remember;
        if (ReferenceEquals(RenderConsole.Host, this))
            RenderConsole.Host = null;
    }

    // Pushes every non-default value at the world that exists now. The overlay is created with the
    // environment, which is well before the object streamer has built anything, so the caller calls this
    // again once the world is finished — otherwise a session started with UG_CONSOLE would have been
    // configured against a world that had no objects in it yet.
    public void ReapplySettings()
    {
        foreach (ConsoleLine line in Console.Reapply())
            Append(line.Severity, line.Text);
    }

    public void ClearScrollback() => _output.Clear();

    // What is selected in the scrollback, onto the clipboard. False means there was nothing selected,
    // which is the caller's cue to leave the keystroke to the prompt.
    internal bool CopySelection()
    {
        string selected = _output.GetSelectedText();
        return selected.Length > 0 && PutOnClipboard(selected);
    }

    // The whole scrollback as plain text — no BBCode, no colours — which is what a bug report wants and
    // what dragging a selection across a few hundred scrolling lines cannot practically produce.
    internal bool CopyScrollback() => PutOnClipboard(_output.GetParsedText());

    // A multi-line paste, taken from the LineEdit because a LineEdit cannot hold one: it drops the
    // newlines, and a recipe copied a command per line arrived as one welded-together word. Flattened
    // onto the console's own `;` grammar it runs as the four commands it reads as, in order.
    //
    // False leaves the keystroke alone, which is what a one-line clipboard wants: the LineEdit's own
    // paste already handles the selection, the caret and the undo stack correctly, and there is nothing
    // to fix about it.
    internal bool PasteBlock()
    {
        if (!DisplayServer.HasFeature(DisplayServer.Feature.Clipboard))
            return false;
        string clipboard = DisplayServer.ClipboardGet();
        if (!clipboard.Contains('\n', StringComparison.Ordinal)
            && !clipboard.Contains('\r', StringComparison.Ordinal))
            return false;

        string flattened = ConsoleCommandLine.Flatten(clipboard);
        if (flattened.Length == 0)
            return false;
        _prompt.InsertTextAtCaret(flattened);
        return true;
    }

    // Godot only warns when the display server has no clipboard (headless, which is where the runtime
    // suite runs), so ask first and let the caller say so rather than appearing to have worked.
    private static bool PutOnClipboard(string text)
    {
        if (text.Length == 0 || !DisplayServer.HasFeature(DisplayServer.Feature.Clipboard))
            return false;
        DisplayServer.ClipboardSet(text);
        return true;
    }

    // Runs a line as though it had been typed. Public for the runtime suite and for UG_CONSOLE.
    public void Submit(string line, bool echo)
    {
        if (echo)
            Append(ConsoleSeverity.Input, "> " + line);
        foreach (ConsoleLine reply in Console.Execute(line))
            Append(reply.Severity, reply.Text);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        // Quoteleft is the key every other game puts this on, and it is where a muscle memory will go
        // first; F1 is the one that works on the layouts where the backtick is a dead key.
        if (key.Keycode == ToggleKey || key.Keycode == Key.Quoteleft)
        {
            if (IsOpen)
                Close();
            else
                Open();
            GetViewport().SetInputAsHandled();
            return;
        }
        if (!IsOpen)
            return;

        switch (key.Keycode)
        {
            case Key.Escape:
                // Consumed here, ahead of the pause menu: Escape closes what is on top, and while the
                // console is open that is the console. Open() puts this node last among its siblings,
                // which is what makes "ahead of" true — _input is delivered in reverse tree order.
                Close();
                break;
            case Key.Up:
                Recall(_history.Previous());
                break;
            case Key.Down:
                Recall(_history.Next());
                break;
            case Key.Tab:
                Complete();
                break;
            case Key.C when key.IsCommandOrControlPressed():
                // RichTextLabel has a copy shortcut of its own, and it never fires here: it only runs
                // when the label is focused, and the label is deliberately never focused (FocusMode is
                // None below, so typing always reaches the prompt). Selecting the log and pressing the
                // key that copies it everywhere else did nothing at all — the event went to the LineEdit,
                // which copied its own empty selection. So the key is taken here instead.
                if (!CopySelection())
                    return; // nothing selected in the log: the prompt's own copy is what was meant
                break;
            case Key.V when key.IsCommandOrControlPressed():
                if (!PasteBlock())
                    return; // one line, or no clipboard: the LineEdit's own paste is already right
                break;
            default:
                // Enter is deliberately absent: letting it through is what makes LineEdit emit
                // TextSubmitted, and typing has to keep working like typing.
                return;
        }
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        int moved = 0;
        while (moved < LinesPerFrame && _pending.TryDequeue(out (LogSeverity Severity, string Text) line))
        {
            Append(line.Severity switch
            {
                LogSeverity.Error => ConsoleSeverity.Failure,
                LogSeverity.Warning => ConsoleSeverity.Notice,
                _ => ConsoleSeverity.Reply,
            }, line.Text);
            moved++;
        }
    }

    private void Remember(LogSeverity severity, string text) => _pending.Enqueue((severity, text));

    private void Open()
    {
        // Last among its siblings, so this node's _input runs before the pause menu's and the player's:
        // the engine delivers input to children in reverse order, and the pause menu is added to the tree
        // after the console is (the character spawns later than the environment does).
        if (GetParent() is { } parent && parent.GetChildCount() > 0)
            parent.MoveChild(this, parent.GetChildCount() - 1);

        _mouseBeforeOpen = Input.MouseMode;
        // Releasing the cursor is also what stops the player walking while you type: PlayerController
        // treats "the mouse is not captured" as nobody holding the controls, exactly as it does for the
        // pause menu. FreeCamera has its own guard — a focused text field owns the keyboard.
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _root.Visible = true;
        _prompt.GrabFocus();
    }

    private void Close()
    {
        _root.Visible = false;
        _prompt.ReleaseFocus();
        // Unless a pause menu is up, in which case the cursor belongs to it, not to us: the console may
        // have been opened over an already-open menu, and re-capturing here would leave its buttons
        // unclickable.
        if (IsInsideTree() && GetTree().GetFirstNodeInGroup(SceneGroups.PauseMenu) is PauseMenu { IsOpen: true })
            return;
        Input.MouseMode = _mouseBeforeOpen;
    }

    private void Recall(string? line)
    {
        if (line == null)
            return; // nothing further back (or forward): keep what is in the box
        _prompt.Text = line;
        _prompt.CaretColumn = line.Length;
    }

    // Completes to the longest head every candidate shares, then lists them — the behaviour every shell
    // has, and the reason the names can be dotted without anybody having to memorise them.
    private void Complete()
    {
        string typed = _prompt.Text;
        // Only the first word is a name; a value is not something this can complete.
        if (typed.Contains(' ', StringComparison.Ordinal))
            return;

        IReadOnlyList<string> matches = Console.Names(typed);
        if (matches.Count == 0)
        {
            Append(ConsoleSeverity.Notice, $"Nothing starts with '{typed}'.");
            return;
        }

        string completed = ConsoleRegistry.CommonPrefix(matches);
        if (completed.Length > typed.Length)
        {
            _prompt.Text = completed;
            _prompt.CaretColumn = completed.Length;
        }
        if (matches.Count > 1)
            Append(ConsoleSeverity.Reply, string.Join("   ", matches));
    }

    private void Run(string line)
    {
        _prompt.Clear();
        if (line.Trim().Length == 0)
            return;
        _history.Add(line);
        Submit(line, echo: true);
    }

    private void Append(ConsoleSeverity severity, string text)
    {
        // The log is full of bracketed tags — "[stream] scene built" — and this label reads BBCode, so
        // every literal bracket is escaped and the only markup on screen is the colour put there here.
        _output.AppendText($"[color={Tint(severity)}]{text.Replace("[", "[lb]", StringComparison.Ordinal)}[/color]\n");
        while (_output.GetParagraphCount() > MaximumParagraphs)
            _output.RemoveParagraph(0);
    }

    private static string Tint(ConsoleSeverity severity) => severity switch
    {
        ConsoleSeverity.Input => "#7fd4ff",
        ConsoleSeverity.Notice => "#ffcc66",
        ConsoleSeverity.Failure => "#ff7b6b",
        _ => "#d6d6d6",
    };

    private void BuildInterface()
    {
        _root = new Control
        {
            Visible = false,
            AnchorRight = 1,
            AnchorBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Ignore, // only the pane itself takes clicks
        };
        AddChild(_root);

        var pane = new PanelContainer
        {
            AnchorRight = 1,
            AnchorBottom = 0.45f, // the top of the screen, like every console this is modelled on
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        var style = new StyleBoxFlat { BgColor = new Color(0.04f, 0.05f, 0.06f, 0.92f) };
        style.SetContentMarginAll(10);
        pane.AddThemeStyleboxOverride("panel", style);
        _root.AddChild(pane);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 6);
        pane.AddChild(column);

        _output = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollFollowing = true,
            SelectionEnabled = true,
            FocusMode = Control.FocusModeEnum.None, // the prompt keeps the keyboard
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        column.AddChild(_output);

        _prompt = new LineEdit
        {
            PlaceholderText = "help · list · find shadow · foliage.enabled 0",
            CaretBlink = true,
        };
        _prompt.TextSubmitted += Run;
        column.AddChild(_prompt);
    }
}
