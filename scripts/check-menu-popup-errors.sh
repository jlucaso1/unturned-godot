#!/usr/bin/env bash
# Reproduction gate for the boot menu's "nonexistent connection" spam.
#
# Hovering a map row used to print this pair on stderr, once per hover, in the shipped build:
#
#   ERROR: Attempt to disconnect a nonexistent connection from 'root:<Window#...>'.
#          Signal: 'focus_entered', callable: ''.
#   ERROR: Attempt to disconnect a nonexistent connection from 'root:<Window#...>'.
#          Signal: 'tree_exited', callable: ''.
#
# Those two signals are connected in exactly one place in the engine: Popup::_initialize_visible_parents
# (scene/gui/popup.cpp), which every embedded Popup runs when it becomes visible and undoes when it
# hides. In a *release export* the undo never matches -- the Callable that callable_mp() builds at
# disconnect time does not compare equal to the one it built at connect time -- so the disconnect fails
# and the connections leak until the popup is freed. Godot's own tooltips are Popups, so any control
# with a tooltip triggers it. Upstream: godotengine/godot#87626, #89657; fix PR #95100 is still open.
#
# The bug does not exist in the editor/debug binary, so this has to drive the exported build with a real
# pointer -- which is why it lives here and not in `dotnet test`.
#
# Usage:
#   ./scripts/check-menu-popup-errors.sh            # export, drive the menu, fail on the error
#   ./scripts/check-menu-popup-errors.sh --keep     # keep the run log for inspection
#
# Environment: GODOT and UNTURNED_PATH as for run-benchmark.sh. Needs the Linux export templates
# (the editor prints where it wants them; ./scripts/install-godot.sh does not fetch them), plus
# Xvfb and xdotool. Anything missing makes this skip, not fail.
set -uo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
keep=0
[[ "${1:-}" == "--keep" ]] && keep=1

skip() {
    echo "[popup-check] skipped: $1" >&2
    exit 0
}

godot="${GODOT:-$("$repo_dir/scripts/install-godot.sh" --print-path)}"
[[ -x "$godot" ]] || skip "no Godot at $godot (run ./scripts/install-godot.sh, or set GODOT)"
command -v Xvfb > /dev/null || skip "no Xvfb"
command -v xdotool > /dev/null || skip "no xdotool"

content="${UNTURNED_PATH:-$("$repo_dir/scripts/fetch-game-data.sh" --print-dir)}"
[[ -d "$content/Maps" ]] || skip "no game content at $content (see ./scripts/fetch-game-data.sh)"

templates="${XDG_DATA_HOME:-$HOME/.local/share}/godot/export_templates"
ls "$templates"/*/linux_release.x86_64 > /dev/null 2>&1 \
    || skip "no Linux export templates under $templates (install them from the editor, Editor > Manage Export Templates)"

export_dir="$repo_dir/build/export/popup-check"
binary="$export_dir/unturned-godot.x86_64"
log="$(mktemp -t popup-check-XXXXXX.log)"
display=":$((90 + RANDOM % 8))"
xvfb_pid=""
game_pid=""

cleanup() {
    [[ -n "$game_pid" ]] && kill "$game_pid" 2> /dev/null
    [[ -n "$xvfb_pid" ]] && kill "$xvfb_pid" 2> /dev/null
    if [[ "$keep" == "1" ]]; then
        echo "[popup-check] run log: $log" >&2
    else
        rm -f "$log"
    fi
}
trap cleanup EXIT

fail() {
    echo "$1" >&2
    keep=1
    exit 1
}

mkdir -p "$export_dir"
echo "[popup-check] exporting a release build (the debug binary does not show the bug)..."
# Past the prerequisite checks a broken export is a broken shipped build, not a reason to skip.
if ! "$godot" --headless --path "$repo_dir" --export-release "Linux" "$binary" > "$log" 2>&1; then
    tail -20 "$log" >&2
    fail "The release export failed, so the menu could not be checked (see above)."
fi
[[ -x "$binary" ]] || fail "The release export left no binary at $binary."

Xvfb "$display" -screen 0 1920x1080x24 > /dev/null 2>&1 &
xvfb_pid=$!
sleep 2

# UG_UI_TRACE makes HoverTooltip log each hint it shows. Without that breadcrumb "no errors" would also
# be what a crashed game, an empty map catalog or a layout that moved under Xvfb looks like.
UG_UI_TRACE=1 UNTURNED_PATH="$content" DISPLAY="$display" "$binary" > "$log" 2>&1 &
game_pid=$!
export DISPLAY="$display"

# Hover the map list, then leave it. Showing and hiding one tooltip is the whole reproduction: it needs
# no click, and the errors land the moment the tooltip goes away. The sweep repeats until the menu
# answers, so a slow install scan does not decide the result, and several rows are covered so a short
# map list cannot pass by being missed.
hovered=0
found=0
for _ in $(seq 1 25); do
    if ! kill -0 "$game_pid" 2> /dev/null; then
        tail -20 "$log" >&2
        fail "The exported game exited before the menu could be driven (see above)."
    fi

    for y in 140 200 260; do
        xdotool mousemove 700 "$y"
        sleep 0.4
    done
    xdotool mousemove 1400 700 # off the list: hides the hint
    sleep 0.8

    grep -q "\[ui\] hover hint:" "$log" && hovered=1
    grep -q "Attempt to disconnect a nonexistent connection" "$log" && found=1
    [[ "$hovered" == "1" || "$found" == "1" ]] && break
done

kill "$game_pid" 2> /dev/null
wait "$game_pid" 2> /dev/null
game_pid=""

if grep -q "Attempt to disconnect a nonexistent connection" "$log"; then
    grep -m 4 "nonexistent connection" "$log" >&2
    fail "The boot menu is showing engine Popups again -- hovering it spams stderr (above)."
fi

# Only meaningful once the menu draws its own hints; on a build that still uses engine tooltips the
# error above is what fails first.
[[ "$hovered" == "1" ]] \
    || fail "The pointer sweep never reached a map row, so the clean stderr proves nothing. Check that the map catalog is not empty and that the menu still lays the list out where this script looks (700, 140-260 at 1920x1080)."

echo "[popup-check] ok: hovering the boot menu showed hints and printed no signal-disconnect errors."
