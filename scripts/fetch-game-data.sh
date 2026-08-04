#!/usr/bin/env bash
# Fetch the game content the data-backed tests need, without a Steam login or a graphical install.
#
# Unturned's dedicated server (app 1110390) is downloadable by Valve's anonymous account, and it ships
# the same content the client does: Bundles/core_<os>.masterbundle (the same SerializedFile object
# graph the client's carries, meshes and textures included), Bundles/*.dat asset definitions, and the
# official maps with their Landscape tiles. That makes it usable both on a machine with no Steam
# install and in CI.
#
# Nothing downloaded here is ever committed: the destination defaults to build/, which is git-ignored,
# and this stays a fetch from Steam's own CDN on every run. See NOTICE.md.
#
# Usage:
#   ./scripts/fetch-game-data.sh                          # Bundles + PEI, into build/game-data
#   ./scripts/fetch-game-data.sh --maps PEI,Washington    # pick maps by folder name
#   ./scripts/fetch-game-data.sh --maps all               # every official map (~1.5 GB)
#   ./scripts/fetch-game-data.sh --dir /opt/unturned      # somewhere else
#   ./scripts/fetch-game-data.sh --print-dir              # just echo the destination
#   ./scripts/fetch-game-data.sh --manifest-key           # echo a cache key for the current content
#   ./scripts/fetch-game-data.sh --verify                 # is the requested content really on disk?
#
# Then point the project at it:
#   export UNTURNED_PATH="$(./scripts/fetch-game-data.sh --print-dir)"
set -euo pipefail

readonly APP_ID=1110390                      # Unturned Dedicated Server, anonymous-downloadable
readonly DD_VERSION=3.4.0

# Receipt a finished --maps all run leaves in the destination. It records every loadable official map
# that was checked, so a stale cache cannot prove completion merely because one other map survived.
readonly COMPLETION_MARKER=.unturned-fetch-complete

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dest="$repo_dir/build/game-data"
maps="PEI"
mode="download"
retries=8
os_name=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --maps) maps="${2:?--maps needs a value}"; shift 2 ;;
        --dir) dest="${2:?--dir needs a value}"; shift 2 ;;
        --os) os_name="${2:?--os needs a value}"; shift 2 ;;
        --retries) retries="${2:?--retries needs a value}"; shift 2 ;;
        --print-dir) mode="print-dir"; shift ;;
        --manifest-key) mode="manifest-key"; shift ;;
        --verify) mode="verify"; shift ;;
        -h|--help) sed -n '2,23p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

if [[ "$mode" == "print-dir" ]]; then
    mkdir -p "$dest"
    (cd "$dest" && pwd)
    exit 0
fi

