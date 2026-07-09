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

    private sealed class Harness
    {
        public readonly LoopbackServerTransport ServerTransport = new();
        public readonly NetServer Server;
        public readonly List<NetClient> Clients = new();
        public double Now;

        public Harness(IServerTransport? transport = null)
        {
            Server = new NetServer(transport ?? ServerTransport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn);
        }

        public readonly List<LoopbackClientTransport> Transports = new();

        public NetClient Join(string name)
        {
            LoopbackClientTransport transport = ServerTransport.CreateClient();
            Transports.Add(transport);
            var client = new NetClient(transport, name);
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

    [Fact]
    public void TwoPlayers_SeeEachOtherJoin()
    {
        var h = new Harness();
        NetClient a = h.Join("A");
        h.Pump();
        Assert.True(a.Joined);
        Assert.Empty(a.Remotes);

        NetClient b = h.Join("B");
        h.Pump();

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
            new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn);

        var host = new NetClient(hostSide.CreateClient(), "Host");
        var friend = new NetClient(lanSide.CreateClient(), "Friend");

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
