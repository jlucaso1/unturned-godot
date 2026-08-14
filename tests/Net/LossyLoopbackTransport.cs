using System;
using System.Collections.Generic;
using UnturnedGodot.Net;
using UnturnedGodot.Repro;

namespace UnturnedGodot.Tests.Net;

// LoopbackTransport with the network put back in.
//
// core/Net/LoopbackTransport.cs says it plainly: "Delivery is perfect and ordered; reliability
// semantics are exercised by the reliable channel's own tests instead." That is the right call for a
// transport whose job is to be a wire between two objects in one process — but it means every
// end-to-end test in this directory runs over a pipe no real player has ever had, and the codebase
// carries FIVE hand-rolled compensations for the pipe they do have:
//
//   * the roster version         core/Net/NetMessages.cs   (WriteWelcome's comment)
//   * the gesture tick floor     core/Net/NetMessages.cs   (WritePlayerGesture's comment)
//   * player tombstones          core/Net/NetClient.cs     (_leftAtVersion)
//   * zombie death tombstones    src/Net/ZombiesView.cs    (_killed)
//   * the id recycling quarantine core/Net/PlayerIdPool.cs (QuarantineSeconds)
//
// Each is well covered on its own, one hand-built reordering at a time. What none of those tests can
// produce is the INTERACTION: a PlayerLeft overtaking a PlayerJoined for a recycled id while a
// ZombieKilled overtakes its region's ZombieList while a gesture lands for a player whose roster entry
// is still in flight. This transport produces exactly that, deterministically, from a seed.
//
// ---------------------------------------------------------------------------------------------------
// What is modelled, and what is deliberately NOT
//
// ESendType.Unreliable is treated as UDP: a datagram may be lost outright, duplicated, or delivered
// out of order within a bounded window.
//
// ESendType.Reliable is treated as ReliableChannel's OUTPUT, not its input. That channel retransmits
// until acknowledged and dedups on the receiving side, so a reliable payload is never permanently
// lost — but its own comment is explicit that it "delivers an unseen sequence the moment it arrives
// rather than in order". So a loss roll on a reliable datagram does not drop it: it pushes the
// datagram back by a retransmission penalty, which is precisely the shape of a frame that was lost,
// resent on the next ResendInterval, and therefore arrived BEHIND traffic that was sent after it.
// That is the hazard all five guards above exist for, and reimplementing ReliableChannel underneath
// this would only test that class twice.
//
// Consequently a reliable payload here is delivered at least once and possibly more than once, in an
// arbitrary order bounded by the reorder window — which is exactly the contract the layers above are
// written against.
//
// ---------------------------------------------------------------------------------------------------
// How the reordering is produced
//
// Every datagram is stamped with a release SLOT: the pipe's current slot plus a jitter draw in
// [0, reorderWindow]. Update(now) advances the slot by one and releases everything at or below it, in
// slot order, ties broken by send order. So a window of 3 means a datagram may be overtaken by up to
// three later ones and no more — bounded, reproducible, and with no wall-clock dependence at all.
//
// Determinism is the whole point, so the draw comes from ReproRandom (PCG-XSH-RR, integer-only,
// identical on every platform and runtime) rather than System.Random, whose seeded sequence is a
// compatibility promise rather than a specification. A failing soak run is reproduced by its seed.
public sealed class LossPolicy
{
    // Fraction of datagrams that hit a loss roll. Unreliable ones are dropped; reliable ones are
    // delayed by a retransmission penalty instead (see the header).
    public double LossRate { get; }

    // Fraction of datagrams delivered twice. Applies to both send types: UDP duplicates datagrams, and
    // ReliableChannel's retransmission produces a second copy whenever an ack is the thing that was lost.
    public double DuplicateRate { get; }

    // How many later datagrams may overtake an earlier one. 0 keeps the pipe ordered.
    public int ReorderWindow { get; }

    private readonly ReproRandom _random;

    public LossPolicy(ulong seed, double lossRate, double duplicateRate, int reorderWindow)
    {
        if (lossRate is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(lossRate));
        if (duplicateRate is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(duplicateRate));
        ArgumentOutOfRangeException.ThrowIfNegative(reorderWindow);

        LossRate = lossRate;
        DuplicateRate = duplicateRate;
        ReorderWindow = reorderWindow;
        _random = new ReproRandom(seed);
    }

    // Every pipe in one scenario draws from ONE generator, so the whole run is a function of the seed
    // rather than of how many pipes happened to exist or the order they were created in.
    internal bool Roll(double rate) => rate > 0 && _random.NextDouble() < rate;

    internal int Jitter() => ReorderWindow == 0 ? 0 : _random.Next(ReorderWindow + 1);

