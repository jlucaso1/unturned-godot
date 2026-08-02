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

    // How long a returned id waits before it is handed out again. Matches the reliable channel's
    // give-up deadline, which bounds how late a retransmitted PlayerLeft can arrive.
    public const double QuarantineSeconds = ReliableChannel.GiveUpAfter;

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

    // Ids not currently held by a player. Some of them may still be cooling.
    public int Available => _neverRented.Count + _released.Count;

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

        if (_released.Count == 0)
        {
            id = 0;
            return false;
        }

        // Past this point every id has been used before. Prefer one that has cooled; if none has,
        // hand out the one free longest anyway. Refusing instead would let 254 quick disconnects lock
        // every joiner out for QuarantineSeconds, which is a worse failure than reopening a narrow
        // ordering race on a single id under exactly that much churn.
        id = _released.Dequeue().Id;
        _rented.Add(id);
        return true;
    }

    // Whether the next id TryRent would recycle has been free long enough to be safe to reuse. False
    // while ids are still cooling — TryRent will hand one out regardless, see the note there.
    public bool NextRecycledIdHasCooled(double now) =>
        _released.Count > 0 && now - _released.Peek().At >= QuarantineSeconds;

    // Idempotent: returning an id that was never rented, or returning one twice, leaves the pool as it
    // was. Disconnect paths are the ones that call this and they are not always reached exactly once.
    public void Return(byte id, double now)
    {
        if (_rented.Remove(id))
            _released.Enqueue((id, now));
    }
}
