using System.Collections.Generic;

namespace UnturnedGodot.Net;

// Hands out the byte player ids the wire protocol carries, and takes them back on disconnect.
//
// Ids are a byte because every snapshot in a StateUpdate spends one on them, and two values are
// already spoken for: 0 is what NetClient.PlayerId reads as before a Welcome arrives, and 255 is
// ZombieSystem's "no target player" (ZombieSystem.cs:57). That leaves 1..254 — 254 concurrent
// players, far past anything this server simulates, but only 254 *allocations* if ids are never
// reused, which is the trap a bare counter falls into.
//
// A returned id does not go straight back into circulation. PlayerLeft and PlayerJoined both travel
// reliably, and ReliableChannel delivers an unseen sequence the moment it arrives rather than in
// order, so a PlayerLeft that was lost and retransmitted can land *after* the PlayerJoined for
// whoever inherited the id. NetClient removes a remote by id alone, so that late leave would delete
// the newcomer — and nothing recreates them, because StateUpdate only updates remotes that already
// exist. Holding an id for the window in which a stale reliable frame can still arrive closes that.
// The window is ReliableChannel.GiveUpAfter: past it the sender has stopped retransmitting.
public sealed class PlayerIdPool
{
    public const byte First = 1;
    public const byte Last = 254;
    public const int Capacity = Last - First + 1;

    // Extra seconds on top of the sender's retry deadline, for a datagram already in flight when that
    // deadline passed. Generous next to any playable RTT — a peer that far behind is being dropped by the
    // state timeout anyway.
    public const double DeliveryGraceSeconds = 5.0;

    // How long a returned id waits before it is handed out again.
    //
    // Be precise about what this does and does not buy. GiveUpAfter bounds when the last PlayerLeft
    // retransmission is *sent*, not when it *arrives*, so no finite wait can prove a stale leave will not
    // land after the replacement's PlayerJoined — the grace makes it require a datagram delayed by more
    // than DeliveryGraceSeconds past the sender's final retry, which is not a window real networks hit.
    // Closing it by construction means making leave handling ordered or generation-aware, which is a wire
    // format change and belongs in its own PR; this one is about the byte counter that wrapped.
    public const double QuarantineSeconds = ReliableChannel.GiveUpAfter + DeliveryGraceSeconds;

    // Never rented, lowest first — so a fresh server hands out 1, 2, 3 rather than something arbitrary.
    private readonly SortedSet<byte> _neverRented = new();

    // Returned ids in release order, so the one that has been free longest cools first.
    private readonly Queue<(byte Id, double At)> _released = new();

    private readonly HashSet<byte> _rented = new();

    public PlayerIdPool()
    {
        for (int id = First; id <= Last; id++)
            _neverRented.Add((byte)id);
    }

    // Ids TryRent would actually hand out right now. Deliberately not "ids nobody holds": a cooling id is
    // not a slot the server can fill, and a caller that reads this to decide whether to advertise room —
    // a lobby listing, an admission gate, a test — must not be told 254 while every join is refused.
    public int AvailableAt(double now) =>
        _neverRented.Count + (NextRecycledIdHasCooled(now) ? CooledCount(now) : 0);

    // Ids held by nobody, cooling or not. The ceiling AvailableAt approaches once the quarantine drains.
    public int Unheld => _neverRented.Count + _released.Count;

    private int CooledCount(double now)
    {
        int cooled = 0;
        foreach ((byte _, double at) in _released)
        {
            if (now - at <= QuarantineSeconds)
                break; // release order, so the first hot one ends the run
            cooled++;
        }
        return cooled;
    }

    // False only when every id is in use — the caller must refuse the join rather than invent one.
    public bool TryRent(double now, out byte id)
    {
        if (_neverRented.Count > 0)
        {
            id = _neverRented.Min;
            _neverRented.Remove(id);
            _rented.Add(id);
            return true;
        }

        // Past this point every id has been used before, so one can only be handed out once it has
        // cooled. Refusing until then keeps the rule absolute — an id is never reused inside the window
        // where a stale PlayerLeft can still land — which is the whole point of the quarantine; a
        // fallback that reuses a hot id under pressure would leave the original race reachable, just
        // harder to hit. The failure this trades into is a join refused for at most QuarantineSeconds,
        // and only after 254 disconnects inside that window. Guarding against a peer that can produce
        // that much churn is connection rate limiting, which this class is the wrong place for.
        if (!NextRecycledIdHasCooled(now))
        {
            id = 0;
            return false;
        }

        id = _released.Dequeue().Id;
        _rented.Add(id);
        return true;
    }

    // Whether the next id TryRent would recycle has been free long enough to be safe to reuse.
    //
    // Strictly past the window, not at it. ReliableChannel drops a pending frame only once
    // `now - firstSent > GiveUpAfter`, so at exactly GiveUpAfter the old PlayerLeft is still pending and
    // that same Update retransmits it — ahead of the replacement's PlayerJoined, which UDP is free to
    // deliver first. One tick of slack on the wrong side of the comparison reopens the whole race.
    public bool NextRecycledIdHasCooled(double now) =>
        _released.Count > 0 && now - _released.Peek().At > QuarantineSeconds;

    // Idempotent: returning an id that was never rented, or returning one twice, leaves the pool as it
    // was. Disconnect paths are the ones that call this and they are not always reached exactly once.
    public void Return(byte id, double now)
    {
        if (_rented.Remove(id))
            _released.Enqueue((id, now));
    }
}
