using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

// A death has to travel. The per-tick state stream carries no "alive" bit and stops mentioning a zombie
// the moment it leaves the population, so without this message the corpse simply stands on every client
// forever — the one failure mode a punch that works server-side can still have.
public class ZombieKillReplicationTests
{
    private const string Level = "PEI";
    private static readonly Vector3 Spawn = new(0, 10f, 0);

    private static bool FlatGround(float x, float z, out float y)
    {
        y = 10f;
        return true;
    }

    // ---- The wire ---------------------------------------------------------------------------------

    [Fact]
    public void RoundTrips()
    {
        byte[] payload = ZombieNetMessages.WriteZombieKilled(3, new ushort[] { 1, 65535, 900 });

        Assert.Equal(ENetMessage.ZombieKilled, NetMessages.TypeOf(payload));
        (byte bound, List<ushort> ids, _) = ZombieNetMessages.ReadZombieKilled(payload);
        Assert.Equal(3, bound);
        Assert.Equal(new ushort[] { 1, 65535, 900 }, ids);
    }

    [Fact]
    public void EmptyRoundTrips()
    {
        (byte bound, List<ushort> ids, _) =
            ZombieNetMessages.ReadZombieKilled(ZombieNetMessages.WriteZombieKilled(0, Array.Empty<ushort>()));
        Assert.Equal(0, bound);
        Assert.Empty(ids);
    }

    // Little-endian ids behind a type/bound/count header, like every other zombie payload.
    [Fact]
    public void WireLayoutIsExact()
    {
        byte[] payload = ZombieNetMessages.WriteZombieKilled(7,
            new (ushort, Godot.Vector3)[] { (0x0201, new Godot.Vector3(1f, 0f, -2f)) });

        // type, bound, count, then the id and the shove that threw the body.
        Assert.Equal(new byte[]
        {
            (byte)ENetMessage.ZombieKilled, 7, 1,
            0x01, 0x02,
            0x00, 0x00, 0x80, 0x3F, // 1
            0x00, 0x00, 0x00, 0x00, // 0
            0x00, 0x00, 0x00, 0xC0, // -2
        }, payload);
    }

    [Fact]
    public void WriteRejectsANullList() =>
        Assert.Throws<ArgumentNullException>(() =>
            ZombieNetMessages.WriteZombieKilled(0, (IReadOnlyList<ushort>)null!));

    // A truncated datagram is a dropped datagram, not a crash: the client's decoder guard is what turns
    // it into a MalformedPacketsDropped tick.
    [Fact]
    public void TruncatedPayloadThrowsForTheGuardToCatch()
    {
        byte[] payload = ZombieNetMessages.WriteZombieKilled(1, new ushort[] { 5, 6 });
        Array.Resize(ref payload, payload.Length - 1);
        Assert.False(MalformedPacket.TryDecode(payload, ZombieNetMessages.ReadZombieKilled, out _));
    }

    // ---- The host ---------------------------------------------------------------------------------

    private sealed class Harness
    {
        public readonly LoopbackServerTransport ServerTransport = new();
        public readonly NetServer Server;
        public readonly ZombieSystem System;
        public readonly ZombieHost Host;
        public readonly List<NetClient> Clients = new();
        public double Now = 5000.0;

        // How many ZombieKilled PAYLOADS arrived, as distinct from how many ids they carried between
        // them: the point of the pending queue is that one tick's deaths ride together.
        public int KillMessages;

        public Harness()
        {
            Server = new NetServer(ServerTransport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn, Level);
            System = new ZombieSystem(
                new[] { new ZombieTable { Name = "Civilian", Health = 100, Damage = 10 } },
                new List<NavBound>
                {
                    new()
                    {
                        Center = new Vector3(0, 140, 0),
                        Size = new Vector3(400, 300, 400),
                        MaxZombies = byte.MaxValue,
                    },
                },
                FlatGround);
            Host = new ZombieHost(System, Server);
        }

        // Every zombie id a per-tick snapshot has mentioned since the client joined.
        public readonly List<ushort> Snapshotted = new();

