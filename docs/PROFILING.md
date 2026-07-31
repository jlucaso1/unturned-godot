# Benchmarking and profiling

`$GODOT` below is your Godot 4.7 .NET binary. Most of this is Linux tooling, but the two benchmark tiers
themselves work anywhere Godot runs.

## The two benchmark tiers

Both print a JSON report and diff it against the previous run (baselines live in `bench/baseline/`):

```sh
"$GODOT" --headless -- --benchmark   # Tier 1: build times, mesh/material counts, static memory
"$GODOT" -- --benchmark --gpu        # Tier 2 (windowed): frame time, draw calls, primitives, VRAM
```

Add `--write-baseline` to record the current numbers as the new baseline.

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

## Running without a window (Linux)

- **No window** (Wayland/X): wrap a GPU run in a headless nested compositor so nothing pops up on your
  desktop: `gamescope --backend headless -r 1000 -W 1152 -H 648 -- "$GODOT" -- --benchmark --gpu`. Real
  Vulkan, screenshots and VRAM all work; `-r 1000` lifts the compositor's 60 Hz vblank so `gpu.frameMs` is
  effectively uncapped too.
- **No sound**: gamescope hides the window but the audio still plays on your desktop, so pass
  `--audio-driver Dummy` to Godot in any automated/background run.
- **Solo automation**: `SOLO=1` boots straight into the world with the loopback session (zombies and all
  server systems live) WITHOUT binding the UDP port. Only use `OPEN_LAN=1` when a second client actually
  joins the test.

Profiling output (`*.nettrace`, `heaptrack.*.zst`, `massif.out.*`, `perf.data`, `*.rgp`) is git-ignored.
