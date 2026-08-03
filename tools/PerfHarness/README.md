# PerfHarness

Isolated micro-benchmarks over the Core parsers, against the real game data. Medians of 15 runs after
warmup, in-process, no engine. Suites skip cleanly when their input isn't on the machine.

```sh
dotnet run -c Release --project tools/PerfHarness                # all suites
dotnet run -c Release --project tools/PerfHarness -- foliage lz4 # a subset
```

Suites: `lz4` (synthetic, no data needed), `foliage`, `heightmap`, `splat`, `objects`, `dat`,
`meshcache`, `previews`, `navcache`. The Unturned install resolves through `UnturnedInstall` —
`UNTURNED_PATH`, else the Steam libraries for this OS (Linux/Windows/macOS, extra drives included);
the map from `MAP` (default `PEI`).

Two diagnostics print a shape rather than a time and only run when named: `nav` (why the baked graph
picked a direction, `NAV_POINT=x,y,z`) and `ress` (below).

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
hand back the union of everything ever extracted. It is a lower bound because a real load also wants
foliage, tree and terrain-layer textures that `Level/Objects.dat` does not reach. That is the useful
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

To A/B a candidate optimization: copy the current implementation into a local variant, `Bench()` both,
and **gate on an output-equivalence check first**. A variant that skips work the real code does
(allocations, output structures) will "win" dishonestly — this harness caught exactly that twice.
