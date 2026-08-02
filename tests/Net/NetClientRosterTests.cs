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

        transport.Deliver(NetMessages.WriteWelcome(1, 0, new[] { Listing(2, "A"), Listing(3, "B") }));
        client.Update(0);
        Assert.Equal(2, client.Remotes.Count);

        // B left while our Hello was being answered a second time, and the PlayerLeft overtook the
        // roster on the wire. The newer roster is the truth.
        transport.Deliver(NetMessages.WriteWelcome(1, 5, new[] { Listing(2, "A") }));
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

        transport.Deliver(NetMessages.WriteWelcome(1, 0, new[] { Listing(2, "A") }));
        client.Update(0);
        RemotePlayer first = client.Remotes[2];

        transport.Deliver(NetMessages.WriteWelcome(1, 5, new[] { Listing(2, "A"), Listing(3, "B") }));
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

        transport.Deliver(NetMessages.WriteWelcome(1, 5, new[] { Listing(2, "A") }));
        transport.Deliver(NetMessages.WritePlayerJoined(9, Listing(3, "C"))); // joined after that roster
        client.Update(0);
        Assert.Equal(2, client.Remotes.Count);

        // A second Welcome, taken BEFORE C joined, overtakes nothing it should undo.
        transport.Deliver(NetMessages.WriteWelcome(1, 7, new[] { Listing(2, "A") }));
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

        transport.Deliver(NetMessages.WritePlayerJoined(9, Listing(3, "C")));
        client.Update(0);

        transport.Deliver(NetMessages.WriteWelcome(1, 12, new[] { Listing(2, "A") })); // C is gone by 12
        client.Update(1.0);

        Assert.Equal("A", Assert.Single(client.Remotes).Value.Name);
    }

    // Our own id never belongs in the remote roster — the server does not list us, but a roster that
    // did would put a second copy of the local player in the world.
    [Fact]
    public void OurOwnIdIsNeverARemote()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Me", Level);

        transport.Deliver(NetMessages.WriteWelcome(1, 0, new[] { Listing(1, "Me"), Listing(2, "A") }));
        client.Update(0);

        Assert.Equal(1, client.PlayerId);
        Assert.Equal("A", Assert.Single(client.Remotes).Value.Name);
    }
}
