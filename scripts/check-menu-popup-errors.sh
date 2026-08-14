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
#   ./scripts/check-menu-popup-errors.sh                    # export, drive the menu, fail on the error
#   ./scripts/check-menu-popup-errors.sh --keep             # keep the run log for inspection
#   ./scripts/check-menu-popup-errors.sh --install-templates  # fetch the one export template this needs
#
# Environment: GODOT and UNTURNED_PATH as for run-benchmark.sh. Needs the Linux export templates, plus
# Xvfb and xdotool. Anything missing makes this skip, not fail.
#
# --install-templates exists because this gate spent its whole life skipping. It needs the export
# templates and nothing in the repo fetched them, so on every machine and in every workflow the
# prerequisite check above was the end of the story: a gate for a bug that only reproduces in a release
# export, which had never once run. Fetching them is what turns it back into a gate.
#
# It fetches ONE FILE rather than the .tpz. The .NET export templates archive is 1.2 GB and carries
# every platform Godot supports; the Linux release template inside it is 28 MB compressed. A .tpz is an
# ordinary zip, so its central directory names the byte range of each member and an HTTP range request
# takes just that one — which is the difference between a 1.2 GB cache entry competing with the game
# content for this repo's cache budget and a download small enough to just do every run. The extracted
# bytes are checked against the archive's own CRC and against the digest pinned below, so this is no
# more trusting than install-godot.sh's checksum.
set -uo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
keep=0
install_templates=0
case "${1:-}" in
    --keep) keep=1 ;;
    --install-templates) install_templates=1 ;;
    "") ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
esac

skip() {
    echo "[popup-check] skipped: $1" >&2
    exit 0
}

# The template has to match the editor exactly, so the version is READ from install-godot.sh rather
# than pinned a second time here: two pins that can disagree is a build that exports against the wrong
# engine and says nothing about it.
godot_version="$(sed -n 's/^readonly GODOT_VERSION=//p' "$repo_dir/scripts/install-godot.sh")"
[[ -n "$godot_version" ]] || { echo "Could not read GODOT_VERSION from install-godot.sh" >&2; exit 1; }

# sha256 of templates/linux_release.x86_64 inside Godot_v<GODOT_VERSION>_mono_export_templates.tpz.
# Bump this together with GODOT_VERSION above: the version is read from install-godot.sh so the two
# cannot drift, but the checksum is a second fact about the same release and this is the one that has to
# be re-measured by hand.
readonly TEMPLATE_SHA256=f724c4ecb43ab5cfda3d3b92a99e183a92082b3228cf5ae52daccc9b22ac5481

templates_root="${XDG_DATA_HOME:-$HOME/.local/share}/godot/export_templates"

if (( install_templates )); then
    url="https://github.com/godotengine/godot-builds/releases/download/$godot_version/Godot_v${godot_version}_mono_export_templates.tpz"
    staging="$(mktemp -d "${TMPDIR:-/tmp}/godot-templates.XXXXXX")"
    trap 'rm -rf -- "$staging"' EXIT

    echo "[popup-check] fetching the Linux release export template for $godot_version..."
    python3 - "$url" "$staging" <<'PY' || exit 1
import struct, subprocess, sys, zlib

url, out_dir = sys.argv[1], sys.argv[2]


def ranged(start, end):
    return subprocess.run(["curl", "-fsSL", "--retry", "3", "-r", f"{start}-{end}", url],
                          check=True, capture_output=True).stdout


# The archive's total length, from a one-byte probe's Content-Range.
head = subprocess.run(["curl", "-fsSL", "--retry", "3", "-r", "0-0", "-D", "-", "-o", "/dev/null", url],
                      check=True, capture_output=True, text=True).stdout
ranges = [l for l in head.splitlines() if l.lower().startswith("content-range")]
if not ranges:
    sys.exit("the server did not answer a range request; it cannot be fetched a member at a time")
total = int(ranges[0].split("/")[1])

# End of central directory, then the central directory itself.
tail = ranged(max(0, total - 69632), total - 1)
eocd = tail.rfind(b"PK\x05\x06")
if eocd < 0:
    sys.exit("no end-of-central-directory record; this is not the archive we expect")
