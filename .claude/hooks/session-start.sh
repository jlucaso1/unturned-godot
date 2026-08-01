#!/usr/bin/env bash
# Gives a Claude Code cloud session a working toolchain and the game content the data-backed tests
# read, then exports UNTURNED_PATH so every later Bash command in the session finds it.
#
# Local sessions are left alone: your own machine already has its SDK, and it very likely has a real
# Steam install that UnturnedInstall finds on its own.
set -euo pipefail

if [[ "${CLAUDE_CODE_REMOTE:-}" != "true" ]]; then
    exit 0
fi

repo_dir="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
content_dir="${UNTURNED_PATH:-$repo_dir/build/game-data}"

# The cloud environment's setup script normally did all of this already, and its result is kept in
# the environment snapshot, so this is a no-op on every session after the first.
UNTURNED_PATH="$content_dir" bash "$repo_dir/scripts/setup-cloud-env.sh" || true

# Only point the session at content that is actually whole. UnturnedInstall.Find accepts whatever
# directory the override names, so exporting a half-finished download would make the data-backed tests
# run against it and fail, where leaving it unset makes them self-skip as designed.
if [[ -n "${CLAUDE_ENV_FILE:-}" ]] \
    && bash "$repo_dir/scripts/fetch-game-data.sh" --verify \
        --maps "${UNTURNED_SETUP_MAPS:-PEI}" --dir "$content_dir" 2>/dev/null; then
    echo "export UNTURNED_PATH=\"$content_dir\"" >> "$CLAUDE_ENV_FILE"
fi

exit 0
