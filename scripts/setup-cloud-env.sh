#!/usr/bin/env bash
# Provision a machine to build, test and lint this project: the .NET SDK, a warm NuGet cache, and the
# game content the data-backed tests read. Written for the ephemeral containers behind Claude Code's
# cloud environments and Codex's cloud environment, but it is an ordinary idempotent script, so it is
# also the fastest way to set up a fresh Linux box or a Docker image.
#
# It provisions everything except Godot itself: the editor is only needed to *run* the game, and the
# whole test suite, the coverage gate and `dotnet format` work without it.
#
# Usage:
#   ./scripts/setup-cloud-env.sh
#
# Environment:
#   UNTURNED_SETUP_MAPS   maps to fetch: a comma-separated list, "all", or "none" to skip the
#                         download entirely (default: PEI, ~165 MB with the bundles)
#   UNTURNED_PATH         where the content goes (default: <repo>/build/game-data)
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
maps="${UNTURNED_SETUP_MAPS:-PEI}"
content_dir="${UNTURNED_PATH:-$repo_dir/build/game-data}"
log_dir="$repo_dir/build/setup-logs"
mkdir -p "$log_dir"

as_root() {
    if [[ "$(id -u)" == "0" ]]; then
        "$@"
    else
        sudo "$@"
    fi
}

# --- .NET SDK ----------------------------------------------------------------------------------
# Ubuntu 24.04 carries dotnet-sdk-10.0 in noble-updates/universe, which is both the fastest install
# and the one the container image can cache. Never a no-op guard on `dotnet` alone: an older SDK on
# PATH cannot build net10.0.
install_dotnet() {
    if dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
        echo "[dotnet] SDK 10 already present: $(dotnet --version)"
        return 0
    fi

    if ! command -v apt-get > /dev/null; then
        echo "[dotnet] No apt-get here; install the .NET SDK 10 yourself: https://dotnet.microsoft.com/download" >&2
        return 1
    fi

    echo "[dotnet] Installing the .NET SDK 10..."
    export DEBIAN_FRONTEND=noninteractive
    as_root apt-get update -qq
    as_root apt-get install -y -qq dotnet-sdk-10.0
    echo "[dotnet] Installed $(dotnet --version)"
}

# --- NuGet -------------------------------------------------------------------------------------
# Restoring during setup is what makes the first build in a session fast, and it is also the step
# that needs the network, which Codex cuts off once the agent starts.
warm_nuget() {
    echo "[nuget] Restoring..."
    dotnet restore "$repo_dir/unturned-godot.sln"
    dotnet restore "$repo_dir/tools/PerfHarness/PerfHarness.csproj"
    echo "[nuget] Restore done"
}

# --- Game content ------------------------------------------------------------------------------
fetch_content() {
    if [[ "$maps" == "none" ]]; then
        echo "[content] UNTURNED_SETUP_MAPS=none, skipping the download"
        return 0
    fi

    # Ask the fetcher whether *this* selection is already whole, rather than just whether some content
    # is there: widening UNTURNED_SETUP_MAPS from PEI to PEI,Washington has to pull Washington, and a
    # download that died partway has to be finished rather than treated as done.
    if "$repo_dir/scripts/fetch-game-data.sh" --verify --maps "$maps" --dir "$content_dir" 2>/dev/null; then
        return 0
    fi

    "$repo_dir/scripts/fetch-game-data.sh" --maps "$maps" --dir "$content_dir"
}

quarantine_incomplete_content() {
    if [[ ! -e "$content_dir" && ! -L "$content_dir" ]]; then
        return 0
    fi

    local quarantine="${content_dir}.incomplete.$$.${RANDOM:-0}"
    if mv -f -- "$content_dir" "$quarantine"; then
        echo "[content] moved incomplete content aside: $quarantine" >&2
    else
        echo "[content] could not quarantine incomplete content at $content_dir" >&2
        return 1
    fi
}

# The download and the toolchain install do not depend on each other, and the container images that
# run this cap setup at a few minutes, so overlap them; the restore has to wait for the SDK.
toolchain_log="$log_dir/toolchain.log"
content_log="$log_dir/content.log"

( install_dotnet && warm_nuget ) > "$toolchain_log" 2>&1 &
toolchain_pid=$!
fetch_content > "$content_log" 2>&1 &
content_pid=$!

toolchain_status=0
content_status=0
wait "$toolchain_pid" || toolchain_status=$?
wait "$content_pid" || content_status=$?

cat "$toolchain_log"
cat "$content_log"

if [[ $toolchain_status -ne 0 ]]; then
    echo "Setup failed: the .NET toolchain could not be installed (see $toolchain_log)." >&2
    exit "$toolchain_status"
fi

# A missing download is worth reporting but not worth failing the session over: every test that reads
# real content self-skips, so the suite is still green, just smaller.
if [[ $content_status -ne 0 ]]; then
    quarantine_incomplete_content
    echo
    echo "Warning: the game content could not be fetched (see $content_log)." >&2
    echo "The suite still runs; its data-backed tests will self-skip." >&2
    exit 0
fi

if [[ "$maps" != "none" ]]; then
    echo
    echo "Game content ready. Point the project at it with:"
    echo "  export UNTURNED_PATH=\"$content_dir\""
fi
