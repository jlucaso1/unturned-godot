using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Godot;

namespace UnturnedGodot.Data;

public readonly record struct FoliageRunDescriptor(long MatrixOffset, int Count);

public sealed class FoliageChunkMetadata
{
    public FoliageGroupKey Key { get; }
    public int Count { get; }
    public FoliageBounds Bounds { get; }
    public IReadOnlyList<FoliageRunDescriptor> Runs { get; }
    public Vector3 Centre => Bounds.Count == 0 ? Vector3.Zero : (Bounds.Min + Bounds.Max) * 0.5f;
    public float PositionRadius => Bounds.Count == 0 ? 0f : (Bounds.Max - Bounds.Min).Length() * 0.5f;

    internal FoliageChunkMetadata(FoliageGroupKey key, int count, FoliageBounds bounds,
        IReadOnlyList<FoliageRunDescriptor> runs)
    {
        Key = key;
        Count = count;
        Bounds = bounds;
        Runs = runs;
    }
}

// Compact, seekable description of Foliage.blob. It retains offsets and bounds, never map-wide transform
// buffers, so the runtime can decode only the chunks whose existing visibility range approaches a camera.
public sealed class FoliageResidencyIndex
{
    private const int CacheVersion = 2;
    // Identity for a cache, not a signature: it exists to catch a source edited behind an unchanged
    // path, length and timestamp. That is accident detection, so it does not need a cryptographic hash,
    // and SHA-256 made every warm boot re-read the whole blob at ~1 GiB/s just to prove it had not
    // changed. xxHash64 answers the same question several times faster over the same bytes.
    private const int SourceHashBytes = 8;
    private const string Magic = "UGFIDX1";
    private const int MatrixRecordBytes = 65;
    private const int DecodeRecordsPerBatch = 4096;

    public string SourcePath { get; }
    public int BlobVersion { get; }
    public int ChunkTiles { get; }
    public IReadOnlyList<Guid> AssetGuids { get; }
    public IReadOnlyList<FoliageChunkMetadata> Chunks { get; }
    public long IndexedInstances { get; }
    public long DecodedStorageBytes => IndexedInstances * 12L * sizeof(float);

    private readonly long _sourceLength;
    private readonly long _sourceWriteTicks;
    private readonly byte[] _sourceHash;

    private FoliageResidencyIndex(string sourcePath, long sourceLength, long sourceWriteTicks,
        byte[] sourceHash, int blobVersion, int chunkTiles, IReadOnlyList<Guid> assets,
        IReadOnlyList<FoliageChunkMetadata> chunks)
    {
        SourcePath = sourcePath;
        _sourceLength = sourceLength;
        _sourceWriteTicks = sourceWriteTicks;
        _sourceHash = sourceHash;
        BlobVersion = blobVersion;
        ChunkTiles = chunkTiles;
        AssetGuids = assets;
        Chunks = chunks;
        long instances = 0;
        foreach (FoliageChunkMetadata chunk in chunks)
            instances += chunk.Count;
        IndexedInstances = instances;
    }

    private sealed class ChunkBuilder
    {
        public FoliageGroupKey Key;
        public int Count;
        public FoliageBounds Bounds = FoliageBounds.Empty;
        public readonly List<FoliageRunDescriptor> Runs = new();

        public ChunkBuilder(FoliageGroupKey key) => Key = key;
    }

