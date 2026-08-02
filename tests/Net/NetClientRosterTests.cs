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
