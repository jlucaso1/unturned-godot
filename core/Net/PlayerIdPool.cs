using System.Collections.Generic;

namespace UnturnedGodot.Net;

// Hands out the byte player ids the wire protocol carries, and takes them back on disconnect.
//
// Ids are a byte because every snapshot in a StateUpdate spends one on them, and two values are
// already spoken for: 0 is what NetClient.PlayerId reads as before a Welcome arrives, and 255 is
// ZombieSystem's "no target player" (ZombieSystem.cs:57). That leaves 1..254 — 254 concurrent
// players, far past anything this server simulates, but only 254 *allocations* if ids are never
// reused, which is the trap a bare counter falls into.
public sealed class PlayerIdPool
{
    public const byte First = 1;
    public const byte Last = 254;
    public const int Capacity = Last - First + 1;

    // Lowest-first, so ids stay small and readable in logs rather than drifting up over a long session.
    private readonly SortedSet<byte> _free = new();

    public PlayerIdPool()
    {
        for (int id = First; id <= Last; id++)
            _free.Add((byte)id);
    }

    public int Available => _free.Count;

    // False when every id is in use — the caller must refuse the join rather than invent one.
    public bool TryRent(out byte id)
    {
        if (_free.Count == 0)
        {
            id = 0;
            return false;
        }

        id = _free.Min;
        _free.Remove(id);
        return true;
    }

    // Idempotent: returning an id that was never rented, or returning one twice, leaves the pool as it
    // was. Disconnect paths are the ones that call this and they are not always reached exactly once.
    public void Return(byte id)
    {
        if (id is >= First and <= Last)
            _free.Add(id);
    }
}
