namespace UnturnedGodot.Tests.Helpers;

// Trait names, in one place so a filter expression and the attribute it selects cannot drift apart.
//
// The suite is hermetic and deterministic with two exceptions, and those two are the reason this
// exists. UdpEndToEndTests and ConnectionCapTests bind REAL localhost UDP ports and sleep for real
// milliseconds waiting on the kernel to schedule datagrams, and they find their port through a
// bind-port-zero probe that closes the socket before the server reopens it — a TOCTOU window in which
// anything else on the runner may take it. They are green and fast today; on a loaded runner they are
// the first things here that will flake.
//
// What the trait buys is triage. Without it, one flaky UDP test means re-running 2 600 tests and
// hoping, and the same run is the only evidence anyone has about which half was at fault. With it:
//
//   dotnet test ... --filter "Category!=RealSockets"     # the deterministic suite, on its own
//   dotnet test ... --filter "Category=RealSockets"      # just the two that touch the network
//
// so a failure can be re-run in isolation in under a second, and a green hermetic run next to a red
// sockets run is a diagnosis rather than a mystery.
public static class TestCategories
{
    public const string Name = "Category";

    // Binds real ports and depends on localhost datagram scheduling.
    public const string RealSockets = "RealSockets";

    // Long, deterministic, in-memory: the netcode soak. Tagged so it can be run alone when the
    // netcode is what changed, not because it is unreliable — it is neither slow nor flaky.
    public const string Soak = "Soak";
}
