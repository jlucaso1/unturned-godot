#!/usr/bin/env bash
# Run the tests that need a live Godot engine (tests/Runtime).
#
# These are NOT the project's test suite. `dotnet test tests/UnturnedGodot.Tests.csproj` is, and it stays
# the place for everything that can be answered without an engine: it references core/ alone, runs its
# collections in parallel and finishes in seconds. What it structurally cannot reach is a Node — lifecycle,
# notification order, what the engine does to a node's processing state behind the script's back. That is
# behaviour with rules of its own, and it has already produced a shipped bug (see
# tests/Runtime/CharacterSkeletonLifecycleTests.cs). This runs that half.
#
# Usage:
#   ./scripts/run-godot-tests.sh                    # every runtime test
#   ./scripts/run-godot-tests.sh --run-tests=Name   # one suite, by test class name
#   ./scripts/run-godot-tests.sh --stop-on-error    # any other GoDotTest flag passes straight through
#
#   GODOT=/path/to/godot ./scripts/run-godot-tests.sh   # a binary other than install-godot.sh's
#
# Headless: these tests build nodes and step frames, and want no window or GPU. Exit status is the suite's,
# so this is directly usable as a CI gate.
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
godot="${GODOT:-$("$repo_dir/scripts/install-godot.sh" --print-path)}"

if [[ ! -x "$godot" ]]; then
    echo "Godot not found at $godot; run ./scripts/install-godot.sh first (or set GODOT)." >&2
    exit 1
fi

# Godot loads the project's C# assembly from the Debug output, and tests/Runtime compiles in Debug alone,
# so a stale or Release-only build is the difference between running the suite and the engine reporting it
# cannot instantiate the runner script.
dotnet build "$repo_dir/unturned-godot.sln" -c Debug --nologo -v quiet

# The flags go BEFORE the engine's `--` separator on purpose: GoDotTest reads OS.GetCmdlineArgs(), which is
# the engine's own argument list, not the OS.GetCmdlineUserArgs() the game's --benchmark uses.
exec "$godot" --headless --audio-driver Dummy --path "$repo_dir" \
    res://tests/Runtime/RuntimeTests.tscn --run-tests --quit-on-finish "$@"
