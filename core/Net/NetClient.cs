using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Net;

// A remote player as this client sees them: name plus the snapshot buffer their server states feed.
public sealed class RemotePlayer
{
    // tellState's LARGE_DISTANCE: a skip bigger than this resets interpolation instead of gliding.
    private const float LargeDistance = 16f;

    private readonly SnapshotBuffer _buffer = new();
    private Vector3 _lastUpdatePos;

    public string Name { get; }

    // The roster version this player was known to be present at. A roster no newer than this cannot be
    // evidence that they left — it simply predates them.
    public uint KnownAtVersion { get; }

    // Latest replicated stance and input-derived moving flag: discrete, so they snap (no interpolation).
    public UnturnedGodot.Player.EPlayerStance Stance { get; private set; }
    public bool Moving { get; private set; }
    public bool Grounded { get; private set; } = true;

    public RemotePlayer(string name, in PoseSnapshot initial, double now, uint knownAtVersion = 0,
        uint spawnedAtTick = 0)
    {
        Name = name;
        KnownAtVersion = knownAtVersion;
        // The gesture floor is the tick this player was admitted on, carried by the roster entry that
        // named them. Player ids are RECYCLED, and a gesture names only an id: without a floor, a swing
        // thrown by the previous holder of this one and retransmitted late would play on whoever holds it
        // now. Nothing that happened before this player arrived belongs to them — and taking the date
        // from the message rather than from whatever tick had been heard most recently is what keeps a
        // punch thrown just after the join, but delivered before its roster entry, from dating too late
        // to survive its own arrival.
        _lastGestureTick = spawnedAtTick;
        _buffer.UpdateLastSnapshot(initial, now);
        _lastUpdatePos = initial.Position;
    }

    public void Push(in PoseSnapshot pose, UnturnedGodot.Player.EPlayerStance stance, bool moving,
        bool grounded, double now)
    {
        Stance = stance;
        Moving = moving;
        Grounded = grounded;
        bool largeDelta = (pose.Position - _lastUpdatePos).LengthSquared() > LargeDistance * LargeDistance;
        _lastUpdatePos = pose.Position;
        if (largeDelta)
            _buffer.UpdateLastSnapshot(pose, now);
        else
            _buffer.AddNewSnapshot(pose, now);
    }

    public PoseSnapshot Sample(double now) => _buffer.GetCurrentSnapshot(now);

    // A one-shot hand animation this player performed, waiting to be played on their avatar. Held as a
    // single pending gesture rather than a queue: two punches cannot land inside one render frame at the
    // cooldown the simulation enforces, and if the network ever delivered a burst, the newest swing is
    // the one worth showing — replaying the older one late would only put the avatar behind.
    public UnturnedGodot.Player.EPlayerGesture PendingGesture { get; private set; }

    private uint _lastGestureTick;

    // Accepts a gesture only if it is newer than the last one heard — which starts as the tick this
    // avatar was learned at, so the guard covers a recycled id as well as a stale retransmission.
    // Reliable delivery retransmits but does not order, so an early swing re-sent late can arrive behind
    // a later one; wrap-safe signed comparison, the same rule the server's own input freshness guard
    // uses.
    public void PushGesture(uint tick, UnturnedGodot.Player.EPlayerGesture gesture)
    {
        if (unchecked((int)(tick - _lastGestureTick)) <= 0)
            return;
        _lastGestureTick = tick;
        PendingGesture = gesture;
    }

    // Hands the pending gesture over and clears it, so a renderer plays each one exactly once.
    public UnturnedGodot.Player.EPlayerGesture TakeGesture()
    {
        UnturnedGodot.Player.EPlayerGesture gesture = PendingGesture;
        PendingGesture = UnturnedGodot.Player.EPlayerGesture.None;
        return gesture;
    }
}

