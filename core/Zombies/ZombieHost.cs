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
    // Deaths waiting to go out, by the region the zombie belonged to. Filled by whatever damaged it —
    // which runs on the same OnTick this does, and may run after it — and drained at the START of the
    // next tick, so a kill is announced exactly once and never races the snapshot pass below.
    // One list of PAIRS rather than two lists kept in step by hand: the payload writes an id and the
    // shove that goes with it, and two collections that could ever differ in length is a corpse thrown
    // by another corpse's blow.
    private readonly Dictionary<byte, List<(ushort Id, Godot.Vector3 Ragdoll)>> _pendingKills = new();
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
        // Subscribed here rather than left to the caller: a stun that the brain decides on and nobody
        // replicates is a zombie frozen on the server and walking on every client, which is worse than no
        // stun at all. Wiring it to the host that already owns the zombie wire is what makes that
        // impossible to forget.
        system.Stunned += ReportStunned;
        // The nav bounds are the only division of the map either side has, and this host already
        // computes one per player per tick for its own replication. Handing the same function to the
        // server lets the PLAYER snapshot stream be filtered by region too — it was the last broadcast
        // still going to every connection regardless of distance. See NetServer.RegionOf.
        server.RegionOf = system.BoundOf;
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

    // Announces a zombie's death to its region on the next tick, and forgets the state tracking that
    // would otherwise keep an id alive in _lastSent for the rest of the session. The zombie is already
    // out of the population by the time this is called — ZombieSystem.Damage removes it — so this is
    // purely the replication half of the same event.
    // `ragdoll` is the shove the killing blow gave the body — DamageTool.damageZombie's own vector,
    // which askDamage hands to sendZombieDead. Zero for a death with no direction behind it.
    public void ReportKilled(ZombieInstance zombie, Godot.Vector3 ragdoll = default)
    {
        System.ArgumentNullException.ThrowIfNull(zombie);
        _lastSent.Remove(zombie.Id);
        if (!_pendingKills.TryGetValue(zombie.Bound, out List<(ushort, Godot.Vector3)>? kills))
            _pendingKills[zombie.Bound] = kills = new List<(ushort, Godot.Vector3)>();
        kills.Add((zombie.Id, ragdoll));
    }

    // Zombies staggered since the last tick, by region. Collected from ZombieSystem.Stunned rather than
    // polled, because a stun is an EVENT — the state snapshots carry position and behaviour state, and a
    // zombie that stands still for a second is indistinguishable in them from one that simply stopped.
    public void ReportStunned(ZombieInstance zombie, byte clip)
    {
        System.ArgumentNullException.ThrowIfNull(zombie);
        if (!_pendingStuns.TryGetValue(zombie.Bound, out List<(ushort, byte)>? stuns))
            _pendingStuns[zombie.Bound] = stuns = new List<(ushort, byte)>();
        stuns.Add((zombie.Id, clip));
    }

    private readonly Dictionary<byte, List<(ushort Id, byte Clip)>> _pendingStuns = new();

    private void Tick(uint tick)
    {
        _views.Clear();
        _connections.Clear();
        _server.ForEachJoinedConnection(_collectPlayer);

        // Deaths first, before this tick's simulation: the population no longer holds them, so nothing
        // below would mention them again, and a client that hears the death before the tick's snapshots
        // never renders one more frame of a zombie that is gone.
        BroadcastKills();
        BroadcastStuns();

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

        _system.AuthoritativeTick = tick; // stamps anything recording alongside the simulation
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
        // count always fits the payload's byte header), sent to that region's connections only. The
        // payload is only serialized once a listener is found: with the map awake and nobody around,
        // a region costs nothing.
        for (int bound = 0; bound < _awakeByBound.Length; bound++)
        {
            List<ZombieSnapshotState> states = _awakeByBound[bound];
            if (states.Count == 0)
                continue;
            List<byte[]>? payload = null;
            foreach ((byte player, ITransportConnection connection) in _connections)
            {
                if (_playerBounds[player] != bound)
                    continue;
                payload ??= ZombieNetMessages.WriteZombieStateChunks(tick, states);
                foreach (byte[] chunk in payload)
                    connection.Send(chunk, ESendType.Unreliable);
            }
        }
    }

    // One reliable payload per region that lost zombies, to that region's connections only — the same
    // addressing the per-tick snapshots use, since a player who cannot see a region was never told the
    // zombie existed.
    private void BroadcastKills()
    {
        if (_pendingKills.Count == 0)
            return;
        foreach ((byte bound, List<(ushort Id, Godot.Vector3 Ragdoll)> kills) in _pendingKills)
        {
            if (kills.Count == 0)
                continue;
            List<byte[]>? payload = null;
            foreach ((byte player, ITransportConnection connection) in _connections)
            {
                if (_playerBounds.GetValueOrDefault(player, LevelNavigationData.NoBound) != bound)
                    continue;
                payload ??= ZombieNetMessages.WriteZombieKilledChunks(bound, kills);
                foreach (byte[] chunk in payload)
                    connection.Send(chunk, ESendType.Reliable);
            }
            kills.Clear();
        }
    }

    // The stuns of one tick, to the region they happened in — the same addressing the kills use, since a
    // player who cannot see a region was never told those zombies existed.
    private void BroadcastStuns()
    {
        if (_pendingStuns.Count == 0)
            return;
        foreach ((byte bound, List<(ushort Id, byte Clip)> stuns) in _pendingStuns)
        {
            if (stuns.Count == 0)
                continue;
            List<byte[]>? payload = null;
            foreach ((byte player, ITransportConnection connection) in _connections)
            {
                if (_playerBounds.GetValueOrDefault(player, LevelNavigationData.NoBound) != bound)
                    continue;
                payload ??= ZombieNetMessages.WriteZombieStunnedChunks(bound, stuns);
                foreach (byte[] chunk in payload)
                    connection.Send(chunk, ESendType.Reliable);
            }
            stuns.Clear();
        }
    }

    // Forgets which region each player has been sent, so the next tick ships the current population
    // again. Loading a bug-repro dump replaces every zombie wholesale — ids, types, clothing, the lot —
    // and the per-tick state snapshots carry none of that: without this the client keeps rendering the
    // avatars of a population that no longer exists.
    public void ResendRegions()
    {
        _playerBounds.Clear();
        _lastSent.Clear();
        // The ids in here belong to the population being replaced. Announcing their deaths after the
        // swap would name zombies the client is about to be told about afresh, under the same ids.
        _pendingKills.Clear();
        // Same reasoning for the stuns: they name zombies of the population being replaced.
        _pendingStuns.Clear();
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
