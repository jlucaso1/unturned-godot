# Running this in a browser

Two questions, and they have very different answers:

1. **Can the game be exported to the web today?** No, and not because of anything in this repository.
   Godot 4 refuses to export a C#/.NET project to the web at all.
2. **Can a browser read the player's own Unturned install, so a web build would never have to ship the
   game's content?** Yes. That part is built, tested against real game data, and lives in [`web/`](../web).

This document records what was actually tried, what the numbers are, and what would have to change — so
that when the engine side unblocks, the work here is a port rather than a research project.

## 1. The engine blocker

Godot 4.7 (.NET) rejects a Web preset before it does anything else:

```console
$ ./scripts/install-godot.sh
$ "$GODOT" --headless --export-release Web build/export/web/index.html
ERROR: Cannot export project with preset "Web" due to configuration errors:
Exporting to Web is currently not supported in Godot 4 when using C#/.NET. Use Godot 3 to target Web
with C#/Mono instead.
If this project does not use C#, use a non-C# editor build to export the project.
```

That is the whole story: there is no export template to install, no flag to flip. The reason is
structural — the .NET WebAssembly runtime expects to be the wasm *main module* and so does Godot's own
engine binary, and only one of them can be.

Where the fix stands:

- [godotengine/godot#70796](https://github.com/godotengine/godot/issues/70796) is the tracking issue.
- [godotengine/godot#99508](https://github.com/godotengine/godot/pull/99508) is the upstream PR. It has
  been a draft carrying a `needs work` label since November 2024, with no milestone beyond "4.x".
- Raul Santos demonstrated a working prototype at GodotCon Boston 2025 by statically linking Mono into
  the engine's wasm module
  ([write-up](https://godotengine.org/article/live-from-godotcon-boston-web-dotnet-prototype/)). Nothing
  from it has landed: Godot 4.7's changelog has no Web/.NET entry.
- [ComplexRobot/godot-dotnet-web-export](https://github.com/ComplexRobot/godot-dotnet-web-export) is a
  community fork that does work, with real constraints: Windows host only, `net9.0` only, `wasm-tools`
  workload required, no GDExtension, invariant globalization forced, and some BCL APIs (crypto) stubbed
  out.

So the honest options are *wait for upstream*, or *build a custom engine*. Neither is a code change in
this repository, and the second buys a build you cannot reproduce on CI.

## 2. What this project would have to change, even with a working engine

The engine unblocking is necessary, not sufficient. Six things in this repo assume a desktop.

### 2.1 Every parser reads through `System.IO`

`core/` touches `File`/`Directory`/`FileStream` at 111 call sites across 39 files, always against a real
filesystem path, and `src/` adds another 103. A browser has no such thing. The fix is a seam — one interface (`Exists`,
`OpenRead`, `ReadAllBytes`, `EnumerateFiles`, `EnumerateDirectories`) with the desktop implementation
delegating straight to `System.IO`, and a browser implementation backed by the layer in `web/`. Densest
callers, and therefore where the seam earns its keep first:

| File | Sites |
|---|---|
| `core/Data/FoliageResidencyIndex.cs` | 14 |
| `core/Unity/ExtractionIndex.cs` | 7 |
| `core/Assets/ContentSource.cs` | 7 |
| `core/Unity/TextureCache.cs` | 6 |
| `core/Assets/ObjectAssetDatabase.cs` | 6 |
| `core/Data/MapCatalog.cs` | 5 |

This refactor is mechanical, behaviour-preserving and testable on the desktop today. It is also the only
item on this list that is worth doing before the engine moves, because it is the one the rest depends on.

### 2.2 The master bundle is read whole

`src/World/TerrainTextures.cs:28` does `File.ReadAllBytes(bundlePath)`. On the client that file is
~1.4 GB. WebAssembly is 32-bit: the entire address space is 4 GB, Godot web builds are practical well
below 2 GB, and .NET arrays cannot exceed 2 GB regardless. A single `ReadAllBytes` of the bundle is fatal
in a way no amount of tuning fixes.

`core/Unity/MasterBundleStream` already has the right shape — it walks the single LZMA block once and
lets the caller pull the SerializedFile prefix and then the `.resS` texture stream from the same pass —
but its entry point is `Open(byte[] bundle)`, so the compressed bytes still arrive as one array. Making
it `Open(Stream)` and feeding it a range-reading stream is the change. `HandleFs.readRange` in `web/`
exists for exactly this: a `File` from the picker is a `Blob`, and slicing one does not materialize the
file.

### 2.3 The extraction cache has nowhere obvious to live

First launch walks the bundle and writes meshes, colliders and deduplicated textures under `user://`;
later runs read only what the map needs. On the web `user://` is IndexedDB, whose quota is a fraction of
free disk and which the browser may evict under pressure. Two better homes, both available here:

- **OPFS** (`navigator.storage.getDirectory()`), the origin-private filesystem. Same eviction rules, but
  a real file API with synchronous access handles inside workers, which matters for §2.6.
- **The player's own disk.** `showDirectoryPicker({ mode: "readwrite" })` on a cache folder they choose
  sidesteps browser quota entirely. It costs one extra pick and is the only option with no eviction risk.

Either way, first launch on the web is a multi-minute extraction against a runtime that is slower than
NativeAOT by a wide margin. Persisting that cache is not a nicety; it is the difference between a demo
and something anyone would open twice.

### 2.4 Multiplayer cannot use UDP

`core/Net/UdpTransport.cs` is built on `UdpClient`. Browsers have no raw UDP — the options are WebSocket
(TCP, so head-of-line blocking on a snapshot stream) or WebRTC data channels (unreliable/unordered, which
is what this netcode actually wants).

This one is cheap, because the seam is already there: `IClientTransport` and `IServerTransport` in
`core/Net/Transport.cs` are five methods each, and `CompositeServerTransport` already mixes loopback and
UDP in one server. A `WebRtcClientTransport` alongside `UdpClientTransport` is an addition, not a
rewrite. A browser client cannot *host*, so listen servers and dedicated servers stay desktop-only.

### 2.5 Threads need the page to be cross-origin isolated

The project runs Jolt on its own thread (`physics/3d/run_on_separate_thread=true`) and uses
`Task.Run`/`Parallel` in 29 places across foliage, streaming, navigation and extraction. Godot's threaded
web export needs `SharedArrayBuffer`, which needs the page served with:

```text
Cross-Origin-Opener-Policy: same-origin
Cross-Origin-Embedder-Policy: require-corp
```

That rules out plain static hosts that will not set headers, and it rules out **itch.io-style iframe
embedding for a different reason**: the File System Access API refuses to open a picker in a cross-origin
iframe (`SecurityError: Cross origin sub frames aren't allowed to show a file picker`). A web build of
this project has to be its own top-level page on its own origin.

### 2.6 Synchronous parsers, asynchronous files

The deepest of the six, and the one the design in `web/` is shaped around.

Every reader in `core/` is synchronous. Every browser file API that can reach a folder the player picked
is asynchronous. The usual bridges do not apply here:

- `FileSystemSyncAccessHandle` is synchronous, but only for OPFS, and only inside a Worker.
- `Atomics.wait` can block a worker on an async read happening elsewhere, but throws on the main thread —
  and Godot's main loop is on the main thread.

So the answer is not to make reads synchronous; it is to make sure the bytes are already there when the
synchronous code asks. **Stage, then parse:**

1. Walk the picked folder once and build an index of `path → size`. Cheap: no file content is read.
2. Before each load phase, `await` the files that phase needs into memory (or into OPFS) — the map's
   `Level.*`, the Landscape tiles, the bundle ranges for the objects actually placed.
3. Run the existing synchronous parsers against them through the §2.1 seam, unchanged.

The project already loads in phases and already streams objects and foliage by region, so this maps onto
work that exists rather than inventing a new pipeline.

## 3. Getting the assets there without shipping them

This is the part that is built and working, and it is the part the licence cares about: this project
ships no game content (see [NOTICE.md](../NOTICE.md)), and a web build must not become the exception.

The browser API for it is the **File System Access API**: `showDirectoryPicker()` returns a
`FileSystemDirectoryHandle` scoped to exactly the folder the player chose. No upload, no zip, no copy —
the page gets a capability to read files that stay where Steam put them. The handle is
structured-cloneable, so IndexedDB can store it and the player picks once rather than once per reload;
the *permission* does not persist, so a restored session costs one click on a "Reconnect" button, which
is the browser being careful rather than an API gap.

What it does and does not cover:

| | Chromium (Chrome, Edge, Opera) | Firefox, Safari |
|---|---|---|
| `showDirectoryPicker()` | yes | no |
| `<input type="file" webkitdirectory>` fallback | yes | yes |
| Pick survives a reload | yes (handle in IndexedDB) | no — re-pick every time |
| Enumeration cost | lazy, per directory | whole tree up front (~100k entries for an install) |
| Writable cache next to the install | yes (`mode: "readwrite"`) | no |

Both are implemented (`web/lib/handle-fs.js`, `web/lib/listing-fs.js`) behind one interface, and the test
suite asserts they produce the same install probe from the same content.

**Pick the Unturned folder, or the Steam library above it.** The grant is exactly the chosen subtree and
nothing above it, so a player who picks `steamapps/common/Unturned` has no path to
`steamapps/workshop/content/304930` and therefore no Workshop maps. Picking the library folder includes
both. The probe detects which one it got and says so, instead of silently listing fewer maps.

## 4. What is in `web/` today

A dependency-free ES-module layer plus a demo page:

| Path | What it is |
|---|---|
| `web/lib/paths.js` | POSIX-shaped path handling; `..` cannot escape the picked root |
| `web/lib/read-only-fs.js` | What both backends share: everything derivable from `file()` and `listDir()`, so the two cannot drift |
| `web/lib/handle-fs.js` | Read-only VFS over a `FileSystemDirectoryHandle`: `stat`, `listDir`, `readFile`, `readRange`, `walk`, cached handle resolution |
| `web/lib/listing-fs.js` | The same interface over a `webkitdirectory` `FileList`, for browsers with no picker |
| `web/lib/handle-store.js` | Persists the picked handle in IndexedDB; re-requests permission on a gesture |
| `web/lib/platform.js` | Which OS the folder came off: picks the masterbundle variant to try first, and whether path lookups fold case |
| `web/lib/dat.js` | Top-level `.dat` reader — menu metadata only; `core/Dat/DatParser.cs` stays the real one |
| `web/lib/catalog.js` | The install probe: mirrors `UnturnedInstall`, `ContentSource`, `MapCatalog` and `LevelInfo` |
| `web/index.html`, `web/app.js` | The demo: pick a folder, see the install and its maps with their own artwork |
| `web/test/` | The suite below |

### Running it

The picker needs a secure context, so `file://` will not do:

```sh
npx http-server web -p 8080     # or: python3 -m http.server -d web 8080
# open http://localhost:8080 and pick your Unturned folder
```

### Testing it

```sh
node web/test/run.mjs            # uses UNTURNED_PATH, or build/game-data
node web/test/run.mjs --keep-open
```

The suite seeds Chromium's origin-private filesystem from real game content and runs the shipping modules
against it. That matters: `navigator.storage.getDirectory()` returns a real `FileSystemDirectoryHandle` —
the same type `showDirectoryPicker()` returns — so `HandleFs` is exercised on the production path, not a
mock. It then drives the demo page end to end with the picker stubbed to hand back that same handle,
because the one thing no automation can click is the native folder dialog.

134 assertions, covering path handling, the `.dat` subset, install detection (both the install folder and
the Steam-library layout), map discovery against PEI's real `Level.dat`/`English.dat`/`Config.json` and
its 16 Landscape tiles, range reads and their clamping, and parity between the two filesystem backends.
It self-skips when the content or Playwright is missing, like the C# suite's data-backed tests. Files
over 256 KB are seeded as empty placeholders — the probe only counts or checks for those, and moving
73 MB through a page for every run buys nothing.

Some layouts no download contains are built by hand instead, because they are where the probe can quietly
disagree with `core/`: two subscribed workshop items whose maps share a folder name (two maps, not one),
heightmap tiles named the way `LevelInfo.TileRegex` will not accept (a map that looks playable here and
loads nothing there), and a map folder that cannot be read (one missing entry, not a failed scan).

## 5. If you want to push this forward

In order, because each step is useful on its own and the later ones are wasted without the earlier ones:

1. **Land the `System.IO` seam in `core/`** (§2.1). Pure desktop work, fully testable now, and the
   prerequisite for everything else.
2. **Make `MasterBundleStream.Open` take a `Stream`** (§2.2). Also desktop work; also a real improvement
   there, since it drops a 1.4 GB allocation from the extraction path.
3. **Add a `WebRtcClientTransport`** (§2.4) behind the existing interface, whenever multiplayer in a
   browser becomes interesting.
4. **Track [#99508](https://github.com/godotengine/godot/pull/99508)**. When it merges, the project has
   to match the template's target framework, threading model and exception handling, drop `PublishAot`
   for the web configuration, and keep `InvariantGlobalization` on — which it already is.

Two expectations worth setting now. First launch on the web means extracting a ~1.4 GB bundle under a
runtime slower than NativeAOT, so the cache in §2.3 is load-bearing rather than an optimization. And
memory is the hard ceiling: PEI's ~667k foliage instances are plausible inside a 32-bit heap, Germany's
7.2M are not. A web build is a demo of one small map, not the desktop build in a tab.
