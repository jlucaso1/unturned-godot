using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// Welcome carries "everyone already here" — a complete roster, not an addition to one. The client
// merged it into whatever it already held, so anyone missing from a later Welcome stayed on screen
// forever as a player nobody can see, hear or shoot.
//
// A second Welcome is ordinary: while unadmitted the client re-sends its Hello every couple of
// seconds, and a joined session that Hellos again is answered with a fresh roster. Two of them in
// flight is all it takes, and UDP is free to hand the client that roster after the PlayerLeft that
// should have removed the ghost — reliable delivery retransmits, it does not reorder.
public class NetClientRosterTests
{
    private const string Level = "PEI";

    private sealed class FakeClientTransport : IClientTransport
    {
        private readonly Queue<byte[]> _inbox = new();
        public bool IsConnected => true;
        public void Deliver(byte[] payload) => _inbox.Enqueue(payload);
        public void Send(byte[] payload, ESendType sendType) { }
        public bool TryReceive(out byte[] payload) => _inbox.TryDequeue(out payload!);
        public void Update(double now) { }
        public void Close() { }
    }

    private static PlayerListing Listing(byte id, string name) =>
        new() { PlayerId = id, Name = name, Position = new Vector3(id, 0, 0) };

    [Fact]
    public void ASecondWelcome_ReplacesTheRoster_InsteadOfMergingIntoIt()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", Level);

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 1, new[] { Listing(2, "A"), Listing(3, "B") }));
        client.Update(0);
        Assert.Equal(2, client.Remotes.Count);

        // B left while our Hello was being answered a second time, and the PlayerLeft overtook the
        // roster on the wire. The newer roster is the truth.
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 5, new[] { Listing(2, "A") }));
        client.Update(1.0);

        Assert.Equal("A", Assert.Single(client.Remotes).Value.Name);
    }

    // And the players who stayed keep their identity: a re-Welcome is not a reason to rebuild everyone
    // from scratch mid-session.
    [Fact]
    public void ASecondWelcome_KeepsThePlayersItStillLists()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", Level);

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 1, new[] { Listing(2, "A") }));
        client.Update(0);
        RemotePlayer first = client.Remotes[2];

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 5, new[] { Listing(2, "A"), Listing(3, "B") }));
        client.Update(1.0);

        Assert.Equal(2, client.Remotes.Count);
        Assert.Same(first, client.Remotes[2]);
    }

    // The mirror of the ghost, and the reason the roster carries the tick it was taken at: a join that
    // is NEWER than the roster must survive it. PlayerJoined is reliable and already delivered, so it
    // is never replayed — erase that remote and the player stays invisible for the rest of the session,
    // since state updates only move remotes that already exist.
    [Fact]
    public void AWelcomeThatPredatesAJoin_DoesNotEraseThatPlayer()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", Level);

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 5, new[] { Listing(2, "A") }));
        transport.Deliver(NetMessages.WritePlayerJoined(9, tick: 0, Listing(3, "C"))); // joined after that roster
        client.Update(0);
        Assert.Equal(2, client.Remotes.Count);

        // A second Welcome, taken BEFORE C joined, overtakes nothing it should undo.
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 7, new[] { Listing(2, "A") }));
        client.Update(1.0);

        Assert.Equal(2, client.Remotes.Count);
        Assert.Equal("C", client.Remotes[3].Name);
    }

    // A player who left is still dropped by a roster taken after they joined — the case the tick is
    // there to tell apart from the one above.
    [Fact]
    public void ARosterTakenAfterAJoin_StillDropsThePlayerItOmits()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", Level);

        transport.Deliver(NetMessages.WritePlayerJoined(9, tick: 0, Listing(3, "C")));
        client.Update(0);

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 12, new[] { Listing(2, "A") })); // C is gone by 12
        client.Update(1.0);

        Assert.Equal("A", Assert.Single(client.Remotes).Value.Name);
    }

    // Roster events are ordered by their own counter, not by the simulation tick, because several of
    // them happen inside ONE server frame: a joined client re-Hellos (answered with a roster) and a new
    // player Hellos (broadcast as a join) before the next 0.08 s step runs, so both carry the same tick.
    // On that tie the older roster used to win and delete the newer player — reliably joined, already
    // consumed, never replayed, invisible for the session.
    [Fact]
    public void AJoinAndARosterFromTheSameServerFrame_KeepTheJoin()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", Level);

        transport.Deliver(NetMessages.WritePlayerJoined(4, tick: 0, Listing(3, "C"))); // the newer event
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 3, new[] { Listing(2, "A") }));
        client.Update(0);

        Assert.Equal(2, client.Remotes.Count);
        Assert.Equal("C", client.Remotes[3].Name);
    }

    // The mirror: a roster taken BEFORE a departure must not bring that player back. The leave carries
    // its own place in the order, so a listing older than it is refused rather than respawned.
    [Fact]
    public void AStaleRoster_DoesNotResurrectAPlayerWhoLeft()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", Level);

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 2, new[] { Listing(2, "A"), Listing(3, "C") }));
        client.Update(0);
        Assert.Equal(2, client.Remotes.Count);

        transport.Deliver(NetMessages.WritePlayerLeft(5, 3));                               // C leaves
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 4, new[] { Listing(2, "A"), Listing(3, "C") }));
        client.Update(1.0); // the older roster arrives second

        Assert.Equal("A", Assert.Single(client.Remotes).Value.Name);
    }

    // A join can be stale too. Someone who connects and drops straight back out produces a join and a
    // leave moments apart, and the leave may arrive first — after which the join describes a world that
    // is already over. Respawning from it leaves a player standing there for good: state updates do not
    // remove remotes, and no second leave is coming.
    [Fact]
    public void AJoinAlreadySupersededByALeave_IsIgnored()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", Level);

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 3, new[] { Listing(2, "A") }));
        transport.Deliver(NetMessages.WritePlayerLeft(5, 3));               // C's departure, first
        transport.Deliver(NetMessages.WritePlayerJoined(4, tick: 0, Listing(3, "C"))); // C's arrival, second
        client.Update(0);

        Assert.Equal("A", Assert.Single(client.Remotes).Value.Name);
    }

    // A departure does not bury the id forever: the pool hands it out again, and a join newer than the
    // leave is a different player who must appear.
    [Fact]
    public void AnIdHandedOutAgainAfterALeave_StillJoins()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", Level);

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 2, new[] { Listing(3, "C") }));
        transport.Deliver(NetMessages.WritePlayerLeft(5, 3));
        transport.Deliver(NetMessages.WritePlayerJoined(6, tick: 0, Listing(3, "Someone else")));
        client.Update(0);

        Assert.Equal("Someone else", Assert.Single(client.Remotes).Value.Name);
    }

    // Ids are recycled, so two players can be described by messages about the same id, and the older
    // description may arrive last. Overwriting the current occupant with it loses BOTH: the stale
    // remote is then removed by the previous occupant's leave, and nothing recreates the player who
    // actually holds the id — state updates only move remotes that already exist.
    [Fact]
    public void AnOlderJoinForARecycledId_DoesNotDisplaceItsCurrentOccupant()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", Level);

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 2, new[] { Listing(2, "A") }));
        transport.Deliver(NetMessages.WritePlayerJoined(6, tick: 0, Listing(3, "D"))); // the id's occupant now
        transport.Deliver(NetMessages.WritePlayerJoined(4, tick: 0, Listing(3, "C"))); // its previous one, late
        transport.Deliver(NetMessages.WritePlayerLeft(5, 3));                 // and their leave, later
        client.Update(0);

        Assert.Equal(2, client.Remotes.Count);
        Assert.Equal("D", client.Remotes[3].Name);
    }

    // Our own id never belongs in the remote roster — the server does not list us, but a roster that
    // did would put a second copy of the local player in the world.
    [Fact]
    public void OurOwnIdIsNeverARemote()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", Level);

        transport.Deliver(NetMessages.WriteWelcome(1, 0, 1, new[] { Listing(1, "Me"), Listing(2, "A") }));
        client.Update(0);

        Assert.Equal(1, client.PlayerId);
        Assert.Equal("A", Assert.Single(client.Remotes).Value.Name);
    }
}