// The client-side session over any IClientTransport: sends the Hello handshake and 12.5 Hz inputs,
// consumes Welcome/Joined/Left and the state broadcasts, and exposes interpolated remote players for
// rendering. The local player's own authoritative state is kept for future reconciliation.
public sealed class NetClient
{
    private readonly IClientTransport _transport;
    private readonly Dictionary<byte, RemotePlayer> _remotes = new();

    // Scratch for reconciling a Welcome's roster against the remotes we hold; reused so a rejoin storm
    // does not allocate a set and a list per message. Across chunks of one roster it accumulates, so it
    // is cleared when a new roster version starts rather than on every message.
    private readonly HashSet<byte> _rosterIds = new();
    private readonly List<byte> _departed = new();

    // Which chunks of the roster currently being assembled have arrived. A player id is a byte and a
    // roster is at most 254 entries, so the chunk count can never exceed the entry count — 254 slots is
    // an upper bound the protocol cannot exceed, and the table never grows.
    private readonly bool[] _assemblingChunks = new bool[byte.MaxValue];
    private bool _assembling;
    private uint _assemblingVersion;
    private int _assemblingSeen;

    // The newest roster version at which each id was seen to LEAVE. Player ids are bytes, so the whole
    // tombstone table is 256 entries and never grows: a roster older than an id's departure may not put
    // that player back on the map, however late it arrives. Cleared when the session itself resets,
    // because a server that restarted counts its versions from zero again.
    private readonly uint[] _leftAtVersion = new uint[256];

    public byte PlayerId { get; private set; }

    // Datagrams that reached a decoder and did not survive it. See NetServer.MalformedPacketsDropped.
    public long MalformedPacketsDropped { get; private set; }

    // Extension seam: message types this client doesn't handle (future replicated systems) land here
    // instead of being dropped, so a feature module can subscribe without editing NetClient.
    public Action<byte[]>? OnUnhandledMessage;

    // Raised when the session is abandoned and rejoined from scratch (the StateTimeout branch in Update).
    // Subscribers holding anything keyed on server-assigned ids must drop it: the host that answers the
    // next Hello may be a restarted one, numbering everything from zero again, and ids from the session
    // that ended name different things in the one that follows.
    public Action? OnSessionReset;

    // Set once the server refuses us (wrong map, wrong build, full). Terminal: the retry loop stops,
    // and the UI has a reason to show instead of a join that quietly never happens.
    public JoinRejection? Rejection { get; private set; }
    public Action<JoinRejection>? OnRejected;

    // The level this client built, as sent in the Hello.
    public string Level => _level;

    public bool Joined { get; private set; }
    public PlayerSnapshotState LocalServerState { get; private set; }
    public IReadOnlyDictionary<byte, RemotePlayer> Remotes => _remotes;

    // Self-healing join: while not admitted, the Hello re-sends on this cadence (covers "connected
    // before the server was up", lost handshakes and reliable channels that already gave up); once
    // joined, this long without any StateUpdate means the server dropped us silently — reset and rejoin.
    public const double HelloRetryInterval = 2.0;
    public const double StateTimeout = 10.0;

    private readonly string _name;
    private readonly string _level;
    private double _lastHello = double.NegativeInfinity;
    private double _lastStateAt;
    private double _now;

    // When each of the last few input frames was sent, so an echo can be turned into a round trip. A
    // ring rather than a dictionary: inputs go out at 12.5 Hz and the echo for one comes back within a
    // tick or two of it, so two seconds of history is generous and nothing has to be evicted by hand.
    private readonly (uint Frame, double SentAt)[] _sentFrames = new (uint, double)[32];
    private int _sentAt;

    // How much of a new reading is taken. Round trips jitter hard — one datagram queued behind a burst
    // is not a slower link — and a ping that jumps with every sample is unreadable and useless as an
    // input to an adaptive interpolation delay. An eighth settles in about a second at this cadence.
    private const double RttSmoothing = 0.125;

    // The smoothed round trip, in seconds; NaN until an echo has come back. Named for what it is rather
    // than "ping", because half of it is the number an interpolation delay wants and the distinction
    // matters once something starts consuming it.
    public double RoundTripSeconds { get; private set; } = double.NaN;