    public static FoliageResidencyIndex? LoadOrBuild(string sourcePath, string cachePath, int chunkTiles,
        out bool cacheHit)
    {
        cacheHit = false;
        if (!File.Exists(sourcePath))
            return null;
        if (TryRead(cachePath, sourcePath, chunkTiles, out FoliageResidencyIndex? cached))
        {
            cacheHit = true;
            return cached;
        }

        FoliageResidencyIndex built = Build(sourcePath, chunkTiles);
        try { built.Write(cachePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The source remains authoritative. An unwritable cache only makes the next load rebuild it.
        }
        return built;
    }

    public static FoliageResidencyIndex Build(string sourcePath, int chunkTiles = 4)
    {
        if (chunkTiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkTiles));
        string fullPath = Path.GetFullPath(sourcePath);
        // Keep a read-share-only handle open for the complete scan and hash. A writer therefore cannot
        // replace records between the metadata pass and its identity hash and leave a mixed sidecar.
        using FileStream stream = OpenRead(fullPath, FileOptions.RandomAccess);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        int version = reader.ReadInt32();
        if (version is not (1 or 2))
            throw new NotSupportedException($"Unsupported Foliage.blob version {version}");
        int tileCount = ReadCount(reader, 16_777_216, "tile");
        var tiles = new (int X, int Y, long Offset)[tileCount];
        for (int i = 0; i < tileCount; i++)
            tiles[i] = (reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt64());

        var assets = new List<Guid>();
        var assetSet = new HashSet<Guid>();
        if (version == 2)
        {
            int assetCount = ReadCount(reader, 1_048_576, "asset");
            for (int i = 0; i < assetCount; i++)
            {
                Guid asset = ReadGuid(reader);
                assets.Add(asset);
                assetSet.Add(asset);
            }
        }
        long bodyStart = stream.Position;

        var byKey = new Dictionary<FoliageGroupKey, int>();
        var builders = new List<ChunkBuilder>();
        byte[] records = new byte[DecodeRecordsPerBatch * MatrixRecordBytes];
        foreach ((int tileX, int tileY, long relativeOffset) in tiles)
        {
            long tileOffset = checked(bodyStart + relativeOffset);
            if (tileOffset < bodyStart || tileOffset > stream.Length - sizeof(int))
                throw new InvalidDataException("Foliage tile offset is outside the source file");
            stream.Position = tileOffset;
            int runCount = ReadCount(reader, 1_048_576, "run");
            int chunkX = (int)Math.Floor(tileX / (double)chunkTiles);
            int chunkY = (int)Math.Floor(tileY / (double)chunkTiles);
            for (int run = 0; run < runCount; run++)
            {
                Guid asset = version == 2 ? Asset(reader.ReadInt32(), assets) : ReadGuid(reader);
                if (version == 1 && assetSet.Add(asset))
                    assets.Add(asset);
                int matrices = ReadCount(reader, 100_000_000, "matrix");
                long matrixOffset = stream.Position;
                if (matrices == 0)
                    continue;

                var key = new FoliageGroupKey(chunkX, chunkY, asset);
                if (!byKey.TryGetValue(key, out int group))
                {
                    group = builders.Count;
                    byKey.Add(key, group);
                    builders.Add(new ChunkBuilder(key));
                }
                ChunkBuilder builder = builders[group];
                builder.Count = checked(builder.Count + matrices);
                builder.Runs.Add(new FoliageRunDescriptor(matrixOffset, matrices));

                int remaining = matrices;
                while (remaining > 0)
                {
                    int batchRecords = Math.Min(remaining, DecodeRecordsPerBatch);
                    int bytes = checked(batchRecords * MatrixRecordBytes);
                    stream.ReadExactly(records.AsSpan(0, bytes));
                    for (int i = 0; i < batchRecords; i++)
                        builder.Bounds = IncludeRecord(builder.Bounds,
                            records.AsSpan(i * MatrixRecordBytes, MatrixRecordBytes));
                    remaining -= batchRecords;
                }
            }
        }

        var chunks = new FoliageChunkMetadata[builders.Count];
        for (int i = 0; i < chunks.Length; i++)
        {
            ChunkBuilder builder = builders[i];
            chunks[i] = new FoliageChunkMetadata(builder.Key, builder.Count, builder.Bounds,
                builder.Runs.ToArray());
        }
        stream.Position = 0;
        byte[] sourceHash = HashSource(stream);
        var info = new FileInfo(fullPath);
        return new FoliageResidencyIndex(fullPath, info.Length, info.LastWriteTimeUtc.Ticks,
            sourceHash, version, chunkTiles, assets, chunks);
    }

