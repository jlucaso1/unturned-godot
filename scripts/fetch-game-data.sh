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

# Receipt a finished run leaves in the destination, naming the selection it completed. Only "all"
# needs it — the set of maps that exists is a Steam-side fact, so nothing on disk can stand in for it
# — which keeps every other selection verifiable against a tree this script never wrote, such as a
# real Steam install someone points --dir at.
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
    local ok=1 map

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

    if [[ "$maps" == "all" ]]; then
        # "Every map Valve currently ships" is not knowable from disk, so the only honest evidence is
        # the receipt a completed run leaves behind. Without it, fall through to the downloader, which
        # is incremental; with it, still require that the maps on disk are themselves whole.
        if [[ "$(cat "$1/$COMPLETION_MARKER" 2>/dev/null)" != "all" ]]; then
            echo "No record of a completed --maps all download in $1." >&2
            ok=0
        elif ! any_map_is_whole "$1"; then
            echo "Missing: any loadable map under $1/Maps" >&2
            ok=0
        fi
    else
        while read -r map; do
            map_is_whole "$1" "$map" || ok=0
        done < <(selected_maps)
    fi

    [[ $ok == 1 ]]
}

# Level.dat is the map itself, and the heightmap tiles are what this project needs to load one. The
# tiles are checked as files rather than as a directory because an interrupted download leaves the
# directory behind: DepotDownloader creates the tree before it has written everything into it. Maps
# predating Landscape tiles (Destruction, Paintball Arena) therefore never pass, which matches the
# fact that the project cannot load them either.
map_is_whole() {
    local root="$1" map="$2" ok=1

    if [[ -z "$map" ]]; then
        echo "Missing: any map under $root/Maps" >&2
        return 1
    fi

    [[ -f "$root/Maps/$map/Level.dat" ]] || { echo "Missing: $root/Maps/$map/Level.dat" >&2; ok=0; }

    shopt -s nullglob
    local tiles=("$root/Maps/$map/Landscape/Heightmaps"/*.heightmap)
    shopt -u nullglob
    if [[ ${#tiles[@]} -eq 0 ]]; then
        echo "Missing: heightmap tiles in $root/Maps/$map/Landscape/Heightmaps" >&2
        ok=0
    fi

    [[ $ok == 1 ]]
}

# For "all", the maps Valve ships include pre-Landscape ones this project cannot load, so the bar is
# that at least one map came down whole rather than that every directory did.
any_map_is_whole() {
    local dir
    shopt -s nullglob
    for dir in "$1"/Maps/*/; do
        if map_is_whole "$1" "$(basename "$dir")" 2>/dev/null; then
            shopt -u nullglob
            return 0
        fi
    done
    shopt -u nullglob
    return 1
}

if [[ "$mode" == "verify" ]]; then
    if ! verify_content "$dest"; then
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

    ids="$(find "$key_dir" -maxdepth 1 -name 'manifest_*.txt' -exec basename {} \; | sort | tr '\n' ' ')"
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

filelist="$(mktemp "${TMPDIR:-/tmp}/unturned-filelist.XXXXXX")"
trap 'rm -f -- "$filelist"' EXIT
{
    printf 'regex:^Bundles/.*\n'
    printf 'regex:^Localization/.*\n'
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
# confusing test failure later. The receipt goes down first because verifying an "all" selection reads
# it, and comes back off if the tree does not hold up, so it never outlives a good download.
printf '%s\n' "$maps" > "$dest/$COMPLETION_MARKER"
if ! verify_content "$dest"; then
    rm -f "$dest/$COMPLETION_MARKER"
    echo "Download finished but $dest is incomplete." >&2
    exit 1
fi

echo
echo "Done. Point the project at it with:"
echo "  export UNTURNED_PATH=\"$dest\""
