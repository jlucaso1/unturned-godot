# Reproducing a bug you saw once

You are playing, something goes wrong — a zombie spinning at a lamp post, a body stuck in a doorway,
an animation stuck in a loop — and by the time you have written it down it is over. The coordinates
you copy into the issue are not a reproduction: nobody else can get the world into that state, and
neither can you.

Press **F7**. The last few seconds of the session are already in memory; that writes them to a file
that replays.

```
[repro] manual -> /home/you/.local/share/godot/app_userdata/unturned-godot/repro/20260803-220501-0.json
[repro] repro dump v1 — PEI @ 2026-08-03T22:05:01Z
[repro]   note        zombies closing on the player over real PEI geometry
[repro]   window      43 ticks from 32 (3.44 s)
[repro]   focus       (-635.18, 33.27, -91.67)
[repro]   camera      SHOT_CAM=-635.18,35.02,-91.67,-0,-42
[repro]   players     1
[repro]   world       19 nav flags, 542 navmesh triangles, 383 collision triangles within 16.0 m
[repro]   zombies     373 (2 awake, 2 hunting)
[repro]   oracle      43 move, 43 ground, 0 vision, 7 path
```

Hand that file to whoever is fixing it — a colleague, an agent, your future self — and they can run
the same five seconds as many times as they like.

## Replaying one

```sh
dotnet run -c Release --project tools/ReproHarness -- info   dump.json   # what is in it
dotnet run -c Release --project tools/ReproHarness -- verify dump.json   # does this build still do it?
dotnet run -c Release --project tools/ReproHarness -- replay dump.json --ticks 200
dotnet run -c Release --project tools/ReproHarness -- pretty dump.json   # indented, for reading
```

`verify` is the first thing to run. It replays the recorded window against the code in your working
tree and reports how far it drifted:

```
world: 383 collision triangles, the dump's navmesh slice
replayed 44 ticks; 88 motion samples compared, 16324 idle zombies checked for staying idle
  position error   max 0 m, mean 0 m
  yaw error        max 0.002°
  state mismatches 0, target mismatches 0, woke up unexpectedly 0
  answers          95 recorded, 0 from geometry, 0 unanswered
  verdict          reproduces the recording
  zombie 191  travelled 15.45 m, net 7.137 m, turned 457.405°, Chase
  zombie 219  travelled 0 m, net 0 m, turned 0.035°, Attack
```

Both halves of that first line matter. The motion samples are the zombies that did something; the
idle checks are the ones the recording says slept through the window, and a build that newly wakes
one of them is caught by that count rather than by a position nobody recorded.

(That is a real dump taken out of a headless PEI session and replayed with no engine in sight.)

Then change the code and run it again. The verdict flips to "diverges" — which is the point, since you
changed the simulation — and the per-zombie line at the bottom is the measurement that says whether
you fixed anything. A zombie that turned 2314° and moved 0.11 m is the bug the harness was built for,
and the report marks it `<- spinning in place`; one that covers 14 m is chasing someone.

`--ticks N` keeps simulating past the recorded window (the players hold their last position), which is
where a fix is judged: the window shows the bug, the tail shows whether it ends.

`--level <map folder>` gives the replay the whole map's navmesh instead of the slice in the dump. Use
it when the route matters beyond the incident. `./scripts/fetch-game-data.sh` downloads a map.

### In a test

The harness is ordinary code, so a dump can become a regression test:

```csharp
ReproDump dump = ReproDump.Read(pathToYourDump);
ReproReplayReport report = new ReproScenario(dump).Run(extraTicks: 200);
ReproMotionSummary hunter = report.Motion[0];
Assert.False(hunter.IsSpinningInPlace);
Assert.True(hunter.NetDisplacementMetres > 5f);
```

A dump carries a slice of the map's own navmesh and collision, which is game content, so real dumps
belong in an issue rather than in this repository (see [NOTICE.md](../NOTICE.md)). The suite's own
end-to-end coverage builds its world from scratch instead: `tests/Repro/ReproRoundTripTests.cs`
records a session, captures it, replays it and asserts the two agree.

### In the game

```sh
REPRO_LOAD=/path/to/dump.json "$GODOT"
```

loads the map, puts the zombies back into their recorded state and stands you where the reporter was.
From there the session simply carries on, in the real renderer, and you watch it happen. Paste the
dump's `SHOT_CAM` value to frame the same view.

## What is in a dump, and why

| Section | Contents |
|---|---|
| `meta` | when, which map, the reason (key press, timer, auto-trigger), the reporter's note, the camera pose, and anything the capture had to warn about |
| `session` | every player's full authoritative movement state, the server tick, the time of day |
| `world` | the level's nav bounds and zombie tables, a **slice** of the pre-baked navmesh around the incident, a patch of the terrain heightfield, and the **collision triangles** within a radius (with the name of the object each one belongs to) |
| `zombies` | the complete brain state — positions, routes, timers, aggro counts, the RNG — plus every answer the world gave it during the window and the motion that followed |
| `log` | the tail of the console |
| `sections` | whatever a subsystem added that this list has never heard of |

The state is from the **first** tick of the window, not the last. A dump you cannot watch develop is a
screenshot, and screenshots are what bug reports already are.

### Why a replay actually reproduces

Two independent sources answer the simulation's questions about the world:

- **The recording.** Every collision resolve, ground probe, vision ray and path query the session made
  is stored with its answer, in call order. Replaying unmodified code therefore reproduces the window
  bit for bit — there is no physics engine in the loop to disagree.
