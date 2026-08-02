using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// The standalone multiplayer scenario: a server and N clients wired over the in-memory loopback
// transport, pumped deterministically — connect, see each other join, watch each other MOVE.
public class EndToEndTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 10f;
        return true;
    }

    private static readonly Vector3 Spawn = new(0, 10f, 0);

    // The level the server runs; clients build it too unless a test says otherwise.
    private const string Level = "PEI";

    private sealed class Harness
    {
        public readonly LoopbackServerTransport ServerTransport = new();
        public readonly NetServer Server;
        public readonly List<NetClient> Clients = new();
        // Starts at a big absolute clock on purpose: a reliable frame stamped "0" by a constructor-time
        // send would instantly exceed ReliableChannel.GiveUpAfter (the listen-server admit/drop bug).
        public double Now = 5000.0;

        public Harness(IServerTransport? transport = null)
        {
            Server = new NetServer(transport ?? ServerTransport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn, Level);
        }

        public readonly List<LoopbackClientTransport> Transports = new();

        public NetClient Join(string name, string level = Level)
        {
            LoopbackClientTransport transport = ServerTransport.CreateClient();
            Transports.Add(transport);
            var client = new NetClient(transport, name, level);
            Clients.Add(client);
            return client;
        }

        // One 12.5 Hz round: server consumes and ticks, then clients consume.
        public void Pump(int rounds = 1)
        {
            for (int i = 0; i < rounds; i++)
            {
                Now += ServerSimulation.TickRate;
                Server.Update(Now);
                foreach (NetClient client in Clients)
                    client.Update(Now);
            }
        }
    }

    // The bug end to end: the host opened PEI, a friend's client had California 2 selected, and the
    // friend joined anyway. Now that client is refused with a reason and never appears in the session,
    // while a client on the right map plays on beside it.
    [Fact]
    public void APlayerWhoBuiltAnotherMap_NeverEntersTheSession()
    {
        var h = new Harness();
        NetClient right = h.Join("Right");
        NetClient wrong = h.Join("Wrong", "California 2");
        h.Pump(4);

        Assert.True(right.Joined);
        Assert.False(wrong.Joined);
        Assert.Equal(1, h.Server.PlayerCount);
        Assert.Empty(right.Remotes);  // nobody from another world showed up in this one
        Assert.Equal(EJoinRejection.LevelMismatch, wrong.Rejection!.Value.Reason);
        Assert.Equal(Level, wrong.Rejection!.Value.ServerLevel);
    }

    [Fact]
    public void TwoPlayers_SeeEachOtherJoin()
    {
        var h = new Harness();
        NetClient a = h.Join("A");
        h.Pump(2); // round 1: A's first Update sends the Hello; round 2: the server admits and replies
        Assert.True(a.Joined);
        Assert.Empty(a.Remotes);

        NetClient b = h.Join("B");
        h.Pump(2); // round 1: B's first Update sends the Hello; round 2: the server admits and replies

        Assert.True(b.Joined);
        Assert.NotEqual(a.PlayerId, b.PlayerId);
        Assert.Single(a.Remotes);            // A was told about B
        Assert.Single(b.Remotes);            // B's Welcome listed A
        Assert.Equal("B", a.Remotes[b.PlayerId].Name);
        Assert.Equal("A", b.Remotes[a.PlayerId].Name);
    }

    [Fact]
    public void OnePlayerSeesTheOtherMoving()
    {
        var h = new Harness();
        NetClient a = h.Join("A");
        NetClient b = h.Join("B");
        h.Pump(3); // handshakes + everyone lands on the ground

        // A runs forward (facing yaw 0 => -Z) for 2 seconds of ticks.
        for (int i = 0; i < 25; i++)
        {
            a.SendInput(new InputCommand((uint)i, 0, -1, false, false, NetAngles.QuantizeYaw(0f), 90));
            h.Pump();
        }
        h.Pump(3); // let the last states flush + interpolation delay elapse

        RemotePlayer aSeenByB = b.Remotes[a.PlayerId];
        PoseSnapshot pose = aSeenByB.Sample(h.Now);

        // B must see A well down the -Z axis: at least half the true distance even with the
        // 100 ms interpolation delay, and no sideways drift.
        float expected = PlayerConfig.SpeedStand * 25 * ServerSimulation.TickRate;
        Assert.True(pose.Position.Z < -expected * 0.5f, $"B sees A at Z={pose.Position.Z}, ran {expected}");
        Assert.Equal(0f, pose.Position.X, 1);

        // And the view is continuous: sampling a bit later moves further, never teleports.
        PoseSnapshot later = aSeenByB.Sample(h.Now + 0.05);
        Assert.True(later.Position.Z <= pose.Position.Z + 0.001f, "interpolation went backwards");
        Assert.True(pose.Position.Z - later.Position.Z < 1f, "interpolation jumped");
    }

    [Fact]
    public void FourPlayers_AllTrackAllOthers()
    {
        var h = new Harness();
        var clients = new List<NetClient>();
        foreach (string name in new[] { "A", "B", "C", "D" })
        {
            clients.Add(h.Join(name));
            h.Pump();
        }
        h.Pump(2);

        foreach (NetClient c in clients)
        {
            Assert.True(c.Joined);
            Assert.Equal(3, c.Remotes.Count);
        }
    }

    [Fact]
    public void Disconnect_RemovesThePlayerEverywhere()
    {
        var h = new Harness();
        NetClient a = h.Join("A");
        h.Join("B");
        h.Pump(2);
        Assert.Single(a.Remotes);

        h.Transports[1].Close();
        h.Pump(2);

        Assert.Empty(a.Remotes);
        Assert.Equal(1, h.Server.PlayerCount);
    }

    [Fact]
    public void ListenServer_HostOverLoopback_PeerOverSecondTransport()
    {
        // The "open to LAN" shape: one NetServer fed by a composite of two transports — the host's
        // loopback and a second transport standing in for the LAN/UDP listener.
        var hostSide = new LoopbackServerTransport();
        var lanSide = new LoopbackServerTransport();
        var server = new NetServer(new CompositeServerTransport(hostSide, lanSide),
            new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn, Level);

        var host = new NetClient(hostSide.CreateClient(), "Host", Level);
        var friend = new NetClient(lanSide.CreateClient(), "Friend", Level);

        double now = 0;
        for (int i = 0; i < 30; i++)
        {
            now += ServerSimulation.TickRate;
            if (i > 3) // the friend walks toward -Z while the host stands still
                friend.SendInput(new InputCommand((uint)i, 0, -1, false, false, 0, 90));
            server.Update(now);
            host.Update(now);
            friend.Update(now);
        }

        Assert.True(host.Joined);
        Assert.True(friend.Joined);
        Assert.Equal("Friend", host.Remotes[friend.PlayerId].Name);
        Assert.Equal("Host", friend.Remotes[host.PlayerId].Name);

        // The host watches the friend actually moving across transports.
        PoseSnapshot seen = host.Remotes[friend.PlayerId].Sample(now);
        Assert.True(seen.Position.Z < -3f, $"host sees friend at Z={seen.Position.Z}");
    }

    [Fact]
    public void ConnectingBeforeTheServerListens_SelfHeals()
    {
        // The client starts pumping while nothing answers (server not up yet / lost handshake): the
        // Hello re-sends on its retry cadence and the join completes once the server appears.
        var h = new Harness();
        LoopbackClientTransport transport = h.ServerTransport.CreateClient();
        var late = new NetClient(transport, "Late", Level);
        h.Clients.Add(late);

        // Pump ONLY the client for a while (the server never drains its queue = effectively absent).
        for (int i = 0; i < 50; i++)
            late.Update(h.Now += ServerSimulation.TickRate);
        Assert.False(late.Joined);

        h.Pump(2); // the server comes alive and drains: the retried Hello admits the client
        Assert.True(late.Joined);
    }

    [Fact]
    public void ServerGoingSilent_ResetsTheSessionForRejoin()
    {
        var h = new Harness();
        NetClient a = h.Join("A");
        h.Pump(2);
        Assert.True(a.Joined);

        // No server updates at all for longer than the state timeout: the client resets itself.
        double t = h.Now;
        for (int i = 0; i < 200; i++)
            a.Update(t += ServerSimulation.TickRate);

        Assert.False(a.Joined);
        Assert.Empty(a.Remotes);

        h.Now = t;
        h.Pump(2); // and rejoins as soon as the server answers again
        Assert.True(a.Joined);
    }

    [Fact]
    public void OpenToLanAtRuntime_AttachesATransportToTheLiveServer()
    {
        // The always-on singleplayer shape: the server starts loopback-only, plays a while, then a second
        // transport is attached mid-session ("open to LAN") and a friend joins the SAME world.
        var hostSide = new LoopbackServerTransport();
        var composite = new CompositeServerTransport(hostSide);
        var server = new NetServer(composite,
            new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn, Level);
        var host = new NetClient(hostSide.CreateClient(), "Host", Level);

        double now = 5000;
        for (int i = 0; i < 5; i++)
        {
            now += ServerSimulation.TickRate;
            server.Update(now);
            host.Update(now);
        }
        Assert.True(host.Joined);

        var lanSide = new LoopbackServerTransport();
        composite.Add(lanSide); // the pause-menu button
        var friend = new NetClient(lanSide.CreateClient(), "Friend", Level);
        for (int i = 0; i < 5; i++)
        {
            now += ServerSimulation.TickRate;
            server.Update(now);
            host.Update(now);
            friend.Update(now);
        }

        Assert.True(friend.Joined);
        Assert.Equal("Host", friend.Remotes[host.PlayerId].Name);
        Assert.Equal("Friend", host.Remotes[friend.PlayerId].Name);
    }

    [Fact]
    public void ServerTick_AndUnhandledMessage_ExtensionSeams()
    {
        // Future replicated systems hook OnTick server-side and OnUnhandledMessage client-side; the
        // protocol enum is the only shared contract.
        var h = new Harness();
        NetClient a = h.Join("A");

        var ticks = new List<uint>();
        h.Server.OnTick = tick => ticks.Add(tick);

        byte[]? unknown = null;
        a.OnUnhandledMessage = payload => unknown = payload;

        h.Pump(3);
        Assert.True(ticks.Count >= 3);
        Assert.Equal((uint)ticks.Count, ticks[^1]); // consecutive fixed ticks

        // A message type from a future feature: today's client routes it to the hook untouched, and a
        // client with no hook subscribed simply drops it (forward compatibility, no crash).
        NetClient b = h.Join("B"); // no OnUnhandledMessage hook
        h.Pump(2);
        h.Server.Broadcast(new byte[] { 250, 1, 2, 3 }, ESendType.Reliable);
        h.Pump();
        Assert.NotNull(unknown);
        Assert.Equal(new byte[] { 250, 1, 2, 3 }, unknown);
        Assert.True(b.Joined);
    }

    [Fact]
    public void CompositeClose_ClosesAllTransports()
    {
        var t1 = new LoopbackServerTransport();
        var t2 = new LoopbackServerTransport();
        var composite = new CompositeServerTransport(t1, t2);
        t1.CreateClient();
        composite.Update(0);
        composite.Close();
        Assert.False(composite.TryReceive(out _)); // queues cleared
    }
}
