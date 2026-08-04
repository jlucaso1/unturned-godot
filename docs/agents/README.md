# Optimization agent prompts

Eight self-contained briefs, one per kind of performance work. Each file is a complete prompt: hand one
to a fresh agent on a fresh machine, with nothing but this repository cloned, and it has everything it
needs — how to stand the environment up, how to measure, where to look, what counts as proof, how to
test, how to prove the frame did not change, and how to open and then drive a pull request.

They are deliberately repetitive. A prompt that says "see the shared contract" is a prompt that breaks
the moment it is pasted somewhere the shared contract is not, so every one of them carries the whole
protocol inline.

| Prompt | Owns | Primary metrics |
|---|---|---|
| [load-time.md](load-time.md) | Time from launch to a playable world, cold and warm | `interactive.loadMs`, `build.*.ms`, time-to-playable |
| [memory-rss.md](memory-rss.md) | Resident memory of a loaded, simulated session | `runtime.rssBytes`, `runtime.managedLiveBytes` |
| [cpu-frame-time.md](cpu-frame-time.md) | Median CPU cost of a gameplay frame | `runtime.processMonitorMs.median`, `runtime.physicsMonitorMs.median`, subsystem counters |
| [frame-pacing.md](frame-pacing.md) | The tail: hitches, stalls, stutter | `runtime.frameMs.p99` / `.max`, `framesOver*`, `emergencyVisible.*` |
| [gpu-rendering.md](gpu-rendering.md) | What gets submitted: draw calls, batching, culling, LOD | `gpu.drawCalls.*`, `gpu.primitives.*`, `gpu.renderObjects.*` |
| [gpu-memory.md](gpu-memory.md) | What the GPU holds: buffers, textures, VRAM | `gpu.videoMemBytes`, `gpu.bufferMemBytes`, `gpu.textureMemBytes` |
| [core-parsers.md](core-parsers.md) | Engine-free throughput inside `core/` | `tools/PerfHarness` suite medians |
| [allocations-gc.md](allocations-gc.md) | Allocation rate and GC pressure, as a cause | `UG_MEM_TRACE` allocation/collection lines |

The lanes are drawn so two agents running at once do not edit the same files or claim the same win.
Each prompt has a "Not yours" section naming the neighbours it must hand work off to.

## What they all agree on

- **No drawbacks.** The bar is a change that is faster or smaller *and* costs nothing: no visual
  difference, no behaviour difference, no parity loss, no other metric traded away. A trade may be
  worth making, but it is not this work — those get written up and handed to a human, not merged.
- **Measured, not argued.** Paired A/B runs on one machine, repeated, with the counts (which are exact)
  separated from the timings (which are not). `docs/PROFILING.md` records several optimizations that
  looked obviously correct and were dropped once measured; the point of these prompts is to keep adding
  to that list rather than to that codebase.
- **Small pull requests.** One idea per PR, with its own numbers and its own screenshots.
- **Proof it looks the same.** Deterministic before/after captures, pixel-compared, published to the
  `perf-screenshots` branch and embedded in the PR body.

## The shared tooling

| Tool | What it does |
|---|---|
| `scripts/install-godot.sh` | Godot 4.7 .NET, Mesa's lavapipe (software Vulkan) and Xvfb |
| `scripts/run-benchmark.sh` | Runs any of the three benchmark tiers, with or without a GPU |
| `scripts/check-structural-metrics.sh` | Gates the committed render-graph counts in `bench/structural/` |
| `scripts/perf-screenshots.sh` | Captures the deterministic before/after screenshot set |
| `scripts/compare-screenshots.py` | Pixel-diffs two captures, with an amplified difference image |
| `scripts/publish-screenshots.sh` | Pushes a set to the `perf-screenshots` branch, prints the Markdown |
| `bench/views.json` | Hand-picked expensive camera poses per map, shared by the benchmark and the screenshot |
| `tools/PerfHarness` | Micro-benchmarks over the `core/` parsers against real game data |

`bench/views.json` is the piece that makes a rendering claim honest: the same five numbers drive
`UG_BENCH_POSES` (what was measured) and `SHOT_CAM` (what was photographed), so the frame in the
timing table is the frame in the screenshot.

```sh
MAP=California2 UG_BENCH_VIEWS=heavy UG_BENCH_POSES_ONLY=1 ./scripts/run-benchmark.sh gpu
MAP=California2 ./scripts/perf-screenshots.sh before --views heavy
```

Adding a view: run the game, fly there (`FREECAM=1`), press `F4`, and copy the `SHOT_CAM=` value out of
the log into `bench/views.json`. Name it after what makes it expensive.

See [../PROFILING.md](../PROFILING.md) for the measurement surface itself — the tiers, every A/B
environment flag, and the findings that are already settled.