- **The geometry.** The collision triangles in the dump are re-swept by a physics-free port of the
  host's own resolver (same capsule, same collide-and-slide, same step-up, same layer masks). This is
  what answers the questions a **changed** brain asks, which is what anyone fixing the bug will be
  issuing.

Every replay reports which source served each query (`95 recorded, 0 from geometry, 0 unanswered`),
so "it stopped reproducing" can never be a silent consequence of the harness inventing a world. A
`verify` run with a non-zero *unanswered* count is not evidence of anything, and says so.

How well each does, measured against a real capture (two zombies closing on a player over PEI's own
terrain and buildings, 44 ticks, taken from a headless session and replayed with `tools/ReproHarness`):

| Source | Max position error | Mean |
|---|---|---|
| the recording | 0 m (yaw 0.004°) | 0 m |
| the geometry alone (`--no-oracle`) | 10.4 m | 1.5 m |

The recording is exact because there is nothing left to disagree with.

The geometry number needs its caveat stated, because the two halves of it behave very differently.
Collision, ground and vision are a port of the host's own resolver and track the engine to
centimetres. **Routing does not**: a map under Godot's polygon budget paths through the engine's
NavigationServer, while a replay with no engine paths over the baked graph, and those are two
different pathfinders. They agree most of the time and then disagree about which side of a building
to pass — after which the trajectories have nothing left to say to each other, which is what a max of
ten metres and a yaw error of 177° means. (Measured across two builds of the baked graph on the same
dump: 0.88 m before a navigation change landed on main, 10.4 m after. The recording reproduced
exactly under both.)

The replay says so itself rather than leaving you to work it out:

```
  answers          0 recorded, 94 from geometry, 0 unanswered
  routes           6 recomputed over the baked graph rather than replayed; the live map may have
                   used the engine's pathfinder, so a divergence here is expected
```

So: `verify` — the recorded answers — is the measurement to trust for "does this build still do it".
`--no-oracle` answers a different and narrower question, "does the body still hit the same walls",
and is the mode that keeps working once your changes make the brain ask new questions.

The randomness is part of the state, not a wildcard: the simulation draws from a generator whose whole
state is one 64-bit integer (`ReproRandom`), and the dump carries it, so a replay continues the exact
sequence the session was on. The seed a session starts from is logged and can be pinned with
`ZOMBIE_SEED=<n>`, which spawns the same population in the same places — the other half of making a
bug someone else hit reachable on your machine.

## Capturing

| Variable | Default | What it does |
|---|---|---|
| `REPRO` | on | `REPRO=0` disables the recorder entirely |
| `REPRO_KEY` | `F7` | capture key |
| `REPRO_DIR` | `user://repro` | where dumps are written |
| `REPRO_WINDOW` | `64` | ticks of history kept (0.08 s each) |
| `REPRO_AUTO` | off | capture by itself when something starts behaving like a bug report |
| `REPRO_CAPTURE_AT` | — | capture once, N seconds in (for headless runs) |
| `REPRO_NOTE` | — | a line of description carried into the dump |
| `REPRO_GEOMETRY_RADIUS` | `16` | metres of collision geometry kept |
| `REPRO_NAV_RADIUS` | `48` | metres of navmesh kept |
| `REPRO_GROUND_RADIUS` | `32` | metres of terrain heightfield kept |
| `REPRO_LOAD` | — | load a dump into this session instead of capturing one |

`REPRO_AUTO=1` is for the bugs you cannot reach the key for. It watches for symptoms rather than
causes — turning without going anywhere, or failing to make progress on a route the brain keeps
re-issuing — and captures once per incident. A headless soak run with it on writes a dump every time
the simulation does something it should not:

```sh
SOLO=1 MAP=PEI REPRO_AUTO=1 REPRO_DIR=/tmp/repro UG_HEADLESS_INTERACTIVE=1 "$GODOT" --headless
```

(`UG_HEADLESS_INTERACTIVE=1` is what makes a headless run play the session rather than load the map
and quit.)

## What it costs

The recorder is on while a session hosts zombies, because a recorder that is not armed catches
nothing. It is built to be affordable there:

- attaching wraps the four world delegates — one extra delegate hop against a physics raycast;
- each tick appends one small struct per world query into lists it already owns, and copies the
  **awake** zombies' motion (a settled map has none);
- the full population state is snapshotted twice per window into two buffers that are reused forever,
  so carrying five seconds of history costs a population copy every ~2.5 s rather than one per tick.

Steady-state allocation after the ring warms up is zero. `dotnet run -c Release --project
tools/PerfHarness -- repro` measures the per-tick cost against the same tick without a recorder.

Measured on PEI with 373 zombies alive: **1.7% added to the zombie tick**, 38 bytes of steady
allocation per tick, and 10 ms to turn a window into a dump when someone presses the key.

A dump of that session is ~190 KB of JSON (a 3.4 s window, the whole population, 383 collision
triangles and 542 navmesh triangles around the incident); writing to a path ending in `.gz` gzips it.
Nothing is converted to dump records, or written anywhere, until someone actually captures.

## Adding a section

The dump is not a zombie-AI format. A subsystem contributes its own section and nothing else has to
change:

- for something with its own shape, hand `ReproCaptureRequest.Sections` a name and a `JsonElement`;
  it rides along untouched and `info` lists it;
- for something that wants to be replayed rather than just read, follow `core/Repro/ReproZombieSection.cs`:
  a state record, a per-tick frame, and — if it asks the world questions — a recorded oracle for them.

The one thing worth copying from the zombie section is the discipline: capture **everything** that
decides the next tick. A field that is captured but not restored is a field the replay makes up, and
that is exactly the difference between "cannot reproduce" and a fix.
