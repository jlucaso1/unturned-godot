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

        Assert.True(pool.TryRent(out byte a));
        Assert.True(pool.TryRent(out byte b));
        Assert.True(pool.TryRent(out byte c));

        Assert.Equal(1, a);
        Assert.Equal(2, b);
        Assert.Equal(3, c);
    }

    [Fact]
    public void NeverHandsOutTheReservedSentinels()
    {
        var pool = new PlayerIdPool();
        var seen = new HashSet<byte>();

        while (pool.TryRent(out byte id))
            Assert.True(seen.Add(id), $"id {id} was handed out twice");

        Assert.Equal(PlayerIdPool.Capacity, seen.Count);
        // 0 is what NetClient.PlayerId reads as before a Welcome; 255 is ZombieSystem's "no target".
        Assert.DoesNotContain((byte)0, seen);
        Assert.DoesNotContain(byte.MaxValue, seen);
    }

    [Fact]
    public void ReturnedIdIsReusedRatherThanExhaustingThePool()
    {
        var pool = new PlayerIdPool();
        for (int i = 0; i < PlayerIdPool.Capacity; i++)
            Assert.True(pool.TryRent(out _));

        Assert.False(pool.TryRent(out _));
        Assert.Equal(0, pool.Available);

        pool.Return(42);

        Assert.Equal(1, pool.Available);
        Assert.True(pool.TryRent(out byte reused));
        Assert.Equal(42, reused);
    }

    [Fact]
    public void ReturningTwice_DoesNotDuplicateAnId()
    {
        var pool = new PlayerIdPool();
        Assert.True(pool.TryRent(out byte id));

        pool.Return(id);
        pool.Return(id);

        Assert.Equal(PlayerIdPool.Capacity, pool.Available);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData(byte.MaxValue)]
    public void ReturningAReservedValue_IsIgnored(byte reserved)
    {
        var pool = new PlayerIdPool();

        pool.Return(reserved);

        Assert.Equal(PlayerIdPool.Capacity, pool.Available);
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
        public void Send(byte[] payload, ESendType sendType) { }
        public void Close() => Closed = true;
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

        public bool TryReceive(out ServerTransportEvent evt) => Events.TryDequeue(out evt);
        public void Update(double now) { }
        public void Close() { }
    }

    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    private static (NetServer, FakeServerTransport) Build()
    {
        var transport = new FakeServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Vector3.Zero);
        return (server, transport);
    }

    private static FakeConnection Join(NetServer server, FakeServerTransport transport, double now)
    {
        var c = new FakeConnection();
        transport.Connect(c);
        transport.Message(c, NetMessages.WriteHello("player"));
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

        for (int i = 0; i < PlayerIdPool.Capacity * 3; i++)
        {
            now += 0.01;
            FakeConnection churn = Join(server, transport, now);
            transport.Disconnect(churn);
            server.Update(now);
        }

        // The victim is still the only player, still holds a slot, and the server still admits others.
        Assert.Equal(1, server.PlayerCount);
        Assert.False(victim.Closed);

        now += 0.01;
        Join(server, transport, now);
        Assert.Equal(2, server.PlayerCount);
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
        Assert.Equal(0, server.FreePlayerSlots);

        now += 0.001;
        FakeConnection turnedAway = Join(server, transport, now);

        Assert.True(turnedAway.Closed);
        Assert.Equal(PlayerIdPool.Capacity, server.PlayerCount);
    }

    [Fact]
    public void DisconnectingFreesTheSlotForTheNextJoin()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        FakeConnection first = Join(server, transport, 0.0);
        int freeWhileOccupied = server.FreePlayerSlots;

        transport.Disconnect(first);
        server.Update(0.1);

        Assert.Equal(0, server.PlayerCount);
        Assert.Equal(freeWhileOccupied + 1, server.FreePlayerSlots);
    }
}
