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

### Where the cold load's decode time actually goes

Measured with `PerfHarness -- lzma` and `-- bundle` on a 4-vCPU container, against the game's own
`core_linux.masterbundle` (110.9 MiB on disk, 1,371.9 MiB decompressed, one LZMA block):

| | bytes | time | share |
|---|---:|---:|---:|
| LZMA, SerializedFile node | 170.9 MiB | 4.60 s | 30% |
| LZMA, `.resS` texture node | 1,180.2 MiB | 7.73 s | 51% |
| LZMA, `.resource` audio node | 20.8 MiB | 2.11 s | 14% |
| `SerializedFile.Read` | 103,549 objects | 0.01 s | <1% |
| `TypeTreeReader.Read`, classes every load scans, once each | 42,010 objects | 0.22 s | 1.5% |
| re-decoding the AssetBundle container (3 passes) | 1 object, 3x | 0.30 s | 2% |

**The pass is LZMA-bound and nothing else is close.** Decompression is ~98% of it; the object table is
free, and the TypeTree reader — the obvious-looking target, and the only part of this that is the port's
own code — is 1.5%. The classes a map actually places add a bounded 0.28 s on top, so even the loosest
reading puts the reader at 3-4%: eliminating it entirely would take well under a second off a ~15 s pass.
Work aimed at cold load time should go at *what is decoded and when* (`ress`, deferral, caching) rather
than at how fast the port turns already-decoded bytes into values.

The one exception, and the cheapest win in the area, is the AssetBundle container. `m_Container` is a
single object costing 0.10 s and 51.5 MiB to decode, and `PrefabGraph.ReadContainer`,
`BundleTextures.Locate` and `AudioExtractor.Plan` each decode it again from scratch on a cold load —
~0.30 s and ~154 MiB re-deriving a table the load already built. For scale, decoding all 42,010
unconditionally-scanned objects once costs 0.22 s. Decoding the container once and passing it around is a
`src/` change rather than a parser one, which is why it is recorded here rather than fixed in `core/`.

Two specifics worth carrying. The decode rate is not one number: it varies 15x between the three nodes
and tracks how compressible each one is, so a rate sampled in one node cannot price a deferral in
another. And the audio `.resource` node decodes at 9.9 MiB/s against the texture node's 152.7 — 1.5% of
the bytes for 14% of the time — which makes it much the most expensive region in the file per megabyte.
`tools/PerfHarness/README.md` has the per-node table and what it means for the `ress` numbers.

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

