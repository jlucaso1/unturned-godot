#!/usr/bin/env bash
# Run one of the benchmark tiers from docs/PROFILING.md, on a machine that may have no GPU and no
# display. Resolves the Godot binary and the game content, and wraps the windowed tiers in Xvfb when
# there is nothing to draw into.
#
# Usage:
#   ./scripts/run-benchmark.sh structural         # Tier 1: build times, mesh/material counts, memory
#   ./scripts/run-benchmark.sh gpu                # Tier 2: frame time, draw calls, primitives, VRAM
#   ./scripts/run-benchmark.sh runtime            # Tier 3: streamed load + gameplay counters
#   ./scripts/run-benchmark.sh gpu --write-baseline   # extra args go through to the harness
#
# Environment:
#   GODOT                  the Godot 4.7 .NET binary (default: what install-godot.sh laid down)
#   UNTURNED_PATH          the game content (default: what fetch-game-data.sh laid down)
#   MAP                    which map (default: PEI)
#   UG_RUNTIME_BENCH_SECS  Tier 3 sampling window (default: 12)
#   UG_BENCH_SKIP_BUILD    1 only when the caller already built the exact checkout being measured
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

tier="${1:-}"
shift || true
case "$tier" in
    structural|gpu|runtime) ;;
    *) sed -n '2,18p' "${BASH_SOURCE[0]}"; exit 2 ;;
esac

# Godot's command-line runner does not compile C# sources. It will happily load whatever assembly the
# editor or a previous checkout left under .godot/mono, which makes a successful benchmark especially
# dangerous: its adapter and timing data look valid while it is measuring old code. Build before any
# content work so every report describes this checkout. Large scripted matrices may build once up front
# and set UG_BENCH_SKIP_BUILD=1 for the individual runs.
if [[ "${UG_BENCH_SKIP_BUILD:-0}" != "1" ]]; then
    if ! command -v dotnet > /dev/null; then
        echo "No dotnet SDK found; cannot ensure the benchmark assembly matches the source." >&2
        exit 1
    fi
    dotnet build "$repo_dir/unturned-godot.csproj" -c Debug --nologo --verbosity quiet
fi

# Pin the map before Godot starts. Main._Ready only reads MAP when it is set, and otherwise falls back
# to whatever the last interactive session left in user://menu.cfg — so leaving it unset would silently
# benchmark whichever map someone last picked in the menu, and write that map's report.
export MAP="${MAP:-PEI}"

# UG_BENCH_VIEWS=heavy[,other] measures the hand-picked poses in bench/views.json instead of hunting
# for their five numbers by hand. The tier itself only knows UG_BENCH_POSES, so this resolves names to
# that; an explicit UG_BENCH_POSES wins, since it is the more specific thing to have written.
if [[ -n "${UG_BENCH_VIEWS:-}" && -z "${UG_BENCH_POSES:-}" ]]; then
    UG_BENCH_POSES="$(python3 - "$repo_dir/bench/views.json" "$MAP" "$UG_BENCH_VIEWS" << 'PY'
import json, sys
path, map_name, wanted = sys.argv[1], sys.argv[2], sys.argv[3]
try:
    views = json.load(open(path))
except OSError:
    sys.exit(f"No view registry at {path}.")
for_map = views.get(map_name)
if not isinstance(for_map, dict):
    sys.exit(f"No views registered for map '{map_name}' in {path}.")
out = []
for name in [w.strip() for w in wanted.split(",") if w.strip()]:
    if name not in for_map:
        known = ", ".join(sorted(k for k in for_map if not k.startswith("_")))
        sys.exit(f"No view '{name}' for map '{map_name}' in {path} (known: {known}).")
    out.append(f"{name}={for_map[name]}")
print(";".join(out))
PY
    )" || exit 1
    export UG_BENCH_POSES
    echo "[benchmark] UG_BENCH_VIEWS=$UG_BENCH_VIEWS -> UG_BENCH_POSES=$UG_BENCH_POSES"
fi

godot="${GODOT:-$("$repo_dir/scripts/install-godot.sh" --print-path)}"
if [[ ! -x "$godot" ]]; then
    echo "No Godot at $godot. Run ./scripts/install-godot.sh, or set GODOT." >&2
    exit 1
fi

