#!/usr/bin/env bash
# Publish a before/after screenshot set to the `perf-screenshots` branch and print the Markdown that
# embeds it in a pull request body.
#
# A PR that claims "no visual change" has to show it, and GitHub only renders images it can fetch over
# HTTP. Committing PNGs onto the working branch would put binaries into the history every reviewer
# clones, so they go onto one long-lived orphan branch instead: no code, no history shared with main,
# and nothing on it is ever merged.
#
# Usage:
#   ./scripts/publish-screenshots.sh <slug> <before-dir> <after-dir> [diff-dir]
#
#   ./scripts/perf-screenshots.sh before && git switch my-optimization && ...
#   ./scripts/perf-screenshots.sh after
#   ./scripts/publish-screenshots.sh foliage-chunk-cells build/screenshots/{before,after}
#
# The slug names the folder on the branch; use the working branch's own name so a later reader can tell
# which PR a folder belongs to. Re-running with the same slug replaces that folder's contents, so a
# corrected capture does not leave the old one embedded in the PR body.
#
# Environment:
#   UG_SHOT_BRANCH   the branch to publish to (default: perf-screenshots)
#   UG_SHOT_REMOTE   the remote to push to (default: origin)
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
branch="${UG_SHOT_BRANCH:-perf-screenshots}"
remote="${UG_SHOT_REMOTE:-origin}"

slug="${1:-}"
before_dir="${2:-}"
after_dir="${3:-}"
diff_dir="${4:-}"
if [[ -z "$slug" || -z "$before_dir" || -z "$after_dir" ]]; then
    sed -n '2,22p' "${BASH_SOURCE[0]}"
    exit 2
fi

# The slug becomes a path on a branch and a URL in a PR body. Keep it to what is safe in both rather
# than discovering the problem as a broken image in someone else's review.
if [[ ! "$slug" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
    echo "Slug '$slug' must be alphanumeric with . _ - (it becomes a path and a URL)." >&2
    exit 2
fi

for dir in "$before_dir" "$after_dir"; do
    [[ -d "$dir" ]] || { echo "No such directory: $dir" >&2; exit 1; }
    compgen -G "$dir/*.png" > /dev/null || { echo "No PNGs in $dir" >&2; exit 1; }
done

origin_url="$(git -C "$repo_dir" remote get-url "$remote")"
# Both forms in the wild: git@github.com:owner/repo.git and https://host/.../owner/repo(.git). Taking
# the last two path components covers each of them, and the SCP-style colon too, since the owner is
# still the directory the repo sits in.
stripped="${origin_url%.git}"
repo="${stripped##*/}"
owner="$(basename "$(dirname "$stripped")")"
owner="${owner##*:}"
if [[ -z "$owner" || -z "$repo" ]]; then
    echo "Could not read owner/repo out of '$origin_url'." >&2
    exit 1
fi

work="$(mktemp -d "${TMPDIR:-/tmp}/ug-screenshots.XXXXXX")"
cleanup() {
    git -C "$repo_dir" worktree remove --force "$work" 2> /dev/null || true
    rm -rf -- "$work"
}
trap cleanup EXIT

# The branch may not exist yet, and it must never share history with main: an orphan keeps the PNGs out
# of every normal clone's object walk. Fetch shallow when it does exist, so republishing does not drag
# down every screenshot ever taken.
if git -C "$repo_dir" ls-remote --exit-code --heads "$remote" "$branch" > /dev/null 2>&1; then
    git -C "$repo_dir" fetch --depth=1 "$remote" "$branch"
    rmdir "$work"
    git -C "$repo_dir" worktree add --force -B "$branch" "$work" "$remote/$branch"
else
    rmdir "$work"
    git -C "$repo_dir" worktree add --detach --force "$work" HEAD
    git -C "$work" checkout --orphan "$branch"
    git -C "$work" rm -rq --cached . 2> /dev/null || true
    find "$work" -mindepth 1 -maxdepth 1 -not -name .git -exec rm -rf {} +
    cat > "$work/README.md" << 'MD'
# perf-screenshots

Before/after captures referenced from pull request bodies. Nothing here is code and nothing here is
ever merged: it is an orphan branch so these PNGs stay out of the history a normal clone walks.

Written by `scripts/publish-screenshots.sh`; captured by `scripts/perf-screenshots.sh`; compared by
`scripts/compare-screenshots.py`. See `docs/agents/` for the protocol they belong to.
MD
fi

dest="$work/$slug"
rm -rf -- "$dest"
mkdir -p "$dest/before" "$dest/after"
cp "$before_dir"/*.png "$dest/before/"
cp "$after_dir"/*.png "$dest/after/"
if [[ -n "$diff_dir" && -d "$diff_dir" ]] && compgen -G "$diff_dir/*.png" > /dev/null; then
    mkdir -p "$dest/diff"
    cp "$diff_dir"/*.png "$dest/diff/"
fi

git -C "$work" add -A
if git -C "$work" diff --cached --quiet; then
    echo "[screenshots] $slug is already published unchanged."
else
    git -C "$work" -c user.name="${GIT_AUTHOR_NAME:-perf screenshots}" \
        -c user.email="${GIT_AUTHOR_EMAIL:-perf-screenshots@localhost}" \
        commit -q -m "Screenshots for $slug"
    # Retry the push: a proxied sandbox drops connections often enough that one failure is not news.
    delay=2
    for attempt in 1 2 3 4 5; do
        if git -C "$work" push -u "$remote" "$branch"; then
            break
        fi
        if [[ $attempt -eq 5 ]]; then
            echo "[screenshots] push failed after 5 attempts." >&2
            exit 1
        fi
        echo "[screenshots] push failed; retrying in ${delay}s..." >&2
        sleep "$delay"
        delay=$((delay * 2))
    done
fi

base="https://raw.githubusercontent.com/$owner/$repo/$branch/$slug"
echo
echo "--- paste into the PR body ---"
echo
echo "| View | Before | After |"
echo "|---|---|---|"
for shot in "$dest/before"/*.png; do
    name="$(basename "$shot" .png)"
    if [[ -f "$dest/after/$name.png" ]]; then
        echo "| \`$name\` | <img src=\"$base/before/$name.png\" width=\"400\"> | <img src=\"$base/after/$name.png\" width=\"400\"> |"
    fi
done
echo
if [[ -d "$dest/diff" ]]; then
    echo "Amplified difference images (16x per-channel delta; black means identical):"
    echo
    for shot in "$dest/diff"/*.png; do
        name="$(basename "$shot" .png)"
        echo "- [\`$name\`]($base/diff/$name.png)"
    done
    echo
fi
