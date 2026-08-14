using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// The netcode's instrument, and the first thing that makes any claim about its cost checkable.
//
// These tests exist because the counters they cover are the evidence every other change in this area is
// argued from: "the roster is the big message", "the state stream dominates at population", "this link
// is losing datagrams". A counter that silently stops counting turns all of those back into opinions,
// so the assertions here are about the accounting itself — direction, attribution by message type, the
// roll-up from a connection to its server, and what happens to bytes nobody can classify.
public class NetTrafficTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 10f;
        return true;
    }

    private static readonly Vector3 Spawn = new(0, 10f, 0);
    private const string Level = "PEI";

    [Fact]
    public void RecordSent_AttributesBytesToTheMessageTypeInTheFirstByte()
    {
        var traffic = new NetTraffic();
        traffic.RecordSent(NetMessages.WriteInput(Input(1)), 40);
        traffic.RecordSent(NetMessages.WriteInput(Input(2)), 40);
        traffic.RecordSent(NetMessages.WriteStateUpdate(7, new List<PlayerSnapshotState>()), 6);

        Assert.Equal(80, traffic.SentBytesOf(ENetMessage.Input));
        Assert.Equal(2, traffic.SentDatagramsOf(ENetMessage.Input));
        Assert.Equal(6, traffic.SentBytesOf(ENetMessage.StateUpdate));
        Assert.Equal(86, traffic.SentBytes);
        Assert.Equal(3, traffic.SentDatagrams);
        Assert.Equal(0, traffic.ReceivedBytes);
    }

    // The two directions are separate stores, not one signed number: a listen server's host sends and
    // receives on the same link and the readings must not cancel.
    [Fact]
    public void SentAndReceivedAreCountedIndependently()
    {
        var traffic = new NetTraffic();
        traffic.RecordSent(new[] { (byte)ENetMessage.StateUpdate }, 10);
        traffic.RecordReceived(new[] { (byte)ENetMessage.Input }, 30);

        Assert.Equal(10, traffic.SentBytes);
        Assert.Equal(30, traffic.ReceivedBytes);
        Assert.Equal(0, traffic.ReceivedBytesOf(ENetMessage.StateUpdate));
        Assert.Equal(30, traffic.ReceivedBytesOf(ENetMessage.Input));
    }

    // A payload from whoever is on the other end of a socket is not necessarily this build's protocol.
    // Those bytes still crossed the link, so they are counted — into a bucket that cannot index past the
    // array, which is the whole point of having one.
    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 200 })]
    [InlineData(new byte[] { 255, 1, 2 })]
    public void UnclassifiablePayloadsLandInTheOtherBucket(byte[] payload)
    {
        var traffic = new NetTraffic();
        traffic.RecordSent(payload, 64);

        Assert.Equal(64, traffic.SentBytes);
        Assert.Equal(64, traffic.SentBytesAt(NetTraffic.OtherType));
        Assert.Equal("other", NetTraffic.NameOf(NetTraffic.OtherType));
    }

    // The roll-up is what lets the console read a server's total without walking every live connection
    // on the render thread.
    [Fact]
    public void AConnectionsTrafficAlsoCountsOnItsParent()
    {
        var server = new NetTraffic();
        var first = new NetTraffic(server);
        var second = new NetTraffic(server);

        first.RecordSent(new[] { (byte)ENetMessage.StateUpdate }, 100);
        second.RecordSent(new[] { (byte)ENetMessage.StateUpdate }, 40);
        second.RecordReceived(new[] { (byte)ENetMessage.Input }, 25);

        Assert.Equal(100, first.SentBytes);
        Assert.Equal(40, second.SentBytes);
        Assert.Equal(140, server.SentBytes);
        Assert.Equal(140, server.SentBytesOf(ENetMessage.StateUpdate));
        Assert.Equal(25, server.ReceivedBytes);
    }

    [Fact]
    public void TheDropCountersRollUpTheSameWay()
    {
        var server = new NetTraffic();
        var connection = new NetTraffic(server);

        connection.CountRefusedSend();
        connection.CountOversizedDropped();
        server.CountRefusedConnection();

        Assert.Equal(1, connection.RefusedSends);
        Assert.Equal(1, server.RefusedSends);
        Assert.Equal(1, server.OversizedDropped);
        Assert.Equal(0, connection.RefusedConnections);
        Assert.Equal(1, server.RefusedConnections);
    }

    // The rate is the last CLOSED window, divided by the time that really elapsed — not by the nominal
    // one second. A host that updates late must not have its bandwidth over-reported.
    [Fact]
    public void RatesPublishOnlyWhenAWindowCloses_AndDivideByTheRealElapsedTime()
    {
        var traffic = new NetTraffic();
        traffic.Update(100.0); // anchors the window

        traffic.RecordSent(new[] { (byte)ENetMessage.StateUpdate }, 1000);
        traffic.Update(100.5);
        Assert.Equal(0, traffic.SentBytesPerSecond); // half a second in: nothing published yet

        traffic.Update(102.0); // the window ran 2 s, so 1000 bytes is 500 B/s and not 1000
        Assert.Equal(500.0, traffic.SentBytesPerSecond, 3);
        Assert.Equal(0.5, traffic.SentDatagramsPerSecond, 3);

        // And the accumulator is emptied, so a quiet window reads as quiet rather than repeating.
        traffic.Update(103.5);
        Assert.Equal(0, traffic.SentBytesPerSecond);
        // Totals are untouched by any of that: they are the session, not the window.
        Assert.Equal(1000, traffic.SentBytes);
    }

    [Fact]
    public void TopSentTypes_RanksByBytesAndLeavesUnusedSlotsNegative()
    {
        var traffic = new NetTraffic();
        traffic.RecordSent(new[] { (byte)ENetMessage.Input }, 50);
        traffic.RecordSent(new[] { (byte)ENetMessage.StateUpdate }, 500);
        traffic.RecordSent(new[] { (byte)ENetMessage.Welcome }, 200);

        System.Span<int> top = stackalloc int[4];
        traffic.TopSentTypes(top);

        Assert.Equal((int)ENetMessage.StateUpdate, top[0]);
        Assert.Equal((int)ENetMessage.Welcome, top[1]);
        Assert.Equal((int)ENetMessage.Input, top[2]);
        Assert.Equal(-1, top[3]); // nothing else was ever sent
    }

    // The end-to-end shape: a real session over the loopback transport, where every send goes through
    // the connection the server actually holds. This is the assertion that the plumbing is connected —
    // the unit tests above would all pass with nothing calling them.
    [Fact]
    public void ALoopbackSessionCountsBothDirectionsOnTheServerAndTheClient()
    {
        var transport = new LoopbackServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Spawn, Level);
        LoopbackClientTransport clientTransport = transport.CreateClient();
        var client = new NetClient(clientTransport, "Ana", Level);

        double now = 5000.0;
        for (int i = 0; i < 12; i++)
        {
            now += ServerSimulation.TickRate;
            server.Update(now);
            client.Update(now);
            if (client.Joined)
                client.SendInput(Input((uint)i), now);
        }

        Assert.True(client.Joined);
        // The server sent this client its Welcome and a snapshot stream; the client sent inputs back.
        Assert.True(server.Traffic.SentBytesOf(ENetMessage.Welcome) > 0);
        Assert.True(server.Traffic.SentBytesOf(ENetMessage.StateUpdate) > 0);
        Assert.True(server.Traffic.ReceivedBytesOf(ENetMessage.Input) > 0);
        // And the two ends agree about the same link, from opposite sides of it.
        Assert.Equal(server.Traffic.SentBytes, client.Traffic.ReceivedBytes);
        Assert.Equal(server.Traffic.ReceivedBytes, client.Traffic.SentBytes);
    }

    // Every counter the report names is reachable from the two objects a session hands the console. The
    // point is the reachability, not the values: before this, five of the six lived where only a test
    // could see them.
    [Fact]
    public void TheReportNamesEveryCounterAndSurvivesAnEmptySession()
    {
        Assert.Equal("No session: nothing is connected, hosting or joining.",
            Assert.Single(NetReport.Stats(null, null)));

        var transport = new LoopbackServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Spawn, Level);
        LoopbackClientTransport clientTransport = transport.CreateClient();
        var client = new NetClient(clientTransport, "Ana", Level);

        double now = 5000.0;
        for (int i = 0; i < 30; i++)
        {
            now += ServerSimulation.TickRate;
            server.Update(now);
            client.Update(now);
            if (client.Joined)
                client.SendInput(Input((uint)i), now);
        }

        string report = string.Join("\n", NetReport.Stats(server, client));
        Assert.Contains("malformed", report, System.StringComparison.Ordinal);
        Assert.Contains("oversized", report, System.StringComparison.Ordinal);
        Assert.Contains("refused-connections", report, System.StringComparison.Ordinal);
        Assert.Contains("refused-sends", report, System.StringComparison.Ordinal);
        Assert.Contains("rejected-positions", report, System.StringComparison.Ordinal);
        Assert.Contains("ping", report, System.StringComparison.Ordinal);
        Assert.Contains("sent by type", report, System.StringComparison.Ordinal);
        // A server alone (a dedicated host) reports without a client half, and vice versa.
        Assert.DoesNotContain("client", string.Join("\n", NetReport.Stats(server, null)),
            System.StringComparison.Ordinal);
        Assert.DoesNotContain("server", string.Join("\n", NetReport.Stats(null, client)),
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void ByteFormattingScalesAndAnUnmeasuredPingIsNotZero()
    {
        Assert.Equal("512 B", NetReport.Bytes(512));
        Assert.Equal("1.5 KB", NetReport.Bytes(1536));
        Assert.Equal("2.00 MB", NetReport.Bytes(2 * 1024 * 1024));
        Assert.Equal("-- ms", NetReport.Ping(double.NaN));
        Assert.Equal("42 ms", NetReport.Ping(0.042));
        Assert.Equal("Net  offline", NetReport.HudLine(null, double.NaN));
        Assert.Contains("nothing yet", NetReport.Breakdown(new NetTraffic()), System.StringComparison.Ordinal);
    }

    private static InputCommand Input(uint frame) =>
        new(frame, 0, 0, jump: false, sprint: false, yaw: 0, pitch: 90);
}
