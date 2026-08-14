using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.Tests.Net;

public class PlayerIdPoolTests
{
    [Fact]
    public void RentsTheLowestFreeIdFirst()
    {
        var pool = new PlayerIdPool();

        Assert.True(pool.TryRent(0.0, out byte a));
        Assert.True(pool.TryRent(0.0, out byte b));
        Assert.True(pool.TryRent(0.0, out byte c));

        Assert.Equal(1, a);
        Assert.Equal(2, b);
        Assert.Equal(3, c);
    }

    [Fact]
    public void NeverHandsOutTheReservedSentinels()
    {
        var pool = new PlayerIdPool();
        var seen = new HashSet<byte>();

        while (pool.TryRent(0.0, out byte id))
            Assert.True(seen.Add(id), $"id {id} was handed out twice");

        Assert.Equal(PlayerIdPool.Capacity, seen.Count);
        // 0 is what NetClient.PlayerId reads as before a Welcome; 255 is ZombieSystem's "no target".
        Assert.DoesNotContain((byte)0, seen);
        Assert.DoesNotContain(byte.MaxValue, seen);
    }

    // The point of the pool: 254 is a limit on concurrent players, not on admissions for the lifetime of
    // the server. A bare counter made it the latter.
    [Fact]
    public void ReturnedIdIsReusedRatherThanExhaustingThePool()
    {
        var pool = new PlayerIdPool();
        for (int i = 0; i < PlayerIdPool.Capacity; i++)
            Assert.True(pool.TryRent(0.0, out _));

        Assert.False(pool.TryRent(0.0, out _));
        Assert.Equal(0, pool.AvailableAt(0.0));

        pool.Return(42, 0.0);

        Assert.Equal(1, pool.Unheld);
        Assert.True(pool.TryRent(PlayerIdPool.QuarantineSeconds + 0.001, out byte reused));
        Assert.Equal(42, reused);
    }

    [Fact]
    public void ReturningTwice_DoesNotDuplicateAnId()
    {
        var pool = new PlayerIdPool();
        Assert.True(pool.TryRent(0.0, out byte id));

        pool.Return(id, 0.0);
        pool.Return(id, 0.0);

        // Unheld, not AvailableAt: the id is back in the pool but still cooling at this instant.
        Assert.Equal(PlayerIdPool.Capacity, pool.Unheld);
        Assert.Equal(PlayerIdPool.Capacity, pool.AvailableAt(PlayerIdPool.QuarantineSeconds + 0.001));
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData(byte.MaxValue)]
    public void ReturningAReservedValue_IsIgnored(byte reserved)
    {
        var pool = new PlayerIdPool();

        pool.Return(reserved, 0.0);

        Assert.Equal(PlayerIdPool.Capacity, pool.AvailableAt(0.0));
    }

    // A cooling id is not a slot the server can fill, so it must not be advertised as one: a lobby or an
    // admission gate reading this would offer room every join is then refused.
    [Fact]
    public void CoolingIdsAreNotCountedAsAvailable()
    {
        var pool = new PlayerIdPool();
        for (int i = 0; i < PlayerIdPool.Capacity; i++)
            Assert.True(pool.TryRent(0.0, out _));
        for (byte id = PlayerIdPool.First; id <= PlayerIdPool.Last; id++)
            pool.Return(id, 10.0);

        Assert.Equal(PlayerIdPool.Capacity, pool.Unheld);
        Assert.Equal(0, pool.AvailableAt(10.0));
        Assert.False(pool.TryRent(10.0, out _));

        Assert.Equal(PlayerIdPool.Capacity, pool.AvailableAt(10.0 + PlayerIdPool.QuarantineSeconds + 0.001));
    }

    // PlayerLeft and PlayerJoined are both reliable, and ReliableChannel delivers an unseen sequence as
    // soon as it arrives rather than in order. Handing a just-freed id to the next joiner therefore lets
    // a retransmitted PlayerLeft for the previous holder delete the newcomer on every client.
    [Fact]
    public void AJustFreedId_IsNotHandedStraightBackOut()
    {
        var pool = new PlayerIdPool();
        Assert.True(pool.TryRent(0.0, out byte first));

        pool.Return(first, 1.0);

        Assert.True(pool.TryRent(1.0, out byte next));
        Assert.NotEqual(first, next);
    }