    // Counters, so a soak can assert its own scenario actually happened rather than trusting the rates.
    // A run that reordered nothing proves nothing, and would look identical to a passing one.
    public long Sent { get; private set; }
    public long Dropped { get; private set; }
    public long Duplicated { get; private set; }
    public long Delayed { get; private set; }
    public long Reordered { get; private set; }

    internal void CountSent() => Sent++;
    internal void CountDropped() => Dropped++;
    internal void CountDuplicated() => Duplicated++;
    internal void CountDelayed() => Delayed++;
    internal void CountReordered() => Reordered++;

    public override string ToString() =>
        $"loss {LossRate:P0}, dup {DuplicateRate:P0}, window {ReorderWindow}: {Sent} sent, "
        + $"{Dropped} dropped, {Duplicated} duplicated, {Delayed} retransmit-delayed, "
        + $"{Reordered} delivered out of order";
}

// One direction of one connection. Holds datagrams until their slot comes up.
internal sealed class LossyPipe<T>
{
    private readonly LossPolicy _policy;
    private readonly List<(long Slot, long Order, T Item)> _held = new();
    private readonly Queue<T> _ready = new();
    private long _slot;
    private long _order;

    // The send order of the last datagram released. A release whose order is lower than this one
    // overtook something, which is what the Reordered counter records.
    private long _lastReleasedOrder = -1;

    internal LossyPipe(LossPolicy policy) => _policy = policy;

    internal void Send(T item, ESendType sendType)
    {
        _policy.CountSent();

        if (_policy.Roll(_policy.LossRate))
        {
            if (sendType == ESendType.Unreliable)
            {
                _policy.CountDropped();
                return;
            }

            // Reliable: not lost, but resent — so it lands behind everything sent in the meantime.
            _policy.CountDelayed();
            Hold(item, _policy.ReorderWindow + 1 + _policy.Jitter());
        }
        else
        {
            Hold(item, _policy.Jitter());
        }

        if (_policy.Roll(_policy.DuplicateRate))
        {
            _policy.CountDuplicated();
            Hold(item, _policy.Jitter());
        }
    }

    private void Hold(T item, int delay) => _held.Add((_slot + delay, _order++, item));

    // Advances the pipe one slot and moves everything due into the ready queue, in slot order with send
    // order breaking ties — so the window is a real bound rather than a shuffle.
    internal void Advance()
    {
        _slot++;
        if (_held.Count == 0)
            return;

        _held.Sort(static (a, b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Order.CompareTo(b.Order));

        int released = 0;
        foreach ((long slot, long order, T item) in _held)
        {
            if (slot > _slot)
                break;
            if (order < _lastReleasedOrder)
                _policy.CountReordered();
            _lastReleasedOrder = Math.Max(_lastReleasedOrder, order);
            _ready.Enqueue(item);
            released++;
        }

        _held.RemoveRange(0, released);
    }

    internal bool TryTake(out T item)
    {
        if (_ready.TryDequeue(out T? taken))
        {
            item = taken;
            return true;
        }
        item = default!;
        return false;
    }

    // Drops everything in flight AND everything held: a closed transport delivers nothing more.
    internal void Clear()
    {
        _held.Clear();
        _ready.Clear();
    }

    // Whether anything is still in the pipe. A soak drains to quiescence before asserting convergence:
    // "the roster agrees" is only a claim about a settled session, and a datagram still in flight is
    // the difference between a real divergence and a snapshot taken mid-delivery.
    internal bool Idle => _held.Count == 0 && _ready.Count == 0;
}

// The server end. Mirrors LoopbackServerTransport's shape so a harness can swap one for the other.
public sealed class LossyLoopbackServerTransport : IServerTransport
{
    private readonly LossPolicy _policy;
    private readonly Queue<ServerTransportEvent> _control = new(); // Connected/Disconnected: never lossy
    private readonly List<LossyLoopbackConnection> _connections = new();
    private int _nextConnectionId = 1;

    public LossyLoopbackServerTransport(LossPolicy policy) => _policy = policy;

    public LossPolicy Policy => _policy;

    public NetTraffic Traffic { get; } = new();

    // Accepted and never called. A connectionless answer is the one path that exists precisely to avoid
    // allocating a connection, and this harness has no unconnected peers to answer — every endpoint here
    // is a LossyLoopbackClientTransport the test constructed through CreateClient. Refusing to carry the
    // property would make the transport unusable rather than honest about what it does not exercise, so
    // the soak simply does not cover that path and UdpEndToEndTests remains where it is covered.
    public Func<byte[], byte[]?>? AnswerConnectionless { get; set; }

    public LossyLoopbackClientTransport CreateClient()
    {
        var connection = new LossyLoopbackConnection(_nextConnectionId++, this, _policy,
            new NetTraffic(Traffic));
        var client = new LossyLoopbackClientTransport(connection, _policy);
        connection.Client = client;
        _connections.Add(connection);
        // Connection lifecycle is the transport's own event, not a datagram on the wire: a UDP stack
        // that lost it would have no connection to lose it on. Losing these here would test the
        // harness's imagination rather than the protocol.
        _control.Enqueue(new ServerTransportEvent(ETransportEvent.Connected, connection,
            Array.Empty<byte>()));
        return client;
    }

