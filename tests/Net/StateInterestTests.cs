using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// The player snapshot stream, which was the last broadcast in the protocol still going to every
// connection regardless of distance and regardless of whether anything had changed.
//
// At the transport's own 254-player ceiling that is 4 KB per datagram times 254 clients times 12.5 Hz —
// about 12.9 MB/s of egress, every datagram of it IP-fragmented. Zombies were given interest management
// long ago (ZombieHost, by nav bound) and so were impacts; players never were, and a player standing
// perfectly still still cost 16 bytes a tick to everyone on the server.
//
// Two filters. The region filter turns O(players^2) into O(players * players in the region) and pays
// one payload build per region instead of one for everyone. The change filter then drops players
// byte-identical to what that region was last told, with a periodic full send so a lost datagram cannot
// strand anyone at a stale position forever.
public class StateInterestTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    private sealed class FakeConnection : ITransportConnection
    {
        private static int NextId;
        public readonly List<byte[]> Sent = new();
        public int Id { get; } = ++NextId;
        public NetTraffic Traffic { get; } = new();
        public void Send(byte[] payload, ESendType sendType) => Sent.Add(payload);
        public void Close() { }

        // Every player id this connection has been told about since the last clear.
        public HashSet<byte> SeenPlayers()
        {
            var ids = new HashSet<byte>();
            foreach (byte[] payload in Sent.Where(p => NetMessages.TypeOf(p) == ENetMessage.StateUpdate))
                foreach (PlayerSnapshotState s in NetMessages.ReadStateUpdate(payload).States)
                    ids.Add(s.PlayerId);
            return ids;
        }

        public int StateUpdates() =>
            Sent.Count(p => NetMessages.TypeOf(p) == ENetMessage.StateUpdate);

        // The token this connection was admitted under, read back off its own Welcome — the same way
        // these tests already read back the player id the server assigned. Remembered once: these tests
        // clear Sent to measure a window, and a real client does not forget its token when the datagram
        // that carried it scrolls out of view.
        private uint _token;

        public uint SessionToken() => _token != 0
            ? _token
            : _token = NetMessages
                .ReadWelcome(Sent.First(p => NetMessages.TypeOf(p) == ENetMessage.Welcome)).SessionToken;
    }

    private sealed class FakeServerTransport : IServerTransport
    {
        public readonly Queue<ServerTransportEvent> Events = new();
        public NetTraffic Traffic { get; } = new();
        public System.Func<byte[], byte[]?>? AnswerConnectionless { get; set; }

        public void Connect(FakeConnection c) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Connected, c, Array.Empty<byte>()));

        public void Message(FakeConnection c, byte[] payload) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Message, c, payload));

        public bool TryReceive(out ServerTransportEvent evt) => Events.TryDequeue(out evt);
        public void Update(double now) { }
        public void Close() { }
    }

    private const string Level = "PEI";

    private sealed class Harness
    {
        public readonly FakeServerTransport Transport = new();
        public readonly NetServer Server;
        public double Now = 1000.0;
        private uint _frame = 1;

        public Harness(Func<Vector3, byte>? regionOf = null)
        {
            Server = new NetServer(Transport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Vector3.Zero, Level)
            {
                RegionOf = regionOf,
            };
        }

        public FakeConnection Join(string name)
        {
            var conn = new FakeConnection();
            Transport.Connect(conn);
            Transport.Message(conn, NetMessages.WriteHello(name, Level));
            return conn;
        }

        // A trusted-position claim, the shape a real client sends: with the session token the Welcome
        // handed this connection, which the server checks before it decodes anything else. Frames
        // increase globally so nothing is refused by the freshness guard.
        public void Claim(FakeConnection conn, Vector3 position) =>
            Transport.Message(conn, NetMessages.WriteInput(new InputCommand(_frame++, 0, 0, false, false,
                0, 90, EPlayerStance.Stand, position), conn.SessionToken()));

        public void Tick(int rounds = 1)
        {
            for (int i = 0; i < rounds; i++)
            {
                Now += ServerSimulation.TickRate;
                Server.Update(Now);
            }
        }
    }

    // The map divided in two by the X axis, standing in for the nav bounds ZombieHost supplies.
    private static byte EastWest(Vector3 position) => position.X >= 0 ? (byte)1 : (byte)2;

    [Fact]
    public void APlayerInAnotherRegionIsNotReplicated()
    {
        var harness = new Harness(EastWest);
        FakeConnection east = harness.Join("East");
        FakeConnection west = harness.Join("West");
        harness.Tick();

        // Both claim a side and settle there. Positions move a little each tick so nothing is skipped
        // for being unchanged — this test is about the region filter alone.
        for (int i = 0; i < 6; i++)
        {
            harness.Claim(east, new Vector3(10f + (i * 0.1f), 0, 0));
            harness.Claim(west, new Vector3(-10f - (i * 0.1f), 0, 0));
            harness.Tick();
        }

        east.Sent.Clear();
        west.Sent.Clear();
        for (int i = 0; i < 4; i++)
        {
            harness.Claim(east, new Vector3(20f + (i * 0.1f), 0, 0));
            harness.Claim(west, new Vector3(-20f - (i * 0.1f), 0, 0));
            harness.Tick();
        }

        // Ids are assigned in admission order, so East is 1 and West is 2.
        Assert.Equal(new HashSet<byte> { 1 }, east.SeenPlayers());
        Assert.Equal(new HashSet<byte> { 2 }, west.SeenPlayers());
    }

    // Crossing a border has to hand over the WHOLE region, not the delta since a tick this connection
    // was not listening to — otherwise a newcomer inherits a baseline it never received and sees an
    // empty world until somebody moves.
    [Fact]
    public void CrossingIntoARegionHandsOverAllOfIt()
    {
        var harness = new Harness(EastWest);
        FakeConnection resident = harness.Join("Resident");
        FakeConnection traveller = harness.Join("Traveller");
        harness.Tick();

        // First claims are baselines and are adopted outright; everything after them has to fit the
        // speed budget, so the traveller walks to the border rather than teleporting over it.
        harness.Claim(resident, new Vector3(2f, 0, 0));
        harness.Claim(traveller, new Vector3(-2f, 0, 0));
        harness.Tick(3);

        // The resident then stands perfectly still for long enough that the change filter stops
        // mentioning them at all.
        harness.Tick(3);
        traveller.Sent.Clear();

        // Half a metre a tick, inside one sprint tick's budget, up to and over the border.
        for (float x = -1.5f; x <= 0.5f; x += 0.5f)
        {
            harness.Claim(traveller, new Vector3(x, 0, 0));
            harness.Tick();
        }

        // They have never been told the resident exists, and the resident has not moved in a while.
        Assert.Contains((byte)1, traveller.SeenPlayers());
    }

    // A player who has not moved costs nothing. This is most of the stream at population: everybody
    // standing in a lobby was 16 bytes each, to everybody, 12.5 times a second.
    [Fact]
    public void AMotionlessPlayerStopsBeingSent()
    {
        var harness = new Harness();
        FakeConnection conn = harness.Join("Still");
        harness.Tick();
        harness.Claim(conn, new Vector3(5f, 0, 5f));
        harness.Tick(2);

        conn.Sent.Clear();
        // Well inside the resync interval, and nothing moves.
        harness.Tick(NetServer.FullResyncTicks / 2);

        Assert.Equal(0, conn.StateUpdates());
    }

    // But not forever. The stream is unreliable, so a client that lost the datagram carrying a player's
    // last movement would hold them at a stale position until they moved again — which, for a player who
    // has stopped, is never. The periodic full send bounds that to about a second.
    [Fact]
    public void AFullSnapshotStillGoesOutPeriodically()
    {
        var harness = new Harness();
        FakeConnection conn = harness.Join("Still");
        harness.Tick();
        harness.Claim(conn, new Vector3(5f, 0, 5f));
        harness.Tick(2);

        conn.Sent.Clear();
        harness.Tick(NetServer.FullResyncTicks + 2);

        Assert.True(conn.StateUpdates() >= 1, "a region has to be resent in full periodically, or a "
            + "client that dropped one datagram is stranded at a stale position for good");
        Assert.Contains((byte)1, conn.SeenPlayers());
    }

    // Any real movement is still sent on the tick it happens: the filter is about identical bytes, not
    // about rate-limiting.
    [Fact]
    public void MovementIsStillSentEveryTick()
    {
        var harness = new Harness();
        FakeConnection conn = harness.Join("Walker");
        harness.Tick();
        harness.Claim(conn, Vector3.Zero);
        harness.Tick(2);

        conn.Sent.Clear();
        for (int i = 1; i <= 5; i++)
        {
            harness.Claim(conn, new Vector3(i * 0.2f, 0, 0));
            harness.Tick();
        }

        Assert.Equal(5, conn.StateUpdates());
    }

    // With no region function — a level with no navigation data, which is the shape this had before —
    // everybody is in one region and everybody sees everybody. The filter must not quietly hide players
    // on a map that has nothing to divide it by.
    [Fact]
    public void WithoutRegionsEveryoneStillSeesEveryone()
    {
        var harness = new Harness();
        FakeConnection a = harness.Join("A");
        FakeConnection b = harness.Join("B");
        harness.Tick();

        harness.Claim(a, new Vector3(500f, 0, 0));
        harness.Claim(b, new Vector3(-500f, 0, 0));
        harness.Tick(2);

        Assert.Equal(new HashSet<byte> { 1, 2 }, a.SeenPlayers());
        Assert.Equal(new HashSet<byte> { 1, 2 }, b.SeenPlayers());
    }

    // A player who leaves a region must be forgotten there, or their stale entry would suppress the
    // first snapshot they get when they come back at the same position.
    [Fact]
    public void ReturningToARegionUnchangedIsStillAnnounced()
    {
        var harness = new Harness(EastWest);
        FakeConnection resident = harness.Join("Resident");
        FakeConnection wanderer = harness.Join("Wanderer");
        harness.Tick();

        var home = new Vector3(0.1f, 0, 0);
        harness.Claim(resident, new Vector3(2f, 0, 0));
        harness.Claim(wanderer, home);
        harness.Tick(2);

        harness.Claim(wanderer, new Vector3(-0.3f, 0, 0)); // over the border, inside a tick's budget
        harness.Tick(2);

        resident.Sent.Clear();
        harness.Claim(wanderer, home); // back, at exactly the position they left region 1 from
        harness.Tick(2);

        Assert.Contains((byte)2, resident.SeenPlayers());
    }

    // The payload is built once per region, not once per connection. That is what the change filter had
    // to be made per region to preserve: a per-connection filter would force a fresh serialization per
    // client per tick and hand most of the saving straight back.
    [Fact]
    public void ARegionsPayloadIsSharedByItsListeners()
    {
        var harness = new Harness(EastWest);
        FakeConnection a = harness.Join("A");
        FakeConnection b = harness.Join("B");
        harness.Tick();

        harness.Claim(a, new Vector3(10f, 0, 0));
        harness.Claim(b, new Vector3(11f, 0, 0));
        harness.Tick(3);

        a.Sent.Clear();
        b.Sent.Clear();
        harness.Claim(a, new Vector3(12f, 0, 0));
        harness.Claim(b, new Vector3(13f, 0, 0));
        harness.Tick();

        byte[] toA = a.Sent.Single(p => NetMessages.TypeOf(p) == ENetMessage.StateUpdate);
        byte[] toB = b.Sent.Single(p => NetMessages.TypeOf(p) == ENetMessage.StateUpdate);
        Assert.Same(toA, toB);
    }
}
