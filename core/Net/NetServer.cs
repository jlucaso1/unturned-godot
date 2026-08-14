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

        // The newest input frame number heard from this client, echoed back once per tick so the client
        // can measure its own round trip. Held here rather than read out of the simulation because the
        // simulation discards a stale or refused frame, and what the probe has to report is what
        // ARRIVED — an echo that went quiet whenever a frame was dropped would read as a lost link.
        public bool HasInputFrame;
        public uint LastInputFrame;

        // The four bytes this client must repeat on every Input it sends. Minted on admission and
        // carried by the Welcome; see NetMessages.WriteWelcome for why it exists.
        public uint Token;

        // The region this connection was last told about. A change means it has been told nothing about
        // where it now is, so the next snapshot for that region has to be the whole of it rather than
        // what changed. Starts at NoRegion, so a freshly admitted player is a change by definition.
        public int LastRegion = NoRegion;
    }

    private readonly IServerTransport _transport;
    private readonly ServerSimulation _simulation;
    private readonly Vector3 _spawnPosition;
    private readonly string _levelName;
    private readonly Dictionary<ITransportConnection, Session> _sessions = new();
    private readonly PlayerIdPool _playerIds = new();
    private double _nextTick = double.NaN;

    // Bumped on every membership event — one admission, one departure, one step — and stamped on the
    // Welcome, PlayerJoined and PlayerLeft it produces. Several of those can happen inside one frame,
    // so the simulation tick cannot order them for a client that receives them out of order; this can.
    private uint _rosterVersion;

    // How many 0.08 s steps one Update may run to make up for lost time. Enough to absorb the jitter of
    // a machine that misses a few frames; short of the "replay the whole stall at once" behaviour that
    // turns a hitch into a burst of hundreds of datagrams per client.
    public const int MaxCatchUpTicks = 5; // 0.4 s

    public int PlayerCount { get; private set; }

    // How many more players this server can admit at `now`. Zero means the next Hello is refused. This
    // excludes ids that are free but still quarantined, so it never advertises room the server would then
    // turn away; see PlayerIdPool for the quarantine, and for why the ceiling is 254 rather than 256.
    public int FreePlayerSlotsAt(double now) => _playerIds.AvailableAt(now);

    // Datagrams that reached a decoder and did not survive it. Non-zero means someone is sending the
    // server bytes it cannot read — a mismatched build, a corrupt link, or a probe.
    public long MalformedPacketsDropped { get; private set; }

    // Everything this server's links have cost, split by message type, plus the transport's own drop
    // counters. Surfaced here because the console is deliberately renderer-side and reads the session
    // through NetworkManager: it should not have to know which transport a listen server is composed of
    // to answer "what is this server sending".
    public NetTraffic Traffic => _transport.Traffic;

    // Trusted positions the simulation refused for not being finite. Reads through to the simulation so
    // the counters that describe a sick session are all reachable from one place.
    public long RejectedPositions => _simulation.RejectedPositions;

    // Input frames that named a live session but carried the wrong token. Non-zero means somebody is
    // sending this port frames for a player they are not — a spoofer, or a client that kept talking
    // across a re-admission it did not notice.
    public long UnauthenticatedInputsDropped { get; private set; }

    // Transport events handled per Update. Comfortably above what a full server generates at its own
    // cadence, so it bounds a flood without shaping normal traffic.
    public const int MaxEventsPerUpdate = 512;

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

    // The hand animations this tick produced, for a replicated system that has to act on one rather
    // than merely relay it: a punch is announced to the other clients from Update, and what the swing
    // DID — who it hit and for how much — is decided by whoever hooks OnTick and reads this. Refilled
    // by every step, so it is only meaningful from inside an OnTick callback.
    public IReadOnlyList<PlayerGestureEvent> Gestures => _simulation.Gestures;

    // One player's authoritative state, for a system that needs where they are and where they are
    // looking. TryGet rather than an indexer because a gesture can outlive the player who threw it by a
    // tick — the disconnect is handled between the step and the callback.
    public bool TryGetPlayerState(byte id, out PlayerMoveState state) =>
        _simulation.TryGetState(id, out state);

    // The level this server runs, by folder name. Not optional: a server always hosts one specific
    // world, and a handshake that cannot name it is how two players ended up walking different maps.
    public string LevelName => _levelName;

    // The authoritative tick the simulation is on, so anything recording alongside it (the bug-repro
    // harness) can stamp its window with the same numbers the server's own logs carry.
    public uint Tick => _simulation.Tick;

    // Stands a player somewhere outright — see ServerSimulation.Teleport. Loading a repro dump into a
    // running session is the only caller: it puts the reporter back where the dump was taken.
    public bool Teleport(byte id, Vector3 position) => _simulation.Teleport(id, position);

    // With the stance and movement the dump recorded: both decide how far away a player is noticed.
    public bool Teleport(byte id, Vector3 position, Player.EPlayerStance stance, bool moving) =>
        _simulation.Teleport(id, position, stance, moving);

    public NetServer(IServerTransport transport, ServerSimulation simulation, Vector3 spawnPosition,
        string levelName)
    {
        _transport = transport;
        _simulation = simulation;
        _spawnPosition = spawnPosition;
        _levelName = levelName;
        // The pre-handshake question a stranger is allowed to ask, answered without the transport
        // allocating anything for them. See IServerTransport.AnswerConnectionless.
        _transport.AnswerConnectionless = AnswerConnectionless;
    }

    // What a peer with no connection may be told, which is exactly one thing: which map this is and how
    // full it is. Anything else returns null and falls through to the ordinary connection path, so this
    // widens nothing — it only moves the cheapest and most common pre-join exchange off the table that
    // was exhaustible.
    private byte[]? AnswerConnectionless(byte[] payload)
    {
        if (payload.Length == 0 || (ENetMessage)payload[0] != ENetMessage.ServerInfoRequest)
            return null;
        return NetMessages.WriteServerInfo(_levelName, PlayerCount, FreePlayerSlotsAt(_now));
    }

    // The clock of the last Update, for the connectionless answer above: it is served from inside the
    // transport's pump, which has no `now` of its own to hand over.
    private double _now;

    public void Update(double now)
    {
        _now = now;
        _transport.Update(now);

        // Drain what is queued, but not without limit: how much work this loop does was decided entirely by
        // how much anyone chose to send, and it runs on the frame thread. Anything left over is still
        // queued in the transport and drains next Update, so a burst is spread rather than dropped.
        int budget = MaxEventsPerUpdate;
        while (budget-- > 0 && _transport.TryReceive(out ServerTransportEvent evt))
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

        // Time the budget below cannot make up is dropped HERE, before any step runs, so the steps that
        // do run carry recent instants. Dropping it afterwards instead left them stamped with the
        // moment the stall began: the claims still in the buffer describe where the player got to
        // DURING the stall, and judged against a third of a second of budget every one of them was
        // refused — the avatar sitting at its pre-stall position until the next frame arrived. The gap
        // is still credited exactly once, to the first step that runs.
        double unmakeable = now - _nextTick - (MaxCatchUpTicks * ServerSimulation.TickRate);
        if (unmakeable > 0)
            _nextTick = now - ((MaxCatchUpTicks - 1) * ServerSimulation.TickRate);

        int caughtUp = 0;
        while (now >= _nextTick && caughtUp < MaxCatchUpTicks)
        {
            // Each step is stamped with the instant it was SCHEDULED for, not with the clock reading of
            // the frame running it. The two differ whenever one frame makes up several ticks, and the
            // trusted-position budget is a speed limit: handing every step of a late frame the same
            // reading pays each of them a fresh minimum tick of movement on top of the one real gap,
            // which on a persistently late server is a standing speed bonus. Scheduled instants are
            // monotonic, one TickRate apart, and after a stall the re-anchor below leaves a gap the
            // size of the stall — so the time that really passed is credited exactly once.
            double stepAt = _nextTick;
            _nextTick += ServerSimulation.TickRate;
            List<PlayerSnapshotState> states = _simulation.Step(stepAt);
            BroadcastStates(states);
            // Everyone but the player who threw it: the owner started its own animation on the frame the
            // button went down, and playing this too would restart the swing a round-trip later.
            foreach (PlayerGestureEvent gesture in _simulation.Gestures)
                BroadcastExcept(gesture.PlayerId,
                    NetMessages.WritePlayerGesture(gesture.PlayerId, _simulation.Tick, gesture.Gesture), ESendType.Reliable);
            SendCorrections();
            SendInputEchoes();
            OnTick?.Invoke(_simulation.Tick);
            caughtUp++;
        }

        // Whatever is left of the gap is dropped rather than replayed, and the clock comes back to the
        // present. A host stalls for real reasons — the world streamer finishing, a navmesh reconcile,
        // a laptop lid — and replaying a minute of ticks inside one frame floods every client with
        // hundreds of datagrams, jumps the zombies a minute along their paths, and makes the very frame
        // that is already late do all of it. Left behind instead, the loop would spend its full budget
        // on every following frame and never catch up at all.
        if (now >= _nextTick)
            _nextTick = now + ServerSimulation.TickRate;
    }

    private void HandleMessage(ITransportConnection connection, byte[] payload, double now)
    {
        if (!_sessions.TryGetValue(connection, out Session? session))
            return;

        // Update runs inside _PhysicsProcess, so an exception escaping here ends the process, and anyone
        // who can reach the port can send bytes that do not decode. Every decode below therefore goes
        // through TryDecode — and nothing else does: the simulation, the roster and OnPlayerAdmitted are
        // our own code, and a fault there is a defect that must surface, not another dropped packet.
        if (!MalformedPacket.TryDecode(payload, ReadType, out ENetMessage type))
        {
            MalformedPacketsDropped++;
            return;
        }

        switch (type)
        {
            // Answerable before (and without) joining: this is the pre-flight a client runs to learn
            // which level to build, so it must work on a connection that has said nothing else.
            //
            // And answered UNRELIABLY, precisely because it is answerable by anyone. A reliable reply is
            // retained per connection until acked or GiveUpAfter, and Update retransmits the whole set
            // every ResendInterval — so an unauthenticated sender that asks and never acks makes the
            // server hold and re-send on its behalf. Across the 256 connections the transport now allows,
            // stopping just short of the pending cap held a quarter of a million frames and turned each
            // 0.25 s into roughly a million sends: the server attacking whatever address it was aimed at.
            //
            // Nothing is lost by dropping it. ServerQuery re-asks on its own RetryInterval until it is
            // answered or times out, which is what a query does — the reliability belonged to the asker
            // all along, and it is the asker who pays for it.
            case ENetMessage.ServerInfoRequest:
                connection.Send(
                    NetMessages.WriteServerInfo(_levelName, PlayerCount, FreePlayerSlotsAt(now)),
                    ESendType.Unreliable);
                break;
            case ENetMessage.Hello:
                {
                    // The version comes first and alone. Reading the whole Hello up front would make
                    // an older client's shorter one merely "malformed": no refusal, no close, and —
                    // since the transport acks it anyway — a client free to retry that forever.
                    if (!MalformedPacket.TryDecode(payload, ReadHelloVersion, out byte version))
                    {
                        MalformedPacketsDropped++;
                        break;
                    }

                    if (version != NetMessages.ProtocolVersion)
                    {
                        // Incompatible build: refuse cleanly instead of mis-parsing its frames. The
                        // reason is sent anyway — a build that speaks a version we do not know may
                        // still read this message, and one that cannot is no worse off.
                        Refuse(connection, EJoinRejection.ProtocolMismatch);
                        break;
                    }

                    if (!MalformedPacket.TryDecode(payload, ReadHello,
                        out (byte Version, string Name, string Level) hello))
                    {
                        MalformedPacketsDropped++;
                        break;
                    }

                    (_, string name, string level) = hello;
                    if (!NetMessages.LevelsMatch(level, _levelName))
                    {
                        // The reported bug, refused at its source: this client built another world.
                        // The reason carries our level, so the client can say which map to load
                        // instead of leaving the player to guess.
                        Refuse(connection, EJoinRejection.LevelMismatch);
                    }
                    else if (!session.Joined)
                    {
                        if (!AdmitPlayer(connection, session, name, now))
                            Refuse(connection, EJoinRejection.ServerFull); // do not invent an id
                    }
                    else
                    {
                        // A joined client re-Helloing lost our state (its state-timeout fired): resend the
                        // Welcome with the current roster — an idempotent rejoin, no duplicate broadcasts.
                        var roster = new List<PlayerListing>();
                        foreach (Session other in _sessions.Values)
                            if (other.Joined && other != session)
                                roster.Add(Listing(other, _simulation.GetState(other.PlayerId)));
                        foreach (byte[] chunk in NetMessages.WriteWelcomeChunks(session.PlayerId,
                            _simulation.Tick, _rosterVersion, roster, session.Token))
                        {
                            connection.Send(chunk, ESendType.Reliable);
                        }
                        OnPlayerAdmitted?.Invoke(session.PlayerId, connection);
                    }
                    break;
                }
            case ENetMessage.Input when session.Joined:
                // The session token first, and before anything else about the frame is decoded.
                //
                // A connection is keyed by (address, port) alone, so anyone who guessed a client's
                // ephemeral port could inject Input frames AS that player — walk them around inside the
                // speed budget, spend their punch allowance — with nothing secret to forge. Checking the
                // token here costs a four-byte read at a fixed offset and makes that a number they were
                // never told as well as a port they guessed.
                if (!MalformedPacket.TryDecode(payload, ReadInputToken, out uint token))
                {
                    MalformedPacketsDropped++;
                    break;
                }
                if (token != session.Token)
                {
                    UnauthenticatedInputsDropped++;
                    break;
                }

                // Dated by when it ARRIVED, not by the last tick: the swing rate limit is measured in
                // real seconds, and a stall is exactly when the two stop being the same thing.
                if (MalformedPacket.TryDecode(payload, ReadInput, out InputCommand input))
                {
                    // Newest-wins, wrap-safe: UDP reorders, and echoing back an older frame number than
                    // one already echoed would show up on the client as a round trip that went backwards.
                    if (!session.HasInputFrame
                        || unchecked((int)(input.Frame - session.LastInputFrame)) > 0)
                    {
                        session.HasInputFrame = true;
                        session.LastInputFrame = input.Frame;
                    }
                    _simulation.QueueInput(session.PlayerId, input, now);
                }
                else
                {
                    MalformedPacketsDropped++;
                }
                break;
        }
    }

    // Says why, then hangs up. The send dispatches the datagram immediately, so the reason is on the
    // wire before the close drops the connection (and with it any retransmission of it). Losing that
    // one datagram is not fatal: the client keeps re-Helloing until it is told, or gives up.
    private void Refuse(ITransportConnection connection, EJoinRejection reason)
    {
        connection.Send(NetMessages.WriteReject(reason, _levelName), ESendType.Reliable);
        connection.Close();
    }

    // Cached so a method group does not allocate a delegate on every received message.
    private static readonly Func<byte[], ENetMessage> ReadType = NetMessages.TypeOf;
    private static readonly Func<byte[], byte> ReadHelloVersion = NetMessages.ReadHelloVersion;
    private static readonly Func<byte[], (byte Version, string Name, string Level)> ReadHello =
        NetMessages.ReadHello;
    private static readonly Func<byte[], InputCommand> ReadInput = NetMessages.ReadInput;
    private static readonly Func<byte[], uint> ReadInputToken = NetMessages.ReadInputSessionToken;

    // False when there is no id left to give — the caller refuses the join. Ids come from a pool and go
    // back on disconnect: a bare incrementing byte wrapped after 255 admissions and handed a live
    // player's id to the next joiner, which overwrote their simulation state and then deleted it when
    // the newcomer left, so the admission after that threw out of GetState.
    private bool AdmitPlayer(ITransportConnection connection, Session session, string name, double now)
    {
        if (!_playerIds.TryRent(now, out byte playerId))
            return false;

        session.PlayerId = playerId;
        session.Name = NetMessages.ClampName(name);
        session.Joined = true;
        session.Token = NextToken();
        PlayerCount++;
        _simulation.AddPlayer(session.PlayerId, _spawnPosition);

        var existing = new List<PlayerListing>();
        foreach (Session other in _sessions.Values)
            if (other.Joined && other != session)
                existing.Add(Listing(other, _simulation.GetState(other.PlayerId)));

        // The admission is itself a membership event, and the roster above predates it: the new player's
        // own Welcome carries the version BEFORE the bump, the PlayerJoined everyone else receives
        // carries the version after. Anyone holding the older roster can then tell that this join is
        // newer than it, rather than deleting a player who has only just arrived.
        // Chunked: a full roster is nine IP fragments as one datagram, retransmitted whole every
        // quarter second until acked. See NetMessages.WriteWelcomeChunks.
        foreach (byte[] chunk in NetMessages.WriteWelcomeChunks(session.PlayerId, _simulation.Tick,
            _rosterVersion, existing, session.Token))
        {
            connection.Send(chunk, ESendType.Reliable);
        }

        _rosterVersion++;
        byte[] joined = NetMessages.WritePlayerJoined(_rosterVersion, _simulation.Tick,
            Listing(session, _simulation.GetState(session.PlayerId)));
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
        _rosterVersion++;
        Broadcast(NetMessages.WritePlayerLeft(_rosterVersion, session.PlayerId), ESendType.Reliable);
    }

    // Which region a position belongs to, for filtering the snapshot stream by interest. Null means one
    // region containing everyone, which is the honest answer for a level with no navigation data — and
    // the exact behaviour this had before.
    //
    // Supplied rather than computed here because the bounds belong to the level, and the server is
    // engine-free and level-agnostic. ZombieHost already computes exactly this per player for its own
    // replication and is what wires it up.
    public Func<Vector3, byte>? RegionOf;

    // The region every player shares when there is nothing to divide them by.
    private const byte OneRegion = 0;

    // Not a region: what a session's last region reads as before it has had one.
    private const int NoRegion = -1;

    // How often a region's snapshot is sent in full rather than as what changed.
    //
    // The delta below is the difference between a standing player costing 16 bytes a tick and costing
    // nothing, which at population is most of the stream. But the stream is unreliable: a client that
    // loses the datagram carrying a player's last movement would otherwise hold them at a stale position
    // until they moved again, which for a player who has stopped is forever. A full send once a second
    // bounds that, and costs one ordinary tick's worth of bytes per second per region.
    public const int FullResyncTicks = 12; // ~0.96 s

    // One region's replication state: who is listening, what was last sent about each player, and when
    // it was last sent in full.
    private sealed class Region
    {
        public readonly List<ITransportConnection> Listeners = new();
        public readonly Dictionary<byte, PlayerSnapshotState> LastSent = new();
        public readonly List<PlayerSnapshotState> Pending = new();
        public uint LastFullTick;
        public bool ForceFull;
    }

    private readonly Dictionary<byte, Region> _regions = new();
    private readonly Dictionary<byte, byte> _playerRegions = new();
    private readonly List<byte> _emptyRegions = new();

    // The state broadcast, filtered by interest and by change.
    //
    // It used to be one payload sent to every connection: every client learned every player's position
    // at 12.5 Hz regardless of distance, and every field every tick regardless of whether it had moved.
    // At the transport's own 254-player ceiling that is 4 KB per datagram times 254 clients times
    // 12.5 Hz — about 12.9 MB/s of egress, every datagram of it IP-fragmented. Zombies had interest
    // management (ZombieHost, by nav bound) and impacts had it; players did not.
    //
    // Two filters, and they compose. The region filter turns O(players^2) into O(players * players in
    // the region), and pays one payload build per REGION rather than one for everyone. The change filter
    // then drops players who are byte-identical to what that region was last told.
    //
    // The change filter is per region rather than per connection, which is what keeps the payload
    // shareable: a per-connection filter would force a fresh serialization per client per tick and give
    // back most of what the region filter just won. Everyone in a region has been sent the same
    // datagrams, so "what this region was last told" is well defined — and a connection that has just
    // arrived forces a full send, so it never inherits a delta it did not receive the base of.
    private void BroadcastStates(List<PlayerSnapshotState> states)
    {
        _playerRegions.Clear();
        foreach (Region region in _regions.Values)
        {
            region.Listeners.Clear();
            region.Pending.Clear();
        }

        for (int i = 0; i < states.Count; i++)
            _playerRegions[states[i].PlayerId] =
                RegionOf is { } of ? of(states[i].Position) : OneRegion;

        foreach ((ITransportConnection conn, Session session) in _sessions)
        {
            if (!session.Joined)
                continue;
            // A player with no state this tick (admitted between the step and here) listens to the
            // region everyone shares, which is also where they will be next tick if there are no bounds.
            byte at = _playerRegions.GetValueOrDefault(session.PlayerId, OneRegion);
            Region region = RegionAt(at);
            region.Listeners.Add(conn);
            if (session.LastRegion == at)
                continue;
            // Crossing a border, or being heard from for the first time: this connection has been told
            // nothing about the region it is now in, so the next payload has to be the whole of it.
            session.LastRegion = at;
            region.ForceFull = true;
        }

        for (int i = 0; i < states.Count; i++)
        {
            PlayerSnapshotState state = states[i];
            Region region = RegionAt(_playerRegions[state.PlayerId]);
            if (region.Listeners.Count == 0)
                continue; // nobody can see this player; serializing them would be work for no reader
            region.Pending.Add(state);
        }

        foreach ((byte at, Region region) in _regions)
        {
            if (region.Listeners.Count == 0)
            {
                // Nobody left. Its delta baseline describes datagrams sent to connections that have
                // gone, and keeping it would let a returning listener be handed a delta whose base it
                // never received — the entry is dropped rather than reset, so the table stays the size
                // of the populated map rather than the whole one.
                _emptyRegions.Add(at);
                continue;
            }

            bool full = region.ForceFull
                || unchecked(_simulation.Tick - region.LastFullTick) >= FullResyncTicks;
            region.ForceFull = false;
            if (full)
                region.LastFullTick = _simulation.Tick;

            // Players who left this region since the last tick have to be forgotten, or their entry
            // would suppress the first snapshot they get when they come back unchanged.
            if (region.LastSent.Count != region.Pending.Count)
                PruneLeavers(region);

            var send = new List<PlayerSnapshotState>(region.Pending.Count);
            for (int i = 0; i < region.Pending.Count; i++)
            {
                PlayerSnapshotState state = region.Pending[i];
                if (!full && region.LastSent.TryGetValue(state.PlayerId, out PlayerSnapshotState last)
                    && Unchanged(last, state))
                {
                    continue;
                }
                region.LastSent[state.PlayerId] = state;
                send.Add(state);
            }

            if (send.Count == 0)
                continue; // a region where nothing moved costs nothing

            foreach (byte[] chunk in NetMessages.WriteStateUpdates(_simulation.Tick, send))
                foreach (ITransportConnection conn in region.Listeners)
                    conn.Send(chunk, ESendType.Unreliable);
        }

        foreach (byte at in _emptyRegions)
            _regions.Remove(at);
        _emptyRegions.Clear();
    }

    private Region RegionAt(byte at)
    {
        if (!_regions.TryGetValue(at, out Region? region))
            _regions[at] = region = new Region();
        return region;
    }

    private static void PruneLeavers(Region region)
    {
        var gone = new List<byte>();
        foreach (byte id in region.LastSent.Keys)
        {
            bool present = false;
            for (int i = 0; i < region.Pending.Count && !present; i++)
                present = region.Pending[i].PlayerId == id;
            if (!present)
                gone.Add(id);
        }
        foreach (byte id in gone)
            region.LastSent.Remove(id);
    }

    // Byte-identical as the wire would encode it: the position verbatim (it is not quantized) and the
    // three bytes the angles and flags pack into. Comparing the encoded form rather than the state is
    // what makes "identical" mean "the datagram would be the same", which is the only thing worth
    // skipping — a sub-degree turn that quantizes to the same byte is not a change anyone can see.
    private static bool Unchanged(in PlayerSnapshotState a, in PlayerSnapshotState b) =>
        a.Position == b.Position && a.Pitch == b.Pitch && a.Yaw == b.Yaw && a.Stance == b.Stance
        && a.Moving == b.Moving && a.Grounded == b.Grounded;

    // Tells a player where the server actually has them, when their claim was refused.
    //
    // Addressed to the owner alone: everyone else already learns the authoritative position from the
    // state stream, and it is the owner who is somewhere the server disagrees with. Unreliable, because
    // a lost correction is superseded by the next tick's — the condition persists as long as the two
    // disagree, so retransmitting a stale position would be worse than dropping it.
    //
    // Usually zero per tick. A correction only exists when the speed budget refused a claim, which on an
    // honest client means a genuine desync — a stalled host, a step the heightfield solver disagreed
    // with — and those are the moments this exists for.
    private void SendCorrections()
    {
        if (_simulation.Corrections.Count == 0)
            return;
        foreach ((ITransportConnection conn, Session session) in _sessions)
        {
            if (!session.Joined)
                continue;
            foreach (PlayerPositionCorrection correction in _simulation.Corrections)
                if (correction.PlayerId == session.PlayerId)
                    conn.Send(NetMessages.WritePositionCorrection(_simulation.Tick, correction.Position),
                        ESendType.Unreliable);
        }
    }

    // One nine-byte probe per joined client per tick, addressed rather than broadcast because the number
    // in it is that client's own frame counter and means nothing to anyone else. Skipped for a client
    // that has not sent an input yet (a spectator, a bot with no frames, the tick between admission and
    // the first datagram): there is nothing to echo, and echoing a zero would report a round trip
    // measured against a frame the client never sent.
    private void SendInputEchoes()
    {
        foreach ((ITransportConnection conn, Session session) in _sessions)
            if (session.Joined && session.HasInputFrame)
                conn.Send(NetMessages.WriteInputEcho(session.LastInputFrame, _simulation.Tick),
                    ESendType.Unreliable);
    }

    public void Broadcast(byte[] payload, ESendType sendType)
    {
        foreach ((ITransportConnection conn, Session session) in _sessions)
            if (session.Joined)
                conn.Send(payload, sendType);
    }

    // Every joined player except one. Used for what a player already knows because they caused it —
    // GatherRemoteClientConnectionsExcludingOwner, in the original's terms.
    public void BroadcastExcept(byte playerId, byte[] payload, ESendType sendType)
    {
        foreach ((ITransportConnection conn, Session session) in _sessions)
            if (session.Joined && session.PlayerId != playerId)
                conn.Send(payload, sendType);
    }

    // Four bytes a guesser was never told. Not cryptography and not meant to be: it turns "guess a
    // 16-bit ephemeral port" into "guess that and a 32-bit number", which is the whole distance between
    // something anyone can do by accident and something they have to mean. Drawn from the system CSPRNG
    // rather than a seeded Random, because a predictable token is not a token.
    //
    // Never zero, so "this session has no token yet" stays distinguishable from a token that happens to
    // be zero — and so a frame written by a client that never received a Welcome cannot pass by default.
    private static uint NextToken()
    {
        Span<byte> bytes = stackalloc byte[4];
        uint token;
        do
        {
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            token = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
        while (token == 0);
        return token;
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