# The maps the caller asked for, one per line, whitespace trimmed.
selected_maps() {
    local map
    IFS=',' read -ra raw <<< "$maps"
    for map in "${raw[@]}"; do
        map="${map#"${map%%[![:space:]]*}"}"
        map="${map%"${map##*[![:space:]]}"}"
        [[ -n "$map" ]] && printf '%s\n' "$map"
    done
}

# --- Verification ------------------------------------------------------------------------------
# Is the requested content actually on disk, whole? A download that dies partway leaves the tree in
# a state that reads as an install to UnturnedInstall.Find, which accepts any directory the override
# names; the data-backed tests then run against half a map and fail instead of self-skipping. Callers
# ask this before trusting a directory, and before deciding a re-run has nothing left to do.
verify_content() {
    local ok=1 map require_all_receipt="${2:-1}"

    shopt -s nullglob
    local bundles=("$1"/Bundles/core*.masterbundle)
    shopt -u nullglob
    if [[ ${#bundles[@]} -eq 0 ]]; then
        echo "Missing: a core masterbundle in $1/Bundles" >&2
        ok=0
    fi

    if [[ ! -d "$1/Bundles/Objects" ]]; then
        echo "Missing: $1/Bundles/Objects" >&2
        ok=0
    fi

    # Either data-directory layout will do; see the filelist above. Checked here so a cache from before
    # this file was fetched fails verification and is refilled, rather than quietly drawing placeholders.
    if [[ ! -f "$1/Unturned_Data/resources.assets" && ! -f "$1/Unturned_Headless_Data/resources.assets" ]]; then
        echo "Missing: $1/Unturned_Headless_Data/resources.assets" >&2
        ok=0
    fi

    if [[ "$maps" == "all" ]]; then
        if ! verify_all_maps "$1" "$require_all_receipt"; then
            ok=0
        fi
    else
        while read -r map; do
            map_is_whole "$1" "$map" || ok=0
        done < <(selected_maps)
    fi

    [[ $ok == 1 ]]
}

# Level.dat is the map itself, and the heightmap tiles are what this project needs to load one. Known
# official grids get exact coordinate checks; --maps all discovers the loadable map set from the tree so
# a new official map cannot be silently omitted from the receipt.
#
# Which directories have to be whole maps is the whole question. Deriving that from Level.dat alone makes
# an interrupted download self-consistent: the half-written map drops out of the expected set, the maps
# that did finish verify, and the receipt is published without ever mentioning the one that is missing.
# Treating every directory as a map is wrong in the other direction — the game keeps editor scratch under
# Maps/ (MapCatalogTests models exactly that), and a scratch folder would fail every run for lacking a
# Level.dat it was never going to have.
#
# So ask the downloader. DepotDownloader mirrors the tree it materializes under .DepotDownloader/staging
# and leaves that skeleton behind, so staging/Maps names exactly the maps this fetcher created — the one
# whose Level.dat never landed included. That is the record the receipt should be built from.
#
# A tree this script did not produce (UNTURNED_PATH on a real install) has no staging directory. There,
# fall back to what a map looks like on disk: Level.dat, or the Landscape/Level folders every official
# map ships. A directory with none of the three is not a map and is left alone.
all_maps() {
    local root="$1" dir map staging="$1/.DepotDownloader/staging/Maps"
    local dirs=()

    shopt -s nullglob
    if [[ -d "$staging" ]]; then
        dirs=("$staging"/*/)
    else
        for dir in "$root"/Maps/*/; do
            if [[ -s "$dir/Level.dat" || -d "$dir/Landscape" || -d "$dir/Level" ]]; then
                dirs+=("$dir")
            fi
        done
    fi
    shopt -u nullglob

    for dir in "${dirs[@]}"; do
        map="${dir%/}"
        map="${map##*/}"
        case "$map" in
            Destruction|Paintball_Arena_0) continue ;;
        esac
        printf '%s\n' "$map"
    done
}

map_tile_bounds() {
    case "$1" in
        "Alpha Valley"|Monolith|Tutorial) printf '%s\n' '-1 0 -1 0' ;;
        PEI|Washington|Yukon) printf '%s\n' '-2 1 -2 1' ;;
        Germany) printf '%s\n' '-3 2 -3 2' ;;
        Russia) printf '%s\n' '-4 3 -4 3' ;;
        *) return 1 ;;
    esac
}

receipt_has_map() {
    local marker="$1" wanted="$2" line
    while IFS= read -r line; do
        [[ "$line" == "map=$wanted" ]] && return 0
    done < "$marker"
    return 1
}