    // The reading a person wants to see. NaN before the first echo, which a UI should print as "--"
    // rather than as zero: no measurement and a zero-latency link are not the same statement.
    public double PingMilliseconds => RoundTripSeconds * 1000.0;

    // Everything this client's link has cost, split by message type. See NetServer.Traffic.
    public NetTraffic Traffic => _transport.Traffic;

    // levelName is the map folder this client actually built. The server admits us only onto that
    // world; see NetMessages.LevelsMatch.
    public NetClient(IClientTransport transport, string name, string levelName)
    {
        _transport = transport;
        _name = name;
        _level = levelName;
    }

    public void SendInput(in InputCommand input) => SendInput(input, _now);

    // `now` is when this frame left, which is what the round trip is measured from. The overload above
    // falls back to the clock the last Update handed us — a fraction of a frame stale, which is noise
    // against a round trip, and it keeps every existing caller working unchanged.
    public void SendInput(in InputCommand input, double now)
    {
        _sentFrames[_sentAt] = (input.Frame, now);
        _sentAt = (_sentAt + 1) % _sentFrames.Length;
        _transport.Send(NetMessages.WriteInput(input), ESendType.Unreliable);
    }

    public void Update(double now)
    {
        _now = now;
        _transport.Update(now); // give the transport the clock BEFORE any reliable send

        // A rejection is an answer, not a hiccup: retrying a Hello the server has already refused only
        // hammers it with a request whose verdict cannot change.
        if (!Joined && Rejection == null && now - _lastHello >= HelloRetryInterval)
        {
            // Deferred from the constructor: a reliable frame stamped before the transport ever saw the
            // real clock would look GiveUpAfter-seconds old on the next Update and kill the channel.
            _lastHello = now;
            _transport.Send(NetMessages.WriteHello(_name, _level), ESendType.Reliable);
        }

        while (_transport.TryReceive(out byte[] payload))
            Handle(payload, now);

        if (Joined && now - _lastStateAt > StateTimeout)
        {
            // The server stopped talking to us (session dropped, host restarted): rejoin from scratch.
            // The tombstones go too — a host that restarted counts its roster versions from zero, and
            // stale ones would refuse every listing in the fresh roster it sends us. Nothing has to be
            // done about the gesture tick floors: each one arrives with the roster entry that creates its
            // avatar, so a restarted host's own low ticks come with the avatars they date.
            Joined = false;
            _remotes.Clear();
            Array.Clear(_leftAtVersion);
            _lastHello = double.NegativeInfinity;
            // The round trip goes with the session. Frame numbers start again on the next join, so a
            // stale ring entry could be matched by an echo for a different frame that happens to reuse
            // the number, reporting a round trip of however long the outage lasted.
            Array.Clear(_sentFrames);
            _sentAt = 0;
            RoundTripSeconds = double.NaN;
            // A half-assembled roster belongs to the session that ended; the next host numbers its
            // versions from zero again, so keeping it would let a stale partial complete against a new
            // roster that happens to reuse the version.
            _assembling = false;
            _assemblingSeen = 0;
            Array.Clear(_assemblingChunks);
            _rosterIds.Clear();
            // Everything else keyed on ids this server handed out has to start over too, for the same
            // reason the roster versions do — a restarted host numbers its zombies from zero again, and a
            // subscriber holding the old session's ids would judge the new session's by them.
            OnSessionReset?.Invoke();
        }
    }

