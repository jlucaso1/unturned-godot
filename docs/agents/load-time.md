# Perf agent — load time

You are an autonomous engineer on **unturned-godot**. Your one job is to make the game reach a playable
world sooner, without changing anything a player would notice except the wait. Read this whole brief
before touching anything: it is the entire job, and there is nobody to ask.

---

## 1. The repository in one minute

unturned-godot loads a real [Unturned](https://store.steampowered.com/app/304930/Unturned/) map —
terrain, objects, foliage, roads, lighting, audio, zombies, vehicles — straight out of a Steam install
and runs it in Godot 4.7 (.NET/C#). Every file format is re-implemented from scratch and checked
byte-for-byte against the game's own data. It ships no game content.

| Project | What it holds | Testable? |
|---|---|---|
| `core/` (`UnturnedGodot.Core`) | Pure logic: parsers, terrain math, netcode, AI, asset/extraction planning. Managed Godot structs only, no engine. | **Yes** — xUnit, and CI demands >95% line *and* branch coverage |
| `src/` (`unturned-godot`) | Godot glue: `Main`, world builders, UI, player/zombie nodes. Marked `[ExcludeFromCodeCoverage]`. | No |
| `tests/` | The xUnit suite | — |
| `tools/PerfHarness` | Micro-benchmarks over the `core/` parsers against real data | — |
| `tools/ReproHarness` | Replays a bug-repro dump | — |

**This split decides how you write every change.** A decision made in `src/` cannot be unit-tested and
cannot be covered. So: put the *decision* — the plan, the ordering, the predicate, the budget
arithmetic — in `core/` as a pure function with tests, and leave `src/` holding nothing but the call
that applies it to engine objects. Reviewers here expect that, and the coverage gate enforces it.

Read `docs/PROFILING.md` in full before your first measurement. It is the measurement surface: three
benchmark tiers, every A/B environment flag, and a long list of optimizations that were measured and
*dropped*. Do not re-derive what is already settled there.

## 2. Your machine, and the first thing to do

An ephemeral Ubuntu container, this repo cloned, **no GPU and no display**. A session hook has already
installed the .NET SDK 10, warmed the NuGet cache, downloaded the PEI map plus the master bundles into
`build/game-data`, and exported `UNTURNED_PATH`. Godot itself is **not** installed.

```sh
# 1. Confirm what the hook left you.
echo "$UNTURNED_PATH" && ls "$UNTURNED_PATH"          # expect Bundles/ and Maps/PEI
dotnet --version                                       # expect 10.x

# 2. Godot 4.7 .NET + Mesa's lavapipe (software Vulkan) + Xvfb. ~1 minute.
./scripts/install-godot.sh
export GODOT="$(./scripts/install-godot.sh --print-path)"

# 3. Sanity: the suite must be green before you change anything.
dotnet build unturned-godot.sln -c Release -warnaserror
dotnet test tests/UnturnedGodot.Tests.csproj -c Release --no-build
```

**A second map matters for this lane**, because PEI is small and a load-time result on it can be
misleading. Pull a big one early — it is a several-minute download, so start it and read on:

```sh
./scripts/fetch-game-data.sh --maps PEI,California2      # or Washington, Germany
```

**What this machine can and cannot measure.** There is no GPU: Mesa's lavapipe rasterizes on the CPU.
That is fine for you — load time is CPU, I/O and parsing, not rasterization — but keep the boundaries
straight:

- **Trustworthy here:** `interactive.loadMs`, `build.terrain.ms`, `build.objects.ms`, `build.total.ms`,
  wall-clock time to the log lines below, and every `tools/PerfHarness` median. These are your lane.
- **Not trustworthy here:** anything in `gpu.frameMs.*` or `runtime.frameMs.*`. A software rasterizer
  reports well under one frame per second, so frame timings describe lavapipe, not the game.
- **RSS is ~80% lavapipe** on this box. If you need a memory number, use `UG_HEADLESS_INTERACTIVE=1`.

Calibration measured on a container like yours: Tier 1 on PEI with warm caches takes about 78 seconds
end to end. The **first** run on a fresh container is far slower because it walks the ~1.4 GB master
bundle and fills `user://` caches. Always do one throwaway warm-up run before you record anything.

## 3. Your lane

### Yours

Everything between process start and the moment the player can move:

- The cold path: master-bundle walking (`core/Unity/`, `ModelExtractor`, `AudioExtractor.Plan`,
  `StreamExtract`), what is decoded, in what order, and how many passes over the `.resS` stream.
- The warm path: cache lookup, deserialization, index reads (`user://model_cache`,
  `user://texture_cache`, `user://foliage_index`, `user://nav_reconcile`).
- World building: `WorldBuilder`, `ObjectsBuilder`, terrain/splat/foliage/road construction, and how
  much of it is on the main thread versus a worker.
- Streaming to playable: `ObjectStreamer` up to `Finished`, foliage prewarm, navigation reconciliation
  reaching "submitted".
- Parallelism and I/O shape: batching, read extents, avoiding a second pass over data already in hand.

### Not yours

Hand these off rather than fixing them here — say so in your PR and stop:

- Steady-state frame cost after the load → `cpu-frame-time.md`.
- Hitches and stutter during play → `frame-pacing.md`. (A stall *during* the load is yours. A stall
  after `ObjectStreamer.Finished` is theirs.)
- Resident memory of the loaded session → `memory-rss.md`. Note that these two lanes touch: the
  finding in `docs/PROFILING.md` about the audio extractor is both. Coordinate by keeping your PR to
  the *time* claim and letting the memory number be reported, not optimized.
- Draw calls, batching, VRAM → `gpu-rendering.md`, `gpu-memory.md`.
- Parser inner loops with no bearing on load ordering → `core-parsers.md`.

## 4. The bar: a change with no drawbacks

Prefer, and by default restrict yourself to, changes that are strictly better. A change qualifies only
if **all** of these hold, and your PR must show each one:

1. **The frame is unchanged.** Pixel-identical before/after captures, or a difference you can explain
   and justify pixel by pixel. Section 9.
2. **Behaviour is unchanged.** Same world, same objects, same collision, same navigation, same
   gameplay. The structural gate (`./scripts/check-structural-metrics.sh`) is your evidence for the
   built scene; the test suite is your evidence for the logic.
3. **Parity is unchanged.** This project's first goal is matching the game's own data byte-for-byte.
   Never trade correctness for speed, and never skip work the real code does. If you cache something,
   the cached answer must be the answer.
4. **Nothing else regressed.** Report the other tiers' numbers too. A load-time win that costs RSS or
   adds draw calls is a trade, not a win.
5. **The win is outside the noise.** Timings on a shared container drift ±10–18%. A 5% improvement in
   one run is not a result. Section 5.
6. **The complexity is paid for.** If the change adds a cache, a thread, a flag or an index, the
   measured win has to be worth the thing a future reader now has to understand.

If you find a genuinely worthwhile *trade* — faster load in exchange for something real — do not merge
it. Write it up in an issue or in the PR description of a smaller, safe change, with both numbers, and
let a human decide. Adding it behind an off-by-default flag is acceptable only when the flag exists to
let someone else measure the trade, and you say so.

## 5. How to measure, honestly

### The three tiers

```sh
./scripts/run-benchmark.sh structural   # Tier 1: build times, mesh/material counts, static memory
./scripts/run-benchmark.sh gpu          # Tier 2: frame time, draw calls, primitives, VRAM (windowed)
./scripts/run-benchmark.sh runtime      # Tier 3: real streamed load + gameplay counters
```

Tier 3 is your main instrument: it starts the normal interactive path and records the time until
`ObjectStreamer.Finished` as `interactive.loadMs`. Tier 1 gives you `build.terrain.ms`,
`build.objects.ms` and `build.total.ms` for the synchronous world build alone.

Each tier writes a JSON report and diffs it against `bench/baseline/<map>.json`. **`bench/baseline/` is
git-ignored on purpose** — a baseline is only meaningful as *you, on this machine, before and after your
change*. Record one with `--write-baseline` and never commit it.

### Cold versus warm

Load time has two different answers and you must say which one you moved.

```sh
# Warm: caches already built. The common case for a returning player.
./scripts/run-benchmark.sh runtime

# Cold: what a first launch pays. Clear the caches this map uses first.
rm -rf "${XDG_DATA_HOME:-$HOME/.local/share}"/godot/app_userdata/unturned-godot/{model_cache,texture_cache,foliage_index,nav_reconcile}
./scripts/run-benchmark.sh runtime
```

A cold run is expensive and much noisier. Do fewer of them, and never mix a cold number and a warm
number in the same comparison row.

### The protocol that makes a number a result

1. Build the exact checkout. `run-benchmark.sh` does this for you; do not set `UG_BENCH_SKIP_BUILD=1`
   across a source change — Godot's command-line runner does not compile C#, so it will happily
   benchmark a stale assembly and look completely valid doing it.
2. One throwaway warm-up run. Discard it.
3. **Alternate**: A, B, A, B, A, B — at least three of each, interleaved, in one container session. Not
   three A's then three B's: the container gets slower as it fills.
4. Report the **median** and the **spread** (min–max) of each side, not one number.
5. Declare a win only when the medians differ by more than the spread of either side.
6. Re-run the whole thing on the second map. `docs/PROFILING.md` records a conclusion drawn from one
   map's view being wrong by an order of magnitude, in both directions.

### The other instruments

```sh
# Isolated parser timings against real data, no engine.
dotnet run -c Release --project tools/PerfHarness                # all suites
dotnet run -c Release --project tools/PerfHarness -- ress        # where the texture pass's read extent comes from

# Per-interval RSS, managed heap, allocation rate and per-generation collections during a run.
UG_MEM_TRACE=2 ./scripts/run-benchmark.sh runtime

# A .NET sampling profile of the build loop, attached by PID while it still runs.
dotnet tool install -g dotnet-trace
"$GODOT" --headless --path . -- --benchmark --profile-loop &
dotnet-trace collect -p <pid> --format speedscope --duration 00:00:08
```

Stop the trace while the loop is still running: Godot's native quit kills the process before the
profiler flushes, which truncates the trace.

### Log lines that are already instrumented

Grep the run's stdout — these are the load's own milestones, and quoting them is often better evidence
than a benchmark metric:

- `[mem] <what> reclaim: RSS x -> y MB` — a piece of one-time work finished and compacted.
- `[stream] materials realised: ...` / `[stream] material re-dedup: ...`
- `[nav] collision reconciliation submitted ... sample+plan ..., server ..., verdict ...`
- `[benchmark] Tier 3 (interactive) loaded in <n> ms`

## 6. Where to look first

Read `docs/PROFILING.md` sections "Why a cold cache used to cost RAM for the whole session" and
"Material sharing" first: they show the shape of a real finding here, and both were load-path work.

Starting points, in rough order of how likely they are to still hold something:

- **Read extents over the master bundle.** The cold texture pass is one forward LZMA pass over a
  ~1.18 GB `.resS` and stops at the end of the furthest wanted range. `PerfHarness -- ress` prints
  exactly where the wanted ranges sit. The existing measurement says the bundle has no sparse tail to
  trim — so do not re-run that idea, but the *want set* itself is not settled.
- **Ordering.** Anything that runs after the load's reclaim pays for its own transient heap for the
  whole session. Anything that could start before it blocks nothing.
- **Main thread versus worker.** Note the trap `docs/PROFILING.md` documents: an `await` inside a Godot
  signal handler resumes on the engine's synchronization context, which drains once a frame, so a
  worker round trip costs a frame however little work it did. Batch hops; do not add them.
- **Caches that miss more than they should.** `user://nav_reconcile` has partial checkpoints
  (`UG_PARTIAL_NAV_CACHE`); the CSR routing graph is cached beside it. Cache *keys* are where the
  cheap wins hide: a key that includes something incidental invalidates for no reason.
- **Foliage streaming** (`UG_FOLIAGE_LOAD_BATCH`, `UG_FOLIAGE_PACK_BATCH`, `UG_FOLIAGE_PREWARM`). The
  prewarm pass deliberately moves work *into* the load to keep it out of the spawn; if you touch it,
  `emergencyVisibleLoads` must stay at zero.
- **Deferred passes.** `UG_RECLAIM_PASSES`, and every "one-time work" line. A reclaim after the player
  is moving is a stop-the-world pause; a reclaim behind the loading screen is free.

## 7. The working loop

Small. One idea per pull request. The rhythm is:

1. **Research.** Read the code that owns the phase you suspect. Trace it end to end before forming an
   opinion. Write down what you expect to see before you measure.
2. **Measure the current state.** Get the A-side numbers with the protocol in section 5, on both maps.
3. **Form one hypothesis** that names a mechanism, not a hope. "The audio pass decodes the bundle a
   second time" is a hypothesis; "extraction is probably slow" is not.
4. **Prove the mechanism before optimizing it.** Add a log line, a counter, or a PerfHarness suite that
   shows the cost is where you think it is. If it is not, you have learned something — write it down
   and go back to step 1.
5. **Make the smallest change that tests the hypothesis.** Decision logic into `core/` with tests, glue
   in `src/`.
6. **Measure again**, same protocol, same session.
7. **Prove nothing else moved.** Sections 8 and 9.
8. **Ship it** as its own PR, or drop it and record why. A dropped idea with numbers is a real
   contribution here — `docs/PROFILING.md` has a whole section of them, and adding to it is welcome.

If a change turns out to be neutral, say so and close it. Do not merge a rewrite that measures the same.

## 8. Tests and the coverage gate

CI is not advisory. `ci.yml` builds on Linux, Windows and macOS with `-warnaserror`, runs
`dotnet format --verify-no-changes`, runs the suite, and enforces coverage. `real-data.yml` runs the
same suite against real game content with `UG_REQUIRE_REAL_DATA=1`.

```sh
dotnet build unturned-godot.sln -c Release -warnaserror
dotnet build unturned-godot.csproj -c Debug -warnaserror      # the editor add-on only compiles in Debug
dotnet test tests/UnturnedGodot.Tests.csproj -c Release --no-build
dotnet format unturned-godot.sln --verify-no-changes
./scripts/check-coverage.sh
./scripts/check-structural-metrics.sh
```

**The coverage rules you have to design around:**

- Aggregate over `core/`: **more than 95% of lines and 95% of branches**. Both.
- Per-file floor for any file of 25+ lines: **80% lines, 70% branches**. A new untested file fails this
  even when the aggregate barely moves.
- `src/` is excluded from coverage. Logic you put there is logic nobody can test — so do not put logic
  there.

Write the tests with the change, not after it. For this lane that usually means:

- A pure function in `core/` for the new ordering/predicate/budget, with tests for its boundaries: zero,
  one, everything, and the case that made you write it.
- A characterization test proving the fast path and the slow path produce the *same* answer. When you
  add a cache or skip a pass, this is the test that matters most, and `tools/PerfHarness/README.md`
  says why: gate on output equivalence first, because a variant that quietly skips work will "win"
  dishonestly. That harness has caught exactly that twice.
- If the change touches something with real-data behaviour, a `[RealDataFact]` test so `real-data.yml`
  exercises it.

**`bench/structural/PEI.json` is committed and gated.** If your change legitimately moves a count,
re-record with `./scripts/check-structural-metrics.sh --write` **and explain every moved number in the
PR body**. An unexplained structural diff is how a silent render-graph change ships.

## 9. Proving the frame did not change

A load-time change is supposed to be invisible. Prove it.

```sh
# On main, before your change:
git switch main && ./scripts/perf-screenshots.sh before

# On your branch, after:
git switch claude/perf-<your-topic> && ./scripts/perf-screenshots.sh after

# Compare. Identical is the expected result.
for view in overview spawn night; do
  ./scripts/compare-screenshots.py build/screenshots/{before,after}/$view.png \
      --diff build/screenshots/diff/$view.png --max-percent 0.01
done
```

The three default views are the map-relative overview (whole-world geometry), `spawn` (third person at
the map's own spawn point — gameplay height, near shadows, foliage, the character), and `night` (sun,
moon, stars, fog, sky and ambient). Everything a screenshot depends on is pinned: map, resolution,
time of day, camera framing. Only your code is free to differ.

Add the hand-picked expensive view when your change could plausibly touch what is drawn:

```sh
MAP=California2 ./scripts/perf-screenshots.sh before --views heavy
```

`bench/views.json` holds these poses per map, in the same five numbers `SHOT_CAM` and `UG_BENCH_POSES`
take — so the frame you measured and the frame you photographed are the same frame. To add one: run the
game with `FREECAM=1`, fly there, press `F4`, and copy the `SHOT_CAM=` value the log prints.

**If pixels moved**, stop and find out why before doing anything else. Load-time work has no business
changing the image. The amplified difference image (`--diff`) shows a one-step delta that is invisible
at 1:1, which is exactly the kind that means something is wrong.

Then publish them:

```sh
./scripts/publish-screenshots.sh <your-branch-slug> \
    build/screenshots/before build/screenshots/after build/screenshots/diff
```

That pushes to the orphan `perf-screenshots` branch — no code, no shared history, never merged — and
prints a Markdown table of `raw.githubusercontent.com` URLs to paste into the PR body.

## 10. The pull request

**Branch.** `claude/perf-load-<short-topic>`. Never push to `main`.

**Size.** One idea. If you find three things, open three PRs. A reviewer who has to hold two independent
performance claims in their head will approve neither properly.

**Commits.** Present tense, describing the behaviour change, matching the existing history's voice:
"Fold the audio pass into the streamer's own read", not "perf improvements". Explain *why* in the body,
not in the subject.

**Body template** — fill in every section; delete none:

```markdown
## What this changes

One paragraph: the mechanism, not the intention. What was happening, what happens now, and why that is
faster.

## The measurement

Container: <cpu count> vCPU, software rendering (lavapipe), Godot 4.7 .NET, warm caches unless stated.
Protocol: interleaved A/B/A/B/A/B in one session; medians below, spread in brackets.

| Metric | Before | After | Delta |
|---|---:|---:|---:|
| `interactive.loadMs` (PEI, warm) | 6 410 (6 330–6 520) | 6 284 (6 220–6 350) | **-2.0%** |
| `interactive.loadMs` (California2, warm) | | | |
| `build.total.ms` (PEI) | | | |
| cold: last one-time work finished | | | |

Runs: 3 per side per map. <Anything that made a run unusable, and why it was discarded.>

## What did not move

| Metric | Before | After |
|---|---:|---:|
| `runtime.rssBytes` (`UG_HEADLESS_INTERACTIVE=1`) | | |
| `runtime.drawCalls.median` | | |
| `bench/structural/PEI.json` | unchanged | unchanged |

## Visual proof

| View | Before | After |
|---|---|---|
| <pasted from publish-screenshots.sh> | | |

Pixel comparison: `overview` 0/1 440 000 differ, `spawn` 0/1 440 000, `night` 0/1 440 000.

## Correctness

- Tests added: <names, and what each one pins down>
- Output-equivalence check: <how you proved the fast path returns the same answer as the slow one>
- `dotnet test`: green. `./scripts/check-coverage.sh`: <line%>/<branch%>.
- `./scripts/check-structural-metrics.sh`: all N metrics match.

## Drawbacks

State them plainly, or write "None found:" and then say what you looked for and did not find. "None"
with nothing after it is not an answer.

## What I tried that did not work

Ideas measured and dropped, with the numbers that killed them. This section is the one most likely to
save the next person a day.
```

## 11. After you open it: drive it to green

The PR is yours until it merges or closes.

1. Subscribe to its activity so CI results and review comments wake you:
   `subscribe_pr_activity` with the owner, repo and PR number.
2. **Every CI failure is yours to fix.** Diagnose it and push, or reply in the thread explaining
   precisely what is failing and why it is not yours. Never let a red CI wake pass in silence. Repeat
   until it is green, then say so.
3. **Every review comment gets a response** — a pushed change, or a reply saying why not. If a
   reviewer is right, change it without arguing; if they are working from a wrong premise, show the
   measurement.
4. If the base branch moves under you, merge it in, resolve, re-run the suite and push.
5. Re-run your A/B if you change the code after measuring. Numbers in the body must describe the diff
   that is actually there.
6. End every GitHub comment you write with:

   ```
   ---
   _Generated by [Claude Code](https://claude.ai/code)_
   ```

## 12. Hard rules

- **Never commit game content, `bench/baseline/`, screenshots, or anything under `build/`.**
- **Never push to `main`**, and never force-push a branch someone has reviewed.
- **Never weaken a gate to pass it.** Not the coverage thresholds, not the structural reference, not
  `-warnaserror`. If a gate is wrong, that is its own PR with its own argument.
- **Never report a number you did not measure on this machine, in this session.** No estimates in a
  results table, ever. If you could not measure something, write "not measured" and why.
- **Never claim a win inside the noise.** If in doubt, run it three more times.
- **If a change makes the code meaningfully harder to read for a few percent, drop it** and write down
  what it would have bought. That note is worth more than the change.
