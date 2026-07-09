using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot.Zombies;

// Server-side glue between the ZombieSystem brain and a NetServer, built purely on the extension
// seams: OnTick advances the zombies against the live player states and broadcasts awake zombies;
// OnPlayerAdmitted ships the freshly admitted client the full population in reliable chunks.
public sealed class ZombieHost
{
    private readonly ZombieSystem _system;
    private readonly NetServer _server;
    private readonly List<ZombiePlayerView> _players = new();
    private readonly List<ZombieSnapshotState> _awake = new();
    private readonly Dictionary<ushort, EZombieState> _lastSent = new();

    public ZombieHost(ZombieSystem system, NetServer server)
    {
        _system = system;
        _server = server;
        server.OnTick += Tick;
        server.OnPlayerAdmitted += SendPopulation;
    }

    private void Tick(uint tick)
    {
        _players.Clear();
        _server.ForEachJoinedPlayer((id, state) =>
            _players.Add(new ZombiePlayerView(id, state.Position, state.Stance, state.Moving)));

        _system.Tick(_players, ServerSimulation.TickRate);

        // Replicate every awake zombie, plus one final snapshot when a zombie settles back to idle so
        // clients see it stop; sleeping zombies cost zero bandwidth.
        _awake.Clear();
        foreach (ZombieInstance zombie in _system.Zombies)
        {
            _lastSent.TryGetValue(zombie.Id, out EZombieState last);
            if (zombie.State == EZombieState.Idle && last == EZombieState.Idle)
                continue;
            _lastSent[zombie.Id] = zombie.State;
            _awake.Add(Snapshot(zombie));
            if (_awake.Count == byte.MaxValue)
            {
                _server.Broadcast(ZombieNetMessages.WriteZombieStates(tick, _awake), ESendType.Unreliable);
                _awake.Clear();
            }
        }
        if (_awake.Count > 0)
            _server.Broadcast(ZombieNetMessages.WriteZombieStates(tick, _awake), ESendType.Unreliable);
    }

    private void SendPopulation(byte playerId, ITransportConnection connection)
    {
        var chunk = new List<ZombieListing>(ZombieNetMessages.ListChunkSize);
        foreach (ZombieInstance zombie in _system.Zombies)
        {
            chunk.Add(new ZombieListing
            {
                Id = zombie.Id,
                Type = zombie.Type,
                Speciality = zombie.Speciality,
                Shirt = zombie.Shirt,
                Pants = zombie.Pants,
                Hat = zombie.Hat,
                Gear = zombie.Gear,
                Move = zombie.Move,
                Idle = zombie.Idle,
                Position = zombie.Position,
                Yaw = NetAngles.QuantizeYaw(zombie.Yaw),
            });
            if (chunk.Count == ZombieNetMessages.ListChunkSize)
            {
                connection.Send(ZombieNetMessages.WriteZombieList(chunk), ESendType.Reliable);
                chunk.Clear();
            }
        }
        if (chunk.Count > 0)
            connection.Send(ZombieNetMessages.WriteZombieList(chunk), ESendType.Reliable);
    }

    private static ZombieSnapshotState Snapshot(ZombieInstance zombie) => new()
    {
        Id = zombie.Id,
        Position = zombie.Position,
        Yaw = NetAngles.QuantizeYaw(zombie.Yaw),
        State = zombie.State,
    };
}
