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

    public RemotePlayer(string name, in PoseSnapshot initial, double now, uint knownAtVersion = 0)
    {
        Name = name;
        KnownAtVersion = knownAtVersion;
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
    private bool _hasGestureTick;

    // Accepts a gesture only if it is newer than the last one heard. Reliable delivery retransmits but
    // does not order, so an early swing re-sent late can arrive behind a later one; wrap-safe signed
    // comparison, the same rule the server's own input freshness guard uses.
    public void PushGesture(uint tick, UnturnedGodot.Player.EPlayerGesture gesture)
    {
        if (_hasGestureTick && unchecked((int)(tick - _lastGestureTick)) <= 0)
            return;
        _hasGestureTick = true;
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
    // does not allocate a set and a list per message.
    private readonly HashSet<byte> _rosterIds = new();
    private readonly List<byte> _departed = new();

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

    // levelName is the map folder this client actually built. The server admits us only onto that
    // world; see NetMessages.LevelsMatch.
    public NetClient(IClientTransport transport, string name, string levelName)
    {
        _transport = transport;
        _name = name;
        _level = levelName;
    }

    public void SendInput(in InputCommand input) =>
        _transport.Send(NetMessages.WriteInput(input), ESendType.Unreliable);

    public void Update(double now)
    {
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
            // stale ones would refuse every listing in the fresh roster it sends us.
            Joined = false;
            _remotes.Clear();
            Array.Clear(_leftAtVersion);
            _lastHello = double.NegativeInfinity;
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

                    (byte id, _, uint rosterVersion, List<PlayerListing> players) = welcome;
                    PlayerId = id;
                    Joined = true;
                    _lastStateAt = now;

                    // The roster is COMPLETE — "everyone already here" as of rosterVersion — so it
                    // replaces what we hold rather than adding to it. A second Welcome is ordinary (an
                    // unadmitted client re-Hellos every couple of seconds, and a joined session that
                    // Hellos again is answered with a fresh roster), and UDP may hand it to us after the
                    // PlayerLeft it predates: merging left that player standing there forever, unseeable
                    // and unshootable. Players still listed keep the remote we already have, so a
                    // re-Welcome does not restart anyone's interpolation mid-session.
                    _rosterIds.Clear();
                    foreach (PlayerListing p in players)
                    {
                        if (p.PlayerId == PlayerId)
                            continue; // the server does not list us; a roster that did would double us
                        if (_leftAtVersion[p.PlayerId] > rosterVersion)
                            continue; // we already know they left, later than this roster was taken
                        _rosterIds.Add(p.PlayerId);
                        if (!_remotes.ContainsKey(p.PlayerId))
                            _remotes[p.PlayerId] = SpawnRemote(p, now, rosterVersion);
                    }

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

                    (uint joinedVersion, PlayerListing p) = joined;
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
                        _remotes[p.PlayerId] = SpawnRemote(p, now, joinedVersion);
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
        }
    }

    // Cached so a method group does not allocate a delegate on every received message.
    private static readonly Func<byte[], ENetMessage> ReadType = NetMessages.TypeOf;
    private static readonly
        Func<byte[], (byte PlayerId, uint Tick, UnturnedGodot.Player.EPlayerGesture Gesture)>
            ReadPlayerGesture = NetMessages.ReadPlayerGesture;
    private static readonly
        Func<byte[], (byte PlayerId, uint Tick, uint RosterVersion, List<PlayerListing> Players)> ReadWelcome =
            NetMessages.ReadWelcome;
    private static readonly Func<byte[], (uint RosterVersion, PlayerListing Player)> ReadPlayerJoined =
        NetMessages.ReadPlayerJoined;
    private static readonly Func<byte[], JoinRejection> ReadReject = NetMessages.ReadReject;
    private static readonly Func<byte[], (uint RosterVersion, byte PlayerId)> ReadPlayerLeft =
        NetMessages.ReadPlayerLeft;
    private static readonly Func<byte[], (uint Tick, List<PlayerSnapshotState> States)> ReadStateUpdate =
        NetMessages.ReadStateUpdate;

    private static RemotePlayer SpawnRemote(PlayerListing p, double now, uint knownAtTick)
    {
        var remote = new RemotePlayer(p.Name, Pose(p.Position, p.Pitch, p.Yaw), now, knownAtTick);
        remote.Push(Pose(p.Position, p.Pitch, p.Yaw), p.Stance, moving: false, grounded: true, now);
        return remote;
    }

    private static PoseSnapshot Pose(Vector3 position, byte pitch, byte yaw) =>
        new(position, NetAngles.DequantizePitch(pitch), NetAngles.DequantizeYaw(yaw));
}