    internal void EnqueueControl(ServerTransportEvent evt) => _control.Enqueue(evt);

    public bool TryReceive(out ServerTransportEvent evt)
    {
        if (_control.TryDequeue(out evt))
            return true;

        // Round-robin across connections rather than draining one to empty, so a chatty peer cannot
        // starve the others inside NetServer's per-Update event budget.
        for (int i = 0; i < _connections.Count; i++)
        {
            int at = (_next + i) % _connections.Count;
            if (_connections[at].TryTakeFromClient(out byte[] payload))
            {
                _next = (at + 1) % _connections.Count;
                evt = new ServerTransportEvent(ETransportEvent.Message, _connections[at], payload);
                return true;
            }
        }

        evt = default;
        return false;
    }

    private int _next;

    public void Update(double now)
    {
        foreach (LossyLoopbackConnection connection in _connections)
        {
            connection.AdvanceToServer();
            connection.Traffic.Update(now);
        }
        // After the children, so this window closes over the bytes they have just counted.
        Traffic.Update(now);
    }

    public void Close()
    {
        _control.Clear();
        foreach (LossyLoopbackConnection connection in _connections)
            connection.ClearBoth();
    }

    // True when no datagram is still in flight in either direction on any connection.
    public bool Idle
    {
        get
        {
            if (_control.Count > 0)
                return false;
            foreach (LossyLoopbackConnection connection in _connections)
                if (!connection.Idle)
                    return false;
            return true;
        }
    }
}

public sealed class LossyLoopbackConnection : ITransportConnection
{
    private readonly LossyLoopbackServerTransport _server;
    private readonly LossyPipe<byte[]> _toClient;
    private readonly LossyPipe<byte[]> _toServer;
    internal LossyLoopbackClientTransport Client = null!; // set by CreateClient before any event flows

    public int Id { get; }

    public NetTraffic Traffic { get; }

    internal LossyLoopbackConnection(int id, LossyLoopbackServerTransport server, LossPolicy policy,
        NetTraffic traffic)
    {
        Id = id;
        _server = server;
        Traffic = traffic;
        _toClient = new LossyPipe<byte[]>(policy);
        _toServer = new LossyPipe<byte[]>(policy);
    }

    // Counted where the payload is handed to the wire, not where the wire delivers it: a datagram the
    // policy then drops was still sent, and a byte counter that hid the dropped ones would report a
    // healthy link precisely when the link is worst.
    public void Send(byte[] payload, ESendType sendType)
    {
        Traffic.RecordSent(payload, payload.Length);
        _toClient.Send(payload, sendType);
    }

    internal void SendToServer(byte[] payload, ESendType sendType)
    {
        Traffic.RecordReceived(payload, payload.Length);
        _toServer.Send(payload, sendType);
    }

    internal void AdvanceToServer() => _toServer.Advance();

    internal void AdvanceToClient() => _toClient.Advance();

    internal bool TryTakeFromClient(out byte[] payload) => _toServer.TryTake(out payload);

    internal bool TryTakeFromServer(out byte[] payload) => _toClient.TryTake(out payload);

    internal bool Idle => _toClient.Idle && _toServer.Idle;

    internal void ClearBoth()
    {
        _toClient.Clear();
        _toServer.Clear();
    }

    internal void NotifyDisconnected() =>
        _server.EnqueueControl(new ServerTransportEvent(ETransportEvent.Disconnected, this,
            Array.Empty<byte>()));

    public void Close()
    {
        Client.CloseFromServer();
        NotifyDisconnected();
    }
}

public sealed class LossyLoopbackClientTransport : IClientTransport
{
    private readonly LossyLoopbackConnection _connection;

    internal LossyLoopbackClientTransport(LossyLoopbackConnection connection, LossPolicy policy) =>
        _connection = connection;

    public NetTraffic Traffic { get; } = new();

    public bool IsConnected { get; private set; } = true;

    internal void CloseFromServer() => IsConnected = false;

    public void Send(byte[] payload, ESendType sendType)
    {
        if (!IsConnected)
            return;
        Traffic.RecordSent(payload, payload.Length);
        _connection.SendToServer(payload, sendType);
    }

    public bool TryReceive(out byte[] payload)
    {
        if (!_connection.TryTakeFromServer(out payload))
            return false;
        Traffic.RecordReceived(payload, payload.Length);
        return true;
    }

    public void Update(double now)
    {
        _connection.AdvanceToClient();
        Traffic.Update(now);
    }

    public void Close()
    {
        if (!IsConnected)
            return;
        IsConnected = false;
        _connection.NotifyDisconnected();
    }
}
