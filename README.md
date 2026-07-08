# unturned-godot

PoC that loads real [Unturned](https://store.steampowered.com/app/304930/Unturned/) map
content (terrain, placed objects) directly from a Steam install and renders it in Godot 4.7
(.NET / C#). The parsers are faithful ports of Unturned's own formats, verified byte-for-byte
against the game's source and content.

Goal order: **parity first, then performance.** A performance HUD is on by default so the
numbers are always in view.

## Structure

| Project | What it holds | Engine dependency |
|---|---|---|
| `core/` (`UnturnedGodot.Core`) | Pure logic: binary/text parsers, terrain math, asset resolution. Only uses managed Godot structs. | none — runs under xUnit |
| `src/` (`unturned-godot`) | Godot glue: `Main`, builders, free camera. `[ExcludeFromCodeCoverage]`. | Godot.NET.Sdk |
| `tests/` (`UnturnedGodot.Tests`) | xUnit suite; 100% line + branch coverage of `core/`. | none |

Keeping the parsers engine-free is what makes full unit-test coverage possible. `core/` and
`tests/` carry a `.gdignore` so the Godot editor leaves them alone (they build via the .NET SDK).

Non-Godot binaries (core + tests, Debug and Release) go to `build/<project>/<config>/` instead of
scattered `bin`/`obj`; the game keeps its Godot-managed output under `.godot/`. All of `build/` is
git-ignored.

### What is ported (all from `U3-SDK` source)

- **Landscape heightmaps** — `Tile_X_Y_Source.heightmap`, 257×257 big-endian uint16, world math.
- **Splatmaps** — `Tile_X_Y_Source.splatmap`, 256×256×8 layer weights.
- **`Level/Objects.dat`** — `River` stream reader, all `SAVEDATA_VERSION`s up to 12.
- **DatParser** — Unturned's `.dat` grammar (nested `{}`/`[]`, quotes, comments, case-insensitive keys).
- **Object assets** — GUID/id/type resolution from `Bundles/Objects/**`.
- **Unity asset bundle + mesh reader** (`core/Unity/`) — a from-scratch parser for the 2022.3
  `core_linux.masterbundle`: UnityFS container, LZ4 (own decoder) + LZMA (SharpCompress, the one
  external dep) blocks, SerializedFile v22, TypeTree-driven object reader, and Mesh geometry
  (vertex channels, submeshes). Real object models are extracted once and cached per GUID.

## Real object models

The masterbundle is a single 1.4 GB LZMA block, so it is parsed **once** (`ModelExtractor`): the
graph walk maps each placed object's GUID to its highest-detail `Model_0` LOD mesh, which is decoded
and cached as a compact `user://model_cache/<guid>.mesh`. Runtime loads only those small meshes.
`Bundle_Override_Path` (holiday/variant objects reusing a base mesh) and external/built-in mesh
references are handled, so **all 4329 PEI objects render with real geometry**. Meshes are untextured.

## Run

The Unturned install path defaults to a local Steam location; override with `UNTURNED_PATH`.

```sh
GODOT=/usr/lib/godot/Godot_v4.7-stable_mono_linux.x86_64

# Windowed
"$GODOT"

# Headless (loads + validates data, then exits)
"$GODOT" --headless

# Render one frame to a PNG
SCREENSHOT_PATH=/tmp/pei.png "$GODOT" --resolution 1600x900
```

Camera: `WASD` move, `Q`/`E` down/up, hold `Shift` to boost, mouse to look, `Esc` to release.
`F3` toggles the performance HUD (FPS, frame time, static memory, draw calls, primitives, nodes).

### Export

The Linux preset in `export_presets.cfg` writes to `build/export/linux/` (git-ignored). Export
secrets, if ever added, land in `export_credentials.cfg`, which is git-ignored — never commit it.

```sh
"$GODOT" --headless --export-release Linux
```

## Develop

```sh
dotnet test tests/UnturnedGodot.Tests.csproj            # run the suite
dotnet format --verify-no-changes                       # lint (fails on style drift)
dotnet format                                           # auto-format

# Coverage
dotnet test tests/UnturnedGodot.Tests.csproj --collect:"XPlat Code Coverage"
```

Style and analyzers are enforced via `.editorconfig` + `Directory.Build.props`
(`EnableNETAnalyzers`, `EnforceCodeStyleInBuild`). Builds are warning-clean.

## Not yet done

- **Textures/materials** — meshes render untextured; terrain colors are a stand-in palette blended
  from real splatmap weights, not the game textures.
- **Stream-data / compressed meshes** — not needed for PEI (none of its objects use them), so the
  `.resS` and `m_CompressedMesh` paths are not decoded yet; such meshes fall back to boxes.

Coverage note: `core/` is 100% line + branch covered by the hermetic (synthetic-input) tests alone;
the real-data tests under `tests/` are extra end-to-end validation and self-skip without the game.
