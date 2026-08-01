#!/usr/bin/env bash
# Provision a machine to build, test and lint this project: the .NET SDK, a warm NuGet cache, and the
# game content the data-backed tests read. Written for the ephemeral containers behind Claude Code's
# cloud environments and Codex's cloud environment, but it is an ordinary idempotent script, so it is
# also the fastest way to set up a fresh Linux box or a Docker image.
#
# Godot is left out by default: the editor is only needed to *run* the game, the test suite and the
# coverage gate and `dotnet format` all work without it, and it would add ~350 MB and a minute to a
# setup that most work here never uses. Set UNTURNED_SETUP_GODOT=1 to get it (plus software rendering,
# so the benchmark tiers run on a GPU-less container).
#
# Usage:
#   ./scripts/setup-cloud-env.sh
#
# Environment:
#   UNTURNED_SETUP_MAPS   maps to fetch: a comma-separated list, "all", or "none" to skip the
#                         download entirely (default: PEI, ~165 MB with the bundles)
#   UNTURNED_SETUP_GODOT  1 to also install Godot 4.7 .NET and Mesa's software Vulkan driver
#   UNTURNED_PATH         where the content goes (default: <repo>/build/game-data)
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
maps="${UNTURNED_SETUP_MAPS:-PEI}"
content_dir="${UNTURNED_PATH:-$repo_dir/build/game-data}"
# UNTURNED_PATH=/opt/unturned/ is the same directory as /opt/unturned, but "${content_dir}.incomplete"
# is not: the trailing separator puts the quarantine path *inside* the tree being quarantined, and mv
# cannot move a directory into its own child. That would turn a tolerated download failure into a setup
# failure, so normalize once here rather than at each use.
while [[ "$content_dir" == */ && "$content_dir" != "/" ]]; do
    content_dir="${content_dir%/}"
done
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

# --- Godot (opt-in) ----------------------------------------------------------------------------
install_godot() {
    if [[ "${UNTURNED_SETUP_GODOT:-0}" != "1" ]]; then
        echo "[godot] UNTURNED_SETUP_GODOT is not 1, skipping"
        return 0
    fi

    "$repo_dir/scripts/install-godot.sh"
}

quarantine_incomplete_content() {
    if [[ ! -e "$content_dir" && ! -L "$content_dir" ]]; then
        return 0
    fi

    local quarantine
    if ! quarantine="$(mktemp -d "${content_dir}.incomplete.XXXXXX")"; then
        echo "[content] could not reserve quarantine path for $content_dir" >&2
        return 1
    fi
    if mv -f -- "$content_dir" "$quarantine/"; then
        echo "[content] moved incomplete content aside: $quarantine/$(basename "$content_dir")" >&2
    else
        echo "[content] could not quarantine incomplete content at $content_dir" >&2
        return 1
    fi
}

# The download and the toolchain install do not depend on each other, and the container images that
# run this cap setup at a few minutes, so overlap them; the restore has to wait for the SDK.
toolchain_log="$log_dir/toolchain.log"
content_log="$log_dir/content.log"

# Godot rides in this branch rather than its own: it wants apt (which takes a lock the SDK install is
# already holding) and it finishes by building the Debug assembly, which needs the SDK anyway.
( install_dotnet && warm_nuget && install_godot ) > "$toolchain_log" 2>&1 &
toolchain_pid=$!
fetch_content > "$content_log" 2>&1 &
content_pid=$!

toolchain_status=0
content_status=0
wait "$toolchain_pid" || toolchain_status=$?
wait "$content_pid" || content_status=$?

cat "$toolchain_log"
cat "$content_log"

# Quarantine first, whatever else went wrong. The two halves fail independently, and when both do, an
# early exit on the toolchain would leave the partial tree exactly where UNTURNED_PATH resolves it —
# so the next session would discover half a map and run its data-backed tests against it.
quarantine_status=0
if [[ $content_status -ne 0 ]]; then
    quarantine_incomplete_content || quarantine_status=1
fi

if [[ $toolchain_status -ne 0 ]]; then
    echo "Setup failed: the .NET toolchain could not be installed (see $toolchain_log)." >&2
    if [[ $quarantine_status -ne 0 ]]; then
        echo "Incomplete content was also left at $content_dir." >&2
    fi
    exit "$toolchain_status"
fi

if [[ $quarantine_status -ne 0 ]]; then
    echo "Setup failed: incomplete content could not be quarantined." >&2
    exit 1
fi

# A missing download is worth reporting but not worth failing the session over: every test that reads
# real content self-skips, so the suite is still green, just smaller.
if [[ $content_status -ne 0 ]]; then
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