        public (NetClient Client, List<ZombieListing> Zoo, List<ushort> Killed) Join(string name)
        {
            var client = new NetClient(ServerTransport.CreateClient(), name, Level);
            var zoo = new List<ZombieListing>();
            var killed = new List<ushort>();
            client.OnUnhandledMessage = payload =>
            {
                switch (NetMessages.TypeOf(payload))
                {
                    case ENetMessage.ZombieList:
                        zoo.AddRange(ZombieNetMessages.ReadZombieList(payload).Listings);
                        break;
                    case ENetMessage.ZombieStates:
                        foreach (ZombieSnapshotState state in
                            ZombieNetMessages.ReadZombieStates(payload).States)
                        {
                            Snapshotted.Add(state.Id);
                        }
                        break;
                    case ENetMessage.ZombieKilled:
                        KillMessages++;
                        killed.AddRange(ZombieNetMessages.ReadZombieKilled(payload).Ids);
                        break;
                }
            };
            Clients.Add(client);
            return (client, zoo, killed);
        }

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

        // A single zombie, by default standing well clear of the spawn so nothing aggros on its own.
        // `x` brings it inside the standing player's detection radius when a test wants it awake.
        public ZombieInstance Plant(ushort id, float x = 60f)
        {
            System.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(x, 10f, 0)) }, new Random(3));
            ZombieInstance zombie = Assert.Single(System.Zombies);
            zombie.Id = id;
            return zombie;
        }
    }

    [Fact]
    public void ADeathReachesTheRegionsClients()
    {
        var h = new Harness();
        ZombieInstance zombie = h.Plant(42);
        (_, List<ZombieListing> zoo, List<ushort> killed) = h.Join("A");
        h.Pump(3);
        Assert.Single(zoo);

        h.System.Damage(zombie, 100, byte.MaxValue, Array.Empty<ZombiePlayerView>());
        h.Host.ReportKilled(zombie);
        h.Pump(2);

        Assert.Equal(new ushort[] { 42 }, killed);
    }

    // Exactly once: the pending list is drained, not replayed, so a client cannot be told twice.
    [Fact]
    public void ADeathIsAnnouncedOnce()
    {
        var h = new Harness();
        ZombieInstance zombie = h.Plant(42);
        (_, _, List<ushort> killed) = h.Join("A");
        h.Pump(3);

        h.System.Damage(zombie, 100, byte.MaxValue, Array.Empty<ZombiePlayerView>());
        h.Host.ReportKilled(zombie);
        h.Pump(10);

        Assert.Single(killed);
    }

    // Two deaths in one region ride the same payload — the second one appends to the list the first
    // opened rather than replacing it.
    [Fact]
    public void TwoDeathsInOneRegionShareAPayload()
    {
        var h = new Harness();
        ZombieInstance first = h.Plant(1);
        var second = new ZombieInstance { Id = 2, Bound = 0, Position = new Vector3(70f, 10f, 0), Health = 100, MaxHealth = 100 };
        var state = new ZombieSystemState();
        state.Zombies.Add(first);
        state.Zombies.Add(second);
        h.System.RestoreState(state);

        (_, _, List<ushort> killed) = h.Join("A");
        h.Pump(3);

        // Dead first, as they would be in a session: ReportKilled is the replication half of a death
        // the brain has already applied.
        h.System.Damage(first, 100, byte.MaxValue, Array.Empty<ZombiePlayerView>());
        h.System.Damage(second, 100, byte.MaxValue, Array.Empty<ZombiePlayerView>());
        h.Host.ReportKilled(first);
        h.Host.ReportKilled(second);
        h.Pump(2);

        Assert.Equal(new ushort[] { 1, 2 }, killed);
        Assert.Equal(1, h.KillMessages); // one payload, not one per death
        Assert.Empty(h.System.Zombies);
    }

    // Nothing dies, nothing is sent: the payload costs a session that never fights nothing at all.
    [Fact]
    public void NoDeathsMeansNoMessages()
    {
        var h = new Harness();
        h.Plant(42);
        (_, _, List<ushort> killed) = h.Join("A");
        h.Pump(10);
        Assert.Empty(killed);
    }

    // The other half of a death travelling correctly: once it has, the server never mentions that
    // zombie again. A snapshot arriving after the kill is what would put the avatar back — the client
    // drops one for an id it no longer holds, but the server not sending it is the guarantee that
    // makes the kill final rather than a race the client keeps having to win.
    [Fact]
    public void NothingIsSnapshottedForAZombieAfterItDies()
    {
        var h = new Harness();
        // Close enough for the player who joins below to be noticed, so it wakes and streams: an idle
        // zombie is never snapshotted at all, and the test would prove nothing.
        ZombieInstance zombie = h.Plant(42, x: 6f);
        (_, _, List<ushort> killed) = h.Join("A");
        h.Pump(8);
        Assert.Contains((ushort)42, h.Snapshotted);

        h.System.Damage(zombie, 100, byte.MaxValue, Array.Empty<ZombiePlayerView>());
        h.Host.ReportKilled(zombie);
        h.Pump(2);
        Assert.Equal(new ushort[] { 42 }, killed);

        h.Snapshotted.Clear();
        h.Pump(10);
        Assert.Empty(h.Snapshotted);
    }

    // Reloading a bug-repro dump replaces the whole population under the same ids, so a death queued
    // against the old one must not name a zombie the client is about to be told about afresh.
    [Fact]
    public void ResendingRegionsDropsPendingDeaths()
    {
        var h = new Harness();
        ZombieInstance zombie = h.Plant(42);
        (_, _, List<ushort> killed) = h.Join("A");
        h.Pump(3);

        h.Host.ReportKilled(zombie);
        h.Host.ResendRegions();
        h.Pump(3);

        Assert.Empty(killed);
    }

    [Fact]
    public void ReportKilledRejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => new Harness().Host.ReportKilled(null!));

    // Two ceilings sit on this payload, and the tighter one now decides.
    //
    // The count header is one byte, so 256 ids were always refused. But fourteen bytes a corpse means
    // 255 of them is 3.5 KB — three IP fragments on a message whose entire job is to be delivered, and
    // losing any one of them leaves those corpses standing forever. The MTU budget therefore refuses
    // long before the header does, and a caller with a whole region to announce splits instead.
    [Fact]
    public void WriteRefusesMoreIdsThanOneDatagramCarries()
    {
        var tooMany = new ushort[ZombieNetMessages.MaxKilledPerDatagram + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZombieNetMessages.WriteZombieKilled(0, tooMany));
        // One fewer is exactly the ceiling, and writes — inside the budget, by construction.
        byte[] full = ZombieNetMessages.WriteZombieKilled(
            0, new ushort[ZombieNetMessages.MaxKilledPerDatagram]);
        Assert.Equal(ZombieNetMessages.MaxKilledPerDatagram,
            ZombieNetMessages.ReadZombieKilled(full).Ids.Count);
        Assert.True(full.Length <= NetChunks.MaxPayloadBytes);
    }

    // A whole region dying at once is legal — a blast, a despawn sweep — and has to reach the client.
    // The chunked form is what makes that a handful of unfragmented datagrams instead of one the
    // network layer splits and the first lost fragment destroys.
    [Fact]
    public void TheChunkedFormCarriesAWholeRegionWithoutFragmenting()
    {
        var kills = new List<(ushort, Vector3)>();
        for (int i = 0; i < byte.MaxValue; i++)
            kills.Add(((ushort)i, Vector3.Zero));

        List<byte[]> chunks = ZombieNetMessages.WriteZombieKilledChunks(4, kills);

        var seen = new List<ushort>();
        foreach (byte[] chunk in chunks)
        {
            Assert.True(chunk.Length <= NetChunks.MaxPayloadBytes);
            (byte bound, List<ushort> ids, List<Vector3> _) = ZombieNetMessages.ReadZombieKilled(chunk);
            Assert.Equal(4, bound); // every chunk still names its own region
            seen.AddRange(ids);
        }

        Assert.Equal(byte.MaxValue, seen.Count);
        for (int i = 0; i < byte.MaxValue; i++)
            Assert.Equal((ushort)i, seen[i]); // and in order, with nothing dropped between chunks
    }
}
