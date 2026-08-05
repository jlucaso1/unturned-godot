using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot;

public readonly record struct FoliageStructuralChunk(ArrayMesh Mesh, int Count, float OriginSpread);

// Spatial owner for visual-only foliage. Core keeps a compact offset/bounds index; this node decodes and
// uploads only the chunks whose existing visibility ranges approach the active camera, then retires their
// RenderingServer resources on the main thread after a larger hysteresis radius.
public partial class FoliageStreamingRenderer : Node3D
{
    private sealed record Resident(Rid Instance, MultiMesh Mesh, int Count, long Bytes);
    private sealed record DecodeResult(int Generation, int Index, FoliageChunk? Chunk, Exception? Error);
    // A warm batch never faults its task: the pass runs unattended behind the loading screen, and a batch
    // abandoned by a cancelled load would otherwise be an unobserved exception nobody is left to await.
    private sealed record WarmBatch(int[] Indices, IReadOnlyList<FoliageChunk>? Chunks, Exception? Error);

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
    private long _emergencyVisibleTicks;
    private long _maxEmergencyVisibleTicks;
    private int _truncatedAdmissions;
    private int _maxDeferredPrefetch;
    private bool _needsRefill;
    private bool _acceptDecoded = true;
    private readonly bool _prewarmEnabled;
    private bool _warming;
    private int _prewarmedChunks;
    private long _prewarmTicks;

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
    // What the emergency path cost the main thread. EmergencyVisibleLoads counts the chunks that had to
    // be decoded and uploaded inside _Process; these price them, because the count alone cannot say
    // whether the frame noticed. Sampled with the same clock in both tiers, including the spawn burst.
    public double EmergencyVisibleTotalMs => Milliseconds(_emergencyVisibleTicks);
    public double EmergencyVisibleMaxMs => Milliseconds(_maxEmergencyVisibleTicks);
    // What the warm pass took off that path: the chunks it made resident before the first frame, and the
    // wall-clock it spent doing so behind the loading screen. Read them together with the emergency
    // counters — the pass is only worth its load time while it keeps those at zero through the spawn.
    public int PrewarmedChunks => _prewarmedChunks;
    public double PrewarmTotalMs => Milliseconds(_prewarmTicks);

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    // How often admission hit UG_FOLIAGE_MAX_PENDING, and the largest single-plan shortfall behind it.
    // Both stay zero while the bound is wide enough for the map; neither is a failure on its own, since
    // the deferred work is refilled — they size the bound and explain a prefetch ring that stays behind.
    public int TruncatedAdmissions => _truncatedAdmissions;
    public int MaximumDeferredPrefetch => _maxDeferredPrefetch;
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
        _prewarmEnabled = EnvFlag.IsOn(System.Environment.GetEnvironmentVariable("UG_FOLIAGE_PREWARM"),
            whenUnset: true);
    }

    public override void _Ready()
    {
        AddToGroup("foliage_streaming");
        // Also joined by FoliageBuilder, which cannot know which of the three foliage shapes it built;
        // a renderer created directly (the runtime suite does) still advertises itself from here.
        AddToGroup(SceneGroups.Foliage);
        SetProcess(true);
        Log.Print($"[foliage-stream] indexed {_index.IndexedInstances} instances in {_items.Count} renderable "
            + $"chunks; prefetch {_prefetchMargin:0} m, hysteresis {_unloadHysteresis:0} m, "
            + $"queue {_maximumPending}, decoded cap {_decodedByteLimit >> 20} MiB");
    }

    // The ring the player will spawn inside, made resident under a frame budget instead of all at once.
    // Without this the first _Process is also the first plan: every chunk already inside its visibility
    // radius is missing, so the correctness gate below decodes and uploads all of them synchronously, on
    // the single frame that follows the scene being built. Measured on PEI at 61 chunks and 18-26 ms, up
    // to ~5 ms of it in one frame.
    //
    // On the streamed path that frame is behind the loading screen, not the first gameplay frame — the
    // world is only released on ObjectStreamer.Finished, which is later. So this buys smoother loading
    // frames and a decode moved off the main thread, not the removal of a gameplay hitch. The emergency
    // path still owns teleports and anything that outruns the prefetch ring; it is only the spawn plan
    // that is paid for here.
    //
    // The pass is the same policy the steady loop runs, only with its decodes taken off the main thread a
    // batch at a time and its uploads paced against the load's own 8 ms frame budget — nothing here is
    // resident that the first plan would not have asked for anyway.
    //
    // It must be called before the renderer's first _Process, which means before the caller's next frame
    // yield: SceneTree emits process_frame ahead of the nodes' own process pass, so a single yield
    // between attaching this node and warming it is enough for the burst to have already happened.
    //
    // loadCancellation is the caller's own load token, and it is not optional in practice. This node
    // cannot leave the tree — and so cannot cancel its lifetime token — until the load that owns it has
    // finished tearing down, and that teardown waits on the task this pass runs inside. Without the
    // caller's token, returning to the menu would wait out every remaining decode and paced upload.
    public async Task PrewarmAsync(CancellationToken loadCancellation = default)
    {
        // _focused means a plan already ran: the burst has been paid and warming would only duplicate it.
        if (!_prewarmEnabled || _focused || _items.Count == 0 || !IsInsideTree())
            return;
        if (GetViewport()?.GetCamera3D() is not { } camera)
            return;

        Vector3 focus = camera.GlobalPosition;
        _warming = true;
        long startedTicks = Stopwatch.GetTimestamp();
        bool completed = false;
        bool incomplete = false;
        // Decoded transforms this pass is holding but has not uploaded yet. They belong in _decodedBytes
        // like any other decode in flight: that counter is what maxDecodedBytes reports, and a warm pass
        // that answered the whole spawn ring would otherwise let the benchmark report a peak of zero
        // while it was holding two batches.
        long outstandingBytes = 0;
        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token, loadCancellation);
        try
        {
            FoliageResidencyPlan plan = FoliageResidencyPlanner.Plan(focus, _items,
                new HashSet<int>(_resident.Keys), _pending, _prefetchMargin, _unloadHysteresis,
                _maximumPending);
            // Half the steady-state cap per batch, because two of them are in flight: the batch being
            // uploaded still holds every transform it decoded while the next one decodes behind it. The
            // pair is what has to fit UG_FOLIAGE_DECODED_MIB, or the pass would peak at twice the bound
            // it is written to respect.
            IReadOnlyList<int[]> batches = FoliageResidencyPlanner.WarmBatches(plan, ExpectedDecodedBytes,
                Math.Max(1, _decodedByteLimit / 2));
            CancellationToken token = cancellation.Token;
            long budget = (long)(Stopwatch.Frequency * 0.008);
            long until = Stopwatch.GetTimestamp() + budget;
            Task<WarmBatch>? decoding = null;
            long held = 0;
            if (batches.Count > 0)
                (decoding, held) = StartWarmDecode(batches[0], token, ref outstandingBytes);
            for (int batch = 0; batch < batches.Count; batch++)
            {
                // Only a decode that had not finished actually costs a frame. Resuming from one means a
                // new frame has started, so the upload deadline restarts with it; charging the worker's
                // wall time to the main thread's budget would spend a whole frame on one upload, once per
                // batch. A decode already in hand continues inside the frame that is running.
                bool decodeWasPending = !decoding!.IsCompleted;
                WarmBatch? decoded = await decoding;
                if (decodeWasPending)
                    until = Stopwatch.GetTimestamp() + budget;
                // Overlap the next batch's IO and decode with this batch's uploads, but only while both
                // fit the cap at once. WarmBatches gives a chunk larger than half the cap a batch of its
                // own, and pipelining behind that one would put the pass over the bound it respects, so
                // an oversized batch is uploaded first and its successor started below instead.
                bool more = batch + 1 < batches.Count;
                bool overlapped = more && held + BatchBytes(batches[batch + 1]) <= _decodedByteLimit;
                long carried = held;
                if (overlapped)
                    (decoding, held) = StartWarmDecode(batches[batch + 1], token, ref outstandingBytes);
                else
                    decoding = null;
                if (decoded.Error != null || decoded.Chunks == null)
                {
                    // Nothing of this batch will be uploaded, so give its charge back here — the loop
                    // below only releases the chunks it actually lands. One failure, not one per chunk:
                    // the read stops at the first bad one and the rest were never attempted. Either way
                    // the refill below hands them to the steady loop rather than leaving them missing.
                    outstandingBytes -= carried;
                    Interlocked.Add(ref _decodedBytes, -carried);
                    if (decoded.Error != null)
                    {
                        _decodeFailures++;
                        incomplete = true;
                        AppShutdown.WarnUnlessQuitting($"[foliage-stream] prewarm batch of "
                            + $"{decoded.Indices.Length} chunks could not be decoded: "
                            + decoded.Error.Message);
                    }
                }
                for (int i = 0; decoded.Chunks != null && i < decoded.Chunks.Count; i++)
                {
                    if (!WarmMayContinue(token))
                        return;
                    long uploaded = ExpectedDecodedBytes(decoded.Indices[i]);
                    Upload(decoded.Indices[i], decoded.Chunks[i]);
                    _prewarmedChunks++;
                    // Resident now, not decoded-and-waiting: hand the bytes over to ResidentBufferBytes'
                    // side of the ledger as each chunk lands, the way DrainDecoded does.
                    outstandingBytes -= uploaded;
                    Interlocked.Add(ref _decodedBytes, -uploaded);
                    if (Stopwatch.GetTimestamp() < until)
                        continue;
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    until = Stopwatch.GetTimestamp() + budget;
                }

                // Start a serialized successor only now, and only once this batch is unreachable: a
                // Debug build holds a local to the end of its scope, so leaving the reference in place
                // would let the two batches' transforms coexist past the cap anyway.
                decoded = null;
                if (more && !overlapped)
                    (decoding, held) = StartWarmDecode(batches[batch + 1], token, ref outstandingBytes);
            }

            // A batch this pass could not decode is refilled by the steady loop, which otherwise would
            // not replan until the camera moved 16 m — and would meet those chunks on the emergency path.
            _needsRefill = plan.PrefetchTruncated || incomplete;
            if (plan.PrefetchTruncated)
                _truncatedAdmissions++;
            _maxDeferredPrefetch = Math.Max(_maxDeferredPrefetch, plan.PrefetchDeferred);
            // Claim the focus this pass planned for. The first _Process then replans only if the camera
            // has actually moved since, and finds the ring resident either way.
            _focused = true;
            _lastFocus = focus;
            completed = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            _decodeFailures++;
            _needsRefill = true;
            AppShutdown.WarnUnlessQuitting($"[foliage-stream] prewarm stopped: {e.Message}");
        }
        finally
        {
            // Whatever a cancelled or failed pass never uploaded is released here. Left behind it would
            // sit in _decodedBytes for the session, and the steady loop reads that counter to decide
            // whether it may start another decode at all.
            if (outstandingBytes != 0)
                Interlocked.Add(ref _decodedBytes, -outstandingBytes);
            // Disposed only once the pass is over. A decode this leaves running holds the token it was
            // given; reading IsCancellationRequested on it stays valid after the source is gone, which is
            // the same contract the steady loop's per-decode linked sources already rely on.
            cancellation.Dispose();
            _prewarmTicks = Stopwatch.GetTimestamp() - startedTicks;
            _warming = false;
        }

        if (completed)
            Log.Print($"[foliage-stream] prewarmed {_prewarmedChunks} chunks ({ResidentInstances} "
                + $"instances, {ResidentBufferBytes >> 20} MiB) around the spawn in {PrewarmTotalMs:0} ms");
    }

    // A cancelled load frees this node between the frames the warm pass yields on, so both its liveness
    // and its place in the tree are re-checked before every upload: past that point the RIDs would go to
    // a scenario being torn down, and the node itself may already be gone.
    private bool WarmMayContinue(CancellationToken token) =>
        !token.IsCancellationRequested && GodotObject.IsInstanceValid(this) && IsInsideTree();

    // Charged when the decode starts, not when it lands. An overlapped batch is already holding its
    // transforms on the worker while the previous one uploads, and accounting for it only on arrival —
    // by which point the previous batch has been released chunk by chunk — would let maxDecodedBytes
    // record one batch where two were held at once. Estimated bytes are exact here: a chunk decodes to
    // its instance count times the 12-float buffer layout, which is what ExpectedDecodedBytes returns.
    private (Task<WarmBatch> Decoding, long Bytes) StartWarmDecode(int[] indices, CancellationToken token,
        ref long outstandingBytes)
    {
        long bytes = BatchBytes(indices);
        outstandingBytes += bytes;
        UpdateMaximum(ref _maxDecodedBytes, Interlocked.Add(ref _decodedBytes, bytes));
        return (DecodeBatch(indices, token), bytes);
    }

    private Task<WarmBatch> DecodeBatch(int[] indices, CancellationToken token)
    {
        Task<WarmBatch> batch = Task.Run(() =>
        {
            try { return new WarmBatch(indices, _index.DecodeChunks(indices, token), null); }
            catch (OperationCanceledException) { return new WarmBatch(indices, null, null); }
            catch (Exception e) { return new WarmBatch(indices, null, e); }
        });
        AppShutdown.Track(batch);
        return batch;
    }

    public override void _Process(double delta)
    {
        // The warm pass owns residency until it is done, and yields to the render loop while it runs.
        if (_warming)
            return;
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
            // Time it in a finally: a cancelled or failed decode still spent the frame time it spent,
            // and dropping those samples would make the total look better the more often it went wrong.
            long startedTicks = Stopwatch.GetTimestamp();
            try { Upload(index, _index.DecodeChunk(index, _lifetimeCancellation.Token)); }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                _decodeFailures++;
                Log.PushWarning($"[foliage-stream] visible chunk {index} could not be decoded: {e.Message}");
            }
            finally
            {
                long elapsed = Stopwatch.GetTimestamp() - startedTicks;
                _emergencyVisibleTicks += elapsed;
                _maxEmergencyVisibleTicks = Math.Max(_maxEmergencyVisibleTicks, elapsed);
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
        if (plan.PrefetchTruncated)
            _truncatedAdmissions++;
        _maxDeferredPrefetch = Math.Max(_maxDeferredPrefetch, plan.PrefetchDeferred);
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
            long expectedBytes = ExpectedDecodedBytes(index);
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

    // What the dev console pulls the draw distance in by, as a fraction of the range the world was built
    // with. Only the drawing moves: residency, prefetch and retirement all key off the built radii, so a
    // chunk this hides is still decoded and still resident. That is the honest way to measure what
    // foliage costs the GPU — the CPU-side streaming stays exactly as it was, and the difference in the
    // frame is the drawing alone. Pulling it in is all it does; pushing past 1 would ask for chunks
    // residency never made resident, which is why the console's range stops there.
    private float _rangeScale = 1f;

    public float RangeScale => _rangeScale;

    public void SetRangeScale(float scale)
    {
        _rangeScale = Mathf.Clamp(scale, 0.01f, 1f);
        foreach ((int index, Resident resident) in _resident)
            ApplyVisibilityRange(resident.Instance, index);
    }

    private void ApplyVisibilityRange(Rid instance, int index) =>
        RenderingServer.InstanceGeometrySetVisibilityRange(instance, 0f,
            _visibilityEnds[index] * _rangeScale, 0f, FoliageBuilder.FadeMarginValue,
            RenderingServer.VisibilityRangeFadeMode.Self);

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
        ApplyVisibilityRange(instance, index);
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

    // What a chunk's transforms occupy once decoded — 12 floats per instance, the MultiMesh buffer layout.
    private long ExpectedDecodedBytes(int index) => (long)_index.Chunks[index].Count * 12 * sizeof(float);

    private long BatchBytes(int[] indices)
    {
        long bytes = 0;
        foreach (int index in indices)
            bytes += ExpectedDecodedBytes(index);
        return bytes;
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
        Log.Print($"[foliage-stream] stopped: {_prewarmedChunks} prewarmed, {_retiredChunks} retired, "
            + $"{_emergencyVisibleLoads} emergency visible loads, {_visibleSetMisses} visible-set misses, "
            + $"{_staleResults} stale results, {_decodeFailures} failures");
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
