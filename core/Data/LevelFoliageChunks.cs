using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Godot;

namespace UnturnedGodot.Data;

public sealed class FoliageChunk
{
    public FoliageGroupKey Key { get; }
    public float[] Packed { get; }
    public FoliageBounds Bounds { get; internal set; }
    public int Count => Packed.Length / 12;
    public Vector3 Origin { get; private set; }
    public bool IsRebased { get; private set; }

    internal FoliageChunk(FoliageGroupKey key, int count)
    {
        Key = key;
        Packed = new float[count * 12];
        Bounds = FoliageBounds.Empty;
    }

    public void RebaseInPlace()
    {
        if (IsRebased || Bounds.Count == 0) return;
        Origin = (Bounds.Min + Bounds.Max) * 0.5f;
        for (int i = 0; i < Packed.Length; i += 12)
        {
            Packed[i + 3] -= Origin.X;
            Packed[i + 7] -= Origin.Y;
            Packed[i + 11] -= Origin.Z;
        }
        IsRebased = true;
    }
}

// Runtime foliage representation. The blob is scanned once for exact chunk sizes, then decoded directly
// into final MultiMesh-layout buffers. No per-tile transform pool coexists with another full chunk copy.
public sealed class LevelFoliageChunks
{
    public int Version { get; }
    public IReadOnlyList<Guid> AssetGuids { get; }
    public IReadOnlyList<FoliageChunk> Chunks { get; }
    public long StorageBytes { get; }
    public long DecodeBatchPeakBytes { get; }

    private LevelFoliageChunks(int version, IReadOnlyList<Guid> assets, IReadOnlyList<FoliageChunk> chunks,
        long decodeBatchPeakBytes)
    {
        Version = version;
        AssetGuids = assets;
        Chunks = chunks;
        long bytes = 0;
        foreach (FoliageChunk chunk in chunks) bytes += (long)chunk.Packed.Length * sizeof(float);
        StorageBytes = bytes;
        DecodeBatchPeakBytes = decodeBatchPeakBytes;
    }

    private readonly record struct RunPlan(int Group, int Start, int Count);

    // Returns foliage asset GUIDs without allocating any transform buffers. Version 2 carries the complete
    // list in its header, which is all cache-state/editor callers need; version 1 has a GUID on each run,
    // so walk only those run headers and skip the matrix bytes.
    public static IReadOnlyList<Guid>? ReadAssetGuids(string path)
    {
        if (!File.Exists(path))
            return null;

        using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int version = reader.ReadInt32();
        if (version is not (1 or 2))
            throw new NotSupportedException($"Unsupported Foliage.blob version {version}");

        if (version == 2)
        {
            int tileCount = reader.ReadInt32();
            for (int i = 0; i < tileCount; i++)
            {
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt64();
            }
            int assetCount = reader.ReadInt32();
            var assets = new List<Guid>(assetCount);
            for (int i = 0; i < assetCount; i++)
                assets.Add(new Guid(reader.ReadBytes(16)));
            return assets;
        }

        int legacyTileCount = reader.ReadInt32();
        var offsets = new long[legacyTileCount];
        for (int i = 0; i < legacyTileCount; i++)
        {
            reader.ReadInt32();
            reader.ReadInt32();
            offsets[i] = reader.ReadInt64();
        }
        long bodyStart = stream.Position;
        var found = new HashSet<Guid>();
        foreach (long offset in offsets)
        {
            stream.Position = bodyStart + offset;
            int runCount = reader.ReadInt32();
            for (int run = 0; run < runCount; run++)
            {
                found.Add(new Guid(reader.ReadBytes(16)));
                int matrices = reader.ReadInt32();
                stream.Position = checked(stream.Position + ((long)matrices * 65));
            }
        }
        return new List<Guid>(found);
    }

    // Every chunk owns a disjoint final buffer, so rebasing can run in parallel without synchronization.
    // The operation itself is idempotent, which also keeps callers that defensively rebase a chunk safe.
    public void RebaseAll(bool parallel = true)
    {
        if (parallel)
            System.Threading.Tasks.Parallel.ForEach(Chunks, static chunk => chunk.RebaseInPlace());
        else
            foreach (FoliageChunk chunk in Chunks) chunk.RebaseInPlace();
    }

