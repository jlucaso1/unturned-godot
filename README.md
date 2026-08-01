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
| **Player** | Port of `PlayerMovement`/`PlayerLook`/`PlayerStance` with the game's own constants; real character model, skeleton and animations; first/third person |
| **Audio** | Footsteps/landings resolved through the terrain splat like `PhysicsTool.GetTerrainMaterialName`, clips extracted from the master bundle's FSB5 banks |
| **Zombies** | Spawn tables, navigation bounds and the pre-baked navmeshes; detection, hunting and the `Zombie.cs` animation set |
| **Multiplayer** | Authoritative server + snapshot-interpolated clients over UDP; singleplayer is the same stack over loopback. Listen server, dedicated server and join-by-address all work |

Goal order is **parity first, then performance**, and a performance HUD (`F3`) is on hand so the numbers stay
in view.

### Not done yet

- **Stream-data meshes**: vertex data kept in the `.resS` stream is not decoded, so those meshes fall back
  to boxes. Quantized geometry (`m_CompressedMesh`), which workshop bundles lean on heavily, *is* decoded,
  as are texture pixels in `.resS`.
- **Gameplay**: no items, inventory, vehicles, building, damage or survival stats. Zombies exist and hunt,
  but you cannot fight back.
- **Old road bundles**: maps built before Unity 2018 (Alpha Valley, Washington, Yukon, and many workshop
  maps) keep `Environment/Roads.unity3d` in a SerializedFile version this reader does not decode yet, so
  their roads fall back to the procedural asphalt/dirt material. Terrain layers are unaffected: those come
  from the master bundle.

## Requirements

- **Godot 4.7, .NET/Mono build** ([download](https://godotengine.org/download)). The plain build cannot run C#.
- **.NET SDK 10**: for the test suite and the standalone tools.
- **Unturned, installed through Steam**: Linux, Windows or macOS. The project finds it automatically
  (including extra Steam library drives via `libraryfolders.vdf`); override with `UNTURNED_PATH` if it lives
  somewhere unusual.

Nothing else is needed: the master bundle, maps and assets are read directly out of that install.

To *develop* rather than play, the Steam install is optional: `./scripts/fetch-game-data.sh` pulls the
same content out of Unturned's anonymously-downloadable dedicated server, which is what CI and the agent
sandboxes use. See [Game content without a Steam install](#game-content-without-a-steam-install).

## Run

Open the project in the Godot editor and press play, or from a terminal:

```sh
# Linux / macOS: point GODOT at your Godot 4.7 .NET binary
GODOT=/usr/bin/godot-mono
"$GODOT"                                   # windowed, boots to the map browser
"$GODOT" --headless                        # load + validate the data, then exit
SCREENSHOT_PATH=/tmp/pei.png "$GODOT" --resolution 1600x900   # render one frame to a PNG
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
`Shift` sprint, `X` crouch, `Z` prone, `H` (or `F5`) toggle first/third person, `Esc` pause. In free-camera
mode: `WASD` + `Q`/`E` down/up, `Shift` to boost. `F3` toggles the performance HUD.

**Multiplayer.** The main menu's *Connect* joins a `host:port`. To host, start a session and hit *Open to
LAN* in the pause menu, or run a dedicated server:

```sh
"$GODOT" --headless -- --server --port=27015 --map=Washington
```

**Useful environment flags** (mostly for automation and screenshots): `UNTURNED_PATH`, `MAP=Washington`
(skip the browser and load that map), `SOLO=1` (boot straight into a local session), `FREECAM=1`,
`JOIN=host:port`, `OPEN_LAN=1`, `PLAYER=1`, `SCREENSHOT_PATH`, `TIME_OF_DAY=0..1`, `DAY_SPEED=N`,
`NAV_DEBUG=1`, `AUDIO_DEBUG=1`.

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

## Structure

| Project | What it holds | Engine dependency |
|---|---|---|
| `core/` (`UnturnedGodot.Core`) | Pure logic: binary/text parsers, terrain math, netcode, zombie AI, asset/extraction planning. Only uses managed Godot structs. | none, runs under xUnit |
| `src/` (`unturned-godot`) | Godot glue: `Main`, world builders, UI, player/zombie nodes. `[ExcludeFromCodeCoverage]`. | Godot.NET.Sdk |
| `tests/` (`UnturnedGodot.Tests`) | xUnit suite; CI requires more than 95% line and branch coverage of `core/`. | none |
| `tools/PerfHarness` | Standalone micro-benchmarks over the Core parsers. | none |

Keeping the parsers engine-free is what makes full unit-test coverage possible. `core/`, `tests/` and
`tools/` carry a `.gdignore` so the Godot editor leaves them alone (they build via the .NET SDK).

Non-Godot binaries (core + tests, Debug and Release) go to `build/<project>/<config>/` instead of scattered
`bin`/`obj`; the game keeps its Godot-managed output under `.godot/`. All of `build/` is git-ignored.

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

# Boot-menu popup gate: exports a release build and drives the menu, because engine Popups only
# misbehave there. Self-skips without Godot export templates, Xvfb or xdotool.
./scripts/check-menu-popup-errors.sh
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
./scripts/fetch-game-data.sh                       # bundles + PEI (~165 MB) into build/game-data
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

## License

Code is [MIT](LICENSE). Unturned and its content belong to Smartly Dressed Games. See
[NOTICE.md](NOTICE.md) for attribution and what this project does and does not include.
