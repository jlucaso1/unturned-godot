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
    private byte _nextPlayerId = 1;
    private double _nextTick = double.NaN;

    public int PlayerCount { get; private set; }

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
                    HandleMessage(evt.Connection, evt.Payload);
                    break;
                case ETransportEvent.Disconnected:
                    HandleDisconnect(evt.Connection);
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
            _nextTick += ServerSimulation.TickRate;
        }
    }

    private void HandleMessage(ITransportConnection connection, byte[] payload)
    {
        if (!_sessions.TryGetValue(connection, out Session? session))
            return;

        switch (NetMessages.TypeOf(payload))
        {
            case ENetMessage.Hello when !session.Joined:
                {
                    (byte version, string name) = NetMessages.ReadHello(payload);
                    if (version != NetMessages.ProtocolVersion)
                        connection.Close(); // incompatible build: refuse cleanly instead of mis-parsing frames
                    else
                        AdmitPlayer(connection, session, name);
                    break;
                }
            case ENetMessage.Input when session.Joined:
                _simulation.QueueInput(session.PlayerId, NetMessages.ReadInput(payload));
                break;
        }
    }

    private void AdmitPlayer(ITransportConnection connection, Session session, string name)
    {
        session.PlayerId = _nextPlayerId++;
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
    }

    private void HandleDisconnect(ITransportConnection connection)
    {
        if (!_sessions.Remove(connection, out Session? session) || !session.Joined)
            return;
        PlayerCount--;
        _simulation.RemovePlayer(session.PlayerId);
        Broadcast(NetMessages.WritePlayerLeft(session.PlayerId), ESendType.Reliable);
    }

    private void Broadcast(byte[] payload, ESendType sendType)
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