    public static LevelFoliageChunks? Load(string path, int chunkTiles = 4)
    {
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int version = reader.ReadInt32();
        if (version is not (1 or 2))
            throw new NotSupportedException($"Unsupported Foliage.blob version {version}");
        int tileCount = reader.ReadInt32();
        var coords = new (int X, int Y, long Offset)[tileCount];
        for (int i = 0; i < tileCount; i++)
            coords[i] = (reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt64());
        var assets = new List<Guid>();
        var assetSet = new HashSet<Guid>();
        if (version >= 2)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                Guid asset = new Guid(reader.ReadBytes(16));
                assets.Add(asset);
                assetSet.Add(asset);
            }
        }
        long bodyStart = stream.Position;

        var groupByKey = new Dictionary<FoliageGroupKey, int>();
        var keys = new List<FoliageGroupKey>();
        var counts = new List<int>();
        var plans = new RunPlan[tileCount][];
        for (int tile = 0; tile < tileCount; tile++)
        {
            stream.Position = bodyStart + coords[tile].Offset;
            int runCount = reader.ReadInt32();
            plans[tile] = new RunPlan[runCount];
            int cx = (int)Math.Floor(coords[tile].X / (double)chunkTiles);
            int cy = (int)Math.Floor(coords[tile].Y / (double)chunkTiles);
            for (int run = 0; run < runCount; run++)
            {
                Guid asset = version >= 2 ? Asset(reader.ReadInt32(), assets) : new Guid(reader.ReadBytes(16));
                if (version == 1 && assetSet.Add(asset))
                    assets.Add(asset);
                int matrices = reader.ReadInt32();
                int group = -1, start = 0;
                if (matrices > 0)
                {
                    var key = new FoliageGroupKey(cx, cy, asset);
                    if (!groupByKey.TryGetValue(key, out group))
                    {
                        group = keys.Count;
                        groupByKey.Add(key, group);
                        keys.Add(key);
                        counts.Add(0);
                    }
                    start = counts[group];
                    counts[group] += matrices;
                }
                plans[tile][run] = new RunPlan(group, start, matrices);
                stream.Position = checked(stream.Position + ((long)matrices * 65));
            }
        }

        var chunks = new FoliageChunk[keys.Count];
        for (int i = 0; i < chunks.Length; i++) chunks[i] = new FoliageChunk(keys[i], counts[i]);
        var bounds = new FoliageBounds[tileCount][];
        int[] order = new int[tileCount];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => coords[a].Offset.CompareTo(coords[b].Offset));
        int batchTiles = int.TryParse(System.Environment.GetEnvironmentVariable("UG_FOLIAGE_LOAD_BATCH"),
            out int configured) ? Math.Clamp(configured, 128, 16384) : 512;
        long decodeBatchPeakBytes = 0;
        for (int first = 0; first < tileCount; first += batchTiles)
        {
            int last = Math.Min(first + batchTiles, tileCount);
            long relativeStart = coords[order[first]].Offset;
            long relativeEnd = last < tileCount ? coords[order[last]].Offset : stream.Length - bodyStart;
            var batch = new byte[checked((int)(relativeEnd - relativeStart))];
            decodeBatchPeakBytes = Math.Max(decodeBatchPeakBytes, batch.Length);
            stream.Position = bodyStart + relativeStart;
            stream.ReadExactly(batch);
            System.Threading.Tasks.Parallel.For(first, last, at =>
            {
                int tile = order[at];
                int p = checked((int)(coords[tile].Offset - relativeStart));
                int runCount = ReadInt32(batch, ref p);
                bounds[tile] = new FoliageBounds[runCount];
                for (int run = 0; run < runCount; run++)
                {
                    if (version >= 2) ReadInt32(batch, ref p); else p += 16;
                    int matrices = ReadInt32(batch, ref p);
                    RunPlan plan = plans[tile][run];
                    FoliageBounds measured = FoliageBounds.Empty;
                    for (int matrix = 0; matrix < matrices; matrix++)
                    {
                        int offset = (plan.Start + matrix) * 12;
                        Pack(batch, ref p, chunks[plan.Group].Packed, offset);
                        measured = measured.Include(chunks[plan.Group].Packed, offset);
                        p++;
                    }
                    bounds[tile][run] = measured;
                }
            });
        }
        for (int tile = 0; tile < tileCount; tile++)
            for (int run = 0; run < plans[tile].Length; run++)
                if (plans[tile][run].Group >= 0)
                {
                    int group = plans[tile][run].Group;
                    chunks[group].Bounds = chunks[group].Bounds.Include(bounds[tile][run]);
                }
        return new LevelFoliageChunks(version, assets, chunks, decodeBatchPeakBytes);
    }

    private static Guid Asset(int index, IReadOnlyList<Guid> assets) =>
        index >= 0 && index < assets.Count ? assets[index] : Guid.Empty;

    private static int ReadInt32(byte[] data, ref int p)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p)); p += 4; return value;
    }

    private static void Pack(byte[] data, ref int p, float[] dest, int o)
    {
        ReadOnlySpan<float> m = MemoryMarshal.Cast<byte, float>(data.AsSpan(p, 64)); p += 64;
        dest[o] = m[0]; dest[o + 1] = m[4]; dest[o + 2] = -m[8]; dest[o + 3] = m[12];
        dest[o + 4] = m[1]; dest[o + 5] = m[5]; dest[o + 6] = -m[9]; dest[o + 7] = m[13];
        dest[o + 8] = -m[2]; dest[o + 9] = -m[6]; dest[o + 10] = m[10]; dest[o + 11] = -m[14];
    }
}
