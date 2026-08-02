using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Net;

// The authoritative server loop over any IServerTransport: accepts Hello handshakes, queues client
// inputs into the simulation, steps it at the fixed 12.5 Hz cadence and broadcasts everyone's state —
// Unturned's Provider + PlayerInput server side, reduced to movement. Dedicated servers run this over
// UDP; a listen server ("open to LAN") runs the same instance over a composite of the host's loopback
// and the LAN transport.
public sealed class NetServer
{
    private sealed class Session
    {
        public byte PlayerId;
        public string Name = string.Empty;
        public bool Joined;
    }

    private readonly IServerTransport _transport;
    private readonly ServerSimulation _simulation;
    private readonly Vector3 _spawnPosition;
    private readonly Dictionary<ITransportConnection, Session> _sessions = new();
    private readonly PlayerIdPool _playerIds = new();
    private double _nextTick = double.NaN;

    public int PlayerCount { get; private set; }

    // How many more players this server can admit. Zero means the next Hello is refused; see PlayerIdPool
    // for why the ceiling is 254 rather than 256.
    public int FreePlayerSlots => _playerIds.Available;

    // Extension seams for replicated systems (zombies, resources, doors): hook the fixed tick to run
    // server logic and use Broadcast to ship your own ENetMessage; hook OnPlayerAdmitted to send a
    // freshly admitted (or re-admitted) player your system's full state, the way Welcome carries the
    // player roster. No NetServer edits required.
    public Action<uint>? OnTick;
    public Action<byte, ITransportConnection>? OnPlayerAdmitted;

    // Every joined player id with its live simulation state and connection — what a replicated system
    // ticks against; the connection lets it address specific players, the way Unturned's per-region
    // replication (GatherRemoteClientConnections) sends zombie payloads only to the connections whose
    // player stands in the region.
    public void ForEachJoinedConnection(Action<byte, PlayerMoveState, ITransportConnection> visit)
    {
        foreach ((ITransportConnection connection, Session session) in _sessions)
            if (session.Joined)
                visit(session.PlayerId, _simulation.GetState(session.PlayerId), connection);
    }

    public NetServer(IServerTransport transport, ServerSimulation simulation, Vector3 spawnPosition)
    {
        _transport = transport;
        _simulation = simulation;
        _spawnPosition = spawnPosition;
    }

    public void Update(double now)
    {
        _transport.Update(now);

        while (_transport.TryReceive(out ServerTransportEvent evt))
        {
            switch (evt.Type)
            {
                case ETransportEvent.Connected:
                    _sessions[evt.Connection] = new Session();
                    break;
                case ETransportEvent.Message:
                    HandleMessage(evt.Connection, evt.Payload, now);
                    break;
                case ETransportEvent.Disconnected:
                    HandleDisconnect(evt.Connection, now);
                    break;
            }
        }

        if (double.IsNaN(_nextTick))
            _nextTick = now;
        while (now >= _nextTick)
        {
            List<PlayerSnapshotState> states = _simulation.Step();
            if (states.Count > 0)
                Broadcast(NetMessages.WriteStateUpdate(_simulation.Tick, states), ESendType.Unreliable);
            OnTick?.Invoke(_simulation.Tick);
            _nextTick += ServerSimulation.TickRate;
        }
    }

    private void HandleMessage(ITransportConnection connection, byte[] payload, double now)
    {
        if (!_sessions.TryGetValue(connection, out Session? session))
            return;

        switch (NetMessages.TypeOf(payload))
        {
            case ENetMessage.Hello:
                {
                    (byte version, string name) = NetMessages.ReadHello(payload);
                    if (version != NetMessages.ProtocolVersion)
                    {
                        connection.Close(); // incompatible build: refuse cleanly instead of mis-parsing frames
                    }
                    else if (!session.Joined)
                    {
                        if (!AdmitPlayer(connection, session, name, now))
                            connection.Close(); // server full: refuse cleanly, do not invent an id
                    }
                    else
                    {
                        // A joined client re-Helloing lost our state (its state-timeout fired): resend the
                        // Welcome with the current roster — an idempotent rejoin, no duplicate broadcasts.
                        var roster = new List<PlayerListing>();
                        foreach (Session other in _sessions.Values)
                            if (other.Joined && other != session)
                                roster.Add(Listing(other, _simulation.GetState(other.PlayerId)));
                        connection.Send(NetMessages.WriteWelcome(session.PlayerId, _simulation.Tick, roster),
                            ESendType.Reliable);
                        OnPlayerAdmitted?.Invoke(session.PlayerId, connection);
                    }
                    break;
                }
            case ENetMessage.Input when session.Joined:
                _simulation.QueueInput(session.PlayerId, NetMessages.ReadInput(payload));
                break;
        }
    }

    // False when there is no id left to give — the caller refuses the join. Ids come from a pool and go
    // back on disconnect: a bare incrementing byte wrapped after 255 admissions and handed a live
    // player's id to the next joiner, which overwrote their simulation state and then deleted it when
    // the newcomer left, so the admission after that threw out of GetState.
    private bool AdmitPlayer(ITransportConnection connection, Session session, string name, double now)
    {
        if (!_playerIds.TryRent(now, out byte playerId))
            return false;

        session.PlayerId = playerId;
        session.Name = name;
        session.Joined = true;
        PlayerCount++;
        _simulation.AddPlayer(session.PlayerId, _spawnPosition);

        var existing = new List<PlayerListing>();
        foreach (Session other in _sessions.Values)
            if (other.Joined && other != session)
                existing.Add(Listing(other, _simulation.GetState(other.PlayerId)));

        connection.Send(NetMessages.WriteWelcome(session.PlayerId, _simulation.Tick, existing), ESendType.Reliable);

        byte[] joined = NetMessages.WritePlayerJoined(Listing(session, _simulation.GetState(session.PlayerId)));
        foreach ((ITransportConnection conn, Session other) in _sessions)
            if (other.Joined && other != session)
                conn.Send(joined, ESendType.Reliable);

        OnPlayerAdmitted?.Invoke(session.PlayerId, connection);
        return true;
    }

    private void HandleDisconnect(ITransportConnection connection, double now)
    {
        if (!_sessions.Remove(connection, out Session? session) || !session.Joined)
            return;
        PlayerCount--;
        _simulation.RemovePlayer(session.PlayerId);
        _playerIds.Return(session.PlayerId, now);
        Broadcast(NetMessages.WritePlayerLeft(session.PlayerId), ESendType.Reliable);
    }

    public void Broadcast(byte[] payload, ESendType sendType)
    {
        foreach ((ITransportConnection conn, Session session) in _sessions)
            if (session.Joined)
                conn.Send(payload, sendType);
    }

    private static PlayerListing Listing(Session session, in PlayerMoveState state) => new()
    {
        PlayerId = session.PlayerId,
        Name = session.Name,
        Position = state.Position,
        Pitch = NetAngles.QuantizePitch(state.Pitch),
        Yaw = NetAngles.QuantizeYaw(state.Yaw),
        Stance = state.Stance,
    };
}
