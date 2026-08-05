namespace UnturnedGodot.DevConsole;

// What a line of console output is: an answer, a warning, or a refusal. The overlay colours by this
// rather than by parsing the text, so a message that happens to start with "error" is not mistaken for
// one, and a headless caller can filter on it without a renderer at all.
public enum ConsoleSeverity
{
    // The command as it was typed, echoed back above its answer so the scrollback reads as a transcript.
    Input,
    Reply,
    // The command did what was asked, but not the way the caller probably expected — a value remembered
    // for a world that is not loaded yet, an effect the active renderer cannot apply.
    Notice,
    Failure,
}

public readonly record struct ConsoleLine(ConsoleSeverity Severity, string Text)
{
    public static ConsoleLine Input(string text) => new(ConsoleSeverity.Input, text);

    public static ConsoleLine Reply(string text) => new(ConsoleSeverity.Reply, text);

    public static ConsoleLine Notice(string text) => new(ConsoleSeverity.Notice, text);

    public static ConsoleLine Failure(string text) => new(ConsoleSeverity.Failure, text);
}
