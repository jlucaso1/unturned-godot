using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;

namespace UnturnedGodot.Zombies;

// Server-side glue between the ZombieSystem brain and a NetServer, replicating REGIONALLY the way
// ZombieManager does: a region's full zombie list ships (reliably) only to a connection whose player
// just entered its nav bound (onBoundUpdated -> SendZombiesToPlayer, guarded per player by
// isZombiesLoaded so re-entries resend and oscillation doesn't), and per-tick state snapshots go only
// to the connections standing in the zombie's own region (GatherRemoteClientConnections). A zombie
// that chases a player across a border keeps replicating to its HOME region — the player who left
// simply stops hearing about it, exactly like the original.
public sealed class ZombieHost
{
    private readonly ZombieSystem _system;
    private readonly NetServer _server;
    private readonly Dictionary<byte, byte> _playerBounds = new(); // each player's current nav bound
    private readonly List<byte> _gone = new();
    private readonly List<ZombiePlayerView> _views = new();
    private readonly List<(byte Player, ITransportConnection Connection)> _connections = new();
    private readonly Dictionary<ushort, EZombieState> _lastSent = new();
    private readonly List<ZombieSnapshotState>[] _awakeByBound;
    private readonly List<ZombieListing> _chunk = new(ZombieNetMessages.ListChunkSize);
    private readonly System.Action<byte, PlayerMoveState, ITransportConnection> _collectPlayer; // cached closure

    public ZombieHost(ZombieSystem system, NetServer server)
    {
        _system = system;
        _server = server;
        _awakeByBound = new List<ZombieSnapshotState>[system.BoundCount];
        for (int i = 0; i < _awakeByBound.Length; i++)
            _awakeByBound[i] = new List<ZombieSnapshotState>();
        _collectPlayer = CollectPlayer;
        server.OnTick += Tick;
        // A (re)admitted player starts from scratch: the self-healing rejoin implies the client lost
        // state (and dropped its avatars), so clearing our tracking makes the next tick resend the
        // region it stands in — the counterpart of onPlayerCreated resetting loadedBounds.
        server.OnPlayerAdmitted += (id, _) => _playerBounds.Remove(id);
    }

    private void CollectPlayer(byte id, PlayerMoveState state, ITransportConnection connection)
    {
        _views.Add(new ZombiePlayerView(id, state.Position, state.Stance, state.Moving));
        _connections.Add((id, connection));

        // PlayerMovement.updateBounds -> ZombieManager.onBoundUpdated: entering a region ships its
        // full list to this connection alone. (The original's per-player isZombiesLoaded guard is
        // always cleared on exit, so a plain send-on-entry is the same behavior: returns resend.)
        byte newBound = _system.BoundOf(state.Position);
        if (_playerBounds.TryGetValue(id, out byte bound) && bound == newBound)
            return;
        if (newBound != LevelNavigationData.NoBound)
            SendRegion(connection, newBound);
        _playerBounds[id] = newBound;
    }

    private void Tick(uint tick)
    {
        _views.Clear();
        _connections.Clear();
        _server.ForEachJoinedConnection(_collectPlayer);

        // Players that vanished from the roster disconnected: drop their region state.
        if (_playerBounds.Count > _connections.Count)
        {
            _gone.Clear();
            foreach (byte id in _playerBounds.Keys)
            {
                bool present = false;
                for (int i = 0; i < _connections.Count && !present; i++)
                    present = _connections[i].Player == id;
                if (!present)
                    _gone.Add(id);
            }
            foreach (byte id in _gone)
                _playerBounds.Remove(id);
        }

        _system.Tick(_views, ServerSimulation.TickRate);

        // Replicate every awake zombie (plus one final snapshot when it settles back to idle so clients
        // see it stop), grouped by the zombie's home region and sent only to that region's connections.
        foreach (List<ZombieSnapshotState> states in _awakeByBound)
            states.Clear();
        foreach (ZombieInstance zombie in _system.Zombies)
        {
            _lastSent.TryGetValue(zombie.Id, out EZombieState last);
            if (zombie.State == EZombieState.Idle && last == EZombieState.Idle)
                continue;
            _lastSent[zombie.Id] = zombie.State;
            _awakeByBound[zombie.Bound].Add(Snapshot(zombie));
        }

        // One message per region (a region holds at most 255 zombies — MaxZombies is a byte — so the
        // count always fits the payload's byte header), sent to that region's connections only.
        for (int bound = 0; bound < _awakeByBound.Length; bound++)
        {
            List<ZombieSnapshotState> states = _awakeByBound[bound];
            if (states.Count == 0)
                continue;
            byte[] payload = ZombieNetMessages.WriteZombieStates(tick, states);
            foreach ((byte player, ITransportConnection connection) in _connections)
                if (_playerBounds[player] == bound)
                    connection.Send(payload, ESendType.Unreliable);
        }
    }

    // SendZombies: the region's complete zombie list, reliable, to one connection, in MTU-sized chunks.
    private void SendRegion(ITransportConnection connection, byte bound)
    {
        _chunk.Clear();
        foreach (ZombieInstance zombie in _system.ZombiesInBound(bound))
        {
            _chunk.Add(new ZombieListing
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
            if (_chunk.Count == ZombieNetMessages.ListChunkSize)
            {
                connection.Send(ZombieNetMessages.WriteZombieList(bound, _chunk), ESendType.Reliable);
                _chunk.Clear();
            }
        }
        if (_chunk.Count > 0)
            connection.Send(ZombieNetMessages.WriteZombieList(bound, _chunk), ESendType.Reliable);
    }

    private static ZombieSnapshotState Snapshot(ZombieInstance zombie) => new()
    {
        Id = zombie.Id,
        Position = zombie.Position,
        Yaw = NetAngles.QuantizeYaw(zombie.Yaw),
        State = zombie.State,
    };
}