map_is_whole() {
    local root="$1" map="$2" ok=1 min_x max_x min_y max_y expected_count bounds
    local minimum_unknown_tiles="${3:-1}"
    local map_root="$root/Maps/$map"

    if [[ -z "$map" ]]; then
        echo "Missing: any map under $root/Maps" >&2
        return 1
    fi

    [[ -s "$map_root/Level.dat" ]] || { echo "Missing: $map_root/Level.dat" >&2; ok=0; }

    shopt -s nullglob
    local tiles=("$map_root/Landscape/Heightmaps"/*.heightmap)
    shopt -u nullglob

    if bounds="$(map_tile_bounds "$map")"; then
        read -r min_x max_x min_y max_y <<< "$bounds"
        expected_count=$(((max_x - min_x + 1) * (max_y - min_y + 1)))
        if [[ ${#tiles[@]} -ne $expected_count ]]; then
            echo "Missing: $map expects $expected_count heightmap tiles, found ${#tiles[@]}" >&2
            ok=0
        fi
        local x y
        for ((x = min_x; x <= max_x; x++)); do
            for ((y = min_y; y <= max_y; y++)); do
                [[ -s "$map_root/Landscape/Heightmaps/Tile_${x}_${y}_Source.heightmap" ]] || {
                    echo "Missing: $map_root/Landscape/Heightmaps/Tile_${x}_${y}_Source.heightmap" >&2
                    ok=0
                }
            done
        done
    else
        # Workshop/preview maps are not part of the depot's fixed set. Require a nonempty rectangular
        # grid, while allowing legitimate one-tile maps.
        if [[ ${#tiles[@]} -lt $minimum_unknown_tiles ]]; then
            echo "Missing: at least $minimum_unknown_tiles heightmap tile(s) in $map_root/Landscape/Heightmaps" >&2
            ok=0
        else
            min_x=2147483647; max_x=-2147483648; min_y=2147483647; max_y=-2147483648
            local tile base tx ty
            for tile in "${tiles[@]}"; do
                base="${tile##*/}"
                if [[ "$base" =~ ^Tile_(-?[0-9]+)_(-?[0-9]+)_Source\.heightmap$ ]]; then
                    tx="${BASH_REMATCH[1]}"; ty="${BASH_REMATCH[2]}"
                    ((tx < min_x)) && min_x=$tx
                    ((tx > max_x)) && max_x=$tx
                    ((ty < min_y)) && min_y=$ty
                    ((ty > max_y)) && max_y=$ty
                else
                    echo "Invalid heightmap filename: $tile" >&2
                    ok=0
                fi
            done
            expected_count=$(((max_x - min_x + 1) * (max_y - min_y + 1)))
            if [[ ${#tiles[@]} -ne $expected_count ]]; then
                echo "Missing: heightmap grid in $map_root is not complete" >&2
                ok=0
            fi
        fi
    fi

    [[ $ok == 1 ]]
}

verify_all_maps() {
    local root="$1" require_receipt="$2" ok=1 map marker="$1/$COMPLETION_MARKER"
    local found=0

    if [[ "$require_receipt" == 1 ]]; then
        if [[ ! -f "$marker" ]] || [[ "$(head -n 1 "$marker" 2>/dev/null)" != "selection=all" ]]; then
            echo "No record of a completed --maps all download in $root." >&2
            return 1
        fi
    fi

    if [[ "$require_receipt" == 1 ]]; then
        while IFS= read -r map; do
            [[ -n "$map" ]] || continue
            found=1
            if ! map_list_contains "$root" "$map"; then
                echo "Completion receipt contains a map no longer present: $map" >&2
                ok=0
            fi
            map_is_whole "$root" "$map" 4 || ok=0
        done < <(sed -n 's/^map=//p' "$marker")

        while IFS= read -r map; do
            [[ -n "$map" ]] || continue
            if ! receipt_has_map "$marker" "$map"; then
                echo "Completion receipt does not contain map: $map" >&2
                ok=0
            fi
        done < <(all_maps "$root")
    else
        while IFS= read -r map; do
            [[ -n "$map" ]] || continue
            found=1
            map_is_whole "$root" "$map" 4 || ok=0
        done < <(all_maps "$root")
    fi

    [[ $ok == 1 && $found == 1 ]]
}

map_list_contains() {
    local root="$1" wanted="$2" map
    while IFS= read -r map; do
        [[ "$map" == "$wanted" ]] && return 0
    done < <(all_maps "$root")
    return 1
}

write_completion_marker() {
    local marker="$dest/$COMPLETION_MARKER" temp="$dest/$COMPLETION_MARKER.tmp.$$" map
    {
        printf '%s\n' 'selection=all'
        while IFS= read -r map; do
            printf 'map=%s\n' "$map"
        done < <(all_maps "$dest")
    } > "$temp"
    mv -f -- "$temp" "$marker"
}

if [[ "$mode" == "verify" ]]; then
    if ! verify_content "$dest" 1; then
        echo "Incomplete game content in $dest (maps: $maps)." >&2
        exit 1
    fi
    echo "Game content verified in $dest (maps: $maps)."
    exit 0
fi

# Which masterbundle variant to pull. The reader accepts any of the three (they differ only in the
# baked shader variants), so this only decides which one lands on disk; default to the host's.
if [[ -z "$os_name" ]]; then
    case "$(uname -s)" in
        Darwin) os_name=macos ;;
        *) os_name=linux ;;
    esac
fi

# --- DepotDownloader ---------------------------------------------------------------------------
# steamcmd cannot filter by file, so it would pull the whole ~1.7 GB server; DepotDownloader takes a
# file list, which brings the default fetch down to ~165 MB. Pinned by version and checksum.
case "$(uname -s)/$(uname -m)" in
    Linux/x86_64)              dd_platform=linux-x64;    dd_sha=a999dec66b4850fc961bd50366696d23c2d0fad7b18790e6a5647b2f19097a53 ;;
    Linux/aarch64|Linux/arm64) dd_platform=linux-arm64;  dd_sha=d9fb612ccebc1db8eeea3b4045d2221ec70431381393ce908fb72f01d4f9c812 ;;
    Darwin/x86_64)             dd_platform=macos-x64;    dd_sha=3214b689564d73e9342a8a4aef693de6ad3d293801b0f300a4466f60ec75befb ;;
    Darwin/arm64)              dd_platform=macos-arm64;  dd_sha=60e80c7c496f3f9a079cd3c62036b35d088c27bc0149baf38f009eb57a52f6a5 ;;
    *) echo "No DepotDownloader build for $(uname -s)/$(uname -m)." >&2; exit 1 ;;
esac

tools_dir="$repo_dir/build/tools/depotdownloader-$DD_VERSION-$dd_platform"
downloader="$tools_dir/DepotDownloader"

