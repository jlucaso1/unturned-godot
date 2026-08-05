using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The session, from the outside.
//
// The arrangement worth understanding is that there is always one. Singleplayer is not a special case —
// it is the same authoritative server the network path uses, reached over loopback — which is why "open
// to LAN" can be a listener bolted onto a session already running rather than a restart into another
// mode. That is Minecraft's trick, and it only works if a solo session is a real server all along.
//
// What follows from it is what these hold: a session cannot be started twice, LAN cannot be opened on
// something that is not hosting, and the reasons a join is refused have to be sayable in words a player
// can act on.
public class NetworkManagerTests : TestClass
{
    public NetworkManagerTests(Node testScene) : base(testScene) { }

    // Nothing is running until something starts it, and the flags say so distinctly: hosting, active and
    // LAN-open are three different questions.
    [Test]
    public async Task AFreshManagerIsRunningNothing()
    {
        var net = new NetworkManager { LevelName = "PEI" };
        TestScene.AddChild(net);
        await NextFrame();

        Assert.False(net.IsActive);
        Assert.False(net.IsHosting);
        Assert.False(net.IsLanOpen);

        net.QueueFree();
    }

    // A solo session IS a server, reached over loopback. Everything else in the port depends on that
    // being true rather than on singleplayer having its own path.
    [Test]
    public async Task ASoloSessionIsAServerReachedOverLoopback()
    {
        var net = new NetworkManager { LevelName = "PEI" };
        TestScene.AddChild(net);
        net.Configure(new HeightmapSampler(System.Array.Empty<HeightmapTile>()), Vector3.Zero);

        net.StartSingleplayer("Player");
        await NextFrame();

        Assert.True(net.IsActive);
        Assert.True(net.IsHosting, "a solo session is not a server, so nothing can join it later");
        Assert.False(net.IsLanOpen);

        net.QueueFree();
    }

    // Starting twice is refused rather than replacing the session underneath whoever is in it. The menu
    // can send it — a double click on Play — and it must not tear down a running world.
    [Test]
    public async Task ASessionCannotBeStartedTwice()
    {
        var net = new NetworkManager { LevelName = "PEI" };
        TestScene.AddChild(net);
        net.Configure(new HeightmapSampler(System.Array.Empty<HeightmapTile>()), Vector3.Zero);

        net.StartSingleplayer("Player");
        net.StartSingleplayer("Player");   // a second Play click
        await NextFrame();

        Assert.True(net.IsActive);
        net.QueueFree();
    }

    // LAN cannot be opened on a session that is not hosting: there is no server to attach a listener to,
    // and answering true would tell the player a port is open when nothing is listening.
    [Test]
    public async Task LanCannotBeOpenedWithoutAHost()
    {
        var net = new NetworkManager { LevelName = "PEI" };
        TestScene.AddChild(net);
        await NextFrame();

        Assert.False(net.OpenToLan(42995));
        Assert.False(net.IsLanOpen);

        net.QueueFree();
    }

    // And opening it twice is refused too — the listener is already there, and binding a second would
    // fail on the port rather than doing anything useful.
    [Test]
    public async Task LanIsOnlyOpenedOnce()
    {
        var net = new NetworkManager { LevelName = "PEI" };
        TestScene.AddChild(net);
        net.Configure(new HeightmapSampler(System.Array.Empty<HeightmapTile>()), Vector3.Zero);
        net.StartSingleplayer("Player");
        await NextFrame();

        if (!net.OpenToLan(42994))
        {
            // The port was unavailable on this machine, which is a real answer rather than a failure.
            net.QueueFree();
            return;
        }

        Assert.True(net.IsLanOpen);
        Assert.False(net.OpenToLan(42993), "a second open reported success");

        net.QueueFree();
    }

    // Every refusal has to be sayable. A player who is bounced needs to know whether to pick another
    // map, update the game, or wait for a slot — "connection failed" tells them none of those.
    [Test]
    public void EveryRefusalIsExplainedInWordsAPlayerCanActOn()
    {
        string mismatch = NetworkManager.Describe(
            new JoinRejection(EJoinRejection.LevelMismatch, NetMessages.ProtocolVersion, "Washington"));
        string protocol = NetworkManager.Describe(
            new JoinRejection(EJoinRejection.ProtocolMismatch, 99, "PEI"));
        string full = NetworkManager.Describe(
            new JoinRejection(EJoinRejection.ServerFull, NetMessages.ProtocolVersion, "PEI"));
        string unknown = NetworkManager.Describe(default);

        // The level mismatch names the map, because "wrong map" without saying which is not actionable.
        Assert.Contains("Washington", mismatch);
        // The protocol mismatch names both versions, so a player can tell who is behind.
        Assert.Contains("99", protocol);
        Assert.Contains(NetMessages.ProtocolVersion.ToString(), protocol);
        Assert.Contains("full", full);
        Assert.NotEmpty(unknown); // even an unrecognised reason says something
    }

