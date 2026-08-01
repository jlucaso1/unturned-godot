#!/usr/bin/env bash
# Gate the shape of the built world against a recorded reference.
#
# The benchmark tiers mix two kinds of number. Wall-clock timings (`*.ms`, `gpu.frameMs.*`) depend on the
# machine and cannot be gated: on a shared CI runner they drift by double digits and mean nothing. The
# rest — how many meshes and materials survive deduplication, how many nodes the scene ends up with, how
# much geometry is uploaded — are counts produced by parsing and building, identical on any machine.
# Measured on this project: across two Tier 1 runs, 13 of 16 metrics reproduced exactly and only the
# three timings moved; across two Tier 2 runs under software rendering, 49 of 67 did, again with only
# the timings moving.
#
# So this gates the counts and ignores the clock. It catches what no test here covers: a batching,
# culling or deduplication change that silently alters the render graph.
#
# Usage:
#   ./scripts/check-structural-metrics.sh                 # run Tier 1 and compare
#   ./scripts/check-structural-metrics.sh --write          # record the current numbers as the reference
#
# Environment: MAP (default PEI), GODOT and UNTURNED_PATH as for run-benchmark.sh.
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
map="${MAP:-PEI}"
reference="$repo_dir/bench/structural/$map.json"
write=0

[[ "${1:-}" == "--write" ]] && write=1

# Godot writes the report under user://, and where that lands depends on the host: Linux uses
# $XDG_DATA_HOME, macOS ~/Library/Application Support, Windows %APPDATA%. Same three candidates
# tools/PerfHarness/Program.cs already walks. Getting this wrong does not misreport a number — it
# reports that the benchmark wrote nothing, on a run that succeeded.
case "$(uname -s)" in
    Darwin) user_dir="$HOME/Library/Application Support/Godot/app_userdata/unturned-godot" ;;
    MINGW*|MSYS*|CYGWIN*)
        appdata="${APPDATA:-$HOME/AppData/Roaming}"
        # Git Bash hands %APPDATA% back in Windows form; the shell needs the POSIX path.
        command -v cygpath > /dev/null && appdata="$(cygpath -u "$appdata")"
        user_dir="$appdata/Godot/app_userdata/unturned-godot"
        ;;
    *) user_dir="${XDG_DATA_HOME:-$HOME/.local/share}/godot/app_userdata/unturned-godot" ;;
esac
report="$user_dir/bench/$map-latest.json"

rm -f "$report"
MAP="$map" "$repo_dir/scripts/run-benchmark.sh" structural

if [[ ! -f "$report" ]]; then
    echo "The benchmark did not write $report." >&2
    exit 1
fi

python3 - "$report" "$reference" "$write" << 'PY'
import json, os, re, sys

report_path, reference_path, write = sys.argv[1], sys.argv[2], sys.argv[3] == "1"

# Everything that is a stopwatch reading rather than a count. The two tiers spell milliseconds
# differently -- Tier 1 has `build.total.ms`, Tier 2 has `gpu.frameMs.median` and
# `cpu.processMonitorMs.median` -- so matching only the dotted suffix would quietly let every GPU-tier
# timing into the gate the moment anyone points this at Tier 2.
def is_timing(name):
    return re.search(r"(^|\.)[a-zA-Z]*[Mm]s(\.|$)", name) is not None

metrics = json.load(open(report_path))["metrics"]
structural = {k: v for k, v in sorted(metrics.items()) if not is_timing(k)}

if write:
    os.makedirs(os.path.dirname(reference_path), exist_ok=True)
    with open(reference_path, "w") as f:
        json.dump(structural, f, indent=2, sort_keys=True)
        f.write("\n")
    print(f"Recorded {len(structural)} structural metrics to {reference_path}")
    sys.exit(0)

if not os.path.exists(reference_path):
    print(f"No reference at {reference_path}; record one with --write.", file=sys.stderr)
    sys.exit(1)

reference = json.load(open(reference_path))

drifted = []
for key in sorted(set(reference) | set(structural)):
    want, got = reference.get(key), structural.get(key)
    if want != got:
        drifted.append((key, want, got))

width = max((len(k) for k in structural), default=10)
for key, value in structural.items():
    print(f"  {key:<{width}}  {value}")

if not drifted:
    print(f"\nAll {len(structural)} structural metrics match {os.path.basename(reference_path)}.")
    sys.exit(0)

print(f"\n{len(drifted)} structural metric(s) drifted from the reference:", file=sys.stderr)
for key, want, got in drifted:
    print(f"  {key}: expected {want}, got {got}", file=sys.stderr)
print("\nIf the change is intended, re-record with ./scripts/check-structural-metrics.sh --write",
      file=sys.stderr)
sys.exit(1)
PY
