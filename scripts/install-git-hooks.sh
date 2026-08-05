#!/usr/bin/env bash
# Point this checkout's hooks at the versioned ones in .githooks/.
#
# .git/hooks is not versioned and never can be, so a hook written there is a hook only its author runs.
# core.hooksPath moves the whole directory, which is the supported way to keep them in the repository
# where they get reviewed, updated and fixed like anything else.
#
# Usage:
#   ./scripts/install-git-hooks.sh            # install
#   ./scripts/install-git-hooks.sh --uninstall  # go back to .git/hooks
#
# This is opt-in per checkout, deliberately. Git will not run hooks from a fresh clone on its own — which
# is a security property worth keeping, since a hook is code a repository would otherwise get to run on
# your machine the moment you commit.
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_dir"

if [[ "${1:-}" == "--uninstall" ]]; then
    git config --unset core.hooksPath || true
    echo "Hooks uninstalled; git is back to .git/hooks."
    exit 0
fi

if [[ "${1:-}" != "" ]]; then
    echo "Unknown argument: $1" >&2
    exit 2
fi

chmod +x .githooks/*
git config core.hooksPath .githooks

echo "Hooks installed (core.hooksPath -> .githooks):"
echo "  pre-commit  format (staged files) + build with warnings as errors + the xUnit suite"
echo "  pre-push    the Godot runtime suite, skipped when the engine is not installed"
echo
echo "A C# commit costs roughly 35-40 s, most of it 'dotnet test' host startup. Escapes:"
echo "  SKIP_TESTS=1 git commit ...          format and build only"
echo "  SKIP_RUNTIME_TESTS=1 git push ...    skip the runtime suite"
echo "  git commit/push --no-verify ...      skip the hook entirely"
echo
echo "Undo with ./scripts/install-git-hooks.sh --uninstall."
