using System;
using System.Collections.Generic;

namespace UnturnedGodot.Unity;

// Plans one forward pass over a bundle's stream nodes (.resS/.resource): which of them have to be read,
// how far into each, and where the pass can stop.
//
// Decoding is the entire cost of reading a bundle (a single LZMA block, one core, no seeking), so the
// plan is what keeps a load from paying for bytes nobody wants: a node with nothing in it is only read
// because a later node still owes something, the last node that owes anything is read no further than its
// last wanted range, and everything after that is never touched. The game's own bundle ends with 20 MB of
// .resource and California 2's with 61 MB; neither is decoded now.
public static class BundlePass
{
    // A stream node inside the bundle, in blob order.
    public readonly record struct Node(string FileName, long Size);

    // A wanted byte range: the stream file that holds it, where it sits inside that file, and the caller's
    // own id for it (handed back with the bytes).
    public readonly record struct Want(string StreamFile, long Offset, int Size, int Index);

    // One node the pass has to read: its index in `nodes`, how far into it to read, and the ranges to take
    // out of it (empty when the node is only being traversed to reach a later one).
    public readonly record struct Step(int Node, long ReadTo, IReadOnlyList<ForwardRegions.Region> Regions);

    // The nodes to read, in order. Wants naming a file this bundle does not carry are dropped: waiting on
    // one of those would keep the pass reading to the very end for bytes that were never here.
    public static List<Step> Plan(IReadOnlyList<Node> nodes, IEnumerable<Want> wants)
    {
        // Each file name is served by the first node carrying it; a bundle does not repeat one.
        var nodeByFile = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < nodes.Count; i++)
            if (!nodeByFile.ContainsKey(nodes[i].FileName))
                nodeByFile[nodes[i].FileName] = i;

        var regions = new Dictionary<int, List<ForwardRegions.Region>>();
        foreach (Want want in wants)
        {
            if (want.Size <= 0 || want.Offset < 0
                || !nodeByFile.TryGetValue(want.StreamFile, out int node)
                || want.Offset + want.Size > nodes[node].Size)
            {
                continue;
            }

            if (!regions.TryGetValue(node, out List<ForwardRegions.Region>? list))
                regions[node] = list = new List<ForwardRegions.Region>();
            list.Add(new ForwardRegions.Region(want.Offset, want.Size, want.Index));
        }

        int last = -1;
        foreach (int node in regions.Keys)
            last = Math.Max(last, node);

        var steps = new List<Step>();
        for (int i = 0; i <= last; i++)
        {
            // The last node is the one that decides where the pass ends, so it always has ranges; anything
            // before it without ranges still has to come out of the stream, which cannot skip.
            if (!regions.TryGetValue(i, out List<ForwardRegions.Region>? list))
            {
                steps.Add(new Step(i, nodes[i].Size, Array.Empty<ForwardRegions.Region>()));
                continue;
            }

            list.Sort((a, b) => a.Offset.CompareTo(b.Offset));

            long readTo = nodes[i].Size;
            if (i == last)
            {
                readTo = 0;
                foreach (ForwardRegions.Region region in list)
                    readTo = Math.Max(readTo, region.Offset + region.Size);
            }

            steps.Add(new Step(i, readTo, list));
        }

        return steps;
    }
}
