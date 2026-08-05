using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Reads the vertex buffers Unity moved out of a bundle's SerializedFile and into one of its .resS nodes.
//
// Most meshes keep their buffer inline and never come here. Unity streams one out on its own schedule,
// and which meshes that hits has nothing to do with what they are: a vehicle's Wheel_LOD0 is streamed
// while the Wheel_LOD1 beside it is inline, and the body of the same vehicle is inline too. Before this,
// a streamed buffer simply made the mesh unusable — the part was dropped from the level being built, so
// every shipped vehicle rendered with its wheels missing at close range and back again at distance.
//
// The ranges are tiny (~12 KB each, 29 of them across everything PEI places) but they sit deep inside a
// stream node that only decodes forwards, so this is one pass over the bundle. It is worth it exactly
// once: the meshes it completes are then cached per GUID like any other, and a warm cache never asks.
public static class VertexStreamReader
{
    // One mesh's external vertex buffer, addressed the way the caller will want it back.
    public readonly record struct Request(long PathId, UnityMesh.StreamRef Stream);

    // The bytes for as many of the requests as the bundle could serve, by path id. A request naming a
    // stream node this bundle does not carry, or a range past its end, is simply absent from the result —
    // the mesh then stays unusable, which is what it was before this existed.
    public static Dictionary<long, byte[]> Read(string bundlePath, IReadOnlyList<Request> requests,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<long, byte[]>();
        if (requests.Count == 0)
            return result;

        using MasterBundleStream? stream = MasterBundleStream.OpenFile(bundlePath);
        if (stream == null)
            return ReadWholeBundle(bundlePath, requests, cancellationToken);

        // Blob order, because the decoder cannot rewind: every node up to the last one owing bytes has to
        // come out of the stream whether or not anything wants it.
        var ordered = new List<MasterBundleStream.Node>(stream.Nodes);
        ordered.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var wants = new List<BundlePass.Want>(requests.Count);
        var nodes = new List<BundlePass.Node>();
        var streamNodes = new List<MasterBundleStream.Node>();
        foreach (MasterBundleStream.Node node in ordered)
            if (IsStreamNode(node.Path))
            {
                nodes.Add(new BundlePass.Node(LastSegment(node.Path), node.Size));
                streamNodes.Add(node);
            }

        for (int i = 0; i < requests.Count; i++)
        {
            UnityMesh.StreamRef s = requests[i].Stream;
            if (s.IsStreamed)
                wants.Add(new BundlePass.Want(s.FileName, s.Offset, s.Size, i));
        }
        if (wants.Count == 0)
            return result;

        List<BundlePass.Step> plan = BundlePass.Plan(nodes, wants);
        if (plan.Count == 0)
            return result;

        // The decoder starts at the front of the blob, so each node is reached by discarding everything
        // between the cursor and its own offset — the SerializedFile the caller has already read, and any
        // node the plan had no reason to touch. Positioning by absolute offset rather than by summing
        // sizes is what keeps this right if a bundle ever leaves a gap between two nodes.
        foreach (BundlePass.Step step in plan)
        {
            if (cancellationToken.IsCancellationRequested)
                return result;

            if (!Skip(stream, streamNodes[step.Node].Offset - stream.Cursor, cancellationToken))
                return result;

            ForwardRegions.Read(stream.Read, step.ReadTo, step.Regions,
                (index, bytes) => result[requests[index].PathId] = bytes,
                cancellationToken: cancellationToken);
        }

        return result;
    }

    // The same ranges out of a bundle the forward reader cannot open: a multi-block or LZ4 bundle, which
    // is a shape workshop content does ship. Decoding the whole blob is what the ordinary reader avoids,
    // but the alternative here is worse than slow — it is a prefab cached with its streamed parts silently
    // dropped and stamped current, so nothing ever asks for them again. Only a bundle that both fails to
    // open as a stream and actually has streamed geometry pays for it.
    private static Dictionary<long, byte[]> ReadWholeBundle(string bundlePath,
        IReadOnlyList<Request> requests, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, byte[]>();
        UnityBundle bundle;
        try
        {
            bundle = UnityBundle.Read(File.ReadAllBytes(bundlePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return result; // unreadable: the caller's meshes lose their streamed parts, as they did before
        }

        // Bundle.Files is keyed by node path; a stream reference names only the last segment of it.
        var byFileName = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, byte[]> file in bundle.Files)
            byFileName[LastSegment(file.Key)] = file.Value;

        foreach (Request request in requests)
        {
            if (cancellationToken.IsCancellationRequested)
                return result;
            UnityMesh.StreamRef s = request.Stream;
            if (!s.IsStreamed || !byFileName.TryGetValue(s.FileName, out byte[]? node)
                || s.Offset < 0 || s.Offset + s.Size > node.Length)
            {
                continue;
            }

            var bytes = new byte[s.Size];
            Array.Copy(node, s.Offset, bytes, 0, s.Size);
            result[request.PathId] = bytes;
        }
        return result;
    }

    // Pulls `count` bytes out of the stream and discards them, in chunks rather than one allocation: the
    // gap in front of the first stream node is the whole SerializedFile, hundreds of megabytes.
    private static bool Skip(MasterBundleStream stream, long count, CancellationToken cancellationToken)
    {
        if (count <= 0)
            return count == 0; // already there; a negative gap means the caller asked to rewind

        var scratch = new byte[Math.Min(count, 4 * 1024 * 1024)];
        long left = count;
        while (left > 0)
        {
            if (cancellationToken.IsCancellationRequested)
                return false;
            int read = stream.Read(scratch, 0, (int)Math.Min(left, scratch.Length));
            if (read <= 0)
                return false;
            left -= read;
        }
        return true;
    }

    private static bool IsStreamNode(string path) =>
        path.EndsWith(".resS", StringComparison.Ordinal)
        || path.EndsWith(".resource", StringComparison.Ordinal);

    private static string LastSegment(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }
}