    private void Handle(byte[] payload, double now)
    {
        // A client trusts its server no further than a server trusts its clients: the bytes still arrive
        // over UDP from whatever answered, and this loop is on the frame thread. Only the decodes are
        // guarded — OnUnhandledMessage runs subscriber code, and a subscriber that reads untrusted bytes
        // (ZombiesView) guards its own decode rather than hiding behind ours.
        if (!MalformedPacket.TryDecode(payload, ReadType, out ENetMessage type))
        {
            MalformedPacketsDropped++;
            return;
        }

        switch (type)
        {
            default:
                OnUnhandledMessage?.Invoke(payload);
                break;
            case ENetMessage.Welcome:
                {
                    if (!MalformedPacket.TryDecode(payload, ReadWelcome, out var welcome))
                    {
                        MalformedPacketsDropped++;
                        break;
                    }

                    (byte id, uint welcomeTick, uint rosterVersion, byte chunkIndex, byte chunkCount,
                        List<PlayerListing> players) = welcome;
                    PlayerId = id;
                    Joined = true;
                    _lastStateAt = now;

                    // A roster now arrives in as many datagrams as it takes, so the "replace what we
                    // hold" rule below applies to the ASSEMBLED roster and not to each piece: read
                    // chunk-wise, chunk 2 is a complete roster that happens to omit everyone in chunk 1,
                    // and acting on it would delete them.
                    //
                    // Assembly is keyed on the roster version, which is the only identity a roster has.
                    // A chunk of an older version is stale by definition and is dropped; the first chunk
                    // of a newer one starts a fresh assembly, abandoning whatever partial set was in
                    // progress — which is right, because that older roster is now superseded whether or
                    // not its remaining chunks ever arrive. Reliable delivery retransmits but does not
                    // order, so chunks of one version may arrive in any order and a duplicate may arrive
                    // at any time; both are handled by accumulating into a set and counting distinct
                    // indices rather than trusting arrival order.
                    if (_assembling && unchecked((int)(rosterVersion - _assemblingVersion)) < 0)
                        break; // a chunk of a roster older than the one we are building
                    if (!_assembling || rosterVersion != _assemblingVersion)
                    {
                        _assembling = true;
                        _assemblingVersion = rosterVersion;
                        _assemblingSeen = 0;
                        Array.Clear(_assemblingChunks);
                        _rosterIds.Clear();
                    }

                    // The roster is COMPLETE — "everyone already here" as of rosterVersion — so once
                    // assembled it replaces what we hold rather than adding to it. A second Welcome is
                    // ordinary (an unadmitted client re-Hellos every couple of seconds, and a joined
                    // session that Hellos again is answered with a fresh roster), and UDP may hand it to
                    // us after the PlayerLeft it predates: merging left that player standing there
                    // forever, unseeable and unshootable. Players still listed keep the remote we
                    // already have, so a re-Welcome does not restart anyone's interpolation mid-session.
                    foreach (PlayerListing p in players)
                    {
                        if (p.PlayerId == PlayerId)
                            continue; // the server does not list us; a roster that did would double us
                        if (_leftAtVersion[p.PlayerId] > rosterVersion)
                            continue; // we already know they left, later than this roster was taken
                        _rosterIds.Add(p.PlayerId);
                        if (!_remotes.ContainsKey(p.PlayerId))
                            _remotes[p.PlayerId] = SpawnRemote(p, now, rosterVersion, welcomeTick);
                    }

                    // Counted per distinct index, so a retransmitted chunk cannot complete the roster on
                    // its own — which would prune everyone in the chunk that has not arrived yet.
                    if (chunkIndex < _assemblingChunks.Length && !_assemblingChunks[chunkIndex])
                    {
                        _assemblingChunks[chunkIndex] = true;
                        _assemblingSeen++;
                    }
                    if (_assemblingSeen < chunkCount)
                        break; // still missing a piece: the roster is not yet evidence of anyone's absence

                    _assembling = false;
                    if (_remotes.Count > _rosterIds.Count)
                    {
                        _departed.Clear();
                        foreach ((byte known, RemotePlayer remote) in _remotes)
                        {
                            // Absent from a roster older than the player is no evidence they left: that
                            // snapshot was taken before they arrived. Their PlayerJoined is reliable and
                            // already consumed, so it is never replayed — dropping them here would leave
                            // them invisible for the rest of the session, since state updates only move
                            // remotes that already exist.
                            if (!_rosterIds.Contains(known) && remote.KnownAtVersion <= rosterVersion)
                                _departed.Add(known);
                        }
                        foreach (byte gone in _departed)
                            _remotes.Remove(gone);
                    }
                    break;
                }
            case ENetMessage.PlayerJoined:
                {
                    if (!MalformedPacket.TryDecode(payload, ReadPlayerJoined, out var joined))
                    {
                        MalformedPacketsDropped++;
                        break;
                    }

                    (uint joinedVersion, uint joinedTick, PlayerListing p) = joined;
                    // A join can be the stale message. Someone who connects and drops straight back out
                    // produces a join and a leave moments apart, and the leave may arrive first: acting
                    // on the join then leaves that player standing there for good, because nothing
                    // removes a remote except a leave and theirs has already been spent. Ids are also
                    // recycled, so an older join can describe the PREVIOUS holder of one we already
                    // have — taking it would lose both, the newer occupant overwritten here and the
                    // stale one removed by the leave that follows.
                    if (p.PlayerId != PlayerId
                        && _leftAtVersion[p.PlayerId] <= joinedVersion
                        && (!_remotes.TryGetValue(p.PlayerId, out RemotePlayer? held)
                            || held.KnownAtVersion < joinedVersion))
                    {
                        _remotes[p.PlayerId] = SpawnRemote(p, now, joinedVersion, joinedTick);
                    }
                    break;
                }
            case ENetMessage.Reject:
                {
                    if (!MalformedPacket.TryDecode(payload, ReadReject, out JoinRejection rejection))
                    {
                        MalformedPacketsDropped++;
                        break;
                    }

                    Rejection = rejection;
                    Joined = false;
                    _remotes.Clear();
                    OnRejected?.Invoke(rejection);
                    break;
                }
            case ENetMessage.PlayerLeft:
                {
                    if (!MalformedPacket.TryDecode(payload, ReadPlayerLeft, out var left))
                    {
                        MalformedPacketsDropped++;
                        break;
                    }

                    (uint leftVersion, byte leftId) = left;
                    // Remembered even if we never held that remote: the roster carrying them may still
                    // be in flight behind this, and it must not put them back.
                    if (leftVersion > _leftAtVersion[leftId])
                        _leftAtVersion[leftId] = leftVersion;
                    if (_remotes.TryGetValue(leftId, out RemotePlayer? leaving)
                        && leaving.KnownAtVersion <= leftVersion)
                    {
                        _remotes.Remove(leftId); // the id was handed out again if the join is newer
                    }
                    break;
                }
            case ENetMessage.StateUpdate:
                {
                    if (!MalformedPacket.TryDecode(payload, ReadStateUpdate, out var update))
                    {
                        MalformedPacketsDropped++;
                        break;
                    }

                    (_, List<PlayerSnapshotState> states) = update;
                    _lastStateAt = now;
                    foreach (PlayerSnapshotState s in states)
                    {
                        if (s.PlayerId == PlayerId)
                            LocalServerState = s;
                        else if (_remotes.TryGetValue(s.PlayerId, out RemotePlayer? remote))
                            remote.Push(Pose(s.Position, s.Pitch, s.Yaw), s.Stance, s.Moving, s.Grounded, now);
                    }
                    break;
                }
            case ENetMessage.PlayerGesture:
                {
                    if (!MalformedPacket.TryDecode(payload, ReadPlayerGesture, out var gesture))
                    {
                        MalformedPacketsDropped++;
                        break;
                    }

                    // Only for players we actually hold. Both messages are reliable, but reliable here
                    // means retransmitted, not ordered, so a punch thrown immediately after joining can
                    // genuinely overtake the PlayerJoined that names its thrower — and this drops it.
                    // Deliberately: holding it until the roster catches up means keeping gestures for
                    // players who may never arrive, and player IDs are reused, so the buffer would need
                    // to tell one occupant of an ID from the next before it dared play anything. That is
                    // a real piece of protocol state to buy back one animation frame on an avatar that
                    // is still fading in. Revisit it when a gesture costs someone health.
                    if (_remotes.TryGetValue(gesture.PlayerId, out RemotePlayer? gesturing))
                        gesturing.PushGesture(gesture.Tick, gesture.Gesture);
                    break;
                }
            case ENetMessage.InputEcho:
                {
                    if (!MalformedPacket.TryDecode(payload, ReadInputEcho, out var echo))
                    {
                        MalformedPacketsDropped++;
                        break;
                    }

                    ObserveRoundTrip(echo.Frame, now);
                    break;
                }
        }
    }

