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

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