    [Fact]
    public void ARecycledIdIsReportedCooled_OnlyAfterTheQuarantineWindow()
    {
        var pool = new PlayerIdPool();
        Assert.True(pool.TryRent(0.0, out byte id));
        pool.Return(id, 5.0);

        Assert.False(pool.NextRecycledIdHasCooled(5.0));
        Assert.False(pool.NextRecycledIdHasCooled(5.0 + PlayerIdPool.QuarantineSeconds - 0.001));
        Assert.False(pool.NextRecycledIdHasCooled(5.0 + PlayerIdPool.QuarantineSeconds)); // still retransmittable
        Assert.True(pool.NextRecycledIdHasCooled(5.0 + PlayerIdPool.QuarantineSeconds + 0.001));
    }

    [Fact]
    public void NothingHasCooled_OnAPoolThatHasNeverRecycled() =>
        Assert.False(new PlayerIdPool().NextRecycledIdHasCooled(1000.0));

    // The rule is absolute: no id leaves the pool inside the window where a stale PlayerLeft for its
    // previous holder can still arrive. A fallback that handed out a hot id under pressure would leave
    // the race reachable, just harder to hit.
    [Fact]
    public void AStillCoolingIdIsNeverHandedOut_EvenWithNoFreshIdLeft()
    {
        var pool = new PlayerIdPool();
        for (int i = 0; i < PlayerIdPool.Capacity; i++)
            Assert.True(pool.TryRent(0.0, out _));

        pool.Return(7, 100.0);

        Assert.False(pool.NextRecycledIdHasCooled(100.0));
        Assert.False(pool.TryRent(100.0, out _));

        // ...and it becomes available the moment the window closes.
        double cooled = 100.0 + PlayerIdPool.QuarantineSeconds + 0.001;
        Assert.True(pool.TryRent(cooled, out byte reused));
        Assert.Equal(7, reused);
    }

    [Fact]
    public void ARecycledIdIsHandedOutOnceCooled()
    {
        var pool = new PlayerIdPool();
        for (int i = 0; i < PlayerIdPool.Capacity; i++)
            Assert.True(pool.TryRent(0.0, out _));
        pool.Return(3, 0.0);
        pool.Return(9, 1.0);

        double afterFirst = PlayerIdPool.QuarantineSeconds + 0.001;
        Assert.True(pool.TryRent(afterFirst, out byte first));
        Assert.Equal(3, first);

        // 9 was released a second later, so it is still hot at this instant.
        Assert.False(pool.TryRent(afterFirst, out _));
        Assert.True(pool.TryRent(afterFirst + 1.0, out byte second));
        Assert.Equal(9, second);
    }
}

// The reconnect churn the pool exists to survive, driven through the real NetServer.
public class PlayerIdExhaustionTests
{
    private sealed class FakeConnection : ITransportConnection
    {
        private static int NextId;
        private readonly int _id = ++NextId;
        public bool Closed;
        public int Id => _id;
        public NetTraffic Traffic { get; } = new();
        public readonly List<byte[]> Sent = new();
        public void Send(byte[] payload, ESendType sendType) => Sent.Add(payload);
        public void Close() => Closed = true;

        // The id the server assigned this connection, read back off its own Welcome.
        public byte AssignedPlayerId =>
            NetMessages.ReadWelcome(Sent.Find(p => NetMessages.TypeOf(p) == ENetMessage.Welcome)!).PlayerId;
    }

    private sealed class FakeServerTransport : IServerTransport
    {
        public readonly Queue<ServerTransportEvent> Events = new();

