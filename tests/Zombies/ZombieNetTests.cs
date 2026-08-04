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
    public void ClientMovementAnimationUsesTheOriginalOneCentimetrePerAxisThreshold()
    {
        Vector3 rendered = new(10f, 20f, 30f);

        Assert.False(ZombieClientMotion.IsMoving(rendered, rendered));
        Assert.False(ZombieClientMotion.IsMoving(
            rendered + new Vector3(0.009f, -0.009f, 0.009f), rendered));
        Assert.True(ZombieClientMotion.IsMoving(
            rendered + new Vector3(0.02f, 0f, 0f), rendered));
        Assert.True(ZombieClientMotion.IsMoving(
            rendered + new Vector3(0f, 0f, -0.02f), rendered));
    }

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

        byte[] payload = ZombieNetMessages.WriteZombieList(5, chunk);
        Assert.Equal(ENetMessage.ZombieList, NetMessages.TypeOf(payload));

        (byte bound, List<ZombieListing> read) = ZombieNetMessages.ReadZombieList(payload);
        Assert.Equal(5, bound);
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

    // The exact wire layout, byte for byte — what a little-endian BinaryWriter historically produced.
    // Locks the format so serializer rewrites can't silently change what's on the wire.
    [Fact]
    public void ZombieStates_WireLayout_IsExact()
    {
        var states = new List<ZombieSnapshotState>
        {
            new() { Id = 0x0102, Position = new Vector3(1f, -2f, 0.5f), Yaw = 7, State = EZombieState.Return },
        };
        byte[] payload = ZombieNetMessages.WriteZombieStates(0x04030201u, states);

        var expected = new List<byte> { (byte)ENetMessage.ZombieStates, 0x01, 0x02, 0x03, 0x04, 1, 0x02, 0x01 };
        expected.AddRange(BitConverter.GetBytes(1f));
        expected.AddRange(BitConverter.GetBytes(-2f));
        expected.AddRange(BitConverter.GetBytes(0.5f));
        expected.Add(7);
        expected.Add((byte)EZombieState.Return);
        Assert.Equal(expected.ToArray(), payload);
    }

    [Fact]
    public void ZombieList_WireLayout_IsExact()
    {
        var chunk = new List<ZombieListing>
        {
            new()
            {
                Id = 0x0201, Type = 3, Speciality = EZombieSpeciality.Crawler,
                Shirt = 1, Pants = 2, Hat = 3, Gear = 4, Move = 5, Idle = 6,
                Position = new Vector3(8f, 16f, -32f), Yaw = 200,
            },
        };
        byte[] payload = ZombieNetMessages.WriteZombieList(9, chunk);

        var expected = new List<byte>
        {
            (byte)ENetMessage.ZombieList, 9, 1,
            0x01, 0x02, 3, (byte)EZombieSpeciality.Crawler, 1, 2, 3, 4, 5, 6,
        };
        expected.AddRange(BitConverter.GetBytes(8f));
        expected.AddRange(BitConverter.GetBytes(16f));
        expected.AddRange(BitConverter.GetBytes(-32f));
        expected.Add(200);
        Assert.Equal(expected.ToArray(), payload);
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
    private const string Level = "PEI";

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
            : this(DefaultBounds(boundCount), boundCount)
        {
        }

        public Harness(List<NavBound> bounds, int boundCount = 0)
        {
            _boundCount = boundCount;
            var table = new ZombieTable { Name = "Civilian", Health = 100, Damage = 10 };
            System = new ZombieSystem(new[] { table }, bounds, FlatGround);
            Server = new NetServer(ServerTransport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn, Level);
            _ = new ZombieHost(System, Server);
        }

        private static List<NavBound> DefaultBounds(int boundCount)
        {
            var bounds = new List<NavBound>();
            for (int b = 0; b < boundCount; b++)
                bounds.Add(new NavBound
                {
                    Center = new Vector3(b * 1000, 140, 0),
                    Size = new Vector3(400, 300, 400),
                    MaxZombies = byte.MaxValue,
                });
            return bounds;
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
        var client = new NetClient(transport, name, Level);
        var zoo = new List<ZombieListing>();
        var batches = new List<List<ZombieSnapshotState>>();
        client.OnUnhandledMessage = payload =>
        {
            switch (NetMessages.TypeOf(payload))
            {
                case ENetMessage.ZombieList:
                    zoo.AddRange(ZombieNetMessages.ReadZombieList(payload).Listings);
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
    public void JoiningClient_ReceivesItsRegionInChunks()
    {
        var h = new Harness();
        h.Populate(240); // ceil(240 * 0.25) = 60 zombies in the spawn's region -> chunks of 50 + 10
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
    public void RejoiningClient_GetsItsRegionAgain()
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
    public void UnpublishedNavmesh_StillChasesAndReplicatesMovement()
    {
        // Regression: the host had a PathQuery attached, but its preloaded graph was deliberately not
        // published until collision reconciliation finished. Aggro replicated while every query failed,
        // so clients played the wake-up animation on a zombie whose position never changed.
        var h = new Harness();
        h.Populate(4);
        ZombieInstance zombie = Assert.Single((IEnumerable<ZombieInstance>)h.System.Zombies);
        zombie.Speciality = EZombieSpeciality.Normal;
        zombie.Position = zombie.Home = new Vector3(8, 10, 0);
        zombie.Yaw = 90f; // face -X, directly toward the player at the origin
        int prematureQueries = 0;
        h.System.PathReady = () => false;
        h.System.PathQuery = (from, to, path, radius) =>
        {
            prematureQueries++;
            return false;
        };

        (_, _, List<List<ZombieSnapshotState>> batches) = Join(h, "A");
        Pump(h, 8);

        Assert.Equal(EZombieState.Chase, zombie.State);
        Assert.True(zombie.Position.X < 6f, $"authoritative zombie froze at {zombie.Position}");
        Assert.Equal(0, prematureQueries); // do not hammer an engine map that is still publishing
        Assert.Contains(batches.SelectMany(b => b),
            state => state.Id == zombie.Id && state.State == EZombieState.Chase && state.Position.X < 8f);
    }

    [Fact]
    public void PartialNavmeshRoute_CrossesAGraphSeamAndReplicatesTheAttack()
    {
        var h = new Harness();
        h.Populate(4);
        ZombieInstance zombie = Assert.Single((IEnumerable<ZombieInstance>)h.System.Zombies);
        zombie.Speciality = EZombieSpeciality.Normal;
        zombie.Position = zombie.Home = new Vector3(8, 10, 0);
        zombie.Yaw = 90f; // face -X, toward the player at the origin
        h.System.PathReady = () => true;
        h.System.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(new Vector3(4, 10, 0)); // the player's navmesh island begins beyond this edge
            return true;
        };

        (_, _, List<List<ZombieSnapshotState>> batches) = Join(h, "A");
        Pump(h, 80);

        Assert.True(zombie.State == EZombieState.Attack,
            $"expected attack after graph seam, got {zombie.State} at {zombie.Position}");
        Assert.True(zombie.Position.X < 2f, $"authoritative zombie stopped at graph seam: {zombie.Position}");
        Assert.Contains(batches.SelectMany(b => b), state =>
            state.Id == zombie.Id && state.State == EZombieState.Chase && state.Position.X < 8f);
        Assert.Contains(batches.SelectMany(b => b), state =>
            state.Id == zombie.Id && state.State == EZombieState.Attack);
    }

    [Fact]
    public void PartialNavmeshRoute_WithARealWallNeverReplicatesMovementThroughIt()
    {
        var h = new Harness();
        h.Populate(4);
        ZombieInstance zombie = Assert.Single((IEnumerable<ZombieInstance>)h.System.Zombies);
        zombie.Speciality = EZombieSpeciality.Normal;
        zombie.Position = zombie.Home = new Vector3(8, 10, 0);
        zombie.Yaw = 90f;
        h.System.PathReady = () => true;
        h.System.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(new Vector3(4, 10, 0));
            return true;
        };
        h.System.MoveResolver = (from, to, radius) =>
            new Vector3(MathF.Max(to.X, 4f), to.Y, to.Z);

        (_, _, List<List<ZombieSnapshotState>> batches) = Join(h, "A");
        Pump(h, 60);

        Assert.Equal(EZombieState.Chase, zombie.State);
        Assert.InRange(zombie.Position.X, 3.99f, 4.01f);
        List<ZombieSnapshotState> snapshots = batches.SelectMany(batch => batch).ToList();
        Assert.DoesNotContain(snapshots, state => state.Id == zombie.Id && state.Position.X < 3.99f);
        Assert.DoesNotContain(snapshots, state => state.Id == zombie.Id && state.State == EZombieState.Attack);
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

        // Every player disconnects: with no target left the zombie gives up (leave: stand for the
        // 3-6 s delay, walk to the retreat point, settle) and eventually falls silent.
        foreach (LoopbackClientTransport transport in h.Transports)
            transport.Close();
        h.Clients.Clear();
        Pump(h, 160); // > 6 s delay + the retreat walk at 12.5 Hz
        Assert.Equal(EZombieState.Idle, zombie.State);
        Assert.Equal(byte.MaxValue, zombie.TargetPlayer);
        Assert.Equal(0f, zombie.LeaveDelay);
    }

    [Fact]
    public void IdleTransition_SendsOneFinalSnapshotThenSilence()
    {
        var h = new Harness();
        h.Populate(4);
        ZombieInstance zombie = Assert.Single((IEnumerable<ZombieInstance>)h.System.Zombies);

        (_, _, List<List<ZombieSnapshotState>> batches) = Join(h, "A");
        Pump(h, 2); // admitted first, so the client actually witnesses the edge

        // Wake the zombie a couple of meters from its retreat point: it walks there streaming
        // Return states, then the host must replicate exactly the Return -> Idle edge and go quiet.
        zombie.State = EZombieState.Return;
        zombie.LeaveTo = zombie.Position;
        zombie.Position = zombie.Position + new Vector3(2f, 0, 0);
        Pump(h, 10);
        List<ZombieSnapshotState> flat = batches.SelectMany(b => b).ToList();
        Assert.Contains(flat, s => s.Id == zombie.Id && s.State == EZombieState.Return);
        Assert.Contains(flat, s => s.Id == zombie.Id && s.State == EZombieState.Idle);

        batches.Clear();
        Pump(h, 5); // steady idle afterwards: zero bandwidth
        Assert.Empty(batches);
    }

    [Fact]
    public void StatesOnlyReachPlayersStandingInTheZombiesRegion()
    {
        // GatherRemoteClientConnections: the whole map wakes, but a client in bound 0 hears only
        // bound 0's zombies — the distant region's traffic never reaches it.
        var h = new Harness(boundCount: 2);
        h.Populate(600); // ceil(600 * 0.25) = 150 per bound = 300 zombies
        Assert.Equal(300, h.System.Zombies.Count);

        (_, List<ZombieListing> zoo, List<List<ZombieSnapshotState>> batches) = Join(h, "A");
        Pump(h, 2); // admitted first, so the wake-up below is actually witnessed
        Assert.Equal(150, zoo.Count); // the join shipped ONLY the spawn region's list

        foreach (ZombieInstance z in h.System.Zombies)
        {
            // Wake the entire map at once, several meters from their retreat point so everyone
            // keeps walking.
            z.State = EZombieState.Return;
            z.LeaveTo = z.Position + new Vector3(0, 0, 30f);
        }
        Pump(h, 1);

        var heard = batches.SelectMany(b => b).Select(s => s.Id).Distinct().ToHashSet();
        Assert.Equal(150, heard.Count);
        var region0 = h.System.ZombiesInBound(0).Select(z => z.Id).ToHashSet();
        Assert.True(heard.SetEquals(region0));
    }

    [Fact]
    public void PartialDisconnect_DropsOnlyTheGonePlayersRegionState()
    {
        var h = new Harness();
        h.Populate(4);
        (_, _, _) = Join(h, "A");
        (_, _, _) = Join(h, "B");
        Pump(h, 3);

        h.Transports[1].Close(); // B drops; A stays and keeps receiving its region normally
        Pump(h, 3);
        Assert.True(h.Clients[0].Joined);
    }

    [Fact]
    public void LeavingEveryBound_SendsNothingAndReturnResends()
    {
        // A tiny bound around the spawn with empty space east of it: walking out lands the player in
        // no region at all (bound 255), and walking back must resend the region's list.
        var h = new Harness(new List<NavBound>
        {
            new() { Center = new Vector3(0, 140, 0), Size = new Vector3(20, 300, 400), MaxZombies = 255 },
        });
        h.System.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(4, 10, 0)) }, new Random(3));
        Assert.Single(h.System.Zombies);
        ZombieInstance zombie = h.System.Zombies[0];
        zombie.Position = new Vector3(-8, 10, 100); // parked away from the walking lane: no aggro
        zombie.Home = zombie.Position;

        (NetClient client, List<ZombieListing> zoo, _) = Join(h, "A");
        Pump(h, 3);
        Assert.Single(zoo); // the spawn region arrived

        uint i = 1;
        for (; i <= 60; i++) // sprint east past x=10: outside every bound
        {
            client.SendInput(new InputCommand(i, 1, 0, false, true, NetAngles.QuantizeYaw(0f), 90));
            Pump(h, 1);
        }
        Assert.Single(zoo); // no bound entered -> nothing was resent

        for (; i <= 200 && zoo.Count == 1; i++) // sprint back west into the bound
        {
            client.SendInput(new InputCommand(i, -1, 0, false, true, NetAngles.QuantizeYaw(0f), 90));
            Pump(h, 1);
        }
        Assert.Equal(2, zoo.Count); // the return resent the region's list
    }

    [Fact]
    public void CrossingIntoANewBound_ShipsThatRegionsList()
    {
        // Two small adjacent bounds around the spawn: sprinting +X crosses from bound 0 into bound 1
        // in a few metres, and the server must ship bound 1's zombies to that client alone.
        var h = new Harness(new List<NavBound>
        {
            new() { Center = new Vector3(0, 140, 0), Size = new Vector3(20, 300, 400), MaxZombies = 255 },
            new() { Center = new Vector3(120, 140, 0), Size = new Vector3(220, 300, 400), MaxZombies = 255 },
        });
        // 4 spawnpoints in bound 1 (x >= 20), none in bound 0.
        var points = new List<ZombieSpawnpointData>();
        for (int i = 0; i < 4; i++)
            points.Add(new ZombieSpawnpointData(0, new Vector3(60 + (i * 10), 10, 0)));
        h.System.Spawn(points, new Random(11));
        Assert.Single(h.System.Zombies);

        (NetClient client, List<ZombieListing> zoo, _) = Join(h, "A");
        Pump(h, 3);
        Assert.Empty(zoo); // spawn bound (0) has no zombies: its list is empty

        // Sprint east until past x = 10 (bound 0's edge): the client enters bound 1.
        for (uint i = 1; i <= 40 && zoo.Count == 0; i++)
        {
            client.SendInput(new InputCommand(i, 1, 0, false, true, NetAngles.QuantizeYaw(0f), 90));
            Pump(h, 1);
        }
        Assert.Single(zoo);
        Assert.Equal(h.System.Zombies[0].Id, zoo[0].Id);
    }
}
