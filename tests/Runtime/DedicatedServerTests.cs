using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The headless authority: the same server a solo session runs over loopback, with a UDP socket in front
// of it and nobody at the keyboard.
//
// The property worth holding is that it STARTS ANYWAY. A dedicated server is unattended by definition —
// it is brought up by a script, on a box nobody is watching — so a map that is missing its terrain, its
// navmesh or its zombie tables must produce a server that listens and reports an empty world, rather
// than a process that exited before anyone could ask it why. The alternative is a host that dies at 3am
// and takes the reason with it.
//
// It also builds its world BEFORE it is in the tree, which is why every physics query it owns resolves
// its World3D lazily. A server that bound eagerly would attach every zombie to nothing.
public class DedicatedServerTests : TestClass
{
    public DedicatedServerTests(Node testScene) : base(testScene) { }

    // A map that is not installed still yields a listening server with an empty world. This is the
    // unattended failure: a typo in a start script, or a workshop map that never downloaded.
    [Test]
    public async Task AMapThatIsNotThereStillYieldsAListeningServer()
    {
        DedicatedServer server = DedicatedServer.Create("/nonexistent-unturned", "NoSuchMap", "NoSuchMap",
            Vector3.Zero, FreePort());
        TestScene.AddChild(server);
        await NextFrame();

        // Punches resolve against the world whether or not the level has zombies — the rubble and the
        // resources are there either way — so this host exists on every level that loads at all.
        Assert.NotNull(server.PunchDamage);

        Free(server);
    }

    // An empty install path takes the same road. The catalog resolves nothing, and the server comes up
    // over a world with no tiles rather than refusing to start.
    [Test]
    public async Task AnEmptyInstallPathTakesTheSameRoad()
    {
        DedicatedServer server = DedicatedServer.Create("", "PEI", "PEI", Vector3.Zero, FreePort());
        TestScene.AddChild(server);
        await NextFrame();

        Assert.NotNull(server.PunchDamage);

        Free(server);
    }

    // Ticking a server nobody has joined is quiet, and it is what an idle host does forever. The tick
    // also starts navigation reconciliation, which on a world with no navmesh has to decline rather than
    // wait for one.
    [Test]
    public async Task AnIdleServerTicksQuietly()
    {
        DedicatedServer server = DedicatedServer.Create("/nonexistent-unturned", "NoSuchMap", "NoSuchMap",
            Vector3.Zero, FreePort());
        TestScene.AddChild(server);

        for (int i = 0; i < 10; i++)
            await NextPhysicsFrame();

        Free(server);
    }

    // Leaving closes the socket. A server whose transport outlived it would hold the port, and the next
    // start — a restart script's whole job — would fail to bind for a process that is already gone.
    [Test]
    public async Task LeavingClosesTheSocket()
    {
        ushort port = FreePort();
        DedicatedServer first = DedicatedServer.Create("/nonexistent-unturned", "NoSuchMap", "NoSuchMap",
            Vector3.Zero, port);
        TestScene.AddChild(first);
        await NextFrame();
        Free(first);
        await NextFrame();

        // The restart: same port, straight away.
        DedicatedServer second = DedicatedServer.Create("/nonexistent-unturned", "NoSuchMap", "NoSuchMap",
            Vector3.Zero, port);
        TestScene.AddChild(second);
        await NextFrame();

        Assert.NotNull(second.PunchDamage);
        Free(second);
    }

    // --- helpers -------------------------------------------------------------------------------------

    // A port the OS says is free, rather than one this file hopes is. Two runtime-test processes on one
    // machine, or any unrelated listener, would otherwise fail these tests before their assertions —
    // DedicatedServer.Create binds immediately, so an occupied port is a failure at construction.
    private static ushort FreePort()
    {
        using var probe = new System.Net.Sockets.UdpClient(0, System.Net.Sockets.AddressFamily.InterNetwork);
        return (ushort)((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private void Free(DedicatedServer server)
    {
        TestScene.RemoveChild(server);
        server.Free(); // _ExitTree closes the transport and releases the navigation state
    }

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);

    private SignalAwaiter NextPhysicsFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);
}