Each piece of one-time load work prints a `[mem] <what> reclaim: RSS x -> y MB` line (Linux, from
`/proc/self/status`) when its transient heap is compacted back to the OS: a quick steady-state RSS check.
`post-load` is the streamer's, and a cold cache adds one per deferred pass that still had work to do.
`UG_RECLAIM_PASSES=1|2` reproduces the measured one/two-compaction A/B; one pass is the default because
California2 returned at least as much RSS in less time in repeated runs. `0` skips the compaction
entirely, which is the control for pricing it — a reclaim that lands after the player is already moving
(the deferred audio fallback's) is a stop-the-world pause in the middle of gameplay. Measured on a warm
mesh cache with a cold audio cache, PEI, 45 s:

| | `UG_RECLAIM_PASSES=0` | default |
|---|---:|---:|
| `runtime.frameMs.max` | 48.2 ms | 79.8 ms |
| `runtime.frameMs.p99` | 7.52 ms | 7.59 ms |
| `runtime.rssBytes` | 701,833,216 B | 343,724,032 B |

One frame pays ~30 ms once; the session keeps 358 MB. The tail is untouched, and the reclaim is worth it
at that price — but it is a real pause, so anything that would run a compaction on a schedule rather than
once, or on a path the player can trigger repeatedly, needs this A/B again before it ships.

`UG_MEM_TRACE=<seconds>` prints a line per interval with RSS, the managed heap's committed/live sizes and
fragmentation, how much was allocated since the previous line, and the per-generation collection counts.
The reclaim lines describe one instant each; the shape between them is what tells a one-time transient
apart from a steady leak, and it is what identified both memory findings below. Off unless set.

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

The runner builds the managed Godot project before launching because command-line Godot does not compile
C# sources and otherwise can silently benchmark an assembly left by another checkout. For a scripted
matrix, build the exact checkout once and set `UG_BENCH_SKIP_BUILD=1` on its individual runs; do not use
that opt-out across source changes.

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

Every boolean flag below goes through `EnvFlag`, so `1`/`true`/`yes`/`on` and `0`/`false`/`no`/`off` all
work, in any case. A value that is none of those is treated as unset and the flag keeps its default —
previously the value was compared rather than read, so `UG_FOLIAGE_RESIDENCY=false` *enabled* residency
(`"false" != "0"`) and `UG_NODE_MULTIMESH=true` left it off (`"true" != "1"`). Flags that take a number or
a path (`UG_OBJECT_CHUNK_METRES`, `SCREENSHOT_PATH`, `TIME_OF_DAY`, …) are unaffected.

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
- `UG_OBJECT_CELL_MIN_TRIS=<count>` is the geometry an average cell must carry for its draw call to pay
  for itself. Groups whose cells fall short are partitioned on a coarser grid instead (doubling until
  they clear it, or until the group is a single batch). Zero gives every group the same fixed cell size,
  which is the A/B control for the whole mechanism. It responds to how dense a group is rather than how
  far it is spread, which is why one setting suits maps of different sizes: a wider map multiplies spread
  but not density.
- `UG_OBJECT_LOD=0` drops the prefab's authored lower level and draws LOD-0 at every distance.
  `UG_OBJECT_LOD_RADII=<n>` is how many mesh radii away that level takes over, `UG_OBJECT_LOD_FADE=1`
  opts into the dithered swap, and `UG_OBJECT_LOD_CHUNK_METRES` bounds the cells of levelled groups.
- `UG_MESH_LOD_THRESHOLD=<pixels>` sets the viewport's mesh LOD threshold, which drives the levels
  Godot generates for every mesh itself; zero disables it and is the control for measuring what
  automatic LOD contributes. The value is clamped to 0..1024, and anything unparseable or non-finite
  leaves the shipped default in place rather than disabling LOD — so a typo shows up as "no change"
  instead of as a large and misleading win. `Main` holds that default, which is above the engine's;
  see the LOD findings below for why.
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
  main thread paid for them. Both cover the whole session, so compare them across runs of the same map
  rather than reading one number as a budget.
- `UG_FOLIAGE_PREWARM=0` restores the unwarmed spawn, where the first plan runs on the frame the player
  appears and every chunk already inside its visibility radius is decoded and uploaded synchronously
  right then. By default the streamer hands the renderer that plan while the loading screen still owns
  the frame: the decodes go to a worker a batch at a time, and the uploads are paced against the load's
  own 8 ms frame budget. Two batches are in flight at once — one uploading, the next decoding behind it —
  so each is cut to half of `UG_FOLIAGE_DECODED_MIB` and the pair stays inside the same bound the steady
  loop decodes under, and what it is holding counts towards `maxDecodedBytes` like any other decode in
  flight. `prewarmedChunks` is what the pass made resident and `prewarm.totalMs` what it spent
  doing so; read them against `emergencyVisibleLoads`, which the pass is there to keep at zero through
  the spawn. Nothing is resident that the first plan would not have asked for a frame later anyway, so
  this trades load time for the burst and not for a larger resident set. The flag is the A/B control:
  measured on PEI (Tier 3, warm cache, GPU-less container), 61 emergency loads / 18–26 ms total /
  3.7–4.0 ms in the worst frame become 0, for 55–80 ms of load spent behind the loading screen and an
  identical settled set of 188 chunks / 129,752 instances. Tier 2 builds its world synchronously and
  jumps its camera between poses, so it never warms and reports `prewarmedChunks` 0.
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
- `UG_NAV_CPU_PROBE=0` sends every reconciliation probe back to the PhysicsServer, except under
  `UG_NAV_PROBE_AUDIT=1`, which needs the field to have anything to compare against and says so in the log
  when it overrides this. By default the load
  records the layer-`World` collision geometry into a `CollisionField` — terrain heightfields and object
  colliders, as the bodies for them are created — and the first session probes that on workers, asking the
  server only about the faces `NavmeshReachability.NeedsConfirmation` names. Direct-space queries are only
  legal inside a physics notification, so the old path serialised thousands of probes into a slice of each
  tick; `runtime.subsystem.NavigationReconcile.calls` and the time to `[nav] collision reconciliation
  submitted` are what this moves. On one paired PEI run on a 4-vCPU software-rendered container
  (`NAV_RECONCILE_BUDGET_MS=100` on both sides, so the frame rate rather than the budget is held
  constant), the same 42,642 faces reconciled in **54,263 ms** with `UG_NAV_CPU_PROBE=0` and **9,780 ms**
  with it on — 298,494 physics-server probes down to 75,096, and the completion line breaks that down as
  `sample+plan 1,779 ms, server 6,284 ms, verdict 83 ms`. The face count in that line reads
  `8,054(+2,674)`: the planned confirmation set, and what the rounds after it added once the server's own
  answers changed what the faces beside them were measured against.
- Two things about that split are worth knowing before tuning it. The CPU work itself is a fraction of a
  second for a whole map (`PerfHarness -- navprobe` measures it in isolation), so `sample+plan` is mostly
  the cost of one hop to the thread pool: an `await` inside a Godot signal handler resumes on the engine's
  synchronization context, which drains once a frame, so a worker round trip costs a frame however little
  work it did. That is why the pass plans every flag in one hop and reaches its verdicts inline, and why
  the reconciliation checkpoints are written on a chain and awaited once at the end rather than per flag.
- `UG_NAV_CONFIRM_MARGIN=<metres>` (default 0.05) widens how close to the step threshold a face has to be
  before its verdict is settled on the server rather than in the field. It is reserved once against
  authored geometry, which is exact, and twice where a face is compared against a neighbour: those are two
  independently sampled surfaces and they can drift apart in opposite directions. The heightfield's
  triangulation is not covered by it — `CollisionField` measures that per probe and reports it as slack,
  which is added on top per face. Independently of the margin, every face the sampled surfaces would
  *drop* is confirmed:
  keeping a face leaves the baked navmesh as shipped, while dropping one takes route out of the graph, so
  only the destructive verdict needs the physics to sign it off.
- `UG_NAV_PROBE_AUDIT=1` probes every face both ways and reports where they part company — including how
  many faces would reach a different verdict than a full server probe, which is the only disagreement that
  changes the game. It is deliberately slower than either path alone, and neither reads nor writes the
  reconciliation cache: reading it would return before any comparison happened, and its verdicts are the
  server's own, so leaving them behind would have the next normal run restore them instead of running the
  hybrid. One PEI run on the container above (all 19 flags, 42,642 faces, 298,494 probes): 8,054 faces
  needed confirming, 90 measured a different surface — none of them a surface the field failed to find —
  and 19 reached a different verdict out of the 6,266 the server dropped. Confirming the drop set is what
  buys most of that last number: without it the same map reached a different verdict on 66 faces, from a
  confirmation set of 3,685. Those counts predate two later widenings of the escalation set — the second
  margin described above, and escalating a twisted heightfield cell whose other triangulation is the one
  inside the probed segment. On the terrain-only `navprobe` suite the pair moved the confirmation set from
  13.9% of faces to 14.6%, so the totals above are low by roughly that.
- One thing the audit settled that reasoning did not. `ObjectsBuilder` leaves `BackfaceCollision` off on
  its `ConcavePolygonShape3D`s, so in principle the server culls a crossing of a face turned away from the
  probe and `CollisionField` should escalate rather than claim one. Measured, it does not: escalating
  back-facing crossings took the uncertain probes from 842 to 99,525 — a third of every probe on the map,
  and the confirmation set from 19% of faces to 50% — while the disagreement count stayed at 90 and the
  verdict differences moved 19 to 15. A third of crossings being back-facing while the server reports the
  same surfaces says the server is not culling them here, so the mesh test stays two-sided. If that ever
  changes, the audit shows it as a jump in "the CPU field invented", not as a timing regression.
- `UG_RUNTIME_BENCH_MOVE=1` makes Tier 3 alternate forward/back once per second, exercising real movement
  collision and the loopback multiplayer position stream instead of profiling only an idle player. Note
  that it oscillates rather than traverses: net displacement stays near zero, so it does not carry the
  camera out of the prefetched foliage ring. Use Tier 2's `UG_FOLIAGE_TRAVERSAL=1` poses for that.
- `UG_HEADLESS_INTERACTIVE=1` runs the interactive session under `--headless` instead of quitting after
  the data loads, so the renderer's share of RSS can be differenced. See
  [Running without a GPU](#running-without-a-gpu) for the measured split.

### Why a cold cache used to cost RAM for the whole session

A session that built its caches held several hundred megabytes more than one that found them, for as long
as it ran — the map, the view and the simulation being identical. `UG_MEM_TRACE=2` on a GPU-less
container, PEI, 60 s of Tier 3 after the load, found two causes and no leak: every figure below is
uncollected transient or native churn, with live managed objects at ~12 MB throughout.

**The one-time work that ran after the load's reclaim.** The audio extraction was deferred behind the
world streamer, so it started *after* `post-load reclaim` had already compacted the heap. It decoded the
whole 1.4 GB masterbundle a second time — for the 20 MB `.resource` node at the end of the blob — and
nothing ever compacted what that left: a 400 MB heap holding 12 MB of live objects, and RSS 390 MB above
the warm session's for the rest of the game. The clips are byte ranges in a stream node exactly like
texture pixels are, so they are now planned into the streamer's own pass (`AudioExtractor.Plan` →
`ModelExtractor.StreamExtract`), which was already reading to within 3 MB of that node. The definition
cache the pass writes is byte-identical to the standalone extractor's, which is how the two are compared.
The fallback remains for what the pass cannot cover — an unstreamable bundle, a cancelled pass, or a warm
mesh cache with a cold audio cache — and it, like every other piece of one-time work, now reclaims when
it finishes rather than relying on a reclaim that ran before it started.

**Navigation reconciliation's per-probe dictionaries.** `IntersectRay` returns a fresh native Variant
dictionary behind a finalizable wrapper, and reconciliation runs seven per navmesh triangle. Left to the
collector, that churn grew RSS by ~1.6 MB/s for as long as the pass ran (`NAV_SKIP_RECONCILE=1` is the
control: RSS goes flat). Disposing each result ends the growth without changing a single reachability
decision.

| Tier 3, PEI, 60 s, headless-interactive | before | after |
|---|---:|---:|
| `runtime.rssBytes`, cold cache | 791,678,976 B | 348,733,440 B |
| `runtime.rssBytes`, warm cache | 400,257,024 B | 340,238,336 B |
| cold − warm | 391,421,952 B | 8,495,104 B |
| `runtime.managedBytes`, cold cache | 305,394,912 B | 37,933,656 B |
| cold: time to playable | 6,410 ms | 6,284 ms |
| cold: last one-time work finished | 26.2 s | 14.6 s |

`interactive.loadMs` covers the streamer up to its last texture, so folding the audio into that pass moves
~1.9 s *into* the number it used to sit after (12.3 s → 14.2 s on this container) while the whole cold
load finishes ~11.6 s sooner and time-to-playable is unchanged. Read the two together.

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

## Material sharing, and why a cold load used to end up with more of them

Materials are shared by complete state: the texture's exact content identity plus colour, blend, metallic,
smoothness and cull. The texture half is the hash of the cached `.tex` file rather than the cache key that
names it, so two keys holding byte-identical pixels get one material.

That identity only exists once the texture has been extracted, and a cold load builds its materials before
that: the mesh phase of a decode pass runs, the scene is built so the map is playable, and the `.resS` tail
streams in afterwards. Every key therefore stood in for its own identity and the aliases never merged — the
realise line read `0 exact texture-key aliases` on a cold load where a warm one read `22`, so a first
session carried more material resources than every session after it, for the same scene.

`ObjectStreamer` closes that at the end of streaming: it re-resolves the provisional identities on a worker
(hashing the texture cache is not frame work), then re-groups the surfaces it recorded and points the
aliases at one material. Read it from the log line it prints, against the realise line above it:

```
[stream] materials realised: 292 resources, 0 exact texture-key aliases
[stream] material re-dedup: 226 identities settled, 38 surfaces re-pointed, 292 -> 273 material
         resources, 22 exact texture-key aliases in 24 ms
```

Measured on PEI (Tier 3, this container), material resources in the built scene:

| Path | Before | After |
|---|---:|---:|
| Cold (base + LOD1 libraries) | 292 + 177 = 469 | 273 |
| Warm (base + LOD1 libraries) | 273 + 165 = 438 | 273 |

Two things move that number, and they are worth keeping apart. Most of it is the lower LOD level: it draws
with the same materials as the base level, and it used to build its own table, so every material existed
twice. Sharing one table across both levels is what removes the 177/165, on the cold and warm path alike
(`WorldBuilder` shares one too, which is why the structural gate's `uniqueMaterials` moved 455 -> 290). The
re-dedup pass is the rest: 292 -> 273, which is exactly the warm number, so a first session and a second one
now build the same scene.

**Draw calls do not move**: 780 median on Tier 3 before and after. Surfaces are grouped inside each mesh by
raw texture key, and a MultiMesh submits one draw per surface whichever material resource that surface
points at. What this saves is material resources and their GPU parameter buffers, not submissions — measure
it with the log lines above and `uniqueMaterials`, not with `runtime.drawCalls.median`.

## Object LOD ideas that were measured and dropped

Recorded because each one looks obviously worth doing until it is measured, and the counts that rule
them out cost hours to reproduce. Measured per pose on PEI and Germany against the shipped defaults.

**A third authored level.** Prefabs name their levels `_0`, `_1`, `_2`; the port caches the first two.
Only a tenth of prefab roots ship a third, and maps place few of them: on the two maps measured they
were about 3% and 4% of placements, bounding what a third level could remove at roughly 2-4% of the
ground view's geometry — and that bound assumes every such placement is simultaneously far enough to
use it. Against that, every extra level costs another batch per chunked group, which is the cost the
cell sizing works to avoid. The assets involved are mostly street furniture (lights, signals, signs),
not the trees and fences that dominate placed geometry.

**Reading Unity's authored LOD distances.** A `LODGroup` stores a screen-height fraction per level,
which at a fixed FOV is a multiple of object size — the same rule shape the port already uses, so the
only question was the constant and the per-asset spread. The authored switches cluster around the value
the port uses, and sweeping that value across a factor of four moves the ground view's geometry by well
under a percent while leaving the aerial poses byte-identical. Per-asset thresholds cannot beat that
bound. The reason is that automatic mesh LOD has already decimated the base mesh by the distances where
an authored level would take over, so which of the two answers first barely matters.

**Raising the viewport's mesh LOD threshold.** Unlike the other two this one is a real trade rather
than a dead end, and how large it looks depends entirely on which map you measure. On a sparse map the
ground poses barely move, because the vantage the harness picks there is open beach with nothing distant
in frame — the near and aerial poses carry the whole effect. On a dense map the same setting takes a
meaningful share off the ground view, which is the view that matters: approaching a tenth at the value
shipped here, and half again as much at the settings above it.

The cost is visible too, and again only a dense map shows it: raising it far enough thins distant tree
canopies and coarsens skyline landmarks, with the differing pixels concentrated in the horizon band.
That cost is very unevenly distributed across the range. One step above the engine default is free by
both measures — the differing pixels are around a hundredth of a percent of the frame and the captures
are indistinguishable — while each step after that costs several times more pixels for progressively
less geometry. So the shipped
value takes the free step and stops; `UG_MESH_LOD_THRESHOLD` reaches the rest for anyone who wants to
retrade it, and zero disables automatic mesh LOD entirely as the A/B control.

The wider lesson is about the harness: a conclusion drawn from one map's ground pose can be wrong by an
order of magnitude, in both the saving and the cost. Check a dense map before believing either.

Profiling output (`*.nettrace`, `heaptrack.*.zst`, `massif.out.*`, `perf.data`, `*.rgp`) is git-ignored.
