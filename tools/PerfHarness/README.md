# PerfHarness

Isolated micro-benchmarks over the Core parsers, against the real game data. Medians of 15 runs after
warmup, in-process, no engine. Suites skip cleanly when their input isn't on the machine.

```sh
dotnet run -c Release --project tools/PerfHarness                # all suites
dotnet run -c Release --project tools/PerfHarness -- foliage lz4 # a subset
```

Suites: `lz4` (synthetic, no data needed), `foliage`, `heightmap`, `splat`, `objects`, `dat`,
`meshcache`, `previews`, `navcache`, `navprobe`, `repro`, `bundle`. The Unturned install resolves through
`UnturnedInstall` — `UNTURNED_PATH`, else the Steam libraries for this OS (Linux/Windows/macOS, extra
drives included); the map from `MAP` (default `PEI`).

`bundle` prices the masterbundle's object metadata: `SerializedFile.Read` over the real object table, then
`TypeTreeReader.Read` over it. It is the slowest suite in the default set (~22 s of a ~41 s run) because
its input has to be LZMA-decoded once before anything can be timed; `BUNDLE_PATH` points it at a bundle
outside the install. See [`bundle`](#bundle--what-the-object-metadata-costs) below for what it found.

`navprobe` measures the pass that navmesh reconciliation now runs on workers instead of on the physics
thread: it probes the map's real navmesh against a `CollisionField` built from the map's real terrain, and
reports both the throughput and how many faces still have to be confirmed against the physics server.
Object colliders are built by the game rather than by Core, so the field here is terrain-only — the probe
count is the real one, and the confirmation rate is a floor.

Three diagnostics print a shape rather than a time and only run when named: `nav` (why the baked graph
picked a direction, `NAV_POINT=x,y,z`), `ress` and `lzma` (both below).

## `ress` — where the texture pass's read extent comes from

The cold load's texture pass is one forward LZMA pass over the masterbundle's ~1.18 GB `.resS`, and it
stops at the **end of the last range anyone asked for**. So the cost is set by the furthest wanted
texture, not by how many are wanted. This suite prints where the wanted ranges sit, the read extent if
the last *k* of them were deferred, the widest gap near the tail, and the measured LZMA decode rate that
turns those megabytes into seconds. Only metadata is decoded — the ranges come from the SerializedFile.

```sh
dotnet run -c Release --project tools/PerfHarness -- ress
RESS_BUNDLE=/path/to/some_linux.masterbundle dotnet run -c Release --project tools/PerfHarness -- ress
```

The cache tag is resolved through `ContentSource`, not from the file name: the game keys the core
bundle's caches by its declared name (`core.masterbundle` → `core`), while the file on disk is
`core_linux.masterbundle`, and a workshop bundle also carries a per-item discriminator. For a bundle
outside the install, name it with `RESS_TAG` — the suite refuses to guess, because a wrong tag misses
every cache key and reports that nothing is wanted.

The answer is bracketed between two want sets. The **upper** bound is every streamed `Texture2D` the
bundle declares. The **lower** bound is what the selected map's placed objects depend on, read out of
`user://model_cache` through `TextureDependencyIndex` — the same index the runtime consults — so it is
scoped to one map, unlike the `texture_cache` directory, which every map writes into and which would
hand back the union of everything ever extracted. The reference is always a **cold** load — an empty
texture cache — so dependencies are taken unfiltered by what happens to be cached now; filtering them
would describe what a resumed pass still owes, a different question whose answer moves with incidental
cache state. It is a lower bound twice over: a real load also wants foliage, tree and terrain-layer
textures that `Level/Objects.dat` does not reach, and a prefab whose mesh this bundle has not extracted
yet contributes nothing. That is the useful
direction: if even the lower bound runs to the end of the node, no larger set ends earlier. When there is
no map-scoped set to be had — no mesh cache, no `Level/Objects.dat`, no placed mesh this bundle extracted
at its current revision — the suite prints which of those it hit and reports the upper bound alone. It
also stops calling that set an upper bound if any serialized file went unscanned, since the ranges those
name are missing from it.

Measured on the game's own `core_linux.masterbundle` (superset of 5,360 streamed textures over a
1,180 MiB `.resS`): they are spread evenly end to end — the last tenth of the node is the second
*densest* — and the widest gap anywhere in the final 295 MiB is 48 KB. Deferring the last 64 ranges saves
9.7 MiB (~0.06 s); reading 10% less needs 11% of the want set deferred. So the bundle's layout has no
sparse tail to trim. The per-folder table bounds each folder's subsets from above but does not settle
them — only a real want set does that, which is what the map-scoped lower bound is for.

## `bundle` — what the object metadata costs

The masterbundle's first node is a ~171 MiB SerializedFile holding ~103k objects. Everything the port
builds a scene from comes through it, and until this suite existed none of it was measured. It reports
the object table, then the TypeTree-driven object reader over three sets:

- **scanned classes** — classes a bundle-wide loop decodes *every* object of, on any load, whatever the
  map: `PrefabGraph`'s `ReadContainer` (AssetBundle), `BuildTransformMaps` (Transform), the
  MeshFilter/SkinnedMeshRenderer sweep and the collider sweep. Each filters on class id alone, so this row
  is load work **measured**.
