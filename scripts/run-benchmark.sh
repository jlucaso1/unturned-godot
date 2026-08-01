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
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

tier="${1:-}"
shift || true
case "$tier" in
    structural|gpu|runtime) ;;
    *) sed -n '2,18p' "${BASH_SOURCE[0]}"; exit 2 ;;
esac

godot="${GODOT:-$("$repo_dir/scripts/install-godot.sh" --print-path)}"
if [[ ! -x "$godot" ]]; then
    echo "No Godot at $godot. Run ./scripts/install-godot.sh, or set GODOT." >&2
    exit 1
fi

content="${UNTURNED_PATH:-$("$repo_dir/scripts/fetch-game-data.sh" --print-dir)}"
if ! "$repo_dir/scripts/fetch-game-data.sh" --verify --dir "$content" > /dev/null 2>&1; then
    echo "No game content at $content. Run ./scripts/fetch-game-data.sh, or set UNTURNED_PATH." >&2
    exit 1
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
        UG_RUNTIME_BENCH_SECS="${UG_RUNTIME_BENCH_SECS:-12}" SOLO=1 run_windowed "$@"
        ;;
esac