content="${UNTURNED_PATH:-$("$repo_dir/scripts/fetch-game-data.sh" --print-dir)}"
# LevelInfo.EnumerateTiles parses both tile coordinates with int.TryParse, so digits that overflow a
# 32-bit integer name no tile the loader will count. Bash arithmetic is 64-bit and wraps silently on a
# long enough run of digits, and a leading zero is read as octal, so bound the digit count first (after
# stripping the leading zeros int.TryParse itself accepts) and force base 10.
tile_coord_is_int32() {
    local digits="${1#-}"
    while [[ ${#digits} -gt 1 && "$digits" == 0* ]]; do
        digits="${digits#0}"
    done
    [[ ${#digits} -le 10 ]] || return 1

    local value=$((10#$digits))
    if [[ "$1" == -* ]]; then
        ((value <= 2147483648))
    else
        ((value <= 2147483647))
    fi
}

# Verify the map this run will actually load, not the fetcher's default: benchmarking Washington against
# a PEI-only tree would otherwise pass here and fail inside Godot, and a Washington-only tree would be
# rejected for missing a map nobody asked for.
if [[ "$MAP" == workshop:* ]]; then
    workshop_map="${MAP#workshop:}"
    if [[ ! -s "$workshop_map/Level.dat" || ! -d "$content/Bundles" ]]; then
        echo "No workshop map $MAP or Unturned content at $content." >&2
        exit 1
    fi
    # Level.dat alone is not enough to load one. MapCatalog.IsSupported is TileCount > 0, and a pre-2020
    # workshop map keeps its terrain in a single legacy heightmap this port does not read: it opens as a
    # zero-tile world, so Tier 1 would write a structurally meaningless report instead of refusing.
    #
    # Counting *.heightmap would not answer the same question the loader does. LevelInfo.EnumerateTiles
    # keeps only Tile_<x>_<y>_Source.heightmap whose coordinates int.TryParse accepts, so a stray,
    # malformed or out-of-range name makes a glob nonempty while TileCount stays zero — the exact
    # zero-tile world this check exists to reject. Match the loader's rule, both halves of it.
    shopt -s nullglob
    workshop_tiles=()
    for tile in "$workshop_map/Landscape/Heightmaps"/*.heightmap; do
        # An `&&` one-liner here would be a set -e trap: the whole list fails on every non-matching file.
        if [[ "${tile##*/}" =~ ^Tile_(-?[0-9]+)_(-?[0-9]+)_Source\.heightmap$ ]] \
            && tile_coord_is_int32 "${BASH_REMATCH[1]}" \
            && tile_coord_is_int32 "${BASH_REMATCH[2]}"; then
            workshop_tiles+=("$tile")
        fi
    done
    shopt -u nullglob
    if [[ ${#workshop_tiles[@]} -eq 0 ]]; then
        echo "Workshop map $workshop_map has no Landscape heightmap tiles; this port cannot load it." >&2
        exit 1
    fi
else
    if ! "$repo_dir/scripts/fetch-game-data.sh" --verify --maps "$MAP" --dir "$content" > /dev/null 2>&1; then
        echo "No $MAP content at $content. Run ./scripts/fetch-game-data.sh --maps $MAP, or set UNTURNED_PATH." >&2
        exit 1
    fi
fi
export UNTURNED_PATH="$content"

# Tier 1 renders nothing, so it takes the headless driver. The other two need a swapchain: use the real
# display when there is one, and a virtual X server otherwise. With no GPU, Mesa's lavapipe answers as a
# CPU Vulkan device on its own — no ICD override needed, since it is then the only one that loads.
run_windowed() {
    if [[ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]]; then
        "$godot" --audio-driver Dummy --path "$repo_dir" -- "$@"
    elif command -v xvfb-run > /dev/null; then
        xvfb-run -a -s "-screen 0 1152x648x24" \
            "$godot" --audio-driver Dummy --path "$repo_dir" -- "$@"
    else
        echo "No display and no xvfb-run; run ./scripts/install-godot.sh first." >&2
        exit 1
    fi
}

case "$tier" in
    structural)
        "$godot" --headless --path "$repo_dir" -- --benchmark "$@"
        ;;
    gpu)
        run_windowed --benchmark --gpu "$@"
        ;;
    runtime)
        # UG_HEADLESS_INTERACTIVE is the no-renderer control for attributing RSS, so it must not be
        # handed a swapchain: wrapping it in Xvfb would silently measure lavapipe again and report the
        # renderer's memory as the game's — the exact confusion the flag exists to remove.
        # Mirror Main's own precedence: a screenshot needs something drawn, so it keeps the swapchain.
        if [[ "${UG_HEADLESS_INTERACTIVE:-}" == "1" && -z "${SCREENSHOT_PATH:-}" ]]; then
            UG_RUNTIME_BENCH_SECS="${UG_RUNTIME_BENCH_SECS:-12}" SOLO=1 \
                "$godot" --headless --audio-driver Dummy --path "$repo_dir" -- "$@"
        else
            UG_RUNTIME_BENCH_SECS="${UG_RUNTIME_BENCH_SECS:-12}" SOLO=1 run_windowed "$@"
        fi
        ;;
esac