    // Turns one echo into a round trip. The server repeats the same frame number until a newer input
    // reaches it, so most echoes are for a frame already measured — matching against the ring and taking
    // the FIRST arrival for each frame is what keeps a repeat from reporting a round trip inflated by
    // however long the server sat on it. A frame that has already rolled out of the ring is simply not
    // measurable and is dropped.
    private void ObserveRoundTrip(uint frame, double now)
    {
        for (int i = 0; i < _sentFrames.Length; i++)
        {
            if (_sentFrames[i].Frame != frame || _sentFrames[i].SentAt <= 0)
                continue;
            double sample = now - _sentFrames[i].SentAt;
            // Spent, so the repeats behind it find nothing. Zeroing the timestamp rather than the frame
            // number keeps frame 0 — a real frame number a session opens on — from matching every empty
            // slot in the ring.
            _sentFrames[i].SentAt = 0;
            if (sample < 0)
                return; // a clock that went backwards is not a measurement
            RoundTripSeconds = double.IsNaN(RoundTripSeconds)
                ? sample
                : (RoundTripSeconds * (1 - RttSmoothing)) + (sample * RttSmoothing);
            return;
        }
    }

    // Cached so a method group does not allocate a delegate on every received message.
    private static readonly Func<byte[], ENetMessage> ReadType = NetMessages.TypeOf;
    private static readonly
        Func<byte[], (byte PlayerId, uint Tick, UnturnedGodot.Player.EPlayerGesture Gesture)>
            ReadPlayerGesture = NetMessages.ReadPlayerGesture;
    private static readonly
        Func<byte[], (byte PlayerId, uint Tick, uint RosterVersion, byte ChunkIndex, byte ChunkCount,
            List<PlayerListing> Players)> ReadWelcome = NetMessages.ReadWelcome;
    private static readonly Func<byte[], (uint RosterVersion, uint Tick, PlayerListing Player)>
        ReadPlayerJoined = NetMessages.ReadPlayerJoined;
    private static readonly Func<byte[], JoinRejection> ReadReject = NetMessages.ReadReject;
    private static readonly Func<byte[], (uint RosterVersion, byte PlayerId)> ReadPlayerLeft =
        NetMessages.ReadPlayerLeft;
    private static readonly Func<byte[], (uint Tick, List<PlayerSnapshotState> States)> ReadStateUpdate =
        NetMessages.ReadStateUpdate;
    private static readonly Func<byte[], (uint Frame, uint Tick)> ReadInputEcho =
        NetMessages.ReadInputEcho;

    // The newest server tick heard, from any message that carries one. State updates are unreliable and
    // unordered, so newest-wins rather than last-wins; wrap-safe like every other sequence comparison.
    private static RemotePlayer SpawnRemote(PlayerListing p, double now, uint knownAtVersion,
        uint spawnedAtTick)
    {
        var remote = new RemotePlayer(p.Name, Pose(p.Position, p.Pitch, p.Yaw), now, knownAtVersion,
            spawnedAtTick);
        remote.Push(Pose(p.Position, p.Pitch, p.Yaw), p.Stance, moving: false, grounded: true, now);
        return remote;
    }

    private static PoseSnapshot Pose(Vector3 position, byte pitch, byte yaw) =>
        new(position, NetAngles.DequantizePitch(pitch), NetAngles.DequantizeYaw(yaw));
}
