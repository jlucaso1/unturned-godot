using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// A roster that no longer fits one datagram, and the reassembly that keeps "replace, do not merge" true
// across its pieces.
//
// The replacement rule is load-bearing and predates this: a Welcome is "everyone already here", so a
// client that merged one would leave a departed player standing there forever, unseeable and
// unshootable. Chunking breaks that rule by construction unless the client can tell a piece from a
// whole — read chunk-wise, chunk 2 is a complete roster that happens to omit everyone in chunk 1.
public class WelcomeChunkTests
{
    private sealed class FakeClientTransport : IClientTransport
    {
        private readonly Queue<byte[]> _inbox = new();
        public bool IsConnected => true;
        public NetTraffic Traffic { get; } = new();
        public void Deliver(byte[] payload) => _inbox.Enqueue(payload);
        public void Send(byte[] payload, ESendType sendType) { }
        public bool TryReceive(out byte[] payload) => _inbox.TryDequeue(out payload!);
        public void Update(double now) { }
        public void Close() { }
    }

    private static PlayerListing Listing(byte id) => new()
    {
        PlayerId = id,
        Name = "P" + id,
        Position = new Vector3(id, 0, 0),
    };

    private static List<PlayerListing> Roster(params byte[] ids)
    {
        var roster = new List<PlayerListing>(ids.Length);
        foreach (byte id in ids)
            roster.Add(Listing(id));
        return roster;
    }

    // Half a roster is not evidence that anyone left. Pruning on the first chunk would delete every
    // player named in the chunks still in flight — and their PlayerJoined is reliable and already
    // consumed, so nothing would ever put them back.
    [Fact]
    public void APartialRosterPrunesNobody()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", "PEI");

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 4, Roster(2, 3), chunkIndex: 0, chunkCount: 2));
        client.Update(0);

        Assert.True(client.Joined); // admitted on the first piece: joining does not wait for the roster
        Assert.Equal(2, client.Remotes.Count);

        // The second piece completes it, and only now may absence mean anything.
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 4, Roster(5), chunkIndex: 1, chunkCount: 2));
        client.Update(1);

        Assert.Equal(3, client.Remotes.Count);
        Assert.True(client.Remotes.ContainsKey(2));
        Assert.True(client.Remotes.ContainsKey(3));
        Assert.True(client.Remotes.ContainsKey(5));
    }

    // Once assembled, the roster still REPLACES: a player we hold who is in none of its chunks has left.
    [Fact]
    public void ACompletedRosterStillReplacesWhatWeHold()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", "PEI");

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 4, Roster(2, 3, 5)));
        client.Update(0);
        Assert.Equal(3, client.Remotes.Count);

        // A newer roster, in two pieces, that no longer names 3.
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 9, Roster(2), chunkIndex: 0, chunkCount: 2));
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 9, Roster(5), chunkIndex: 1, chunkCount: 2));
        client.Update(1);

        Assert.Equal(2, client.Remotes.Count);
        Assert.False(client.Remotes.ContainsKey(3));
    }

    // Reliable delivery retransmits; it does not order. Chunks of one roster may arrive in any order.
    [Fact]
    public void ChunksMayArriveOutOfOrder()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", "PEI");

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 4, Roster(5), chunkIndex: 2, chunkCount: 3));
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 4, Roster(3), chunkIndex: 0, chunkCount: 3));
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 4, Roster(4), chunkIndex: 1, chunkCount: 3));
        client.Update(0);

        Assert.Equal(3, client.Remotes.Count);
    }

    // A duplicate must not complete the roster on its own: counting arrivals rather than distinct
    // indices would let two copies of chunk 0 "finish" a two-piece roster and prune everyone in chunk 1.
    [Fact]
    public void ARetransmittedChunkDoesNotCompleteTheRoster()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", "PEI");

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 4, Roster(2, 3)));
        client.Update(0);
        Assert.Equal(2, client.Remotes.Count);

        // A newer two-piece roster whose first chunk arrives twice and whose second is still in flight.
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 9, Roster(2), chunkIndex: 0, chunkCount: 2));
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 9, Roster(2), chunkIndex: 0, chunkCount: 2));
        client.Update(1);

        // 3 is not in the chunks seen so far, but the roster is not complete, so it is not evidence.
        Assert.True(client.Remotes.ContainsKey(3));
    }

    // A chunk of a roster older than the one being assembled is stale and cannot contribute to it —
    // otherwise a retransmission of the previous roster would count toward completing the current one.
    [Fact]
    public void AChunkOfAnOlderRosterIsIgnored()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", "PEI");

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 9, Roster(2), chunkIndex: 0, chunkCount: 2));
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 4, Roster(7), chunkIndex: 1, chunkCount: 2));
        client.Update(0);

        // The stale piece neither added its player nor completed version 9.
        Assert.False(client.Remotes.ContainsKey(7));
        Assert.Single(client.Remotes);
    }

    // A newer roster abandons a partial older one: that older roster is superseded whether or not its
    // remaining chunks ever arrive, and holding its half-built id set would let it prune against the
    // wrong version.
    [Fact]
    public void ANewerRosterAbandonsAPartialOne()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", "PEI");

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 4, Roster(2, 3)));
        client.Update(0);

        // Version 9 starts assembling, then version 12 arrives complete and settles the matter.
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 9, Roster(2), chunkIndex: 0, chunkCount: 2));
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 12, Roster(3)));
        client.Update(1);

        Assert.Single(client.Remotes);
        Assert.True(client.Remotes.ContainsKey(3));
    }

    // A tombstone still wins over a roster chunk that predates the departure, exactly as it did when a
    // roster was one datagram.
    [Fact]
    public void ADepartureStillBeatsAnOlderRostersChunk()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", "PEI");

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 4, Roster(2, 3)));
        transport.Deliver(NetMessages.WritePlayerLeft(6, 3));
        client.Update(0);
        Assert.False(client.Remotes.ContainsKey(3));

        // A retransmitted chunk of roster 4, arriving after the leave it predates, must not resurrect 3.
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 4, Roster(3), chunkIndex: 0, chunkCount: 1));
        client.Update(1);

        Assert.False(client.Remotes.ContainsKey(3));
    }
}
