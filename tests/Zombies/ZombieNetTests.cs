using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

public class ZombieNetMessagesTests
{
    [Fact]
    public void ZombieList_RoundTrips()
    {
        var chunk = new List<ZombieListing>
        {
            new()
            {
                Id = 7, Type = 2, Speciality = EZombieSpeciality.Sprinter,
                Shirt = 0, Pants = byte.MaxValue, Hat = 1, Gear = byte.MaxValue,
                Move = 3, Idle = 2,
                Position = new Vector3(10.5f, 34f, -20.25f), Yaw = 90,
            },
            new()
            {
                Id = 300, Type = 0, Speciality = EZombieSpeciality.Mega,
                Shirt = byte.MaxValue, Pants = byte.MaxValue, Hat = byte.MaxValue, Gear = byte.MaxValue,
                Position = new Vector3(-1f, 0f, 1f), Yaw = 0,
            },
        };

        byte[] payload = ZombieNetMessages.WriteZombieList(chunk);
        Assert.Equal(ENetMessage.ZombieList, NetMessages.TypeOf(payload));

        List<ZombieListing> read = ZombieNetMessages.ReadZombieList(payload);
        Assert.Equal(2, read.Count);
        Assert.Equal(7, read[0].Id);
        Assert.Equal(EZombieSpeciality.Sprinter, read[0].Speciality);
        Assert.Equal(0, read[0].Shirt);
        Assert.Equal(byte.MaxValue, read[0].Pants);
        Assert.Equal(1, read[0].Hat);
        Assert.Equal(3, read[0].Move);
        Assert.Equal(2, read[0].Idle);
        Assert.Equal(new Vector3(10.5f, 34f, -20.25f), read[0].Position);
        Assert.Equal(90, read[0].Yaw);
        Assert.Equal(300, read[1].Id);
        Assert.Equal(EZombieSpeciality.Mega, read[1].Speciality);
    }

    [Fact]
    public void ZombieStates_RoundTrip()
    {
        var states = new List<ZombieSnapshotState>
        {
            new() { Id = 4, Position = new Vector3(1, 2, 3), Yaw = 45, State = EZombieState.Chase },
            new() { Id = 9, Position = new Vector3(-4, 5, -6), Yaw = 180, State = EZombieState.Attack },
        };

        byte[] payload = ZombieNetMessages.WriteZombieStates(123u, states);
        Assert.Equal(ENetMessage.ZombieStates, NetMessages.TypeOf(payload));

        (uint tick, List<ZombieSnapshotState> read) = ZombieNetMessages.ReadZombieStates(payload);
        Assert.Equal(123u, tick);
        Assert.Equal(2, read.Count);
        Assert.Equal(4, read[0].Id);
        Assert.Equal(45, read[0].Yaw);
        Assert.Equal(EZombieState.Chase, read[0].State);
        Assert.Equal(new Vector3(-4, 5, -6), read[1].Position);
        Assert.Equal(EZombieState.Attack, read[1].State);
    }
}

