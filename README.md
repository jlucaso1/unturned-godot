# unturned-godot

![CI](../../actions/workflows/ci.yml/badge.svg)

An experiment: load a real [Unturned](https://store.steampowered.com/app/304930/Unturned/) map (terrain,
objects, foliage, roads, lighting, audio, characters, zombies) straight out of your Steam install and run
it in [Godot 4.7](https://godotengine.org/) (.NET / C#). Every file format is re-implemented from scratch
and checked byte-for-byte against the game's own data, using
[U3-SDK](https://github.com/SmartlyDressedGames/U3-SDK) as the reference for how each one is serialized.

> **Unofficial, and not a game.** This is a hobby port/experiment, not affiliated with or endorsed by
> Smartly Dressed Games. It ships **no** game content: you need your own copy of Unturned installed
> through Steam, and everything you see is read from it at runtime. See [NOTICE.md](NOTICE.md).

## What runs today

| Area | State |
|---|---|
| **Terrain** | Landscape heightmaps + splatmaps, each tile's own eight layer textures resolved through `Level.hierarchy` and the master bundles, physics materials |
| **Objects** | All placed objects and trees with their real meshes, materials and textures, streamed in with per-GUID collision. Workshop maps also load the objects from their mod's own master bundle |
| **Foliage** | `Foliage.blob` grass, flowers and pebbles as chunked MultiMeshes (~667k instances on PEI, 7.2M on Germany) |
| **Roads / water** | Bezier splines lofted through the port of `Road.buildMesh`, real road textures; sea plane from the map's lighting |
| **Lighting** | Day/night cycle driven by the map's `Lighting.dat` keyframes: sun, ambient, fog, ported skybox (sun disc, stars, moon phases, clouds) |
| **Player** | Port of `PlayerMovement`/`PlayerLook`/`PlayerStance` with the game's own constants; real character model, skeleton and animations; first/third person. Left click throws a punch (`PlayerEquipment`), animated from either camera and replicated so everyone sees it — the swing is mixed onto the swinging arm alone (`mixAnimation`/`AddMixingTransform`), so the walk or sprint cycle runs on underneath it; ladders are climbable — walk into one or look at it and interact, both through the game's own climb rules |
| **Damage** | `PlayerEquipment`'s punch table, ported number for number: 15 base scaled per limb, 20 to a resource, 5 to a destructible object, 2 to a buildable, and the vehicle turned off. Zombies carry the map's own table health and die to it; trees and rubble carry theirs off the asset `.dat` (`Health`, `Vulnerable_To_Fists`, `Rubble_Health`, `Rubble_Blade_ID`) |
| **Audio** | Footsteps/landings resolved through the terrain splat like `PhysicsTool.GetTerrainMaterialName`, clips extracted from the master bundle's FSB5 banks |
| **Zombies** | Spawn tables, navigation bounds and the pre-baked navmeshes; detection, hunting and the `Zombie.cs` animation set |
| **Vehicles** | The map's own `Spawns/Vehicles.dat` rolled through its spawn tables and redirectors, as many as the level's size allows, each drawn from its real `Vehicle.prefab`. Parked scenery for now: no driving, physics or damage |
| **Multiplayer** | Authoritative server + snapshot-interpolated clients over UDP; singleplayer is the same stack over loopback. Listen server, dedicated server and join-by-address all work |

Goal order is **parity first, then performance**, and a performance HUD (`F3`) is on hand so the numbers stay
in view.

### Not done yet

- **Stream-data meshes**: vertex data kept in the `.resS` stream *is* decoded now, for the prefabs a map
  needs — it costs one extra forward pass over the bundle on a cold cache, and only when something still
  to be extracted has a streamed buffer. Quantized geometry (`m_CompressedMesh`), which workshop bundles
  lean on heavily, and texture pixels in `.resS` were already decoded.
- **Gameplay**: no items, inventory, building or survival stats. Punching works — zombies take the
  game's own per-limb damage, aggro onto whoever hit them, and die — but the fist is the only damage
  source there is, and nothing yet gives the player a health bar of their own. Vehicles spawn and
  render, but they are scenery: nothing drives, collides with or damages them, and a vehicle sits at the
  height its spawnpoint was authored at instead of settling onto the ground as its rigidbody would.
- **First-person arms**: the swing animates in first person, but what it animates is the third-person
  body drawn from inside its own head, not the game's purpose-built arms. The `Viewmodel` rig carries a
  single renderer whose skin weights live in the compressed vertex stream this port does not decode yet,
  so it imports as an unposable bind-pose mesh. The stand-in rig does ride its own skull the way the game
  parents its `ViewmodelCamera` under `firstSkeleton/Spine/Skull`, so the framing holds across stances and
  through a swing; what cannot be borrowed is that camera's authored *rotation*, which only means something
  on the rig it was authored against. `UG_VIEWMODEL_OFFSET="x,y,z"` nudges it meanwhile.
- **Breaking the world visually**: a punched tree or rubble pile loses health on the server and the
  destruction is reported (`PUNCH_LOG=1`), but the placement is still drawn. Objects are rendered as
  batched `MultiMesh` instances with no per-instance handle, so removing one — or swapping a felled tree
  for its stump prefab, which is what the game does — needs that batching to carry an index first. Worth
  knowing: none of the game's own trees is `Vulnerable_To_Fists`, so bare hands never fell one anyway;
  the rubble props are what a fist really breaks.
- **Ladders**: the ones a map places as objects are climbable. Player-built barricade ladders are not,
  because barricades do not exist here yet — their prefabs carry the same climbing volume on the same
  layer, so they come for free once they do. Neither does the climb/swim transition, for the same reason:
  there is no swimming stance to move between.
- **NPC clothing**: the NPC characters Russia places stand in the player rig's default look. Their
  `.dat` names a Shirt, Pants, Hat and Face, and those are item assets whose meshes are a family nothing
  reads yet, so every one of them is currently the same undressed character.
- **Projected decals**: the `Decal` objects a map places (graffiti, faction tags) draw their texture on a
  flat quad at the authored transform. Unturned projects them onto whatever is underneath, so one on
  uneven ground creases where this stays flat — every one the official maps place sits on a wall or a
  road, where the two agree. The decal a prefab carries as a child component (the blast marks Germany
  scatters) is a separate mechanism and is not read yet.

## Requirements

- **Godot 4.7, .NET/Mono build** ([download](https://godotengine.org/download)). The plain build cannot run C#.
- **.NET SDK 10**: for the test suite and the standalone tools.
- **Unturned, installed through Steam**: Linux, Windows or macOS. The project finds it automatically
  (including extra Steam library drives via `libraryfolders.vdf`); override with `UNTURNED_PATH` if it lives
  somewhere unusual.

Nothing else is needed: the master bundle, maps and assets are read directly out of that install.

To *develop* rather than play, the Steam install is optional: `./scripts/fetch-game-data.sh` pulls the
same content out of Unturned's anonymously-downloadable dedicated server, which is what CI and the agent
sandboxes use — including the character prefabs, which the server keeps in `Unturned_Headless_Data/`
where the retail client keeps them in `Unturned_Data/`. See
[Game content without a Steam install](#game-content-without-a-steam-install).

## Run

Open the project in the Godot editor and press play, or from a terminal:

```sh
# Linux / macOS: point GODOT at your Godot 4.7 .NET binary
GODOT=/usr/bin/godot-mono
"$GODOT"                                   # windowed, boots to the map browser
"$GODOT" --headless                        # load + validate the data, then exit
SCREENSHOT_PATH=/tmp/pei.png "$GODOT" --resolution 1600x900   # render one frame to a PNG

# Linux automation: isolate the game in a headless Gamescope compositor so it cannot steal focus
SCREENSHOT_PATH=/tmp/pei.png gamescope --backend headless -W 1600 -H 900 -w 1600 -h 900 -- \
  "$GODOT" --resolution 1600x900
```

```powershell
# Windows (PowerShell)
$env:UNTURNED_PATH = "D:\SteamLibrary\steamapps\common\Unturned"   # only if autodetection misses
& "C:\Godot\Godot_v4.7-stable_mono_win64.exe"
```

**Picking a map.** The menu lists every map installed on this machine: the ones that ship with the game,
anything under `Bundles/Workshop/Maps`, and Steam Workshop subscriptions, each with its own artwork, blurb
and size. Pick one and press Play; the choice is remembered for next time. Maps whose terrain predates
Landscape tiles (Destruction, Paintball Arena) are listed but cannot load, and say so.

First launch extracts models, textures and audio out of the master bundle into Godot's `user://` cache.
That takes a few minutes once; later runs start from the cache, and picking a map with assets that were
never extracted streams in just those.

**Controls** (Unturned's own defaults, from `PlayerSettings`): `WASD` move, mouse look, `Space` jump,
`Shift` sprint, `X` crouch, `Z` prone, `F` climb the ladder you are looking at (walking into one climbs it
too), `H` (or `F5`) toggle first/third person, `Esc` pause. In free-camera
mode: `WASD` + `Q`/`E` down/up, `Shift` to boost. `F3` toggles the performance HUD, `F1` (or `` ` ``) opens
the console, and `F7` writes a bug-repro dump — the last few seconds of the simulation, replayable headless
or back inside the game (see [docs/REPRO.md](docs/REPRO.md)).

**Multiplayer.** The main menu's *Connect* joins a `host:port`. You do not pick the map: the client asks
the server which one it is running and loads that, so both ends are always on the same world — and a
server refuses a client that is somehow on another map instead of admitting it onto the wrong one. If the
server's map is not installed here, it says so instead of joining. To host, start a session and hit *Open
to LAN* in the pause menu, or run a dedicated server:

```sh
"$GODOT" --headless -- --server --port=27015 --map=Washington
```

### The console

`F1` (or the backtick) drops a console over the top of the screen: the game's log, live, and a prompt.

It exists for measuring. The goal order is parity first, then performance, and a frame time only means
something next to a second one taken under **one** deliberate difference. Restarting with a different
environment variable gives you that across two loads, two shader caches and two thermal states; typing it
gives it to you between two frames of the same session, with the `F3` HUD in view the whole time:

```
> objects.trees.enabled 0        # what do the trees cost right now?
> foliage.enabled 0              # and the grass?
> sun.shadows.distance 32        # half the shadow range, same lighting
> reset all                      # back to how the game ships
```

Names read `subject[.part].property`, the last segment is always the property, and the same word means the
same thing everywhere (`enabled` for every switch). Nothing here rebuilds the world or touches collision,
navigation, audio or the server — a toggle changes what is *submitted to the renderer* and nothing else, so
the frame before and the frame after differ in exactly one thing. Settings survive a return to the menu and
are re-applied to the next map you load, which is what makes "the same difference, on two maps" a thing you
can actually do.

| Namespace | Variables |
|---|---|
| `terrain` | `terrain.enabled`, `terrain.splat.unpainted.enabled` (sample the splat layers a pixel gives no weight to — off by default, so this is the A/B control for the skip that makes the ground cheap; the image is the same either way) |
| `objects` | `objects.enabled`, `.small.enabled`, `.medium.enabled`, `.large.enabled`, `.trees.enabled` (Unturned's RESOURCE family: trees, rocks, bushes), `.shadows.enabled` |
| `foliage` | `foliage.enabled`, `foliage.range` (draw distance as a fraction of the built one; streaming is unchanged, so this isolates the cost of *drawing* it) |
| world | `roads.enabled`, `water.enabled`, `vehicles.enabled`, `npcs.enabled`, `zombies.enabled`, `players.enabled` |
| places | `locations.enabled` — the map's town and landmark names, floated over the world. Off by default (the game names a place when you reach it); one command brings them back |
| sun | `sun.enabled`, `sun.shadows.enabled`, `sun.shadows.distance`, `sun.shadows.cascades` (1/2/4 — each split is another pass over its slice of the casters), `sun.shadows.blend` (cross-fade the cascade seam; on costs a second shadow lookup in the band) |
| environment | `env.sky.enabled`, `env.fog.enabled`, `env.volumetric.enabled`, `env.ssao.enabled`, `env.ssil.enabled`, `env.glow.enabled` |
| renderer | `r.scale`, `r.msaa`, `r.taa.enabled`, `r.occlusion.enabled`, `r.lod.threshold`, `r.shadow.atlas` (positional), `r.shadow.directional` (the sun's shadow map edge — cleared and written every frame, so it is memory bandwidth first), `r.shadow.filter` (taps per shadowed pixel), `r.debug` (overdraw/wireframe), `r.vsync.enabled`, `r.fps.max` |
| commands | `help`, `list`, `find <text>`, `reset <name\|all>`, `perf`, `copy`, `clear`, `quit` |

`help` and `list` are the authority — the table above is a map, not a manual. `find shadow` answers "what
can I turn off about shadows", Tab completes names, and Up/Down walk what you already typed. A line may
carry several statements: `foliage.enabled 0; objects.trees.enabled 0; perf`.

`perf` answers two different questions, one per line. The first is the workload — draw calls, primitives,
render objects. The second is where the frame actually goes, and its leading number is the one to read
first: wall-clock frame minus the idle step minus physics, i.e. the part of the frame the engine did not
report as CPU work.

That remainder only means "waiting on the GPU" when the frame was allowed to run flat out, so the line
names it for what it is. Uncapped and with vsync off it reads `gpu wait`, and **0.00 means the CPU is the
bottleneck** — removing GPU work will not move the frame. Under vsync or `r.fps.max` it reads
`idle (vsync)` / `idle (fps cap)` instead, because there the remainder is mostly the limiter sleeping and
says nothing about the GPU. Worth knowing when following `r.fps.max`'s own advice to cap while measuring.
Godot has no GPU-time monitor either way; MangoHud or PIX is still the instrument for true GPU frame time.

One trap worth knowing, because it silently reports the wrong frame: the monitors describe the **last
completed** frame, so `terrain.enabled 0; perf` on a single line prices the frame *before* the change.
Put `perf` on its own line. `fps` is the one exception on the line — it is the last *second* averaged,
which is what an fps readout means, so it and `frame` lag each other while something is changing.

Recipes travel by clipboard, so both directions work. **Pasting** a block written a command per line —
the shape one is written in here, in a note or in an issue — lands in the prompt as those commands in
order, comments and all, and Enter runs them: the prompt holds one line, so the block is flattened onto
the `;` above rather than welded into a name that does not exist. Nothing runs until you press it, which
is what lets you read back what you are about to do. **Ctrl+C** copies whatever you have selected in the
scrollback, and `copy` puts the whole of it on the clipboard as plain text — the transcript a bug report
wants, and what dragging a selection across hundreds of scrolling lines cannot practically produce.

`UG_CONSOLE="foliage.enabled 0; sun.shadows.enabled 0"` runs a line at startup, so a benchmark tier, a
screenshot or a bug report can be given the same configuration a person would have typed; `SHOW_CONSOLE=1`
opens the pane for a capture run. A headless session has no console — there is no key to press and nothing
being drawn to switch off.

**Useful environment flags** (mostly for automation and screenshots): `UNTURNED_PATH`, `MAP=Washington`
(skip the browser and load that map), `SOLO=1` (boot straight into a local session), `FREECAM=1`,
`JOIN=host:port`, `OPEN_LAN=1`, `PLAYER=1`, `SCREENSHOT_PATH`, `TIME_OF_DAY=0..1`, `DAY_SPEED=N`,
`NAV_DEBUG=1`, `NAV_PREVIEW=1` (`NAV_XRAY`, `NAV_LIFT`, `NAV_RIM`, `NAV_BEACONS`, `NAV_BOUNDS`),
`AUDIO_DEBUG=1`, `UG_CONSOLE="<console line>"`, `SHOW_CONSOLE=1`, `REPRO_*` ([docs/REPRO.md](docs/REPRO.md)).

### Export

`export_presets.cfg` carries Linux, Windows and macOS presets writing to `build/export/<platform>/`
(git-ignored). Install the matching export templates in the Godot editor first:

```sh
"$GODOT" --headless --export-release Linux
"$GODOT" --headless --export-release "Windows Desktop"
"$GODOT" --headless --export-release macOS
```

The game project publishes with NativeAOT, and NativeAOT compiles through the host toolchain, so the
Windows export only builds on Windows. Exporting it from Linux fails in ILCompiler with "Cross-OS native
compilation is not supported". Set `AllowNonAotWindowsExport=1` to export a plain IL build instead:

```sh
AllowNonAotWindowsExport=1 "$GODOT" --headless --export-release "Windows Desktop"
```

That build runs on the .NET runtime shipped with the export template and drops every AOT tuning in
`unturned-godot.csproj`, so it is for local checks only; the shipping Windows build has to come from Windows.

Export secrets, if you ever add any, land in `export_credentials.cfg`, which is git-ignored, so never commit it.

There is no Web preset, because Godot 4.7 refuses to export a C#/.NET project to the web at all
(`Exporting to Web is currently not supported in Godot 4 when using C#/.NET`). The half that does not
depend on the engine is built and tested though: `web/` reads a real Unturned install straight off the
player's disk through the browser's directory picker, so a web build would ship no game content either.
[docs/WEB-EXPORT.md](docs/WEB-EXPORT.md) has the repro, what would have to change here, and how to run it.

## Structure

| Project | What it holds | Engine dependency |
|---|---|---|
| `core/` (`UnturnedGodot.Core`) | Pure logic: binary/text parsers, terrain math, netcode, zombie AI, asset/extraction planning. Only uses managed Godot structs. | none, runs under xUnit |
| `src/` (`unturned-godot`) | Godot glue: `Main`, world builders, UI, player/zombie nodes. `[ExcludeFromCodeCoverage]`. | Godot.NET.Sdk |
| `tests/` (`UnturnedGodot.Tests`) | xUnit suite; CI requires more than 95% line and branch coverage of `core/`. | none |
| `addons/unturned/` | Editor add-on: the "Unturned" dock (map preview, cache warming, navigation overlay, camera readout). Debug/editor builds only. | GodotSharpEditor |
| `tools/PerfHarness` | Standalone micro-benchmarks over the Core parsers. | none |
| `tools/ReproHarness` | Replays a bug-repro dump: `info`, `verify`, `replay`. | none |
| `web/` | Browser file layer: directory picker, read-only VFS over the picked folder, install/map probe, demo page. Vanilla ES modules, no build step. | none, runs in Chromium |

Keeping the parsers engine-free is what makes full unit-test coverage possible. `core/`, `tests/` and
`tools/` carry a `.gdignore` so the Godot editor leaves them alone (they build via the .NET SDK).

Non-Godot binaries (core + tests, Debug and Release) go to `build/<project>/<config>/` instead of scattered
`bin`/`obj`; the game keeps its Godot-managed output under `.godot/`. All of `build/` is git-ignored.

### Editor add-on

Enable **Unturned** under Project > Project Settings > Plugins to get a dock that builds a map into the
edited scene, warms its mesh cache in the background, tunes the viewport for the map's scale, and lifts the
editor camera's pose back out as a `SHOT_CAM` the headless screenshot path reproduces. Everything it adds is
unowned, so saving the scene never writes it into the `.tscn`.

**Show navmesh** draws the map's baked navigation on top of all that, for finding the defects that are
invisible in game — a zombie takes the long way round, or stands somewhere forever, and nothing on screen
says why. Each defect has a shape you can recognise once it is drawn:

| What you see | What it is |
|---|---|
| A red rim line in the middle of open floor | A hole: the walkable surface stops there |
| A patch in its own colour with a beacon over it | An island nothing can walk to from the rest of the map |
| Magenta box reaching well past the coloured surface | Spawn ground (Bounds.dat, expanded 64 m) with no navmesh under it |

With X-ray off, realised terrain, sidewalks and object floors can hide a baked face that sits slightly
below them; a missing patch in that view alone is therefore not a navmesh hole. Toggle X-ray and look at
the red rim: a real topological hole remains empty and has a rim, while an occluded face becomes visible.
X-ray also reveals legitimate separate floors underground or inside buildings, so their different colours
are not by themselves defects. The default 0.55 m lift clears ordinary kerbs while leaving walls able to
occlude the overlay. When the overlay is loaded, **Copy screenshot cmd** includes all of its toggles and
lift so the runtime capture reproduces the diagnostic view as well as the camera pose.

It reads only the map's `Environment/` folder — no masterbundle, no warm cache, no map preview — so it is up
in well under a second, and the dock's log summarizes what it found. Islands, rims and everything else come
from [`BakedNavGraph.Survey`](core/Data/NavmeshSurvey.cs), which measures the **pathfinder's own
adjacency** rather than the raw triangles: on PEI those disagree about 3 449 edges, every one of which the
raw reading would have drawn as a hole that is not there.

### How the content is read

`core/Unity/` is a from-scratch reader for the game's `core_*.masterbundle`: UnityFS container, LZ4 (own
decoder) + LZMA (SharpCompress) blocks, SerializedFile v22, TypeTree-driven object reader, meshes (vertex
channels, UVs, submeshes, skinning), materials (`_Color`/`_MainTex`) and Texture2D (DXT1/DXT5/BC7/RGB/RGBA,
plus a Crunch decoder for the crunched variants workshop maps lean on).

The bundle is one ~1.4 GB LZMA block, so it is walked **once** (`ModelExtractor`): each placed object's GUID
maps to its highest-detail LOD mesh and, through the object's `MaterialPalette`, to each submesh's flat
`_Color` and, where present, `_MainTex` texture from the `.resS` stream. Meshes, colliders and deduplicated
textures are cached under `user://`; later runs load only what the map needs. `Bundle_Override_Path`
(holiday/variant reuse) and external mesh references are handled.

## Develop

```sh
dotnet build unturned-godot.sln                                    # game + core + tests
dotnet test tests/UnturnedGodot.Tests.csproj                       # run the suite
dotnet format unturned-godot.sln --verify-no-changes               # lint (fails on style drift)
dotnet format unturned-godot.sln                                   # auto-format

# Coverage gate (excludes generated code; requires >95% for both lines and branches)
./scripts/check-coverage.sh

# The tests that need a live engine (Node lifecycle, notification order) — headless, no GPU. The suite
# above references core/ alone and so cannot instantiate a Node; this is the half that can.
./scripts/run-godot-tests.sh

# Run the format/lint/test gates locally, before the commit exists. Opt-in per checkout: git does not
# run a fresh clone's hooks on its own, which is a security property worth keeping.
#   pre-commit  format (staged files only) + build with warnings as errors + the xUnit suite  (~35 s)
#   pre-push    the Godot runtime suite, skipped when the engine is not installed             (~3 s)
# Escapes: SKIP_TESTS=1 git commit, SKIP_RUNTIME_TESTS=1 git push, or --no-verify for either.
./scripts/install-git-hooks.sh

# Boot-menu popup gate: exports a release build and drives the menu, because engine Popups only
# misbehave there. Self-skips without Godot export templates, Xvfb or xdotool.
./scripts/check-menu-popup-errors.sh

# Browser file layer: runs web/ against real game content in Chromium. Self-skips without either.
node web/test/run.mjs

# The browser's .dat port against core/Dat/DatParser.cs, over generated documents.
node web/test/differential.mjs

# The browser's casing tables against the BCL, over every code point.
node web/test/casing.mjs
```

Style and analyzers are enforced via `.editorconfig` + `Directory.Build.props` (`EnableNETAnalyzers`,
`EnforceCodeStyleInBuild`); builds are warning-clean and CI builds with `-warnaserror`.

The tests that touch real game data self-skip when Unturned is not installed, so the suite is green on a
bare machine, which is what `ci.yml` runs, on Linux, Windows and macOS.

### Game content without a Steam install

Unturned's dedicated server is downloadable through Steam's anonymous account, and it carries the same
content the client reads. That is enough for the whole suite, and it needs no Steam login, no owned copy
and no Steam client:

```sh
./scripts/fetch-game-data.sh                       # bundles + PEI + prefabs (~200 MB) into build/game-data
export UNTURNED_PATH="$(./scripts/fetch-game-data.sh --print-dir)"
dotnet test tests/UnturnedGodot.Tests.csproj       # now with the data-backed tests running for real
```

`real-data.yml` runs exactly that in CI, caching the content on the depot manifest IDs. For a whole
machine at once — .NET SDK, NuGet cache and content — `./scripts/setup-cloud-env.sh` does the lot; see
[docs/CLOUD-ENVIRONMENTS.md](docs/CLOUD-ENVIRONMENTS.md) for wiring it into Claude Code's cloud
environments or Codex.

If your Godot came from a distro package and its exact `GodotSharp`/`Godot.NET.Sdk` version is not on
nuget.org yet, register the local nupkg folder per machine (never in the repo's `nuget.config`, where a missing
local source fails the whole restore):

```sh
dotnet nuget add source /usr/lib/godot-mono/GodotSharp/Tools/nupkgs -n GodotLocal
```

Benchmarking and profiling: see [docs/PROFILING.md](docs/PROFILING.md).

Saw something go wrong in play? Press `F7` and hand over the file:
[docs/REPRO.md](docs/REPRO.md) explains what a dump carries and how to replay it.

```sh
dotnet run -c Release --project tools/ReproHarness -- verify dump.json    # does this build still do it?
```

## License

Code is [MIT](LICENSE). Unturned and its content belong to Smartly Dressed Games. See
[NOTICE.md](NOTICE.md) for attribution and what this project does and does not include.