if [[ ! -x "$downloader" ]]; then
    echo "Fetching DepotDownloader $DD_VERSION ($dd_platform)..." >&2
    # The X run has to end the template: BSD mktemp, which is what macOS ships, rejects a suffix after
    # it. unzip does not care what the file is called.
    archive="$(mktemp "${TMPDIR:-/tmp}/depotdownloader.XXXXXX")"
    curl -fsSL -o "$archive" \
        "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_$DD_VERSION/DepotDownloader-$dd_platform.zip"

    actual="$(sha256sum "$archive" 2>/dev/null || shasum -a 256 "$archive")"
    actual="${actual%% *}"
    if [[ "$actual" != "$dd_sha" ]]; then
        rm -f -- "$archive"
        echo "DepotDownloader checksum mismatch: expected $dd_sha, got $actual" >&2
        exit 1
    fi

    mkdir -p "$tools_dir"
    unzip -oq "$archive" -d "$tools_dir"
    chmod +x "$downloader"
    rm -f -- "$archive"
fi

# Steam hands out a randomized list of connection managers, and only the candidates it offers on :443
# survive an HTTP CONNECT proxy (the :27019 ones time out). Retrying re-queries the directory and
# draws a fresh candidate, which is what makes this work in proxied sandboxes; already-downloaded
# files are skipped, so a retry only costs the reconnect.
run_downloader() {
    local attempt
    for ((attempt = 1; attempt <= retries; attempt++)); do
        if "$downloader" "$@"; then
            return 0
        fi
        echo "Steam connection attempt $attempt/$retries failed, retrying..." >&2
    done

    echo "Could not reach Steam after $retries attempts." >&2
    return 1
}

# --- Cache key ---------------------------------------------------------------------------------
# The depot manifest IDs change exactly when Valve ships an update, so hashing them (plus what this
# run selects) gives a CI cache key that is precise instead of time-based.
if [[ "$mode" == "manifest-key" ]]; then
    key_dir="$(mktemp -d "${TMPDIR:-/tmp}/unturned-manifests.XXXXXX")"
    trap 'rm -rf -- "$key_dir"' EXIT
    run_downloader -app "$APP_ID" -os "$os_name" -manifest-only -dir "$key_dir" >&2

    ids="$(
        for manifest in "$key_dir"/manifest_*.txt; do
            [[ -f "$manifest" ]] || continue
            basename "$manifest"
        done | sort | tr '\n' ' '
    )"
    if [[ -z "$ids" ]]; then
        echo "Could not read the depot manifests." >&2
        exit 1
    fi

    digest="$(printf '%s|%s|%s' "$ids" "$maps" "$os_name" | sha256sum 2>/dev/null || \
              printf '%s|%s|%s' "$ids" "$maps" "$os_name" | shasum -a 256)"
    echo "unturned-content-${digest:0:16}"
    exit 0
fi

# --- What to pull ------------------------------------------------------------------------------
# Bundles/ carries the masterbundle plus every asset .dat the object database resolves GUIDs through;
# Localization/ is what the map browser reads its names from. Maps are opt-in because each is 50-130 MB.
mkdir -p "$dest"
dest="$(cd "$dest" && pwd)"

# An old receipt must never survive a new attempt: if the retry fails, the previous success cannot be
# mistaken for the content that this invocation was asked to produce.
rm -f -- "$dest/$COMPLETION_MARKER"

filelist="$(mktemp "${TMPDIR:-/tmp}/unturned-filelist.XXXXXX")"
trap 'rm -f -- "$filelist"' EXIT
{
    printf 'regex:^Bundles/.*\n'
    printf 'regex:^Localization/.*\n'
    # The Unity data file holding the character prefabs — Player_Client with its Viewmodel subtree, the
    # zombies, the ragdolls — and the skybox materials. The server build calls its data directory
    # Unturned_Headless_Data where the retail client calls it Unturned_Data; both carry the same object
    # graph, and UnturnedInstall.FindDataFile accepts either. ~31 MB, and without it the game draws a
    # placeholder capsule instead of the real character.
    printf 'regex:^Unturned_Headless_Data/resources\\.(assets|resource)$\n'
    if [[ "$maps" == "all" ]]; then
        printf 'regex:^Maps/.*\n'
    else
        while read -r map; do
            printf 'regex:^Maps/%s/.*\n' "$map"
        done < <(selected_maps)
    fi
} > "$filelist"

echo "Downloading Unturned content (app $APP_ID, maps: $maps, bundle: $os_name) into $dest"
run_downloader -app "$APP_ID" -os "$os_name" -filelist "$filelist" -dir "$dest"

# A download that reports success but left the tree short is worth catching here rather than in a
# confusing test failure later. Validate before publishing the all-map receipt, so it never outlives a
# complete content tree.
if [[ "$maps" == "all" ]]; then
    if ! verify_content "$dest" 0; then
        echo "Download finished but $dest is incomplete." >&2
        exit 1
    fi
    write_completion_marker
elif ! verify_content "$dest"; then
    echo "Download finished but $dest is incomplete." >&2
    exit 1
fi

echo
echo "Done. Point the project at it with:"
echo "  export UNTURNED_PATH=\"$dest\""
