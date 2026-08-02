#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
result_dir="$(mktemp -d "${TMPDIR:-/tmp}/unturned-godot-coverage.XXXXXX")"
trap 'rm -rf -- "$result_dir"' EXIT

dotnet test "$repo_dir/tests/UnturnedGodot.Tests.csproj" -c Release \
    --settings "$repo_dir/coverlet.runsettings" \
    --collect:"XPlat Code Coverage" \
    --results-directory "$result_dir" "$@"

coverage_file="$(find "$result_dir" -name coverage.cobertura.xml -type f -print -quit)"
if [[ -z "$coverage_file" ]]; then
    echo "Coverage gate failed: coverage.cobertura.xml was not produced." >&2
    exit 1
fi

read -r line_rate branch_rate < <(
    sed -n 's/^<coverage line-rate="\([^"]*\)" branch-rate="\([^"]*\)".*/\1 \2/p' \
        "$coverage_file"
)

if [[ -z "${line_rate:-}" || -z "${branch_rate:-}" ]]; then
    echo "Coverage gate failed: could not read the aggregate rates." >&2
    exit 1
fi

awk -v lines="$line_rate" -v branches="$branch_rate" 'BEGIN {
    printf "Core coverage: %.2f%% lines, %.2f%% branches\n", lines * 100, branches * 100
    if (lines <= 0.95 || branches <= 0.95) {
        print "Coverage gate failed: line and branch coverage must both be greater than 95%." > "/dev/stderr"
        exit 1
    }
}'

# The aggregate alone cannot see a single badly covered file. core/ is ~13k covered lines, so a new
# 300-line file with no tests moves the total by well under a point and the gate stays green while the
# thing nobody tested ships. The per-file floor is what notices that.
#
# It sits well below the aggregate on purpose. Measured across all 120 files when this was added, the
# worst was 83.8% lines and 73.2% branches, so these thresholds pass with room and will not fail on
# ordinary churn — they are a floor against a file arriving untested, not a second 95% bar.
python3 - "$coverage_file" <<'PY'
import collections
import sys
import xml.etree.ElementTree as ET

MIN_LINES = 0.80
MIN_BRANCHES = 0.70

# Small files are excluded: one uncovered line out of six is 83%, which says nothing about whether the
# file is tested, and a floor that fires on it would train people to ignore the floor.
MIN_LINES_TO_JUDGE = 25

per_file = collections.defaultdict(lambda: [0, 0, 0, 0])  # hit, total, branches covered, branches total
for cls in ET.parse(sys.argv[1]).getroot().iter("class"):
    entry = per_file[cls.get("filename", "")]
    for line in cls.iter("line"):
        entry[1] += 1
        if int(line.get("hits", "0")) > 0:
            entry[0] += 1
        condition = line.get("condition-coverage")
        if condition and "(" in condition:
            covered, total = condition.split("(")[1].rstrip(")").split("/")
            entry[2] += int(covered)
            entry[3] += int(total)

failures = []
for name, (hit, total, covered_branches, total_branches) in sorted(per_file.items()):
    if total < MIN_LINES_TO_JUDGE:
        continue
    line_rate = hit / total
    branch_rate = covered_branches / total_branches if total_branches else 1.0
    if line_rate < MIN_LINES or branch_rate < MIN_BRANCHES:
        failures.append(f"  {name}: {line_rate * 100:.1f}% lines, {branch_rate * 100:.1f}% branches "
                        f"({total} lines)")

if failures:
    print(f"Coverage gate failed: {len(failures)} file(s) below the per-file floor "
          f"({MIN_LINES * 100:.0f}% lines, {MIN_BRANCHES * 100:.0f}% branches):", file=sys.stderr)
    print("\n".join(failures), file=sys.stderr)
    raise SystemExit(1)

judged = sum(1 for _, total, _, _ in per_file.values() if total >= MIN_LINES_TO_JUDGE)
print(f"Per-file floor: {judged} files at or above "
      f"{MIN_LINES * 100:.0f}% lines / {MIN_BRANCHES * 100:.0f}% branches.")
PY