        public void Connect(FakeConnection c) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Connected, c, Array.Empty<byte>()));

        public void Message(FakeConnection c, byte[] payload) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Message, c, payload));

        public void Disconnect(FakeConnection c) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Disconnected, c, Array.Empty<byte>()));

        public NetTraffic Traffic { get; } = new();
        public System.Func<byte[], byte[]?>? AnswerConnectionless { get; set; }
        public bool TryReceive(out ServerTransportEvent evt) => Events.TryDequeue(out evt);
        public void Update(double now) { }
        public void Close() { }
    }

    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    private const string Level = "PEI";

    private static (NetServer, FakeServerTransport) Build()
    {
        var transport = new FakeServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Vector3.Zero, Level);
        return (server, transport);
    }

    private static FakeConnection Join(NetServer server, FakeServerTransport transport, double now)
    {
        var c = new FakeConnection();
        transport.Connect(c);
        transport.Message(c, NetMessages.WriteHello("player", Level));
        server.Update(now);
        return c;
    }

    // The original failure: a byte counter with no wrap check recycled a live player's id onto a
    // newcomer, and the admission after that newcomer left threw KeyNotFoundException out of
    // ServerSimulation.GetState — from inside _PhysicsProcess, so the process went with it.
    [Fact]
    public void ChurningMoreConnectionsThanTheIdSpace_DoesNotDisturbASittingPlayer()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        double now = 0.0;

        FakeConnection victim = Join(server, transport, now);
        Assert.Equal(1, server.PlayerCount);

        // Well past the id space, and slowly enough that returned ids keep cooling back into service —
        // a real server's join/leave churn, not a flood.
        for (int i = 0; i < PlayerIdPool.Capacity * 3; i++)
        {
            now += PlayerIdPool.QuarantineSeconds / 4;
            FakeConnection churn = Join(server, transport, now);
            transport.Disconnect(churn);
            server.Update(now);
        }

        // The victim is still the only player, still holds a slot, and the server still admits others.
        Assert.Equal(1, server.PlayerCount);
        Assert.False(victim.Closed);

        now += PlayerIdPool.QuarantineSeconds;
        FakeConnection newcomer = Join(server, transport, now);
        Assert.Equal(2, server.PlayerCount);
        Assert.NotEqual(victim.AssignedPlayerId, newcomer.AssignedPlayerId);
    }

    [Fact]
    public void AFullServerRefusesTheNextJoinInsteadOfReusingALiveId()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        double now = 0.0;

        for (int i = 0; i < PlayerIdPool.Capacity; i++)
        {
            now += 0.001;
            Join(server, transport, now);
        }

        Assert.Equal(PlayerIdPool.Capacity, server.PlayerCount);
        Assert.Equal(0, server.FreePlayerSlotsAt(now));

        now += 0.001;
        FakeConnection turnedAway = Join(server, transport, now);

        Assert.True(turnedAway.Closed);
        Assert.Equal(PlayerIdPool.Capacity, server.PlayerCount);

        // And it is told why, rather than watching the connection die for no stated reason.
        byte[] reject = turnedAway.Sent.Find(p => NetMessages.TypeOf(p) == ENetMessage.Reject)!;
        Assert.Equal(EJoinRejection.ServerFull, NetMessages.ReadReject(reject).Reason);
    }

    // The server-level shape of the ordering hazard: a client that had the leaver's id must not see the
    // newcomer take it, because a retransmitted PlayerLeft would then delete the newcomer's remote.
    [Fact]
    public void ADepartedPlayersId_IsNotGivenToTheNextJoiner()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        FakeConnection first = Join(server, transport, 0.0);
        byte departed = first.AssignedPlayerId;

        transport.Disconnect(first);
        server.Update(0.1);

        FakeConnection second = Join(server, transport, 0.2);
        FakeConnection third = Join(server, transport, 0.3);

        Assert.NotEqual(departed, second.AssignedPlayerId);
        Assert.NotEqual(departed, third.AssignedPlayerId);
        Assert.NotEqual(second.AssignedPlayerId, third.AssignedPlayerId);
    }

    [Fact]
    public void DisconnectingFreesTheSlotForTheNextJoin()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        FakeConnection first = Join(server, transport, 0.0);
        int freeWhileOccupied = server.FreePlayerSlotsAt(0.0);

        transport.Disconnect(first);
        server.Update(0.1);

        Assert.Equal(0, server.PlayerCount);
        Assert.Equal(freeWhileOccupied, server.FreePlayerSlotsAt(0.1));
        // ...and the returned id joins the count only once it has cooled.
        Assert.Equal(freeWhileOccupied + 1, server.FreePlayerSlotsAt(0.1 + PlayerIdPool.QuarantineSeconds + 0.001));
    }
}