- **targeted classes** — Mesh, Material, Texture2D, Shader, MeshRenderer, GameObject, AudioClip and the
  audio MonoBehaviours, which the port reaches only by path-id or GUID lookup from an asset that names
  them. `ModelExtractor` skips GUIDs the map does not need before it touches any of them, so a map that
  places a small subset of the bundle decodes a small subset of this row. An **upper bound**, not a
  measurement.
- **every object** — a bound too, and a looser one. A class in neither list is not a class the port never
  reads (`CharacterModel` walks the player rig's own AnimationClips by id); it means nothing decodes every
  object of it.

Measured on the game's own `core_linux.masterbundle` (4-vCPU container, medians after warmup, three
interleaved runs in one session — the range across those runs is the last column):

| | count | input | median | allocated | across runs |
|---|---:|---:|---:|---:|---:|
| `SerializedFile.Read` | 103,549 objects | 170.9 MiB | 9.7 ms | 9.9 MiB | 8.9–9.9 ms |
| `TypeTreeReader.Read`, scanned | 42,010 | 5.5 MiB | 230 ms | 125 MiB | 221–243 ms |
| `TypeTreeReader.Read`, targeted (bound) | 50,739 | 102.7 MiB | 284 ms | 384 MiB | 271–307 ms |
| `TypeTreeReader.Read`, every object (bound) | 103,549 | 167.9 MiB | 2,666 ms | 2,121 MiB | 2,599–2,758 ms |

The allocation figures are byte-identical run to run, as they should be for a deterministic decode; only
the clock moves.

Three things fall out. The object table itself is free — 9.7 ms to index 103k objects, so nothing is to
be gained by making it lazier. The decode every load pays unconditionally is **230 ms**, which against a
~15 s LZMA pass is 1.5%; even the loosest bound on it is 2%. And **the reader's cost is allocation, not
parsing**: the scanned set turns 5.5 MiB of object bytes into 125 MiB of managed objects — **22.9x** —
because a Transform or a BoxCollider is a handful of floats that becomes nested `Dictionary` objects with
a boxed leaf each. Over the whole file it is 168 MiB into 2,121 MiB. That is inherent in the output shape
— a boxed leaf per primitive, a `Dictionary<string, object>` per struct, a `List<object>` per array — not
in how the shape is computed. The per-class table says where the rest lands: AnimationClip alone is
1.4 GiB of the 2.1 GiB, and nothing scans it, which is why the whole-file row is a bound.

**A negative result, so nobody re-derives it.** `TypeTreeReader.ReadValue` switches on `node.Type`, a
string, for every value it reads, including every element of every array — and the tree it walks is
already cached by node-list identity, so the kind could be resolved once at build time instead. Doing
that (a `Kind` byte per node, resolved in `BuildTree`, switch on the byte) is **worth nothing**: over all
103,549 objects, 2,846 ms → 2,958 ms, and over the class subsets a load reads 622 ms → 617 ms — both inside a
spread of several hundred milliseconds, and allocation is unchanged at 2,121 MiB because the same objects
are still produced. Equivalence was checked first over all 103,549 objects by deep-comparing the two
object graphs including dictionary key order and float bit patterns: identical. The string switch is not
the bottleneck; the allocator is. Attack the output shape or nothing.

## `lzma` — decode rate by region

`ress` prices a deferral by sampling the decode rate once, at the head of the first stream node, and
applying it to the whole node. This walks the entire blob and reports the rate per window, clipping
windows to node boundaries so a node's figure is exact. `LZMA_WINDOW=<MiB>` sets the window (default 32).
It decodes the whole ~1.4 GB blob, so it costs more than every timed suite together and only runs when
named.

```sh
dotnet run -c Release --project tools/PerfHarness -- lzma
```

Measured on `core_linux.masterbundle` (the same container):

| node | size | time | rate |
|---|---:|---:|---:|
| SerializedFile (metadata) | 170.9 MiB | 4.60 s | 37.2 MiB/s |
| `.resS` (texture pixels) | 1,180.2 MiB | 7.73 s | 152.7 MiB/s |
| `.resource` (audio banks) | 20.8 MiB | 2.11 s | **9.9 MiB/s** |
| whole blob | 1,371.9 MiB | 14.4 s | 95.0 MiB/s |

The rate varies by **15x across the three nodes** and by 30x window to window, and it tracks how
compressible the content is rather than where in the stream the pass has reached: metadata is dense and
match-poor, DXT blocks compress well and decode fast, and FSB5 audio is already compressed, so LZMA has
nothing but literals to emit for it.

Two consequences for anyone reading `ress`. Its "every 100 MiB not read is ~0.74 s" is a rate sampled
inside `.resS`, so it is roughly right *for `.resS` deferrals* and wrong everywhere else — deferring
audio is worth ~10 s per 100 MiB, not 0.74 s. And the audio node is 1.5% of the blob's bytes but **15% of
its decode time**: folding audio extraction into the streamer's pass (see `docs/PROFILING.md`) reads
20.8 MiB further for ~2.1 s, not the ~0.15 s the sampled rate implies. That is still a large win against
a second full 1.4 GB pass, but it is the most expensive 20 MiB in the file and worth pricing correctly
before anything else is moved behind it.

To A/B a candidate optimization: copy the current implementation into a local variant, `Bench()` both,
and **gate on an output-equivalence check first**. A variant that skips work the real code does
(allocations, output structures) will "win" dishonestly — this harness caught exactly that twice, and
the `bundle` negative result above is what an honest zero looks like.