// The full server-side loop over the in-memory loopback: a host with zombies, clients that join and
// receive the population, aggro that streams state updates — all through the NetServer seams
// (OnTick/OnPlayerAdmitted/Broadcast), with zero NetServer or NetClient edits.
public class ZombieHostTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 10f;
        return true;
    }

    private static readonly Vector3 Spawn = new(0, 10f, 0);

    private sealed class Harness
    {
        public readonly LoopbackServerTransport ServerTransport = new();
        public readonly NetServer Server;
        public readonly ZombieSystem System;
        public readonly List<NetClient> Clients = new();
        public readonly List<LoopbackClientTransport> Transports = new();
        public double Now = 5000.0;

        private readonly int _boundCount;

        // Bound 0 covers the player spawn; each extra bound sits 1000 m further out on X.
        public Harness(int boundCount = 1)
        {
            _boundCount = boundCount;
            var bounds = new List<NavBound>();
            for (int b = 0; b < boundCount; b++)
                bounds.Add(new NavBound
                {
                    Center = new Vector3(b * 1000, 140, 0),
                    Size = new Vector3(400, 300, 400),
                    MaxZombies = byte.MaxValue,
                });
            var table = new ZombieTable { Name = "Civilian", Health = 100, Damage = 10 };
            System = new ZombieSystem(new[] { table }, bounds, FlatGround);
            Server = new NetServer(ServerTransport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn);
            _ = new ZombieHost(System, Server);
        }

        // Populates every bound with ceil(pointsPerBound * 0.25) zombies on a grid whose nearest
        // point sits 20 m from the bound center on X — outside the standing player's 12 m detection
        // radius, so nothing aggros the spawn-standing player unless a test moves it closer.
        public void Populate(int pointsPerBound)
        {
            var points = new List<ZombieSpawnpointData>();
            for (int b = 0; b < _boundCount; b++)
            {
                for (int i = 0; i < pointsPerBound; i++)
                {
                    float x = (b * 1000) + 20 + ((i % 16) * 10);
                    float zGodot = -180 + ((i / 16) * 9);
                    points.Add(new ZombieSpawnpointData(0, new Vector3(x, 10, -zGodot))); // Unity z-flip
                }
            }
            System.Spawn(points, new Random(11));
        }
    }

    private static (NetClient Client, List<ZombieListing> Zoo,
        List<List<ZombieSnapshotState>> Batches) Join(Harness h, string name)
    {
        LoopbackClientTransport transport = h.ServerTransport.CreateClient();
        h.Transports.Add(transport);
        var client = new NetClient(transport, name);
        var zoo = new List<ZombieListing>();
        var batches = new List<List<ZombieSnapshotState>>();
        client.OnUnhandledMessage = payload =>
        {
            switch (NetMessages.TypeOf(payload))
            {
                case ENetMessage.ZombieList:
                    zoo.AddRange(ZombieNetMessages.ReadZombieList(payload));
                    break;
                case ENetMessage.ZombieStates:
                    batches.Add(ZombieNetMessages.ReadZombieStates(payload).States);
                    break;
            }
        };
        h.Clients.Add(client);
        return (client, zoo, batches);
    }

    private static void Pump(Harness h, int rounds = 1)
    {
        for (int i = 0; i < rounds; i++)
        {
            h.Now += ServerSimulation.TickRate;
            h.Server.Update(h.Now);
            foreach (NetClient client in h.Clients)
                client.Update(h.Now);
        }
    }

    [Fact]
    public void JoiningClient_ReceivesTheWholePopulationInChunks()
    {
        var h = new Harness();
        h.Populate(240); // ceil(240 * 0.25) = 60 zombies -> reliable chunks of 50 + 10
        Assert.Equal(60, h.System.Zombies.Count);

        (_, List<ZombieListing> zoo, _) = Join(h, "A");
        Pump(h, 3);

        Assert.Equal(60, zoo.Count);
        Assert.Equal(h.System.Zombies.Select(z => (int)z.Id).OrderBy(i => i),
            zoo.Select(z => (int)z.Id).OrderBy(i => i));
        ZombieInstance first = h.System.Zombies[0];
        ZombieListing listed = zoo.Single(z => z.Id == first.Id);
        Assert.Equal(first.Position, listed.Position);
        Assert.Equal(first.Type, listed.Type);
        Assert.Equal(first.Speciality, listed.Speciality);
        Assert.Equal(first.Shirt, listed.Shirt);
        Assert.Equal(first.Move, listed.Move);
        Assert.Equal(first.Idle, listed.Idle);
    }

    [Fact]
    public void RejoiningClient_GetsThePopulationAgain()
    {
        var h = new Harness();
        h.Populate(4); // one zombie
        (NetClient client, List<ZombieListing> zoo, _) = Join(h, "A");
        Pump(h, 3);
        Assert.Single(zoo);

        // Starve the client of server traffic past its state timeout: the self-healing rejoin
        // re-Hellos, and the idempotent rejoin must resend the zombie population with the Welcome.
        double t = h.Now;
        for (int i = 0; i < 200; i++)
            client.Update(t += ServerSimulation.TickRate);
        Assert.False(client.Joined);

        h.Now = t;
        Pump(h, 3);
        Assert.True(client.Joined);
        // Every Hello the client queued while starving replays as an idempotent rejoin, and each
        // rejoin resends the population — at least one beyond the original join.
        Assert.True(zoo.Count >= 2);
        Assert.All(zoo, z => Assert.Equal(zoo[0].Id, z.Id));
    }

    [Fact]
    public void SleepingZombies_CostNoStateBandwidth()
    {
        var h = new Harness();
        h.Populate(4); // one zombie at x>=20: outside the standing player's 12 m radius
        (_, _, List<List<ZombieSnapshotState>> batches) = Join(h, "A");
        Pump(h, 10);
        Assert.Empty(batches);
    }

    [Fact]
    public void AggroedZombie_StreamsChaseThenAttack()
    {
        var h = new Harness();
        h.Populate(4);
        ZombieInstance zombie = Assert.Single((IEnumerable<ZombieInstance>)h.System.Zombies);
        zombie.Yaw = 0f;
        zombie.Speciality = EZombieSpeciality.Normal; // pin the speed: a rolled crawler is too slow here
        zombie.Home = new Vector3(8, 10, 0); // within the standing player's 12 m detection radius
        zombie.Position = zombie.Home;

        (_, _, List<List<ZombieSnapshotState>> batches) = Join(h, "A");
        Pump(h, 3); // admit + first 0.1 s detection pass

        Assert.Contains(batches, b => b.Any(s => s.Id == zombie.Id && s.State == EZombieState.Chase));

        Pump(h, 20); // 8 m at 5.5 m/s: the zombie reaches attack range and swings
        Assert.Contains(batches, b => b.Any(s => s.State == EZombieState.Attack));
    }

    [Fact]
    public void LosingItsTarget_TheZombieWalksHomeAndFallsSilent()
    {
        var h = new Harness();
        h.Populate(4);
        ZombieInstance zombie = Assert.Single((IEnumerable<ZombieInstance>)h.System.Zombies);
        zombie.Yaw = 0f;
        zombie.Home = new Vector3(8, 10, 0);
        zombie.Position = zombie.Home;

        (_, _, List<List<ZombieSnapshotState>> batchesA) = Join(h, "A");
        (_, _, List<List<ZombieSnapshotState>> batchesB) = Join(h, "B");
        Pump(h, 5);
        Assert.NotEmpty(batchesA); // both spectators see the chase
        Assert.NotEmpty(batchesB);

        // Every player disconnects: with no target left the zombie walks home and settles down.
        foreach (LoopbackClientTransport transport in h.Transports)
            transport.Close();
        h.Clients.Clear();
        Pump(h, 40);
        Assert.Equal(EZombieState.Idle, zombie.State);
        Assert.Equal(byte.MaxValue, zombie.TargetPlayer);
        Assert.True(zombie.Position.DistanceTo(zombie.Home) <= ZombieSystem.ArriveRadius + 0.01f);
    }

    [Fact]
    public void IdleTransition_SendsOneFinalSnapshotThenSilence()
    {
        var h = new Harness();
        h.Populate(4);
        ZombieInstance zombie = Assert.Single((IEnumerable<ZombieInstance>)h.System.Zombies);

        (_, _, List<List<ZombieSnapshotState>> batches) = Join(h, "A");
        Pump(h, 2); // admitted first, so the client actually witnesses the edge

        // Wake the zombie a couple of meters from home: it walks back streaming Return states,
        // then the host must replicate exactly the Return -> Idle edge and go quiet.
        zombie.State = EZombieState.Return;
        zombie.Position = zombie.Home + new Vector3(2f, 0, 0);
        Pump(h, 10);
        List<ZombieSnapshotState> flat = batches.SelectMany(b => b).ToList();
        Assert.Contains(flat, s => s.Id == zombie.Id && s.State == EZombieState.Return);
        Assert.Contains(flat, s => s.Id == zombie.Id && s.State == EZombieState.Idle);

        batches.Clear();
        Pump(h, 5); // steady idle afterwards: zero bandwidth
        Assert.Empty(batches);
    }

    [Fact]
    public void MoreThan255AwakeZombies_SplitAcrossStateBatches()
    {
        var h = new Harness(boundCount: 2);
        h.Populate(600); // ceil(600 * 0.25) = 150 per bound = 300 zombies
        Assert.Equal(300, h.System.Zombies.Count);

        (_, _, List<List<ZombieSnapshotState>> batches) = Join(h, "A");
        Pump(h, 2); // admitted first, so the wake-up below is actually witnessed
        foreach (ZombieInstance z in h.System.Zombies)
        {
            // Wake the entire map at once, several meters from home so everyone keeps walking.
            z.State = EZombieState.Return;
            z.Position = z.Home + new Vector3(0, 0, 30f);
        }
        Pump(h, 1);

        Assert.True(batches.Count >= 2); // 300 awake > the 255-per-message cap
        Assert.Equal(300, batches.SelectMany(b => b).Select(s => s.Id).Distinct().Count());
    }
}
