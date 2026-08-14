using System;
using Godot;

namespace UnturnedGodot;

// How a session says "this cannot continue" — which depends entirely on whether anyone is watching.
//
// A session with a person at it puts the reason on the loading screen and offers the way back to the map
// browser. A session without one has neither a display to draw that on nor an input to press its button
// with, so the same screen would leave a benchmark waiting forever on an overlay nothing can dismiss; it
// ends the process with a nonzero status instead, which is what the wrapper scripts read.
//
// That decision was made three separate times in Main — a failed world build, a failed join, and a join
// the server refused — each with its own copy of the same four-line comment. Three copies of one rule is
// three chances for the next failure path to get it wrong, and getting it wrong in the headless direction
// hangs CI rather than failing it.
//
// Which reporter is in play is decided once, at boot, from the same flag the rest of the mode reads.
public interface IFailureReporter
{
    // `screen` is the loading screen already on display, or null to raise a fresh one over a session that
    // is already running. `recover` is the way back; a reporter with nobody to press it never calls it.
    void Fatal(LoadingScreen? screen, string message, Action? recover, string? heading = null);
}

// No display, no input: report on stderr and end the process. Used by every automation mode.
public sealed class HeadlessFailureReporter : IFailureReporter
{
    private readonly SceneTree _tree;

    public HeadlessFailureReporter(SceneTree tree) => _tree = tree;

    // Every argument is ignored, and that is the point: there is no screen to put the message on and
    // nobody to press the button. The message is already on stderr — each call site logs it before
    // asking, because the log line carries more than the screen ever shows (the whole exception, not
    // its type and message).
    public void Fatal(LoadingScreen? screen, string message, Action? recover, string? heading = null)
    {
        // QuitNow rather than RequestQuit: a failure this early has no decode to drain, so the grace
        // period would be a wait for nothing — but it still leaves through AppShutdown, because a native
        // SceneTree.Quit never returns to managed code and a coverage run would record nothing at all.
        AppShutdown.QuitNow(_tree, 1);
    }
}

// Somebody is watching: say what happened and offer the way back.
public sealed class InteractiveFailureReporter : IFailureReporter
{
    private readonly Node _host;

    public InteractiveFailureReporter(Node host) => _host = host;

    public void Fatal(LoadingScreen? screen, string message, Action? recover, string? heading = null)
    {
        if (screen == null)
        {
            // Nothing is on screen, so this arrived mid-session. The player was walking when it did,
            // which means the cursor is captured and the button under it is unclickable — release it,
            // exactly as the menu and the pause screen do.
            Input.MouseMode = Input.MouseModeEnum.Visible;
            screen = new LoadingScreen { Name = "Failure" };
            _host.AddChild(screen);
        }

        if (heading == null)
            screen.Fail(message, recover ?? (() => { }));
        else
            screen.Fail(message, recover ?? (() => { }), heading);
    }
}
