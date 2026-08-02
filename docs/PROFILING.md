# Benchmarking and profiling

`$GODOT` below is your Godot 4.7 .NET binary. Most of this is Linux tooling, but the three benchmark tiers
themselves work anywhere Godot runs.

## The three benchmark tiers

All three print a JSON report and diff it against your own previous run (baselines live in
`bench/baseline/`, which is git-ignored — see [Baselines are yours](#baselines-are-yours)):

```sh
"$GODOT" --headless -- --benchmark   # Tier 1: build times, mesh/material counts, static memory
"$GODOT" -- --benchmark --gpu        # Tier 2 (windowed): frame time, draw calls, primitives, VRAM
UG_RUNTIME_BENCH_SECS=12 SOLO=1 "$GODOT" # Tier 3: real streamed load + gameplay CPU/physics/rendering
```

Tier 2 uses deterministic map-relative camera poses, including two at actual gameplay height. Tier 3
starts the normal interactive path (player, loopback server, zombies, navigation and streaming), records
the time until `ObjectStreamer.Finished`, warms up, samples the spawn camera for the requested duration,
writes `user://bench/<map>-runtime-latest.json`, then performs the normal cooperative shutdown.

Tier 3 also reports p90/p95/p99/max frame times, medians/tails split by whether a 60 Hz physics tick ran,
and the percentage of frames above the 240 Hz and 120 Hz budgets. Compare those tails with a control map
in the same gamescope session: host scheduling and the compositor can contribute isolated spikes even when
the game has ample median headroom.

The project runs Godot's 3D physics server on its supported separate thread. Direct-space queries must
therefore originate from a physics notification; code started by an idle-frame signal should await the next
`physics_frame` first. Tier 3's subsystem counters expose navigation reconciliation, player physics,
`MoveAndSlide`, step-up, networking and zombie-view costs independently.

Add `--write-baseline` to record the current numbers as the new baseline.

### Baselines are yours

`bench/baseline/` is git-ignored, so a fresh clone has none and the first run of each tier says so:

```
[benchmark] No baseline at bench/baseline/PEI.json — run once with `--write-baseline` to capture one.
```

That is deliberate. A baseline holds wall-clock timings measured on one machine, and the only sound way
to read one is *me, on this machine, before and after my change*. Shared across machines it is noise: a
4-vCPU container reports `+9760%` on `build.total.ms` against a desktop's numbers, which says nothing
about the code. A committed baseline also rots — the one this repo used to carry drifted to `nodes: 1824`
against a tree that builds 40, because nobody re-recorded it for 46 commits.

The counts, which *are* machine-independent, live in `bench/structural/` instead and are committed and
gated in CI. `./scripts/check-structural-metrics.sh` diffs them and `--write` re-records when a change is
meant; see [the gate](#running-without-a-gpu) for what it does and does not cover.

## Parsers in isolation

```sh
dotnet run -c Release --project tools/PerfHarness            # all suites
dotnet run -c Release --project tools/PerfHarness -- foliage lz4
```

Micro-benchmarks the Core parsers against the real game data. See `tools/PerfHarness/README.md`. Each
suite skips cleanly when its input is missing, so it runs on any machine with some subset of the data.

## Where the time and memory go

- **GPU** (AMD, headless): `amdgpu_top -J -n 1` while the app runs, for GPU-busy % per block and VRAM used.
- **GPU per render pass**: Godot's own `--gpu-profile` flag (before the `--`, works in release builds)
  prints each pass's GPU time (shadows, depth prepass, opaque, sky, transparent, tonemap) to stdout every
  frame.
- **RAM**: `heaptrack --record-only -o /tmp/ht "$GODOT" -- --benchmark --gpu`, then `heaptrack_print
  /tmp/ht.zst` for peak/leaked totals. The shipped Godot binary is stripped, so call stacks don't
  symbolicate. Read `/proc/<pid>/smaps_rollup` (RSS, `Private_Dirty`) for a live breakdown instead, and
  attribute per subsystem by differencing runs rather than by stack.
- **CPU** (.NET): run the profile loop and attach by PID:

  ```sh
  "$GODOT" --headless -- --benchmark --profile-loop &
  dotnet-trace collect -p <pid> --format speedscope --duration 00:00:08
  ```

  Stop the trace while the loop still runs. Launching Godot *under* dotnet-trace truncates the trace:
  Godot's native quit kills the process before the profiler flushes. Godot's built-in script profiler
  covers GDScript only and does not see C#.

`ObjectStreamer` prints a `post-load reclaim: RSS x -> y MB` line (Linux, from `/proc/self/status`) after
the one-time load's transient heap is compacted back to the OS: a quick steady-state RSS check.
`UG_RECLAIM_PASSES=1|2` reproduces the measured one/two-compaction A/B; one pass is the default because
California2 returned at least as much RSS in less time in repeated runs.

## Running without a GPU

On a machine with no GPU at all — an agent sandbox, a CI runner — `./scripts/install-godot.sh` lays down
Godot 4.7 .NET plus Mesa's lavapipe, which answers as a real Vulkan 1.4 device on the CPU, and Xvfb for
the window the GPU tiers want. `./scripts/run-benchmark.sh` then runs any tier without you assembling
the incantation:

```sh
./scripts/install-godot.sh              # Godot + software Vulkan + Xvfb
./scripts/run-benchmark.sh structural   # Tier 1 — needs neither, --headless renders nothing
./scripts/run-benchmark.sh gpu          # Tier 2 — lavapipe under Xvfb
./scripts/run-benchmark.sh runtime      # Tier 3 — same
```

**What survives software rendering, measured rather than assumed.** Two identical runs of each tier on a
GPU-less 4-vCPU container:

| Tier | Metrics | Reproduced exactly | Drifted |
|---|---|---|---|
| 1 (structural) | 16 | 13 | the 3 `*.ms` timings, ±18% |
| 2 (GPU) | 67 | 49 | the 18 `gpu.frameMs.*` / `cpu.processMonitorMs.*`, ±2.5% |

Every count reproduced bit-for-bit: draw calls, primitives and render objects per pose, VRAM and buffer
and texture bytes, pipeline compilations, and even the foliage streaming counters. Only the clock moved.
That is what `scripts/check-structural-metrics.sh` gates in CI, and it is why it gates nothing timed.

**What the numbers do not mean.** A lavapipe `gpu.frameMs` is a CPU rasterizing, so it neither predicts a
real GPU's frame time nor ranks changes the way a GPU would — shading, bandwidth and overdraw all price
differently there. Its VRAM figures are system memory. And comparing against a baseline recorded on real
hardware is worse than useless: `gpu.primitives.median` reads 176k here against 1,237k on an RX 6600 for
the same pose, because at 400+ ms a frame the foliage streamer never settles (`foliage.settled: 0`) and
most of the map's grass is simply never submitted. The harness notices the mismatch and says so:

```
WARNING: environment differs from baseline (baseline vulkan/AMD Radeon RX 6600 (RADV NAVI23)
         vs current vulkan/llvmpipe (LLVM 20.1.2, 256 bits)) — deltas may be noise.
```

Keep a separate baseline per environment, and read the timed half of any GPU-less run as scaffolding
rather than as a measurement.

**And read RSS there as mostly the renderer.** `UG_HEADLESS_INTERACTIVE=1` runs the normal interactive
session — streaming, navigation, physics, netcode, zombies, foliage residency — with no rendering driver
at all, so differencing it against the same session under lavapipe attributes RSS between the game and
the rasterizer. On PEI in a GPU-less container:

| Tier 3 session | Peak RSS | `videoMemoryBytes` |
|---|---:|---:|
| lavapipe under Xvfb | 1762 MB | 236 MB |
| `UG_HEADLESS_INTERACTIVE=1`, no driver | 355 MB | 0 |

```sh
UG_HEADLESS_INTERACTIVE=1 ./scripts/run-benchmark.sh runtime   # the runtime tier skips Xvfb for this
```

Roughly 1.4 GB — about 80% of the process — is the software rasterizer holding in host memory what a
real GPU keeps in VRAM. Two consequences. Any RSS figure measured in an agent sandbox or a CI runner is
mostly lavapipe, so memory work must be judged against the headless number or on real hardware. And the
game's own resident state for a fully loaded, fully simulated PEI is around 355 MB, which is the figure
an optimization is actually competing with.

The headless session reports `drawCalls`, `primitives` and `videoMemoryBytes` as zero and runs frames far
faster than a real one (1733 samples in 12 s against 8 under lavapipe), so its timings describe an
unthrottled loop, not a frame budget. `SCREENSHOT_PATH` still takes precedence: a screenshot needs
something drawn.

## Running without a window (Linux)

- **No window** (Wayland/X): wrap every GPU or interactive benchmark in a headless nested compositor so
  nothing pops up on your desktop: `gamescope --backend headless -r 1000 -W 1152 -H 648 -- "$GODOT"
  --audio-driver Dummy -- --benchmark --gpu`. Real Vulkan, screenshots and VRAM all work; `-r 1000` lifts
  the compositor's 60 Hz vblank so `gpu.frameMs` is effectively uncapped too. Tier 3 uses
  `UG_RUNTIME_BENCH_SECS=12 SOLO=1 gamescope --backend headless -r 1000 -W 1152 -H 648 -- "$GODOT"
  --audio-driver Dummy`.
- **No sound**: gamescope hides the window but the audio still plays on your desktop, so pass
  `--audio-driver Dummy` to Godot in any automated/background run.
- **Solo automation**: `SOLO=1` boots straight into the world with the loopback session (zombies and all
  server systems live) WITHOUT binding the UDP port. Only use `OPEN_LAN=1` when a second client actually
  joins the test.

## Spatial-culling A/B controls

The production defaults partition only object groups spread across more than one cell (`UG_OBJECT_CHUNK_METRES`),
use 128 m foliage chunks with a 160 m per-instance visibility range, and give navmesh reconciliation 0.25 ms per
physics frame on every map. Read the current cell size from the default in `ObjectsBuilder` rather than from
this page — it is tuned against measurements and moves. Object collision compounds are partitioned into 2048 m cells; the partitioner
stops expanding compounds after 8,000 object bodies so its extra bodies cannot consume Jolt's remaining
pool. The foliage chunk's actual positional and scaled-mesh radii are added to Godot's
aggregate-AABB range, so an instance near a chunk edge never fades early. These preserve geometry,
materials, shadows and world transforms; they change only submission/culling granularity and how quickly
the already-usable baked navigation graph is refined.

- `TERRAIN_OCCLUDERS=0` disables the default coarse terrain occluders. Every occluder triangle is built
  below the minimum source height of its complete cell, so it cannot hide geometry above the terrain;
  disabling is retained for performance and screenshot A/B checks.
- `UG_OBJECT_CHUNK_METRES=0` disables object partitioning. `UG_OBJECT_CHUNK_REQUIRE_SPREAD=0` partitions
  every eligible repeated asset instead of leaving compact groups in one batch. `UG_CHUNK_SPARSE_OBJECTS=0`
  restores one map-spanning MultiMesh for groups of fewer than eight instances that cross chunk cells.
  `UG_SPARSE_OBJECT_MIN_TRIS=<count>` is an experimental cutoff for studying batch/geometry tradeoffs;
  production uses zero so no map-spanning sparse AABB survives.
  Placeholder boxes for unresolved assets use the same cells (and still share one mesh/material); setting
  `UG_OBJECT_CHUNK_METRES=0` restores their former map-wide batch as well.
- `UG_OBJECT_CHUNK_MIN_TRIS=<count>` partitions only groups whose total placed triangle count reaches the
  threshold.
- `UG_COLLISION_CHUNK_METRES=0` disables physics-body partitioning for A/B comparisons.
- `UG_FOLIAGE_CHUNK_TILES=<1..32>` changes the number of 32 m foliage tiles per render chunk;
  `UG_FOLIAGE_DISTANCE=<metres>` changes its fade range.
- `UG_FOLIAGE_LOAD_BATCH=<128..16384>` controls how many offset-sorted foliage tiles are read per
  bounded file batch (default 512). `UG_FOLIAGE_STREAM_LOAD=0` restores whole-file loading for memory A/B.
- `UG_FOLIAGE_PACK_BATCH=<1..65536>` bounds how many chunk buffers coexist before upload (default 256);
  65536 reproduces the old all-at-once peak on current maps.
- `UG_FOLIAGE_RESIDENCY=0` restores the all-resident foliage renderer for visual and memory A/B checks.
  The default path keeps a versioned seek index in `user://foliage_index`, synchronously guarantees the
  camera-visible set, decodes the wider prefetch ring on a worker, and retires chunks beyond the unload
  hysteresis radius. Its tuning controls are `UG_FOLIAGE_PREFETCH_MARGIN` (256 m),
  `UG_FOLIAGE_UNLOAD_HYSTERESIS` (128 m), `UG_FOLIAGE_TELEPORT_DISTANCE` (512 m),
  `UG_FOLIAGE_MAX_PENDING` (256), `UG_FOLIAGE_DECODE_WORKERS` (1),
  `UG_FOLIAGE_UPLOADS_PER_FRAME` (16), and `UG_FOLIAGE_DECODED_MIB` (32 MiB). The runtime and GPU JSON
  reports include resident/indexed chunks and instances, buffer bytes, maximum queue/decoded bytes,
  retirements, stale results, failures, and visible-set misses. `truncatedAdmissions` counts the plans
  that hit `UG_FOLIAGE_MAX_PENDING` and `maxDeferredPrefetch` the largest single-plan shortfall behind it;
  both are zero when the bound is wide enough for the map. They are not failures — deferred work is
  refilled on later plans — but a prefetch ring that is persistently behind is what turns a chunk entering
  the visible radius into a synchronous main-thread decode, so size the bound with these two before
  blaming frame-time tails on upload bursts. `emergencyVisible.totalMs` and `emergencyVisible.maxMs`
  price that synchronous work: `emergencyVisibleLoads` says how many chunks took it, these say what the
  main thread paid for them. Both cover the whole session, including the deterministic burst at spawn,
  so compare them across runs of the same map rather than reading one number as a budget.
  The residency counts are always reported, but on their own
  keys depending on whether the upload queue had drained: `residentChunks` when `runtime.foliage.settled`
  (Tier 3) or `foliage.settled` (Tier 2) is 1, and `residentChunksUnsettled` when it is 0. The split is
  deliberate — a mid-fill snapshot describes work in progress rather than the steady resident set, and
  keeping it on a separate key means a baseline diff reports it as added rather than as a regression
  against a settled baseline. Expect the unsettled keys on any machine slow enough that the per-frame
  upload budget never drains the queue; a GPU-less container samples at well under 1 FPS and never settles.
- `UG_FOLIAGE_TRAVERSAL=1` adds deterministic far-apart ground poses to Tier 2. It exercises teleport
  cancellation and retirement and is intended to be combined with the foliage counters; zero
  `visibleSetMisses` is the correctness gate.
- `UG_COMPACT_HEIGHTMAP=0` keeps the resident terrain sampler in floats for memory/CPU A/B instead of
  the default exact source `ushort` representation.

- `UG_DEDUP_GPU=0` disables byte-exact sharing of cached meshes, textures and terrain control maps.
- `UG_DEDUP_COLLIDERS=0`, `UG_KEEP_PHYSICS_PLACEMENTS=1`, and `UG_NODE_MULTIMESH=1` restore respectively
  per-GUID collider parsing/shapes, retained physics placement tuples, and one Node3D wrapper per render
  MultiMesh for memory/lifecycle A/B measurements.
- `UG_PARALLEL_FOLIAGE_REBASE=0`, `UG_DIRECT_COLLISION_BUCKETS=0`, and `UG_ROAD_ARRAYS=0` restore
  sequential foliage rebasing, the temporary flat collision-placement list, and SurfaceTool road uploads.
- `UG_NODE_PHYSICS=1` restores one Node3D wrapper per static physics body; the default owns the same body
  RIDs from one lifecycle node. `UG_DEDUP_MATERIAL_CONTENT=0` restores one material resource per texture
  cache key instead of sharing resources whose complete cached texture contents and material properties
  are identical.
- `UG_DEDUP_FINAL_SHAPES=0` disables exact sharing of already-baked primitive/concave Shape3D resources.
  `UG_KEEP_RID_UPLOAD_METADATA=1` retains the verbose body/render definitions after their RIDs are live;
  the default keeps only the resources and local transforms required by the servers and lifecycle.
- `UG_KEEP_NAV_RECONCILE_STATE=1` retains rejected-triangle HashSets after publication and cache writing.
  `UG_STATIC_MAP_PREVIEW_CACHE=1` retains all decoded map artwork for the process lifetime; by default it
  belongs to the map picker and is released when the menu closes. Run `PerfHarness -- previews` to measure
  the installed library's full decoded RGBA footprint.
- `UG_PARTIAL_NAV_CACHE=0` disables per-flag atomic reconciliation checkpoints. By default an interrupted
  first California2 session resumes only the missing nav flags instead of repeating all completed raycasts.
- `NAV_RECONCILE_BUDGET_MS=<milliseconds>` overrides the per-physics-frame collision reconciliation budget.
  Completed results persist under `user://nav_reconcile`; remove the selected map's cache file when a
  deliberately cold reconciliation run is required. The reconciled CSR routing graph is cached beside it;
  valid hits deserialize the graph directly, while misses build and write it off the main thread.
- `UG_RUNTIME_BENCH_MOVE=1` makes Tier 3 alternate forward/back once per second, exercising real movement
  collision and the loopback multiplayer position stream instead of profiling only an idle player. Note
  that it oscillates rather than traverses: net displacement stays near zero, so it does not carry the
  camera out of the prefetched foliage ring. Use Tier 2's `UG_FOLIAGE_TRAVERSAL=1` poses for that.
- `UG_HEADLESS_INTERACTIVE=1` runs the interactive session under `--headless` instead of quitting after
  the data loads, so the renderer's share of RSS can be differenced. See
  [Running without a GPU](#running-without-a-gpu) for the measured split.

### Foliage residency reference A/B

On 2026-08-01, one paired California2 Release run on the profiling machine above measured:

| Metric | all-resident (`UG_FOLIAGE_RESIDENCY=0`) | spatial residency | Delta |
|---|---:|---:|---:|
| Tier 3 load | 3,045 ms | 2,595 ms | -14.8% |
| Tier 3 RSS | 2,303,627,264 B | 1,391,706,112 B | -39.6% |
| Tier 3 video-memory monitor | 625,598,368 B | 366,269,616 B | -41.5% |
| Tier 2 GPU buffers | 335,129,156 B | 146,663,060 B | -56.2% |
| Tier 2 total video memory | 616,933,648 B | 300,016,992 B | -51.4% |

The streamed run kept 534 of 14,601 renderable chunks (5,683,776 transform-buffer bytes) at the
interactive spawn. The deterministic far-pose run retired 704 chunks before reporting, remained at 326
resident chunks, and recorded zero visible-set misses, stale results, and decode failures. Frame timing is
intentionally not summarized from one pair; use repeated reports because compositor and host scheduling
noise is larger than the observed difference.

Profiling output (`*.nettrace`, `heaptrack.*.zst`, `massif.out.*`, `perf.data`, `*.rgp`) is git-ignored.
