using System;
using System.Collections.Generic;
using System.Threading;

namespace UnturnedGodot.Unity;

// Pulls a set of byte ranges out of a stream that can only be read forwards, which is what a bundle's
// LZMA block is: seeking back would mean decompressing it again from the start.
//
// The ranges are visited in offset order, the gaps between them are read into a reusable scratch buffer
// and thrown away, and a range that starts inside the previous one is served from the bytes already in
// hand (content-identical textures do share their pixels). Peak memory is one range plus the scratch,
// not the whole node.
public static class ForwardRegions
{
    public readonly struct Region
    {
        public readonly long Offset;
        public readonly int Size;
        public readonly int Index; // the caller's own id for this range, handed back with its bytes

        public Region(long offset, int size, int index)
        {
            Offset = offset;
            Size = size;
            Index = index;
        }
    }

    // Reads `regions` out of `read` (a forward-only reader returning how many bytes it produced), calling
    // `emit` with each range's bytes. `nodeSize` is how long the node is, so the remainder is drained and
    // whatever follows in the stream stays aligned. Ranges are read in offset order regardless of the
    // order given; a range the stream ends before completing is skipped.
    public static void Read(Func<byte[], int, int, int> read, long nodeSize, IEnumerable<Region> regions,
        Action<int, byte[]> emit, int scratchSize = 4 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        var ordered = new List<Region>(regions);
        ordered.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var scratch = new byte[Math.Max(scratchSize, 1)];
        long position = 0;
        long previousStart = 0;
        byte[] previous = Array.Empty<byte>();

        foreach (Region region in ordered)
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            if (region.Size < 0 || region.Offset < 0 || region.Offset + region.Size > nodeSize)
                continue;

            var buffer = new byte[region.Size];
            int have = 0;

            // Overlap with the range just read: those bytes are gone from the stream, so take them back
            // out of that range's buffer.
            if (region.Offset < previousStart + previous.Length)
            {
                int from = (int)(region.Offset - previousStart);
                have = Math.Min(previous.Length - from, buffer.Length);
                Buffer.BlockCopy(previous, from, buffer, 0, have);
            }

            while (position < region.Offset + have) // skip the gap ahead of what is still missing
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                int want = (int)Math.Min(scratch.Length, region.Offset + have - position);
                int got = read(scratch, 0, want);
                if (got <= 0)
                    return;
                position += got;
            }

            while (have < buffer.Length)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                int got = read(buffer, have, buffer.Length - have);
                if (got <= 0)
                    return;
                have += got;
                position += got;
            }

            // Keep whichever buffer reaches furthest into the node, not simply the last one: a range
            // nested inside its predecessor covers less, and a later range needing the tail of the outer
            // one would then read from wherever the stream now sits instead of from bytes already in hand.
            if (region.Offset + buffer.Length >= previousStart + previous.Length)
            {
                previousStart = region.Offset;
                previous = buffer;
            }

            emit(region.Index, buffer);
        }

        while (position < nodeSize) // drain what is left of the node
        {
            if (cancellationToken.IsCancellationRequested)
                return;
            int want = (int)Math.Min(scratch.Length, nodeSize - position);
            int got = read(scratch, 0, want);
            if (got <= 0)
                return;
            position += got;
        }
    }
}
