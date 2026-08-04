# Perf agent — GPU memory: buffers, textures, VRAM

You are an autonomous engineer on **unturned-godot**. Your one job is to reduce what the renderer holds
— vertex and instance buffers, textures, and total video memory — with the frame coming out
pixel-identical. Read this whole brief before touching anything: it is the entire job, and there is
nobody to ask.

---

## 1. The repository in one minute

unturned-godot loads a real [Unturned](https://store.steampowered.com/app/304930/Unturned/) map —
terrain, objects, foliage, roads, lighting, audio, zombies, vehicles — straight out of a Steam install
and runs it in Godot 4.7 (.NET/C#). Every file format is re-implemented from scratch and checked
byte-for-byte against the game's own data. It ships no game content.

The content path matters to you specifically. `core/Unity/` is a from-scratch reader for the game's
`core_*.masterbundle`: UnityFS container, LZ4 and LZMA blocks, SerializedFile v22, TypeTree-driven
object reader, meshes (vertex channels, UVs, submeshes, skinning), materials (`_Color` / `_MainTex`)
and Texture2D — DXT1, DXT5, BC7, RGB, RGBA, plus a Crunch decoder for the crunched variants workshop
maps lean on. Meshes, colliders and deduplicated textures are cached under `user://`; later runs load
only what the map needs.

| Project | What it holds | Testable? |
|---|---|---|
| `core/` (`UnturnedGodot.Core`) | Pure logic: parsers, the Unity reader, terrain math, asset/extraction planning. Managed Godot structs only, no engine. | **Yes** — xUnit, and CI demands >95% line *and* branch coverage |
| `src/` (`unturned-godot`) | Godot glue: `WorldBuilder`, `ObjectsBuilder`, `ObjectStreamer`, foliage renderer, UI. Marked `[ExcludeFromCodeCoverage]`. | No |
| `tests/` | The xUnit suite | — |
| `tools/PerfHarness` | Micro-benchmarks over the `core/` parsers against real data | — |

**This split decides how you write every change.** Identity, deduplication keys, format selection and
residency policy are *decisions*: put them in `core/` as pure functions with tests, and leave `src/`
holding the upload. The existing material identity — the hash of the cached texture's contents plus
colour, blend, metallic, smoothness and cull — is exactly this shape, and it is that way because the
cache *key* turned out not to be a strong enough identity.

Read `docs/PROFILING.md` in full before your first measurement.

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

./scripts/fetch-game-data.sh --maps PEI,California2    # start early; several minutes
```

A dense map matters here: PEI carries ~667 k foliage instances, Germany 7.2 M. A buffer-sizing result
from PEI alone tells you very little.

### The trap

**Godot's command-line runner does not compile C# sources.** It loads whatever assembly is under
`.godot/mono/temp/bin/Debug` — note *Debug*, so `dotnet build -c Release` alone does not update what a
benchmark run executes. `scripts/run-benchmark.sh` builds Debug for you; **never set
`UG_BENCH_SKIP_BUILD=1` across a source change.**

### What "VRAM" means on a machine with no GPU

Mesa's lavapipe answers as a real Vulkan 1.4 device, and Godot's video-memory monitors report real
numbers for it — but **that memory is system memory**. Two consequences you must keep straight and must
state in every PR:

- **The byte counts are still the right claim.** `gpu.videoMemBytes`, `gpu.bufferMemBytes` and
  `gpu.textureMemBytes` are what the renderer allocated for the resources you gave it. Across two
  identical Tier 2 runs on a container like yours, every count reproduced bit-for-bit — VRAM, buffer
  and texture bytes included. Only the clock moved.
- **They are not a real device's VRAM figures**, and they say nothing about bandwidth, sampling cost or
  whether a smaller format is actually faster to sample. If your change trades bytes for shader work,
  you cannot measure the shader half here — say so and hand the trade to a human.
- **Host RSS on this machine is ~80% lavapipe**, so do not read process RSS as GPU memory or vice
  versa. `UG_HEADLESS_INTERACTIVE=1` gives the game's own resident state with no driver at all, which
  is how the two are told apart.

## 3. Your lane

### Yours

Everything the renderer holds:

- **Deduplication of GPU resources.** `UG_DEDUP_GPU` (byte-exact sharing of cached meshes, textures and
  terrain control maps), `UG_DEDUP_MATERIAL_CONTENT`, `UG_DEDUP_FINAL_SHAPES`, `UG_DEDUP_COLLIDERS`.
  All on by default; the question is what is still not covered.
- **Instance/transform buffers.** A MultiMesh's transform buffer is often the largest single thing on a
  big map — the foliage residency work took Tier 2 GPU buffers from 335 MB to 147 MB on California2.
- **Foliage residency.** `UG_FOLIAGE_RESIDENCY`, `UG_FOLIAGE_DECODED_MIB`, `UG_FOLIAGE_PACK_BATCH`,
  `UG_FOLIAGE_PREFETCH_MARGIN`, `UG_FOLIAGE_UNLOAD_HYSTERESIS`, and the reported resident/indexed
  chunk and instance counts and buffer bytes.
- **Texture footprint.** Which formats survive extraction, whether mipmaps exist, whether a texture is
  resident for a map that never draws it, and the terrain control maps.
- **Material resources and their GPU parameter buffers** — this is where material dedup actually pays
  (see the warning in section 6).
- **Map preview artwork.** `UG_STATIC_MAP_PREVIEW_CACHE`; `PerfHarness -- previews` measures the
  installed library's full decoded RGBA footprint.
- **Vertex layout**: channels that are uploaded and never read.

### Not yours

Hand these off rather than fixing them here — say so in your PR and stop:

- **Draw calls, primitives, culling and LOD** → `gpu-rendering.md`. You own resident bytes; they own
  submissions. If your change is really about what gets submitted, it is theirs.
- **Host RSS** → `memory-rss.md`. If you free 100 MB of managed heap, that is theirs.
- **Stalls caused by upload pacing** → `frame-pacing.md`.
- **Extraction time and cache warming** → `load-time.md`. A smaller texture that takes longer to decode
  is a trade needing their numbers.

## 4. The bar: a change with no drawbacks

Prefer, and by default restrict yourself to, changes that are strictly better. A change qualifies only
if **all** of these hold, and your PR must show each one:

1. **The frame is pixel-identical.** Not "looks the same" — identical, across every default view and
   the hand-picked expensive one, on both maps. Section 9. A texture-format or mip change that is
   "visually indistinguishable" is a trade, not a win, and belongs to a human.
2. **Behaviour is unchanged**: same collision, same navigation, same interactions.
3. **Parity is unchanged.** A deduplicated resource must be byte-identical to the ones it replaced.
   Sharing by a key that is *nearly* an identity is the classic bug in this lane, and it is silent
   until it is on screen.
4. **Nothing else regressed**: draw calls, frame time, load time, host RSS.
5. **The win is in the byte counts**, which reproduce exactly here. Never lead with a lavapipe frame
   time.
6. **The complexity is paid for.**

If you find a genuinely worthwhile *trade* — fewer bytes for more sampling cost, or a format this
machine cannot price — do not merge it. Write it up with both numbers, say what you could not measure
here, and let a human decide.

## 5. How to measure, honestly

```sh
# Tier 2: the video-memory monitors, per pose, plus draw calls and primitives.
./scripts/run-benchmark.sh gpu

# Only the hand-picked expensive view: shorter, and a report with nothing else in it.
MAP=California2 UG_BENCH_VIEWS=heavy UG_BENCH_POSES_ONLY=1 ./scripts/run-benchmark.sh gpu

# Tier 3: the same monitors in a real streamed, simulated session, plus residency counters.
./scripts/run-benchmark.sh runtime

# The game's own host memory with no rasterizer, to keep the two apart.
UG_HEADLESS_INTERACTIVE=1 ./scripts/run-benchmark.sh runtime

# Decoded footprint of the installed map library's artwork, in isolation.
dotnet run -c Release --project tools/PerfHarness -- previews

# Where the cold texture pass's read extent comes from, and how the ranges are laid out.
dotnet run -c Release --project tools/PerfHarness -- ress
```

The metrics that are yours:

| Key | What it is |
|---|---|
| `gpu.videoMemBytes` | Total video memory the driver reports |
| `gpu.bufferMemBytes` | Vertex, index and instance buffers |
| `gpu.textureMemBytes` | Texture memory |
| `runtime.videoMemoryBytes` | The same, in a real simulated session (harness threshold: 5%) |
| `foliage.residentChunks` / `residentInstances` / buffer bytes | What residency is actually holding |
| `uniqueMeshes`, `uniqueMaterials` (Tier 1, committed and gated) | What survived deduplication |

**Always check `foliage.settled` before reading a foliage-affected number.** On a container this slow
it is often 0, and the counts then land on the `residentChunksUnsettled` key instead — deliberately, so
a mid-fill snapshot is reported as work in progress rather than diffed against a settled baseline as a
regression. A comparison between a settled run and an unsettled one is not a comparison.

### The protocol that makes a number a result

1. Build the exact checkout (see the trap in section 2).
2. One throwaway warm-up run. Discard it.
3. **Alternate** A, B, A, B, A, B — at least three of each, interleaved, in one session.
4. **Byte counts: report them straight**, they reproduce exactly. A count that moved by a few bytes is
   real and worth explaining.
5. **Cold versus warm matters here.** A cold load used to build more material resources than a warm one
   for the same scene (292 + 177 against 273 + 165), because texture identity does not exist until the
   texture is extracted. Say which path each row is.
6. **Both maps.** Foliage buffer results in particular do not transfer from PEI to a large map.
7. Timings: report, never lead.

## 6. Where to look first

Read `docs/PROFILING.md` → "Foliage residency reference A/B" and "Material sharing, and why a cold load
used to end up with more of them". They are the two worked examples in this lane, and the second one
contains the warning below.

Starting points:

- **Transform buffers.** Residency fixed foliage; nothing else has been asked the same question.
- **Textures nobody samples.** A map that never draws an asset should not hold its texture. The
  `TextureDependencyIndex` the runtime consults is the right starting thread.
- **Two names for the same bytes.** The material story is one instance; there may be others — meshes,
  control maps, colliders.
- **Formats.** Which of DXT1/DXT5/BC7/RGB/RGBA/Crunch actually survives to the GPU, and whether
  anything is being expanded on the way. Note that changing a format is a pixel change unless you can
  show it is not.
- **The `user://` caches as a source of truth.** Two cache entries holding identical bytes cost twice
  on disk and, once loaded, twice in memory.
- **Vertex channels** uploaded but never read by any material.

**The warning that will save you a wasted PR.** Material *resources* and draw *calls* are not the same
thing. The re-dedup work that took 469 material resources to 273 moved **zero** draw calls — 780 median
on Tier 3, before and after — because a MultiMesh submits one draw per surface whichever material
resource that surface points at. What it saved was material resources and their GPU parameter buffers.
That is a real win and it is *yours*; just measure it with `uniqueMaterials` and the
`[stream] material re-dedup:` log line, and never claim a draw-call improvement from it.

## 7. The working loop

Small. One idea per pull request.

1. **Research.** Read the extraction and upload path for the resource you suspect, end to end. Note who
   owns it and when it is released.
2. **Measure the current state**, both maps, and record `foliage.settled` for each run.
3. **Form one hypothesis** naming a mechanism: "the LOD1 library builds its own material table, so
   every material exists twice" is a hypothesis.
4. **Prove the mechanism first**, ideally by A/B-ing an existing flag before writing any code.
5. **Make the smallest change that tests it.** Identity/policy into `core/` with tests, upload in
   `src/`.
6. **Measure again**, same protocol, same session.
7. **Prove the pixels are identical.** Section 9 — this is load-bearing here.
8. **Ship it** as its own PR, or drop it and write it up with numbers.

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

- **An identity test with teeth.** If you share resources, test that two *nearly* identical things do
  **not** merge — different colour, different blend, one byte different in the texture — as well as
  that two identical ones do. A dedup bug is invisible in the byte counts and glaring on screen.
- **A lifetime test**: what is released, and that what remains is still correct after release.
- **A decode round-trip test** if you touch formats: decode, and compare against the reference bytes
  the existing tests already use.
- A `[RealDataFact]` test when the behaviour depends on real content — the extraction paths are exactly
  what `real-data.yml` exists for.

**`bench/structural/PEI.json` is committed and gated**, and `uniqueMeshes` / `uniqueMaterials` /
`estimatedGeometryBytes` are the counts this lane moves. Re-record with
`./scripts/check-structural-metrics.sh --write` **and explain every moved number in the PR body**.

## 9. Proving the frame did not change

A wrong deduplication and a lost mip look identical in the byte counts — they look like wins. The
screenshots are the only thing standing between that and a merge.

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

`--max-percent 0.0` is deliberate: your target is *zero* differing pixels. The amplified difference
image (`--diff`) shows a one-step delta invisible at 1:1 — for a texture or material change that is
exactly the signature of a resource that is not the one it used to be.

**Cold and warm both.** A material identity only exists once its texture has been extracted, so run the
capture once against a cold cache and once against a warm one:

```sh
rm -rf "${XDG_DATA_HOME:-$HOME/.local/share}"/godot/app_userdata/unturned-godot/{model_cache,texture_cache}
```

If you touch foliage residency, add the traversal correctness gate:

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

## 10. The pull request

**Branch.** `claude/perf-vram-<short-topic>`. Never push to `main`.

**Size.** One idea. If you find three things, open three PRs.

**Commits.** Present tense, describing the behaviour change: "Share one material table across both LOD
levels", not "reduce VRAM".

**Body template** — fill in every section; delete none:

```markdown
## What this changes

One paragraph: which resource existed more than once (or larger than it needed to be), why, and what
holds it now.

## The measurement

Container: <cpu count> vCPU, **software rendering (lavapipe)** — the byte counts below reproduce
bit-for-bit and are the claim, but lavapipe's "video memory" is system memory and this machine cannot
price sampling cost or bandwidth.
`foliage.settled`: <0 or 1> on both sides.

| Metric | Before | After | Delta |
|---|---:|---:|---:|
| `gpu.bufferMemBytes` (California2, `view_heavy`) | | | |
| `gpu.textureMemBytes` (California2) | | | |
| `gpu.videoMemBytes` (California2) | | | |
| `runtime.videoMemoryBytes` (PEI, Tier 3) | | | |
| `uniqueMaterials` (Tier 1, PEI, cold) | | | |
| `uniqueMaterials` (Tier 1, PEI, warm) | | | |

Runs: 3 per side per map. <Anything discarded, and why.>

## What did not move

| Metric | Before | After |
|---|---:|---:|
| `gpu.drawCalls.median.ground` | | |
| `runtime.rssBytes` (`UG_HEADLESS_INTERACTIVE=1`) | | |
| `interactive.loadMs` | | |
| `foliage.visibleSetMisses` | 0 | 0 |

## Visual proof — zero differing pixels

| View | Before | After |
|---|---|---|
| <pasted from publish-screenshots.sh> | | |

`overview` 0/1 440 000, `spawn` 0/1 440 000, `night` 0/1 440 000, `heavy` (California2) 0/1 440 000.
Captured against a cold cache and a warm one; both identical.

## Correctness

- Tests added: <names — including the near-miss identity test that proves two nearly-identical
  resources do NOT merge>
- `dotnet test`: green. `./scripts/check-coverage.sh`: <line%>/<branch%>.
- `./scripts/check-structural-metrics.sh`: <all N match / which moved and why>.

## Drawbacks

State them plainly, or write "None found:" and then say what you looked for. Specifically: is anything
decoded, sampled or re-read more often now, and what could this machine not measure?

## What I tried that did not work

Ideas measured and dropped, with the numbers that killed them.
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
- **Never weaken a gate to pass it.**
- **Never share two resources on a key that is not a complete identity.** If you cannot state the
  identity in one sentence, it is not one.
- **Never report lavapipe's video memory as a real device's VRAM.** Report it as what it is: what the
  renderer allocated, counted exactly.
- **Never accept differing pixels as "close enough".**
- **Never report a number you did not measure on this machine, in this session.**
