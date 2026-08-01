using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

public readonly record struct FoliageStructuralChunk(ArrayMesh Mesh, int Count, float OriginSpread);

// Spatial owner for visual-only foliage. Core keeps a compact offset/bounds index; this node decodes and
// uploads only the chunks whose existing visibility ranges approach the active camera, then retires their
// RenderingServer resources on the main thread after a larger hysteresis radius.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class FoliageStreamingRenderer : Node3D
{
    private sealed record Resident(Rid Instance, MultiMesh Mesh, int Count, long Bytes);
    private sealed record DecodeResult(int Generation, int Index, FoliageChunk? Chunk, Exception? Error);

    private FoliageResidencyIndex _index = null!;
    private readonly List<FoliageResidencyItem> _items = new();
    private readonly Dictionary<int, ArrayMesh> _meshes = new();
    private readonly Dictionary<int, float> _visibilityEnds = new();
    private readonly Dictionary<int, Resident> _resident = new();
    private readonly HashSet<int> _pending = new();
    private readonly HashSet<int> _active = new();
    private readonly PriorityQueue<int, float> _queue = new();
    private readonly ConcurrentQueue<DecodeResult> _decoded = new();
    private readonly object _decodedGate = new();
    private CancellationTokenSource _generationCancellation = new();
    private readonly CancellationTokenSource _lifetimeCancellation =
        CancellationTokenSource.CreateLinkedTokenSource(AppShutdown.Token);

    private readonly float _prefetchMargin;
    private readonly float _unloadHysteresis;
    private readonly float _teleportDistance;
    private readonly int _maximumPending;
    private readonly int _maximumWorkers;
    private readonly int _uploadsPerFrame;
    private readonly long _decodedByteLimit;
    private int _generation;
    private int _workers;
    private long _decodedBytes;
    private long _reservedDecodeBytes;
    private bool _focused;
    private Vector3 _lastFocus;
    private int _emergencyVisibleLoads;
    private int _visibleSetMisses;
    private int _decodeFailures;
    private int _retiredChunks;
    private int _staleResults;
    private int _maxQueued;
    private long _maxDecodedBytes;
    private bool _needsRefill;
    private bool _acceptDecoded = true;

    public int ResidentChunks => _resident.Count;
    public long ResidentInstances { get; private set; }
    public long ResidentBufferBytes { get; private set; }
    public int IndexedChunks => _items.Count;
    public long IndexedInstances => _index.IndexedInstances;
    public int PendingChunks => _pending.Count;
    public long DecodedPendingBytes => Interlocked.Read(ref _decodedBytes);
    public int EmergencyVisibleLoads => _emergencyVisibleLoads;
    public int VisibleSetMisses => _visibleSetMisses;
    public int RetiredChunks => _retiredChunks;
    public int StaleResults => _staleResults;
    public int DecodeFailures => _decodeFailures;
    public int MaximumQueued => _maxQueued;
    public long MaximumDecodedBytes => _maxDecodedBytes;
    public bool IsSettled => _focused && !_needsRefill && _pending.Count == 0 && _queue.Count == 0
        && _decoded.IsEmpty && Volatile.Read(ref _workers) == 0;
    public IEnumerable<MultiMesh> MultiMeshes
    {
        get { foreach (Resident value in _resident.Values) yield return value.Mesh; }
    }
    public IEnumerable<FoliageStructuralChunk> StructuralChunks
    {
        get
        {
            foreach (FoliageResidencyItem item in _items)
            {
                FoliageChunkMetadata chunk = _index.Chunks[item.Index];
                Vector3 size = chunk.Bounds.Max - chunk.Bounds.Min;
                yield return new FoliageStructuralChunk(_meshes[item.Index], chunk.Count,
                    MathF.Max(size.X, MathF.Max(size.Y, size.Z)));
            }
        }
    }

    public static FoliageStreamingRenderer Create(FoliageResidencyIndex index,
        IReadOnlyDictionary<Guid, ArrayMesh> meshLibrary, float drawDistance)
    {
        var owner = new FoliageStreamingRenderer
        {
            Name = "Foliage",
            _index = index,
        };

        for (int indexNumber = 0; indexNumber < index.Chunks.Count; indexNumber++)
        {
            FoliageChunkMetadata chunk = index.Chunks[indexNumber];
            if (!meshLibrary.TryGetValue(chunk.Key.Asset, out ArrayMesh? mesh))
                continue;
            float meshRadius = mesh.GetAabb().Size.Length() * 0.5f
                * MathF.Sqrt(chunk.Bounds.MaxScaleSquared);
            float visibilityEnd = drawDistance + chunk.PositionRadius + meshRadius;
            // Godot continues drawing through VisibilityRangeEndMargin while fading. Residency must cover
            // that complete renderability interval, not just the nominal end, or edge chunks disappear
            // during the final 32 m of their legacy fade.
            owner._items.Add(new FoliageResidencyItem(indexNumber, chunk.Centre,
                visibilityEnd + FoliageBuilder.FadeMarginValue));
            owner._meshes[indexNumber] = mesh;
            owner._visibilityEnds[indexNumber] = visibilityEnd;
        }
        return owner;
    }

    public FoliageStreamingRenderer()
    {
        _prefetchMargin = EnvFloat("UG_FOLIAGE_PREFETCH_MARGIN", 256f, 32f, 2048f);
        _unloadHysteresis = EnvFloat("UG_FOLIAGE_UNLOAD_HYSTERESIS", 128f, 16f, 2048f);
        _teleportDistance = EnvFloat("UG_FOLIAGE_TELEPORT_DISTANCE", 512f, 64f, 8192f);
        _maximumPending = EnvInt("UG_FOLIAGE_MAX_PENDING", 256, 8, 8192);
        _maximumWorkers = EnvInt("UG_FOLIAGE_DECODE_WORKERS", 1, 1, 4);
        _uploadsPerFrame = EnvInt("UG_FOLIAGE_UPLOADS_PER_FRAME", 16, 1, 256);
        _decodedByteLimit = (long)EnvInt("UG_FOLIAGE_DECODED_MIB", 32, 4, 512) * 1024 * 1024;
    }

    public override void _Ready()
    {
        AddToGroup("foliage_streaming");
        SetProcess(true);
        Log.Print($"[foliage-stream] indexed {_index.IndexedInstances} instances in {_items.Count} renderable "
            + $"chunks; prefetch {_prefetchMargin:0} m, hysteresis {_unloadHysteresis:0} m, "
            + $"queue {_maximumPending}, decoded cap {_decodedByteLimit >> 20} MiB");
    }

    public override void _Process(double delta)
    {
        DrainDecoded();
        Camera3D? camera = GetViewport()?.GetCamera3D();
        if (camera != null)
        {
            Vector3 focus = camera.GlobalPosition;
            if (!_focused || focus.DistanceSquaredTo(_lastFocus) >= 16f * 16f)
                Replan(focus, !_focused || focus.DistanceSquaredTo(_lastFocus)
                    >= _teleportDistance * _teleportDistance);
            else if (_needsRefill && _queue.Count == 0 && _pending.Count < _maximumPending
                && Volatile.Read(ref _workers) < _maximumWorkers)
                Replan(focus, teleport: false);
        }
        PumpWorkers();
    }

    private void Replan(Vector3 focus, bool teleport)
    {
        if (teleport && _focused)
        {
            _generation++;
            _generationCancellation.Cancel();
            _generationCancellation.Dispose();
            _generationCancellation = new CancellationTokenSource();
        }
        _focused = true;
        _lastFocus = focus;

        // Rebuild queued (not active) work in current near-first order. Active results remain tracked and
        // are either accepted at this focus or discarded by generation/radius checks on the main thread.
        foreach ((int index, _) in _queue.UnorderedItems)
            _pending.Remove(index);
        _queue.Clear();

        var resident = new HashSet<int>(_resident.Keys);
        FoliageResidencyPlan plan = FoliageResidencyPlanner.Plan(focus, _items, resident, _pending,
            _prefetchMargin, _unloadHysteresis, _maximumPending);

        // Correctness gate: a chunk already inside its previous renderability radius is decoded and
        // uploaded before this process frame reaches rendering. Normally prefetch makes this list empty;
        // teleports and initial spawn are the deterministic emergency path.
        foreach (int index in plan.VisibleMissing)
        {
            if (_resident.ContainsKey(index))
                continue;
            _emergencyVisibleLoads++;
            try { Upload(index, _index.DecodeChunk(index, _lifetimeCancellation.Token)); }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                _decodeFailures++;
                Log.PushWarning($"[foliage-stream] visible chunk {index} could not be decoded: {e.Message}");
            }
            if (!_resident.ContainsKey(index))
                _visibleSetMisses++;
        }

        foreach (int index in plan.Retire)
            Retire(index);
        foreach (int index in plan.Prefetch)
        {
            if (_pending.Count >= _maximumPending)
                break;
            FoliageResidencyItem item = Item(index);
            _queue.Enqueue(index, focus.DistanceSquaredTo(item.Centre));
            _pending.Add(index);
        }
        _needsRefill = plan.PrefetchTruncated;
        _maxQueued = Math.Max(_maxQueued, _pending.Count);
    }

    private void PumpWorkers()
    {
        while (Volatile.Read(ref _workers) < _maximumWorkers && _queue.Count > 0
            && Interlocked.Read(ref _decodedBytes) < _decodedByteLimit)
        {
            int index = _queue.Peek();
            if (_resident.ContainsKey(index))
            {
                _queue.Dequeue();
                _pending.Remove(index);
                continue;
            }
            long expectedBytes = (long)_index.Chunks[index].Count * 12 * sizeof(float);
            long committed = Interlocked.Read(ref _decodedBytes) + Interlocked.Read(ref _reservedDecodeBytes);
            if (committed > 0 && committed + expectedBytes > _decodedByteLimit)
                break;
            _queue.Dequeue();
            _active.Add(index);
            Interlocked.Increment(ref _workers);
            Interlocked.Add(ref _reservedDecodeBytes, expectedBytes);
            int generation = _generation;
            CancellationToken generationToken = _generationCancellation.Token;
            CancellationToken lifetimeToken = _lifetimeCancellation.Token;
            var linked = CancellationTokenSource.CreateLinkedTokenSource(generationToken, lifetimeToken);
            Task worker = Task.Run(() => Decode(generation, index, expectedBytes, linked));
            AppShutdown.Track(worker);
        }
    }

    private void Decode(int generation, int index, long reservedBytes,
        CancellationTokenSource cancellation)
    {
        try
        {
            FoliageChunk chunk = _index.DecodeChunk(index, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            long bytes = (long)chunk.Packed.Length * sizeof(float);
            PublishDecoded(new DecodeResult(generation, index, chunk, null), bytes);
        }
        catch (OperationCanceledException)
        {
            PublishDecoded(new DecodeResult(generation, index, null, null), 0);
        }
        catch (Exception e)
        {
            PublishDecoded(new DecodeResult(generation, index, null, e), 0);
        }
        finally
        {
            cancellation.Dispose();
            Interlocked.Add(ref _reservedDecodeBytes, -reservedBytes);
            Interlocked.Decrement(ref _workers);
        }
    }

    private void PublishDecoded(DecodeResult result, long bytes)
    {
        // Exit takes the same gate before draining, so a worker can never publish a map-sized array
        // immediately after teardown has declared the queue empty.
        lock (_decodedGate)
        {
            if (!_acceptDecoded)
                return;
            if (bytes > 0)
            {
                long decoded = Interlocked.Add(ref _decodedBytes, bytes);
                UpdateMaximum(ref _maxDecodedBytes, decoded);
            }
            _decoded.Enqueue(result);
        }
    }

    private void DrainDecoded()
    {
        int uploaded = 0;
        while (uploaded < _uploadsPerFrame && _decoded.TryDequeue(out DecodeResult? result))
        {
            _active.Remove(result.Index);
            _pending.Remove(result.Index);
            if (result.Chunk != null)
                Interlocked.Add(ref _decodedBytes, -(long)result.Chunk.Packed.Length * sizeof(float));
            if (result.Error != null)
            {
                _decodeFailures++;
                Log.PushWarning($"[foliage-stream] chunk {result.Index} could not be decoded: "
                    + result.Error.Message);
                continue;
            }
            if (result.Chunk == null)
                continue;
            if (result.Generation != _generation || !ShouldRemainLoaded(result.Index, _lastFocus))
            {
                _staleResults++;
                continue;
            }
            if (!_resident.ContainsKey(result.Index))
            {
                Upload(result.Index, result.Chunk);
                uploaded++;
            }
        }
    }

    private void Upload(int index, FoliageChunk chunk)
    {
        if (_resident.ContainsKey(index) || !_meshes.TryGetValue(index, out ArrayMesh? mesh))
            return;
        var multimesh = new MultiMesh
        {
            Mesh = mesh,
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            InstanceCount = chunk.Count,
            Buffer = chunk.Packed,
        };
        Rid instance = RenderingServer.InstanceCreate();
        RenderingServer.InstanceSetBase(instance, multimesh.GetRid());
        RenderingServer.InstanceSetTransform(instance,
            GlobalTransform * new Transform3D(Basis.Identity, chunk.Origin));
        RenderingServer.InstanceGeometrySetCastShadowsSetting(instance,
            RenderingServer.ShadowCastingSetting.Off);
        RenderingServer.InstanceGeometrySetVisibilityRange(instance, 0f, _visibilityEnds[index], 0f,
            FoliageBuilder.FadeMarginValue, RenderingServer.VisibilityRangeFadeMode.Self);
        RenderingServer.InstanceSetScenario(instance, GetWorld3D().Scenario);
        RenderingServer.InstanceSetVisible(instance, IsVisibleInTree());
        long bytes = (long)chunk.Packed.Length * sizeof(float);
        _resident[index] = new Resident(instance, multimesh, chunk.Count, bytes);
        ResidentInstances += chunk.Count;
        ResidentBufferBytes += bytes;
    }

    private void Retire(int index)
    {
        if (!_resident.Remove(index, out Resident? resident))
            return;
        if (resident.Instance.IsValid)
            RenderingServer.FreeRid(resident.Instance);
        resident.Mesh.Dispose();
        ResidentInstances -= resident.Count;
        ResidentBufferBytes -= resident.Bytes;
        _retiredChunks++;
    }

    private bool ShouldRemainLoaded(int index, Vector3 focus)
    {
        FoliageResidencyItem item = Item(index);
        float radius = item.VisibilityRadius + _prefetchMargin + _unloadHysteresis;
        return focus.DistanceSquaredTo(item.Centre) <= radius * radius;
    }

    private FoliageResidencyItem Item(int index)
    {
        // Items exclude unresolved meshes, but index numbers remain unique; use the compact index directly
        // rather than retaining a second map-sized lookup table.
        FoliageChunkMetadata chunk = _index.Chunks[index];
        return new FoliageResidencyItem(index, chunk.Centre,
            _visibilityEnds[index] + FoliageBuilder.FadeMarginValue);
    }

    public override void _Notification(int what)
    {
        if (what != NotificationVisibilityChanged)
            return;
        bool visible = IsVisibleInTree();
        foreach (Resident resident in _resident.Values)
            RenderingServer.InstanceSetVisible(resident.Instance, visible);
    }

    public override void _ExitTree()
    {
        _lifetimeCancellation.Cancel();
        _generationCancellation.Cancel();
        _queue.Clear();
        _pending.Clear();
        _active.Clear();
        lock (_decodedGate)
        {
            _acceptDecoded = false;
            while (_decoded.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _decodedBytes, 0);
        }
        foreach (int index in new List<int>(_resident.Keys))
            Retire(index);
        _generationCancellation.Dispose();
        _lifetimeCancellation.Dispose();
        Log.Print($"[foliage-stream] stopped: {_retiredChunks} retired, {_emergencyVisibleLoads} emergency "
            + $"visible loads, {_visibleSetMisses} visible-set misses, {_staleResults} stale results, "
            + $"{_decodeFailures} failures");
    }

    private static int EnvInt(string name, int fallback, int min, int max) =>
        int.TryParse(System.Environment.GetEnvironmentVariable(name), out int value)
            ? Math.Clamp(value, min, max) : fallback;

    private static float EnvFloat(string name, float fallback, float min, float max) =>
        float.TryParse(System.Environment.GetEnvironmentVariable(name),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float value)
            ? Math.Clamp(value, min, max) : fallback;

    private static void UpdateMaximum(ref long target, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref target))
            && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}
