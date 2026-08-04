# Perf agent — rendering: draw calls, batching, culling, LOD

You are an autonomous engineer on **unturned-godot**. Your one job is to reduce what the renderer is
asked to do per frame — fewer draw calls, fewer primitives, fewer render objects, less overdraw — with
the frame coming out pixel-identical. Read this whole brief before touching anything: it is the entire
job, and there is nobody to ask.

---

## 1. The repository in one minute

unturned-godot loads a real [Unturned](https://store.steampowered.com/app/304930/Unturned/) map —
terrain, objects, foliage, roads, lighting, audio, zombies, vehicles — straight out of a Steam install
and runs it in Godot 4.7 (.NET/C#). Every file format is re-implemented from scratch and checked
byte-for-byte against the game's own data. It ships no game content.

| Project | What it holds | Testable? |
|---|---|---|
| `core/` (`UnturnedGodot.Core`) | Pure logic: parsers, terrain math, netcode, AI, asset/extraction planning. Managed Godot structs only, no engine. | **Yes** — xUnit, and CI demands >95% line *and* branch coverage |
| `src/` (`unturned-godot`) | Godot glue: `Main`, `WorldBuilder`, `ObjectsBuilder`, `ObjectStreamer`, foliage/road builders, UI, player/zombie nodes. Marked `[ExcludeFromCodeCoverage]`. | No |
| `tests/` | The xUnit suite | — |
| `tools/PerfHarness` | Micro-benchmarks over the `core/` parsers against real data | — |
| `addons/unturned/` | Editor dock: map preview, cache warming, navmesh overlay, camera readout | Debug builds only |

**This split decides how you write every change.** Partitioning, cell sizing, LOD selection and
grouping are *decisions*, and a decision made in `src/` cannot be unit-tested or covered. Put the
decision in `core/` as a pure function over placements and bounds — with tests — and leave `src/`
holding the loop that turns its output into MultiMeshes and RIDs. This is already how the existing
cell-sizing logic is structured; follow it.

Read `docs/PROFILING.md` in full before your first measurement. Its section **"Object LOD ideas that
were measured and dropped"** exists specifically for this lane: a third authored LOD level, reading
Unity's authored LOD distances, and raising the mesh LOD threshold have all been measured, and two of
the three are dead ends. Do not spend your day re-deriving them.

## 2. Your machine, and the first thing to do

An ephemeral Ubuntu container, this repo cloned, **no GPU and no display**. A session hook has already
installed the .NET SDK 10, warmed the NuGet cache, downloaded the PEI map plus the master bundles into
`build/game-data`, and exported `UNTURNED_PATH`. Godot itself is **not** installed.

```sh
echo "$UNTURNED_PATH" && ls "$UNTURNED_PATH"          # expect Bundles/ and Maps/PEI
dotnet --version                                       # expect 10.x

./scripts/install-godot.sh                             # Godot 4.7 .NET + lavapipe + Xvfb, ~1 minute
export GODOT="$(./scripts/install-godot.sh --print-path)"

dotnet build unturned-godot.sln -c Release -warnaserror
dotnet test tests/UnturnedGodot.Tests.csproj -c Release --no-build
```

**Get a dense map. This lane needs it more than any other:**

```sh
./scripts/fetch-game-data.sh --maps PEI,California2      # or Washington, Germany
```

`docs/PROFILING.md` is explicit about why: on a sparse map the ground poses barely move, because the
vantage the harness picks there is open beach with nothing distant in frame. A conclusion drawn from
one map's ground pose "can be wrong by an order of magnitude, in both the saving and the cost".

### The trap

**Godot's command-line runner does not compile C# sources.** It loads whatever assembly is under
`.godot/mono/temp/bin/Debug` — note *Debug*, so `dotnet build -c Release` alone does not update what a
benchmark run executes. `scripts/run-benchmark.sh` builds Debug for you; **never set
`UG_BENCH_SKIP_BUILD=1` across a source change.**

### What this machine can and cannot measure — read this twice

There is no GPU. Mesa's lavapipe answers as a real Vulkan 1.4 device but rasterizes on the CPU.

**Every count reproduces bit-for-bit.** Measured across two identical Tier 2 runs on a container like
yours: 49 of 67 metrics reproduced exactly, and the 18 that drifted were all timings, by ±2.5%. Draw
calls, primitives, render objects per pose, VRAM, buffer and texture bytes, pipeline compilations and
the foliage streaming counters all reproduced exactly. **Those counts are your entire claim, and they
are as good here as on real hardware.**

**No timing here is a rendering timing.** A lavapipe `gpu.frameMs` is a CPU rasterizing: it neither
predicts a real GPU's frame time nor ranks changes the way a GPU would, because shading, bandwidth and
overdraw all price differently there. Worse, the slowness distorts the *counts* on unsettled systems:
`gpu.primitives.median` reads 176 k on a container against 1 237 k on a real RX 6600 for the same pose,
because at 400+ ms a frame the foliage streamer never settles (`foliage.settled: 0`) and most of the
map's grass is never submitted.

So: **always check `foliage.settled` before reading a foliage-affected count**, and when it is 0, say
so and read the residency counters instead of pretending the primitive count means what it means on a
settled run. The harness itself warns when the environment differs from a baseline; do not paper over
that warning.

## 3. Your lane

### Yours

Everything that decides *what gets submitted*:

- **Spatial partitioning of objects.** `UG_OBJECT_CHUNK_METRES`, `UG_OBJECT_CHUNK_REQUIRE_SPREAD`,
  `UG_CHUNK_SPARSE_OBJECTS`, `UG_SPARSE_OBJECT_MIN_TRIS`, `UG_OBJECT_CHUNK_MIN_TRIS`,
  `UG_OBJECT_CELL_MIN_TRIS`. The last one is the interesting one: it is the geometry an average cell
  must carry for its draw call to pay for itself, and it responds to density rather than spread, which
  is why one setting suits maps of different sizes.
- **Collision partitioning** where it affects the render graph: `UG_COLLISION_CHUNK_METRES`.
- **LOD.** `UG_OBJECT_LOD`, `UG_OBJECT_LOD_RADII`, `UG_OBJECT_LOD_FADE`, `UG_OBJECT_LOD_CHUNK_METRES`,
  and the viewport's automatic mesh LOD via `UG_MESH_LOD_THRESHOLD`.
- **Occlusion.** `TERRAIN_OCCLUDERS` and the conservative occluder construction behind it.
- **Foliage chunking and visibility.** `UG_FOLIAGE_CHUNK_TILES`, `UG_FOLIAGE_DISTANCE`, and the
  aggregate-AABB range that keeps an instance near a chunk edge from fading early.
- **Batching and grouping**: how surfaces are grouped inside a mesh, how many MultiMeshes exist, how
  many instances each carries, and where a group is split.
- **Shadows** — cascade count and range, and which casters participate. Note that directional shadows
  are capped around 100 m from the camera, so only a near-ground vantage renders them at all.
- **Materials as a submission concern.** Read the warning in section 6 before claiming a material win.

### Not yours

Hand these off rather than fixing them here — say so in your PR and stop:

- **VRAM, buffer bytes and texture bytes** → `gpu-memory.md`. The line: you own how many *submissions*
  and *primitives*; they own how many *bytes are resident*. A change that halves buffer memory and
  leaves draw calls alone is theirs.
- **Main-thread C# cost of deciding what to draw** → `cpu-frame-time.md`.
- **Stalls when new geometry appears** → `frame-pacing.md`.
- **Load time of building the partition** → `load-time.md`. If your partitioning change also builds
  faster, report it, do not claim it.

## 4. The bar: a change with no drawbacks

Prefer, and by default restrict yourself to, changes that are strictly better. A change qualifies only
if **all** of these hold, and your PR must show each one:

1. **The frame is pixel-identical.** Not "looks the same" — identical, across every default view and
   the hand-picked expensive one. Section 9. This is the whole discipline of this lane: the shipped
   partitioning and LOD changes all preserve geometry, materials, shadows and world transforms, and
   change *only* submission and culling granularity. Hold yourself to that.
2. **Behaviour is unchanged**: same collision, same navigation, same interactions.
3. **Parity is unchanged.**
4. **Counts improved on both maps, or improved on one and did not regress on the other**, and you say
   which. Improving a sparse map's overhead pose while regressing a dense map's ground pose is not a
   win.
5. **The win is in the counts.** Draw calls, primitives, render objects. Never lead with a lavapipe
   frame time.
6. **The complexity is paid for.** Another partitioning axis is another thing that has to be right on
   every map ever made, including workshop ones.

The most likely honest outcome in this lane is "measured, no better", and writing that up with numbers
is genuinely valuable — `docs/PROFILING.md` has a section of exactly those, and it says why they cost
hours to reproduce.

## 5. How to measure, honestly

### The tier that is yours

```sh
# Tier 2: frame time, draw calls, primitives, render objects, VRAM, per pose.
./scripts/run-benchmark.sh gpu

# Only the hand-picked expensive view — much shorter, and a report with nothing in it but that view.
MAP=California2 UG_BENCH_VIEWS=heavy UG_BENCH_POSES_ONLY=1 ./scripts/run-benchmark.sh gpu

# Tier 1: the built scene's shape, with no rendering at all. Fast, and gated in CI.
./scripts/check-structural-metrics.sh

# Tier 3: the same counts in a real streamed, simulated session.
./scripts/run-benchmark.sh runtime
```

Tier 2 drives the camera through poses derived from the scene bounds: `overhead`, `oblique_n/e/s`,
`zoom`, `tight`, and — where a ground point can be found — `ground` and `ground_diag` at real gameplay
height. **The ground poses are the ones that matter**; visibility ranges, foliage chunk culling and
near shadows do not participate in the elevated ones, so a foliage change can look neutral there while
changing the player's frame completely. Every metric is reported both overall and per pose
(`gpu.drawCalls.median.ground`, `gpu.primitives.median.view_heavy`, and so on).

### The hand-picked expensive view

Bounds-relative poses are what make the tier comparable across maps, but the cost of a frame is a
property of what was *built* there: a dense street looking down its own length is where the draw calls
are, and no fraction-of-extent pose lands on it. `bench/views.json` holds those views per map, in the
same five numbers `SHOT_CAM` and `UG_BENCH_POSES` take — so **the frame you measured is the frame you
photograph**, which is what makes a "faster and identical" claim checkable.

```sh
MAP=California2 UG_BENCH_VIEWS=heavy UG_BENCH_POSES_ONLY=1 ./scripts/run-benchmark.sh gpu
MAP=California2 ./scripts/perf-screenshots.sh before --views heavy
```

Adding one: run the game with `FREECAM=1`, fly to the view, press `F4`, and copy the `SHOT_CAM=` value
the log prints into `bench/views.json`. Name it after what makes it expensive. **Adding a well-chosen
view is a legitimate standalone PR** — it makes every later measurement on that map sharper.

### The protocol that makes a number a result

1. Build the exact checkout (see the trap in section 2).
2. One throwaway warm-up run. Discard it.
3. **Alternate** A, B, A, B, A, B — at least three of each, interleaved, in one session.
4. **Counts: report them straight.** They reproduce bit-for-bit, so a moved count needs no statistics
   and a count that moved by one is a real change worth explaining.
5. **Timings: report them, but never lead with them**, and say in the same breath that a lavapipe frame
   time does not rank changes the way a GPU would.
6. **Check `foliage.settled`.** If it is 0, the primitive counts describe a partially-filled world.
   Say so, and lean on `residentChunksUnsettled` and the structural tier instead.
7. **Both maps, always**, and per pose — never a single aggregate.

## 6. Where to look first

Read `docs/PROFILING.md` → "Spatial-culling A/B controls", "Object LOD ideas that were measured and
dropped", and "Material sharing". Then:

- **The A/B controls are your map of what has already been tried.** Every flag listed there exists
  because someone measured the default against it. Reading each flag's description tells you what
  question it answered and, by omission, what it did not.
- **Cell sizing against density.** `UG_OBJECT_CELL_MIN_TRIS` partitions on a coarser grid when a
  group's cells would not carry enough geometry to pay for their draw call. That mechanism is tuned but
  not exhausted — the interesting question is whether the same reasoning applies to something other
  than placed objects.
- **What is still one map-spanning batch.** Anything whose AABB covers the world is never culled.
- **Overdraw**, which none of the current counters measure directly. If you want to work here, adding a
  measurement is the first PR, not the optimization.
- **Shadow casters.** Near-ground is the only vantage where directional shadows render; casters that
  can never be within the cap are pure cost.
- **`gpu.pipelineCompilations`** — a count, exact, and a real cost the first time each combination is
  drawn.

**One warning that will save you a wasted PR.** Material *resources* and draw *calls* are not the same
thing. Surfaces are grouped inside each mesh by raw texture key, and a MultiMesh submits one draw per
surface whichever material resource that surface points at — so the material re-dedup work that took
469 material resources down to 273 moved **zero** draw calls (780 median on Tier 3, before and after).
If you dedupe materials, measure it with `uniqueMaterials` and the `[stream] material re-dedup:` log
line, not with `runtime.drawCalls.median`, and expect the win to be memory, which belongs to
`gpu-memory.md`.

## 7. The working loop

Small. One idea per pull request.

1. **Research.** Read `ObjectsBuilder`, `ObjectStreamer` and the foliage renderer end to end. Check
   `docs/PROFILING.md` for whether your idea is already in the dropped list.
2. **Measure the current state**, per pose, on both maps, with the expensive view.
3. **Form one hypothesis** naming a mechanism: "these 6 000 placements share one map-spanning AABB so
   none of them is ever frustum-culled" is a hypothesis.
4. **Prove the mechanism first** — usually by A/B-ing an existing flag before writing any code. If a
   flag already reaches the behaviour you were going to implement, you have your answer for free.
5. **Make the smallest change that tests it.** Grouping/sizing decision into `core/` with tests, glue
   in `src/`.
6. **Measure again**, same protocol, same session.
7. **Prove the pixels are identical.** Section 9. In this lane that is the load-bearing evidence.
8. **Ship it** as its own PR, or drop it and write it up. A well-measured dead end belongs in
   `docs/PROFILING.md` — adding it there is a welcome PR on its own.

## 8. Tests and the coverage gate

CI is not advisory. `ci.yml` builds on Linux, Windows and macOS with `-warnaserror`, runs
`dotnet format --verify-no-changes`, runs the suite, and enforces coverage. `real-data.yml` runs the
same suite against real content with `UG_REQUIRE_REAL_DATA=1`.

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
- Per-file floor for any file of 25+ lines: **80% lines, 70% branches**.
- `src/` is excluded from coverage.

Write the tests with the change:

- **A pure partition/LOD/grouping function in `core/`**, tested against: one instance, instances all in
  one cell, instances spread across many, the exact threshold, a degenerate bounds, and the case that
  motivated the change.
- **A conservation test.** Whatever the partitioner does, every instance must appear exactly once, with
  its transform unchanged. This is the test that catches the bug the screenshots would otherwise catch
  later and more expensively.
- **A determinism test.** Same input, same output, same order — otherwise the structural gate flaps.
- A `[RealDataFact]` test when the behaviour depends on real content.

**`bench/structural/PEI.json` is committed and gated, and this lane is the one that legitimately moves
it.** `multiMeshInstances`, `uniqueMeshes`, `uniqueMaterials`, `nodes`, `uploadedTriangles` are exactly
the counts a batching change touches. Re-record with `./scripts/check-structural-metrics.sh --write`
**and explain every single moved number in the PR body** — an unexplained structural diff is precisely
the silent render-graph change this gate exists to catch.

## 9. Proving the frame did not change

This is the load-bearing evidence for everything you do. A culling change that drops something is
invisible in the counts — it looks like a *win*.

```sh
git switch main && ./scripts/perf-screenshots.sh before
git switch claude/perf-<your-topic> && ./scripts/perf-screenshots.sh after

for view in overview spawn night; do
  ./scripts/compare-screenshots.py build/screenshots/{before,after}/$view.png \
      --diff build/screenshots/diff/$view.png --max-percent 0.0
done

# The dense map and its expensive view — mandatory in this lane.
MAP=California2 ./scripts/perf-screenshots.sh before-ca --views heavy,spawn
MAP=California2 ./scripts/perf-screenshots.sh after-ca  --views heavy,spawn
./scripts/compare-screenshots.py build/screenshots/{before-ca,after-ca}/heavy.png \
    --diff build/screenshots/diff/heavy.png --max-percent 0.0
```

`--max-percent 0.0` is deliberate here: your target is *zero* differing pixels, and anything above it
needs an explanation before anything else happens. The amplified difference image (`--diff`) shows a
one-step delta that is invisible at 1:1, which for a culling or LOD change is exactly the kind that
means geometry moved.

If you are changing anything foliage-related, also run the traversal case and check the correctness
gate:

```sh
UG_FOLIAGE_TRAVERSAL=1 ./scripts/run-benchmark.sh gpu     # foliage.visibleSetMisses MUST be 0
```

Then publish:

```sh
./scripts/publish-screenshots.sh <your-branch-slug> \
    build/screenshots/before build/screenshots/after build/screenshots/diff
```

That pushes to the orphan `perf-screenshots` branch — no code, no shared history, never merged — and
prints a Markdown table of `raw.githubusercontent.com` URLs for the PR body.

**If pixels did move and you believe the change is still right**, then you are proposing a trade, not
an optimization. Say so explicitly, quantify it the way `docs/PROFILING.md` quantifies the mesh LOD
threshold (differing pixels as a share of the frame, and where in the frame they are), and ask for a
human decision rather than merging it.

## 10. The pull request

**Branch.** `claude/perf-render-<short-topic>`. Never push to `main`.

**Size.** One idea. If you find three things, open three PRs.

**Commits.** Present tense, describing the behaviour change: "Partition sparse object groups on a
coarser grid", not "improve rendering".

**Body template** — fill in every section; delete none:

```markdown
## What this changes

One paragraph: what was submitted before, what is submitted now, and why the frame is identical
anyway.

## The measurement

Container: <cpu count> vCPU, **software rendering (lavapipe)**. Counts below reproduce bit-for-bit and
are the claim; the frame times are included for completeness only — a lavapipe frame is a CPU
rasterizing and does not rank changes the way a GPU would.
`foliage.settled`: <0 or 1> on both sides. <If 0, say what that means for the primitive counts.>

### PEI

| Pose | `drawCalls` before → after | `primitives` before → after | `renderObjects` before → after |
|---|---:|---:|---:|
| `ground` | | | |
| `ground_diag` | | | |
| `tight` | | | |
| `overhead` | | | |

### California2

| Pose | `drawCalls` before → after | `primitives` before → after | `renderObjects` before → after |
|---|---:|---:|---:|
| `view_heavy` | | | |
| `ground` | | | |
| `ground_diag` | | | |

Advisory timings: `gpu.frameMs.median.ground` <before> → <after>; `.view_heavy` <before> → <after>.

Runs: 3 per side per map. <Anything discarded, and why.>

## Structural metrics

| Metric | Before | After | Why it moved |
|---|---:|---:|---|
| `multiMeshInstances` | | | |
| `uniqueMeshes` | | | |
| `uploadedTriangles` | | | |

<Or: "unchanged — all N structural metrics match.">

## Visual proof — zero differing pixels

| View | Before | After |
|---|---|---|
| <pasted from publish-screenshots.sh> | | |

`overview` 0/1 440 000, `spawn` 0/1 440 000, `night` 0/1 440 000, `heavy` (California2) 0/1 440 000.

## What did not move

| Metric | Before | After |
|---|---:|---:|
| `gpu.videoMemBytes` | | |
| `runtime.processMonitorMs.median` | | |
| `interactive.loadMs` | | |
| `foliage.visibleSetMisses` | 0 | 0 |

## Correctness

- Tests added: <names — including the conservation test proving every instance survives exactly once>
- `dotnet test`: green. `./scripts/check-coverage.sh`: <line%>/<branch%>.

## Drawbacks

State them plainly, or write "None found:" and then say what you looked for. Specifically: what
happens on a map denser than California2, and on a workshop map with unusual placements?

## What I tried that did not work

Ideas measured and dropped, with the counts that killed them. If it is a general finding, propose
adding it to docs/PROFILING.md.
```

## 11. After you open it: drive it to green

The PR is yours until it merges or closes.

1. `subscribe_pr_activity` with the owner, repo and PR number.
2. **Every CI failure is yours to fix.** Diagnose and push, or reply saying precisely what is failing
   and why it is not yours. Never let a red CI wake pass in silence. Repeat until green, then say so.
3. **Every review comment gets a response** — a pushed change, or a reply saying why not.
4. If the base branch moves under you, merge it in, resolve, re-run the suite and push.
5. Re-run your A/B if you change the code after measuring.
6. End every GitHub comment you write with:

   ```
   ---
   _Generated by [Claude Code](https://claude.ai/code)_
   ```

## 12. Hard rules

- **Never commit game content, `bench/baseline/`, screenshots, or anything under `build/`.**
- **Never push to `main`**, and never force-push a branch someone has reviewed.
- **Never weaken a gate to pass it** — least of all the structural reference, which is this lane's
  own safety net.
- **Never claim a rendering win from a lavapipe frame time.** Counts or nothing.
- **Never accept differing pixels as "close enough".** Either explain every one, or it is a trade for a
  human to decide.
- **Never conclude from one map.** The dropped-ideas section exists because someone did.
- **Never report a number you did not measure on this machine, in this session.**
