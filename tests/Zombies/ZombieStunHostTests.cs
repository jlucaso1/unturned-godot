using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

// A stagger travelling from the brain to the clients that can see it.
//
// The brain deciding to stun and nobody hearing about it is the worst of the three failures this feature
// can have: the body is frozen on the server and walking on every screen, and nothing later corrects it —
// the per-tick snapshots carry position and behaviour state, and a zombie standing still for a second is
// indistinguishable in them from one that simply stopped.
public class ZombieStunHostTests
{
    private const string Level = "PEI";
    private static readonly Vector3 Spawn = new(0, 10f, 0);

    private static bool FlatGround(float x, float z, out float y)
    {
        y = 10f;
        return true;
    }

    private sealed class Harness
    {
        public readonly LoopbackServerTransport ServerTransport = new();
        public readonly NetServer Server;
        public readonly ZombieSystem System;
        public readonly ZombieHost Host;
        public readonly List<NetClient> Clients = new();
        public double Now = 5000.0;

        // How many ZombieStunned PAYLOADS arrived, as distinct from how many zombies they named: one
        // tick's staggers ride together.
        public int StunMessages;

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

        public List<(ushort Id, byte Clip)> Join(string name)
        {
            var client = new NetClient(ServerTransport.CreateClient(), name, Level);
            var stuns = new List<(ushort, byte)>();
            client.OnUnhandledMessage = payload =>
            {
                if (NetMessages.TypeOf(payload) != ENetMessage.ZombieStunned)
                    return;
                StunMessages++;
                stuns.AddRange(ZombieNetMessages.ReadZombieStunned(payload).Stuns);
            };
            Clients.Add(client);
            return stuns;
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

        public ZombieInstance Plant(ushort id, float x = 60f)
        {
            System.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(x, 10f, 0)) }, new Random(3));
            ZombieInstance zombie = Assert.Single(System.Zombies);
            zombie.Id = id;
            return zombie;
        }
    }

    // The brain's own event is enough: nothing has to remember to call the host.
    [Fact]
    public void AStunTheBrainDecidesOnReachesTheRegionsClients()
    {
        var h = new Harness();
        ZombieInstance zombie = h.Plant(42);
        List<(ushort Id, byte Clip)> stuns = h.Join("A");
        h.Pump(3);

        h.System.Damage(zombie, 30, byte.MaxValue, Array.Empty<ZombiePlayerView>());
        h.Pump(2);

        Assert.Equal(42, Assert.Single(stuns).Id);
    }

    // Exactly once: the pending list is drained rather than replayed.
    [Fact]
    public void AStunIsAnnouncedOnce()
    {
        var h = new Harness();
        ZombieInstance zombie = h.Plant(42);
        List<(ushort Id, byte Clip)> stuns = h.Join("A");
        h.Pump(3);

        h.System.Stun(zombie);
        h.Pump(10);

        Assert.Single(stuns);
        Assert.Equal(1, h.StunMessages);
    }

    // Two staggers in one region ride one payload, the same way two deaths do.
    [Fact]
    public void TwoStunsInOneRegionShareAPayload()
    {
        var h = new Harness();
        ZombieInstance first = h.Plant(1);
        var second = new ZombieInstance
        {
            Id = 2,
            Bound = 0,
            Position = new Vector3(70f, 10f, 0),
            Health = 100,
            MaxHealth = 100,
        };
        var state = new ZombieSystemState();
        state.Zombies.Add(first);
        state.Zombies.Add(second);
        h.System.RestoreState(state);

        List<(ushort Id, byte Clip)> stuns = h.Join("A");
        h.Pump(3);

        h.System.Stun(first);
        h.System.Stun(second);
        h.Pump(2);

        Assert.Equal(new ushort[] { 1, 2 }, stuns.ConvertAll(s => s.Id));
        Assert.Equal(1, h.StunMessages);
    }

    [Fact]
    public void NoStunsMeansNoMessages()
    {
        var h = new Harness();
        h.Plant(42);
        List<(ushort Id, byte Clip)> stuns = h.Join("A");
        h.Pump(10);

        Assert.Empty(stuns);
        Assert.Equal(0, h.StunMessages);
    }

    // The reel the server rolled is what the client is told to play — a client picking its own would have
    // two players watching the same zombie stagger differently.
    [Fact]
    public void TheServersChosenReelIsWhatTravels()
    {
        var h = new Harness();
        ZombieInstance zombie = h.Plant(42);
        var chosen = new List<byte>();
        h.System.Stunned += (_, clip) => chosen.Add(clip);
        List<(ushort Id, byte Clip)> stuns = h.Join("A");
        h.Pump(3);

        h.System.Stun(zombie);
        h.Pump(2);

        Assert.Equal(Assert.Single(chosen), Assert.Single(stuns).Clip);
    }

    // A player standing in another region never heard the zombie existed, so it is not told about its
    // stagger either — the same addressing the kills and the snapshots use.
    [Fact]
    public void AStunIsNotSentToAPlayerOutsideTheRegion()
    {
        var h = new Harness();
        ZombieInstance zombie = h.Plant(42);
        List<(ushort Id, byte Clip)> stuns = h.Join("A");
        h.Pump(3);

        // Move the player clean out of every bound, so the host stops counting them as inside one.
        h.Server.OnTick += _ => { };
        zombie.Bound = 3; // a region no player is standing in
        h.System.Stun(zombie);
        h.Pump(3);

        Assert.Empty(stuns);
    }

    // Reloading a repro dump replaces the whole population, so a stagger still queued names a zombie that
    // is about to stop existing under that id.
    [Fact]
    public void ResendingRegionsDropsTheQueuedStuns()
    {
        var h = new Harness();
        ZombieInstance zombie = h.Plant(42);
        List<(ushort Id, byte Clip)> stuns = h.Join("A");
        h.Pump(3);

        h.System.Stun(zombie);
        h.Host.ResendRegions();
        h.Pump(3);

        Assert.Empty(stuns);
    }
}