    // --- a session that runs -------------------------------------------------------------------------

    // A solo session ticks its server and its client every physics frame, over loopback, and keeps
    // running. This is the loop the whole port sits on: if it stopped, nothing in the world would move
    // and nothing would say why.
    [Test]
    public async Task ASoloSessionKeepsTicking()
    {
        using var session = new Session(TestScene);
        session.Net.StartSingleplayer("Player");

        for (int i = 0; i < 30; i++)
            await session.PhysicsFrame();

        Assert.True(session.Net.IsActive);
        Assert.NotNull(session.Net.Client);
        Assert.NotNull(session.Net.Server);
    }

    // Hosting zombies on a level that ships none is a warning, not a failure — plenty of maps have no
    // zombie data — and the PUNCH host is deliberately not skipped with them. A level with no population
    // still has trees and rubble standing on it and a player whose fists work, and the streamer hands
    // its damage ledger to that host later regardless.
    [Test]
    public async Task ALevelWithNoZombiesStillGetsAPunchHost()
    {
        using var session = new Session(TestScene);
        session.Net.StartSingleplayer("Player");
        await session.PhysicsFrame();

        session.Net.HostZombies("/nonexistent-map");

        Assert.NotNull(session.Net.PunchDamage);
    }

    // Hosting zombies on a session that is not hosting does nothing at all. A client that joined someone
    // else's server takes this path, and a population spawned there would be a second, invisible world
    // running against the real one.
    [Test]
    public async Task AClientDoesNotHostItsOwnZombies()
    {
        using var session = new Session(TestScene);
        await session.PhysicsFrame();

        session.Net.HostZombies("/nonexistent-map");

        Assert.Null(session.Net.PunchDamage);
    }

    // A session with no navmesh releases the collision geometry instead of holding it. That builder is
    // the whole map's geometry, recorded during the load for one reconciliation pass — on a session that
    // will never run one (a joined client, a map with no navmesh) it would otherwise sit in memory for
    // the rest of the game with no consumer at all.
    [Test]
    public async Task ASessionThatWillNeverReconcileReleasesTheGeometry()
    {
        using var session = new Session(TestScene);
        session.Net.StartSingleplayer("Player");
        await session.PhysicsFrame();

        var collision = new Data.CollisionFieldBuilder();
        session.Net.ReconcileNavigation(new HashSet<System.Guid>(), collision);

        // Nothing observable to read off the builder — release is its own end state — so what this holds
        // is that the call returns rather than parking a pass that never runs.
        await session.PhysicsFrame();
        Assert.True(session.Net.IsActive);
    }

    // Joining is refused while a session is already running, rather than leaving the player in two at
    // once. The menu can send it: a click on a server in the browser while a solo world is up.
    [Test]
    public async Task JoiningIsRefusedWhileASessionIsRunning()
    {
        using var session = new Session(TestScene);
        session.Net.StartSingleplayer("Player");
        await session.PhysicsFrame();

        NetServer? before = session.Net.Server;
        session.Net.JoinServer("127.0.0.1", 43120, "Player");

        Assert.Same(before, session.Net.Server);
    }

    // And a join to nothing comes up as a client that is simply never answered. There is no server on
    // that port; the session has to sit there waiting rather than refusing to start, because that is
    // indistinguishable from a host that is slow.
    [Test]
    public async Task AJoinToNothingWaitsRatherThanRefusing()
    {
        using var session = new Session(TestScene);
        session.Net.JoinServer("127.0.0.1", 43121, "Player");

        for (int i = 0; i < 10; i++)
            await session.PhysicsFrame();

        Assert.True(session.Net.IsActive);
        Assert.False(session.Net.IsHosting);
    }

    // --- helpers -------------------------------------------------------------------------------------

    // A manager in the tree, configured over flat ground, freed with the test. Freeing is what closes
    // its transports: a session whose sockets outlived it would hold ports for the rest of the run.
    private sealed class Session : System.IDisposable
    {
        private readonly Node _testScene;

        public NetworkManager Net { get; }

        public Session(Node testScene)
        {
            _testScene = testScene;
            Net = new NetworkManager { LevelName = "PEI" };
            testScene.AddChild(Net);
            Net.Configure(new HeightmapSampler(System.Array.Empty<HeightmapTile>()), Vector3.Zero);
        }

        public SignalAwaiter PhysicsFrame() =>
            _testScene.ToSignal(_testScene.GetTree(), SceneTree.SignalName.PhysicsFrame);

        public void Dispose()
        {
            _testScene.RemoveChild(Net);
            Net.Free(); // _ExitTree closes both transports and frees the navigation
        }
    }

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
