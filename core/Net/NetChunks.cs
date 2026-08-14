using System;
using System.Collections;
using System.Collections.Generic;

namespace UnturnedGodot.Net;

// The MTU discipline, in one place, for every message that walks a collection.
//
// The transport's MaxPayloadBytes is 16 KiB, and that is the ceiling on what a datagram may be — it is
// not a budget anything should spend. The PATH's limit is about 1500 bytes, and a UDP datagram past it
// is split into IP fragments that the network layer reassembles: lose ANY one of them and the whole
// datagram is gone. So the effective loss rate of a stream does not merely track the link's, it
// compounds with the fragment count — a 4 KB snapshot is three fragments, and at 1% link loss it
// arrives about 97% of the time rather than 99%. That is worst exactly when the server is busiest and
// the payloads are largest, which is the opposite of what a degrading system should do.
//
// ZombieList already knew this — "reliable datagrams are not fragmented, so the full population ships
// in MTU-sized chunks" — and nothing else did. This is that rule, factored out so it applies by
// construction rather than by whoever remembered.
public static class NetChunks
{
    // The budget one datagram may spend on payload.
    //
    // 1200 rather than a computed 1500 - 28: the path MTU is not the local link's, and every tunnel
    // between two players takes a bite out of it — PPPoE 8, IPv6-in-IPv4 20, WireGuard 60, a corporate
    // VPN more. 1200 is the number QUIC picked as the largest datagram it will assume works anywhere
    // without discovery, and there is no reason to be braver than QUIC about it. The reliable channel's
    // own three bytes of framing sit outside this, and 1200 + 3 + 28 is still comfortably under 1500.
    public const int MaxPayloadBytes = 1200;

    // How many fixed-size records fit in one datagram after the message's own header.
    //
    // At least one, always: a record larger than the whole budget cannot be split by this mechanism, and
    // silently emitting zero of them would be a payload that says "nothing here" about a collection that
    // is not empty — the worst failure a chunker has, because the reader believes it.
    public static int Capacity(int headerBytes, int itemBytes)
    {
        if (itemBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(itemBytes), "a record has to have a size");
        return Math.Max(1, (MaxPayloadBytes - headerBytes) / itemBytes);
    }

    // Splits a collection across as many payloads as it takes, each written by `write` from a window
    // onto the original list.
    //
    // Always returns at least one payload, even for an empty collection: an empty Welcome still has to
    // tell the client its own id, and an empty ZombieList is how a region says it holds nothing.
    //
    // The window is a view, not a copy — a per-tick payload builder must not allocate a List per chunk
    // per connection — and it is handed to `write` as IReadOnlyList so every existing writer takes it
    // unchanged.
    public static List<byte[]> Split<T>(IReadOnlyList<T> items, int capacity,
        Func<IReadOnlyList<T>, byte[]> write)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(write);
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "a chunk has to hold something");

        var chunks = new List<byte[]>((items.Count + capacity - 1) / capacity);
        if (items.Count <= capacity)
        {
            chunks.Add(write(items));
            return chunks;
        }

        for (int at = 0; at < items.Count; at += capacity)
            chunks.Add(write(new Window<T>(items, at, Math.Min(capacity, items.Count - at))));
        return chunks;
    }

    // A read-only view of a slice of a list. Sealed and allocation-light: one small object per chunk
    // rather than a copy of the chunk's contents.
    private sealed class Window<T> : IReadOnlyList<T>
    {
        private readonly IReadOnlyList<T> _items;
        private readonly int _from;

        public Window(IReadOnlyList<T> items, int from, int count)
        {
            _items = items;
            _from = from;
            Count = count;
        }

        public int Count { get; }

        public T this[int index] => index >= 0 && index < Count
            ? _items[_from + index]
            : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
                yield return _items[_from + i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
