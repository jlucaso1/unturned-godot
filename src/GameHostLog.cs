using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace UnturnedGodot;

// Points core/'s log seam at the game's console.
//
// Both channels go through AppShutdown rather than straight to Log, because everything on the other end
// of this seam runs on a worker: the audio extraction, the collider cache read, the decal fetch. A line
// arriving during teardown reaches a subsystem that is already gone, and PushWarning reaches further into
// the engine than a print does. That guard is exactly what AppShutdown.PrintUnlessQuitting is for, so it
// is what the game installs here.
//
// Installed by a module initializer rather than from Main, because Main is not the only way into this
// assembly: the editor add-on builds a world from the dock, and the runtime suite runs the same
// extractions under GoDotTest. Whichever of them the engine loads this assembly for, the sink is in place
// before any of their code runs — which is what stops "the warning exists but nobody installed a sink"
// from being a thing that can happen.
internal sealed class GameHostLog : IHostLog
{
    // CA2255 says module initializers belong in application code rather than in a library. This IS the
    // application — the game — and the analyzer cannot tell, because Godot builds it as a .dll that the
    // engine loads. The rule's actual hazard is a library imposing startup work on whoever references it;
    // nothing references this assembly, and the work is one field assignment.
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The ModuleInitializer attribute should not be used in libraries",
        Justification = "This assembly is the game, not a library; Godot loads it as the entry point.")]
    internal static void Install() => HostLog.Sink = new GameHostLog();

    public void Print(string message) => AppShutdown.PrintUnlessQuitting(message);

    public void Warn(string message) => AppShutdown.WarnUnlessQuitting(message);

    // Ungated, unlike the other two. stderr is what a wrapper script reads to decide whether a headless
    // run worked, and a run that is on its way out is exactly when it most needs to say why.
    public void Error(string message) => Log.PrintErr(message);
}
