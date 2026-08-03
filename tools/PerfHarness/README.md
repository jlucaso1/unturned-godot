# PerfHarness

Isolated micro-benchmarks over the Core parsers, against the real game data. Medians of 15 runs after
warmup, in-process, no engine. Suites skip cleanly when their input isn't on the machine.

```sh
dotnet run -c Release --project tools/PerfHarness                # all suites
dotnet run -c Release --project tools/PerfHarness -- foliage lz4 # a subset
```

Suites: `lz4` (synthetic, no data needed), `foliage`, `heightmap`, `splat`, `objects`, `dat`,
`meshcache`, `navcache`, `navprobe`. The Unturned install resolves through `UnturnedInstall` —
`UNTURNED_PATH`, else the Steam libraries for this OS (Linux/Windows/macOS, extra drives included); the
map from `MAP` (default `PEI`).

`navprobe` measures the pass that navmesh reconciliation now runs on workers instead of on the physics
thread: it probes the map's real navmesh against a `CollisionField` built from the map's real terrain, and
reports both the throughput and how many faces still have to be confirmed against the physics server.
Object colliders are built by the game rather than by Core, so the field here is terrain-only — the probe
count is the real one, and the confirmation rate is a floor.

To A/B a candidate optimization: copy the current implementation into a local variant, `Bench()` both,
and **gate on an output-equivalence check first**. A variant that skips work the real code does
(allocations, output structures) will "win" dishonestly — this harness caught exactly that twice.
