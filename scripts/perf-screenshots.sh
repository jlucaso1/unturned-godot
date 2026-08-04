#!/usr/bin/env bash
# Capture the deterministic screenshot set that a performance change has to prove it did not move.
#
# Every optimization in this repo is supposed to be invisible: the frame after it must be the frame
# before it. That claim is only worth something when both sides are captured the same way, so this
# fixes everything a screenshot depends on — map, resolution, time of day, camera framing — and leaves
# only the code under test free to differ.
#
# Usage:
#   ./scripts/perf-screenshots.sh before                  # capture into build/screenshots/before/
#   ./scripts/perf-screenshots.sh after                   # ... and after/, once the change is applied
#   ./scripts/perf-screenshots.sh before --views overview,spawn
#   ./scripts/perf-screenshots.sh before --cam street=120,42,-310,-8,35
#
# Then diff them:
#   ./scripts/compare-screenshots.py build/screenshots/{before,after}/overview.png \
#       --diff build/screenshots/diff-overview.png
#
# Views:
#   overview   the map-relative high three-quarter framing, midday — whole-world geometry
#   spawn      third person at the map's own spawn point, midday — gameplay height: foliage,
#              near shadows, LOD and the character, i.e. what the player actually sees
#   night      the overview framing at night — sun, moon, stars, fog, sky and ambient
#   <name>     any --cam name=px,py,pz,pitch,yaw, captured through SHOT_CAM
#
# Environment:
#   GODOT           the Godot 4.7 .NET binary (default: what install-godot.sh laid down)
#   UNTURNED_PATH   the game content (default: what fetch-game-data.sh laid down)
#   MAP             which map (default: PEI). Use a dense map too — see docs/PROFILING.md on why a
#                   conclusion drawn from one map's view can be wrong by an order of magnitude.
#   UG_SHOT_RES     capture resolution (default: 1600x900)
#   UG_SHOT_TIME    the midday time of day (default: 0.35)
#   UG_SHOT_NIGHT   the night time of day (default: 0.02)
#   UG_SHOT_SKIP_BUILD  1 only when the caller already built the exact checkout being captured
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

label="${1:-}"
if [[ -z "$label" || "$label" == -* ]]; then
    sed -n '2,30p' "${BASH_SOURCE[0]}"
    exit 2
fi
shift

views="overview,spawn,night"
declare -a custom_cams=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --views) views="$2"; shift 2 ;;
        --cam) custom_cams+=("$2"); shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

# Godot's command-line runner does not compile C# sources: it loads whatever assembly is already under
# .godot/mono. Without this, a "before" and an "after" capture can both render the same stale build and
# agree perfectly while proving nothing. Same reasoning as scripts/run-benchmark.sh.
if [[ "${UG_SHOT_SKIP_BUILD:-0}" != "1" ]]; then
    dotnet build "$repo_dir/unturned-godot.csproj" -c Debug --nologo --verbosity quiet
fi

export MAP="${MAP:-PEI}"
resolution="${UG_SHOT_RES:-1600x900}"
midday="${UG_SHOT_TIME:-0.35}"
night="${UG_SHOT_NIGHT:-0.02}"

godot="${GODOT:-$("$repo_dir/scripts/install-godot.sh" --print-path)}"
if [[ ! -x "$godot" ]]; then
    echo "No Godot at $godot. Run ./scripts/install-godot.sh, or set GODOT." >&2
    exit 1
fi

content="${UNTURNED_PATH:-$("$repo_dir/scripts/fetch-game-data.sh" --print-dir)}"
if [[ ! -d "$content/Bundles" ]]; then
    echo "No Unturned content at $content. Run ./scripts/fetch-game-data.sh, or set UNTURNED_PATH." >&2
    exit 1
fi
export UNTURNED_PATH="$content"

out_dir="$repo_dir/build/screenshots/$label"
mkdir -p "$out_dir"

# bench/views.json holds the hand-picked expensive poses per map. A name that is not in there has to
# fail loudly: capturing the default framing instead would produce a pair of images that compare
# perfectly while showing nothing of the view someone asked about.
resolve_view() {
    python3 - "$repo_dir/bench/views.json" "$MAP" "$1" << 'PY'
import json, sys
path, map_name, view = sys.argv[1], sys.argv[2], sys.argv[3]
try:
    views = json.load(open(path))
except OSError:
    sys.exit(f"No view registry at {path}.")
for_map = views.get(map_name)
if not isinstance(for_map, dict) or view not in for_map:
    known = ", ".join(sorted(k for k in for_map if not k.startswith("_"))) if isinstance(for_map, dict) else ""
    sys.exit(f"No view '{view}' for map '{map_name}' in {path}"
             + (f" (known: {known})" if known else " (no views registered for this map)")
             + ". Add one, or pass --cam name=px,py,pz,pitch,yaw.")
print(for_map[view])
PY
}

# A screenshot needs a swapchain, so this always runs windowed: the real display when there is one, and
# Xvfb (over Mesa's lavapipe, on a machine with no GPU) otherwise. The rasterizer is deterministic, so
# two captures on the same container are comparable even though their timings are not.
capture() {
    local name="$1"
    shift
    local target="$out_dir/$name.png"
    rm -f "$target"
    echo "[shot] $label/$name ..."
    if [[ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]]; then
        env "$@" SCREENSHOT_PATH="$target" \
            "$godot" --audio-driver Dummy --resolution "$resolution" --path "$repo_dir" > /dev/null
    else
        command -v xvfb-run > /dev/null || {
            echo "No display and no xvfb-run; run ./scripts/install-godot.sh first." >&2
            exit 1
        }
        env "$@" SCREENSHOT_PATH="$target" \
            xvfb-run -a -s "-screen 0 ${resolution}x24" \
            "$godot" --audio-driver Dummy --resolution "$resolution" --path "$repo_dir" > /dev/null
    fi
    [[ -s "$target" ]] || { echo "[shot] $name produced no image" >&2; exit 1; }
}

IFS=',' read -r -a wanted <<< "$views"
for view in "${wanted[@]}"; do
    case "$view" in
        overview) capture overview "TIME_OF_DAY=$midday" ;;
        # SETTLE is the number of frames the character is given to fall onto the terrain before the
        # grab. The default (40) is what the interactive path uses; a lower one photographs a player
        # still in the air, which differs between runs for reasons that are not the change under test.
        spawn)    capture spawn "TIME_OF_DAY=$midday" "PLAYER=1" "SETTLE=40" ;;
        night)    capture night "TIME_OF_DAY=$night" ;;
        "")       ;;
        *)
            # Anything else is a name in bench/views.json — the hand-picked expensive views, kept in
            # one place so a render measurement and its screenshot use the same five numbers.
            pose="$(resolve_view "$view")" || exit 2
            capture "$view" "TIME_OF_DAY=$midday" "SHOT_CAM=$pose"
            ;;
    esac
done

for entry in "${custom_cams[@]:-}"; do
    [[ -n "$entry" ]] || continue
    name="${entry%%=*}"
    pose="${entry#*=}"
    if [[ "$name" == "$entry" || -z "$pose" ]]; then
        echo "--cam wants name=px,py,pz,pitch,yaw, got '$entry'" >&2
        exit 2
    fi
    capture "$name" "TIME_OF_DAY=$midday" "SHOT_CAM=$pose"
done

echo
echo "Captured into $out_dir:"
ls -1 "$out_dir"
