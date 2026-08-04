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
        (byte bound, List<ushort> ids) = ZombieNetMessages.ReadZombieKilled(payload);
        Assert.Equal(3, bound);
        Assert.Equal(new ushort[] { 1, 65535, 900 }, ids);
    }

    [Fact]
    public void EmptyRoundTrips()
    {
        (byte bound, List<ushort> ids) =
            ZombieNetMessages.ReadZombieKilled(ZombieNetMessages.WriteZombieKilled(0, Array.Empty<ushort>()));
        Assert.Equal(0, bound);
        Assert.Empty(ids);
    }

    // Little-endian ids behind a type/bound/count header, like every other zombie payload.
    [Fact]
    public void WireLayoutIsExact()
    {
        byte[] payload = ZombieNetMessages.WriteZombieKilled(7, new ushort[] { 0x0201 });
        Assert.Equal(new byte[] { (byte)ENetMessage.ZombieKilled, 7, 1, 0x01, 0x02 }, payload);
    }

    [Fact]
    public void WriteRejectsANullList() =>
        Assert.Throws<ArgumentNullException>(() => ZombieNetMessages.WriteZombieKilled(0, null!));

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

        // A single zombie standing well clear of the spawn, so nothing aggros on its own.
        public ZombieInstance Plant(ushort id)
        {
            System.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(60f, 10f, 0)) }, new Random(3));
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

    // The count header is one byte wide, so a caller handing over more ids than it can express has to
    // be refused rather than silently writing a payload that claims to carry none.
    [Fact]
    public void WriteRefusesMoreIdsThanTheCountFieldHolds()
    {
        var tooMany = new ushort[byte.MaxValue + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ZombieNetMessages.WriteZombieKilled(0, tooMany));
        // One fewer is exactly the ceiling, and writes.
        Assert.Equal(byte.MaxValue,
            ZombieNetMessages.ReadZombieKilled(
                ZombieNetMessages.WriteZombieKilled(0, new ushort[byte.MaxValue])).Ids.Count);
    }
}