cd_size, cd_offset = struct.unpack("<II", tail[eocd + 12:eocd + 20])
directory = ranged(cd_offset, cd_offset + cd_size - 1)

wanted = {"templates/linux_release.x86_64", "templates/version.txt"}
entries = {}
at = 0
while True:
    at = directory.find(b"PK\x01\x02", at)
    if at < 0:
        break
    method = struct.unpack("<H", directory[at + 10:at + 12])[0]
    crc, compressed, uncompressed = struct.unpack("<III", directory[at + 16:at + 28])
    name_len = struct.unpack("<H", directory[at + 28:at + 30])[0]
    local_header = struct.unpack("<I", directory[at + 42:at + 46])[0]
    name = directory[at + 46:at + 46 + name_len].decode()
    if name in wanted:
        entries[name] = (method, crc, compressed, uncompressed, local_header)
    at += 4

missing = wanted - entries.keys()
if missing:
    sys.exit(f"the archive does not contain {', '.join(sorted(missing))}")

for name, (method, crc, compressed, uncompressed, local_header) in entries.items():
    # The local header repeats the name and may carry a different extra field, so its real length is
    # read rather than assumed from the central directory's copy.
    header = ranged(local_header, local_header + 29)
    name_len, extra_len = struct.unpack("<HH", header[26:30])
    start = local_header + 30 + name_len + extra_len
    blob = ranged(start, start + compressed - 1)
    raw = zlib.decompress(blob, -15) if method == 8 else blob
    if len(raw) != uncompressed or zlib.crc32(raw) != crc:
        sys.exit(f"{name} failed the archive's own checksum")
    with open(f"{out_dir}/{name.rsplit('/', 1)[1]}", "wb") as handle:
        handle.write(raw)
PY

    actual="$(sha256sum "$staging/linux_release.x86_64")"
    actual="${actual%% *}"
    if [[ "$actual" != "$TEMPLATE_SHA256" ]]; then
        echo "[popup-check] template checksum mismatch: expected $TEMPLATE_SHA256, got $actual" >&2
        exit 1
    fi

    # Godot looks the template dir up by the contents of version.txt, so that name comes from the
    # archive rather than from a string built here.
    destination="$templates_root/$(tr -d '[:space:]' < "$staging/version.txt")"
    mkdir -p "$destination"
    cp "$staging/linux_release.x86_64" "$staging/version.txt" "$destination/"
    chmod +x "$destination/linux_release.x86_64"
    echo "[popup-check] installed $destination/linux_release.x86_64"
    exit 0
fi

godot="${GODOT:-$("$repo_dir/scripts/install-godot.sh" --print-path)}"
[[ -x "$godot" ]] || skip "no Godot at $godot (run ./scripts/install-godot.sh, or set GODOT)"
command -v Xvfb > /dev/null || skip "no Xvfb"
command -v xdotool > /dev/null || skip "no xdotool"

content="${UNTURNED_PATH:-$("$repo_dir/scripts/fetch-game-data.sh" --print-dir)}"
[[ -d "$content/Maps" ]] || skip "no game content at $content (see ./scripts/fetch-game-data.sh)"

ls "$templates_root"/*/linux_release.x86_64 > /dev/null 2>&1 \
    || skip "no Linux export templates under $templates_root (run this script with --install-templates, or install them from the editor: Editor > Manage Export Templates)"

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

# Godot's exit status is NOT enough, and trusting it is how this gate would report a clean menu over a
# build that cannot start. Measured on 4.7-stable: when `dotnet publish` fails, the exporter logs
# "Failed to build project", downgrades the whole run to "completed with warnings", writes a .pck with
# no managed assemblies in it -- and EXITS ZERO. The binary produced then dies on launch with
# "ERROR: .NET: Assemblies not found" and a segfault, which the pointer sweep below would have read as
# "the game exited before the menu could be driven" if it noticed at all.
#
# So the log is the authority on whether the managed half was actually built.
if grep -qE "Failed to build project|ERROR: Export \.NET Project" "$log"; then
    grep -m 6 -E "error [A-Z]+[0-9]+|Failed to build project" "$log" >&2
    fail "The export's .NET publish failed, so the binary carries no managed assemblies (see above)."
fi

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