    public FoliageChunk DecodeChunk(int index, CancellationToken cancellationToken = default)
    {
        if ((uint)index >= (uint)Chunks.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        byte[] records = ArrayPool<byte>.Shared.Rent(DecodeRecordsPerBatch * MatrixRecordBytes);
        try
        {
            using var stream = new FileStream(SourcePath, FileMode.Open, System.IO.FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
            return DecodeChunk(index, stream, records, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(records);
        }
    }

    public IReadOnlyList<FoliageChunk> DecodeChunks(IReadOnlyList<int> indices,
        CancellationToken cancellationToken = default)
    {
        var result = new FoliageChunk[indices.Count];
        byte[] records = ArrayPool<byte>.Shared.Rent(DecodeRecordsPerBatch * MatrixRecordBytes);
        try
        {
            using var stream = new FileStream(SourcePath, FileMode.Open, System.IO.FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.RandomAccess);
            for (int i = 0; i < indices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int index = indices[i];
                if ((uint)index >= (uint)Chunks.Count)
                    throw new ArgumentOutOfRangeException(nameof(indices));
                result[i] = DecodeChunk(index, stream, records, cancellationToken);
            }
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(records);
        }
    }

    private FoliageChunk DecodeChunk(int index, FileStream stream, byte[] records,
        CancellationToken cancellationToken)
    {
        FoliageChunkMetadata metadata = Chunks[index];
        var chunk = new FoliageChunk(metadata.Key, metadata.Count) { Bounds = metadata.Bounds };
        int destination = 0;
        foreach (FoliageRunDescriptor run in metadata.Runs)
        {
            stream.Position = run.MatrixOffset;
            int remaining = run.Count;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int batchRecords = Math.Min(remaining, DecodeRecordsPerBatch);
                int bytes = checked(batchRecords * MatrixRecordBytes);
                stream.ReadExactly(records.AsSpan(0, bytes));
                int source = 0;
                for (int i = 0; i < batchRecords; i++)
                {
                    Pack(records, source, chunk.Packed, destination);
                    source += MatrixRecordBytes;
                    destination++;
                }
                remaining -= batchRecords;
            }
        }
        if (destination != metadata.Count)
            throw new InvalidDataException("Foliage chunk run table did not fill its declared buffer");
        chunk.RebaseInPlace();
        return chunk;
    }

    public void Write(string cachePath)
    {
        string? directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, System.IO.FileAccess.Write,
                FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                writer.Write(CacheVersion);
                writer.Write(SourcePath);
                writer.Write(_sourceLength);
                writer.Write(_sourceWriteTicks);
                writer.Write(_sourceHash);
                writer.Write(BlobVersion);
                writer.Write(ChunkTiles);
                writer.Write(AssetGuids.Count);
                foreach (Guid asset in AssetGuids)
                    writer.Write(asset.ToByteArray());
                writer.Write(Chunks.Count);
                foreach (FoliageChunkMetadata chunk in Chunks)
                {
                    writer.Write(chunk.Key.ChunkX);
                    writer.Write(chunk.Key.ChunkY);
                    writer.Write(chunk.Key.Asset.ToByteArray());
                    writer.Write(chunk.Count);
                    WriteVector(writer, chunk.Bounds.Min);
                    WriteVector(writer, chunk.Bounds.Max);
                    writer.Write(chunk.Bounds.MaxScaleSquared);
                    writer.Write(chunk.Bounds.Count);
                    writer.Write(chunk.Runs.Count);
                    foreach (FoliageRunDescriptor run in chunk.Runs)
                    {
                        writer.Write(run.MatrixOffset);
                        writer.Write(run.Count);
                    }
                }
            }
            File.Move(temporary, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    // Streams the source so a 273 MB blob is never materialised just to be verified.
    private static byte[] HashSource(Stream source)
    {
        var hash = new XxHash64();
        hash.Append(source);
        return hash.GetCurrentHash();
    }

    public static bool TryRead(string cachePath, string sourcePath, int chunkTiles,
        out FoliageResidencyIndex? index)
    {
        index = null;
        if (!File.Exists(cachePath) || !File.Exists(sourcePath))
            return false;
        try
        {
            string fullPath = Path.GetFullPath(sourcePath);
            var source = new FileInfo(fullPath);
            using var stream = new FileStream(cachePath, FileMode.Open, System.IO.FileAccess.Read,
                FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadString() != Magic || reader.ReadInt32() != CacheVersion
                || !PathEquals(reader.ReadString(), fullPath)
                || reader.ReadInt64() != source.Length
                || reader.ReadInt64() != source.LastWriteTimeUtc.Ticks)
                return false;
            byte[] expectedHash = reader.ReadBytes(SourceHashBytes);
            if (expectedHash.Length != SourceHashBytes)
                return false;
            byte[] actualHash;
            using (FileStream hashStream = OpenRead(fullPath, FileOptions.SequentialScan))
                actualHash = HashSource(hashStream);
            if (!expectedHash.AsSpan().SequenceEqual(actualHash))
                return false;
            int blobVersion = reader.ReadInt32();
            if (reader.ReadInt32() != chunkTiles)
                return false;
            int assetCount = ReadCount(reader, 1_048_576, "cached asset");
            var assets = new Guid[assetCount];
            for (int i = 0; i < assets.Length; i++)
                assets[i] = ReadGuid(reader);
            int chunkCount = ReadCount(reader, 16_777_216, "cached chunk");
            var chunks = new FoliageChunkMetadata[chunkCount];
            for (int i = 0; i < chunks.Length; i++)
            {
                var key = new FoliageGroupKey(reader.ReadInt32(), reader.ReadInt32(), ReadGuid(reader));
                int count = ReadCount(reader, 100_000_000, "cached instance");
                Vector3 min = ReadVector(reader);
                Vector3 max = ReadVector(reader);
                var bounds = new FoliageBounds(min, max, reader.ReadSingle(), reader.ReadInt32());
                if (bounds.Count != count)
                    return false;
                int runCount = ReadCount(reader, 1_048_576, "cached run");
                var runs = new FoliageRunDescriptor[runCount];
                int runInstances = 0;
                for (int run = 0; run < runCount; run++)
                {
                    long offset = reader.ReadInt64();
                    int runSize = ReadCount(reader, 100_000_000, "cached run instance");
                    if (offset < 0 || offset > source.Length - ((long)runSize * MatrixRecordBytes))
                        return false;
                    runInstances = checked(runInstances + runSize);
                    runs[run] = new FoliageRunDescriptor(offset, runSize);
                }
                if (runInstances != count)
                    return false;
                chunks[i] = new FoliageChunkMetadata(key, count, bounds, runs);
            }
            if (stream.Position != stream.Length)
                return false;
            index = new FoliageResidencyIndex(fullPath, source.Length, source.LastWriteTimeUtc.Ticks,
                actualHash, blobVersion, chunkTiles, assets, chunks);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException
            or InvalidDataException or ArgumentException or OverflowException or FormatException)
        {
            return false;
        }
    }

    public static string CacheFileName(string sourcePath)
    {
        byte[] identity = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(sourcePath)));
        return Convert.ToHexString(identity.AsSpan(0, 16)).ToLowerInvariant() + ".fidx";
    }

    private static FileStream OpenRead(string path, FileOptions options) =>
        new(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read, 1024 * 1024, options);

    private static int ReadCount(BinaryReader reader, int maximum, string kind)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum)
            throw new InvalidDataException($"Invalid foliage {kind} count {count}");
        return count;
    }

    private static Guid ReadGuid(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(16);
        if (bytes.Length != 16)
            throw new EndOfStreamException();
        return new Guid(bytes);
    }

    private static Guid Asset(int index, IReadOnlyList<Guid> assets) =>
        index >= 0 && index < assets.Count ? assets[index] : Guid.Empty;

    private static FoliageBounds IncludeRecord(FoliageBounds bounds, ReadOnlySpan<byte> record)
    {
        ReadOnlySpan<float> matrix = MemoryMarshal.Cast<byte, float>(record[..64]);
        var position = new Vector3(matrix[12], matrix[13], -matrix[14]);
        float scaleSquared =
            (matrix[0] * matrix[0]) + (matrix[4] * matrix[4]) + (matrix[8] * matrix[8])
            + (matrix[1] * matrix[1]) + (matrix[5] * matrix[5]) + (matrix[9] * matrix[9])
            + (matrix[2] * matrix[2]) + (matrix[6] * matrix[6]) + (matrix[10] * matrix[10]);
        return new FoliageBounds(bounds.Count == 0 ? position : bounds.Min.Min(position),
            bounds.Count == 0 ? position : bounds.Max.Max(position),
            MathF.Max(bounds.MaxScaleSquared, scaleSquared), bounds.Count + 1);
    }

    private static void Pack(byte[] data, int p, float[] destination, int record)
    {
        ReadOnlySpan<float> m = MemoryMarshal.Cast<byte, float>(data.AsSpan(p, 64));
        int o = record * 12;
        destination[o] = m[0]; destination[o + 1] = m[4]; destination[o + 2] = -m[8];
        destination[o + 3] = m[12];
        destination[o + 4] = m[1]; destination[o + 5] = m[5]; destination[o + 6] = -m[9];
        destination[o + 7] = m[13];
        destination[o + 8] = -m[2]; destination[o + 9] = -m[6]; destination[o + 10] = m[10];
        destination[o + 11] = -m[14];
    }

    private static void WriteVector(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z);
    }

    private static Vector3 ReadVector(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static bool PathEquals(string a, string b) => string.Equals(a, b,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
