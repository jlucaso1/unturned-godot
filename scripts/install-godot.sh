#!/usr/bin/env bash
# Install the Godot 4.7 .NET editor binary, plus what it needs to render on a machine with no GPU.
#
# The xUnit suite does not need Godot; the runtime suite does (./scripts/run-godot-tests.sh, which calls
# this script for its binary). Beyond that, this is for actually running the game and, more usefully on a
# headless box, for the benchmark tiers in docs/PROFILING.md. On a container with no GPU (agent
# sandboxes, CI runners) Mesa's lavapipe provides a real Vulkan 1.4 device on the CPU and Xvfb provides
# the window the GPU tier wants, so the tiers run — see the caveats in docs/PROFILING.md about which
# of their metrics survive software rendering and which do not.
#
# Usage:
#   ./scripts/install-godot.sh              # Godot + software rendering
#   ./scripts/install-godot.sh --print-path # echo where the binary is (or will be)
set -euo pipefail

# The engine has to match what the PROJECT targets, not merely be close to it. unturned-godot.csproj
# targets Godot.NET.Sdk/4.7.1 and core/ pins GodotSharp 4.7.1; when an older editor opens the project to
# export it, it rewrites the csproj's SDK version DOWN to its own — a real edit, left in the working tree
# — and the restore then fails with NU1605 (GodotSharp downgrade 4.7.1 -> 4.7.0). The exporter does not
# treat that as fatal: it writes a .pck with no managed assemblies and EXITS ZERO, and the binary dies on
# launch with "ERROR: .NET: Assemblies not found". So this pin is part of the release build's
# correctness, not just of which editor a developer happens to get.
readonly GODOT_VERSION=4.7.1-stable
readonly GODOT_SHA256=6ca7ff0459f1b806900be683c1b0837c607a9c16834c530dc68c81b9fc3ae1f6

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
install_dir="$repo_dir/build/tools/godot-$GODOT_VERSION-mono"
binary="$install_dir/Godot_v${GODOT_VERSION}_mono_linux.x86_64"

if [[ "${1:-}" == "--print-path" ]]; then
    echo "$binary"
    exit 0
fi

if [[ "$(uname -s)/$(uname -m)" != "Linux/x86_64" ]]; then
    echo "This installer only covers Linux x86_64; install Godot 4.7 .NET yourself elsewhere." >&2
    exit 1
fi

as_root() {
    if [[ "$(id -u)" == "0" ]]; then "$@"; else sudo "$@"; fi
}

# --- Software rendering ------------------------------------------------------------------------
# mesa-vulkan-drivers carries lavapipe (the lvp ICD); xvfb is the virtual X server the windowed tier
# needs. Both are no-ops when a real GPU and display are already present, so this stays safe to run.
if ! ls /usr/share/vulkan/icd.d/lvp_icd*.json > /dev/null 2>&1 || ! command -v xvfb-run > /dev/null; then
    echo "[godot] Installing Mesa's software Vulkan driver and Xvfb..."
    export DEBIAN_FRONTEND=noninteractive
    as_root apt-get update -qq
    as_root apt-get install -y -qq mesa-vulkan-drivers xvfb
fi

# --- Godot -------------------------------------------------------------------------------------
if [[ -x "$binary" ]]; then
    echo "[godot] Already installed: $("$binary" --version 2>/dev/null | tail -1)"
else
    echo "[godot] Fetching Godot $GODOT_VERSION (.NET)..."
    archive="$(mktemp "${TMPDIR:-/tmp}/godot.XXXXXX")"
    curl -fsSL -o "$archive" \
        "https://github.com/godotengine/godot-builds/releases/download/$GODOT_VERSION/Godot_v${GODOT_VERSION}_mono_linux_x86_64.zip"

    actual="$(sha256sum "$archive" 2>/dev/null || shasum -a 256 "$archive")"
    actual="${actual%% *}"
    if [[ "$actual" != "$GODOT_SHA256" ]]; then
        rm -f -- "$archive"
        echo "[godot] Checksum mismatch: expected $GODOT_SHA256, got $actual" >&2
        exit 1
    fi

    # The zip holds one directory; lift its contents so the binary path stays predictable.
    staging="$(mktemp -d "${TMPDIR:-/tmp}/godot-unpack.XXXXXX")"
    unzip -oq "$archive" -d "$staging"
    mkdir -p "$install_dir"
    cp -r "$staging"/*/. "$install_dir"/
    rm -rf -- "$archive" "$staging"

    chmod +x "$binary"
    echo "[godot] Installed $("$binary" --version 2>/dev/null | tail -1)"
fi

# Godot loads the project's C# assembly from .godot/mono/temp/bin/Debug, so a Release-only build leaves
# it unable to instantiate any script ("Cannot instantiate C# script because the associated class could
# not be found"). Build Debug here so the engine is usable the moment this script finishes.
echo "[godot] Building the Debug assembly Godot loads..."
dotnet build "$repo_dir/unturned-godot.sln" -c Debug --nologo -v quiet

echo
echo "Godot ready:"
echo "  export GODOT=\"$binary\""
echo "Run the benchmark tiers with ./scripts/run-benchmark.sh (see docs/PROFILING.md)."
