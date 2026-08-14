using System;

namespace UnturnedGodot;

// Where core/ says something to whoever is hosting it.
//
// Most of core/ answers a question and returns; it has nothing to say. The exceptions are the one-time
// jobs a load runs — extracting audio, reading collider caches, fetching decal textures, handing the
// load's transient heap back — and they all have the same shape: the work is pure file and byte handling,
// which is why it lives here, but a piece of it can be skipped, and a skip that says nothing is a surface
// that plays silence with no trace of why.
//
// The game's own channel cannot be called from here. Log.Print reaches GD.Print, and most of these call
// sites additionally have to fall silent once teardown has started (AppShutdown.PrintUnlessQuitting).
// Both are engine facts, and neither belongs in a project that has to build and test without an engine.
//
// So this is a seam: core/ writes to it, the host installs one over it, and a test installs its own to
// read back what a run reported. Unset it is silent, which is the right default for the xUnit suite —
// a parser test that printed a hundred warnings would drown the run it belongs to.
public interface IHostLog
{
    // Progress. Ordinary, expected, and worth one line.
    void Print(string message);

    // Something was skipped, dropped or could not be written. The run continues; this is the trace.
    void Warn(string message);

    // The stderr channel, for what a wrapper script or a CI log has to be able to see without being
    // asked. Distinct from Warn because a run that greps its own output cares which stream a line is on.
    void Error(string message);
}

public static class HostLog
{
    // Discards everything, and is what core/ uses until somebody installs a real one.
    private sealed class SilentLog : IHostLog
    {
        internal static readonly SilentLog Instance = new();

        public void Print(string message) { }

        public void Warn(string message) { }

        public void Error(string message) { }
    }

    private static IHostLog Installed = SilentLog.Instance;

    // The installed sink. Null restores silence rather than throwing: a load-time job must not fail on the
    // way it reports, and a caller clearing the sink means "stop talking", not "crash".
    public static IHostLog Sink
    {
        get => Installed;
        set => Installed = value ?? SilentLog.Instance;
    }

    public static void Print(string message) => Installed.Print(message);

    public static void Warn(string message) => Installed.Warn(message);

    public static void Error(string message) => Installed.Error(message);
}
