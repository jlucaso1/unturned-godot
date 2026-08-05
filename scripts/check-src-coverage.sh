#!/usr/bin/env bash
# Measure how much of src/ — the game itself, the half that talks to the engine — the runtime suite covers.
#
# check-coverage.sh measures core/ and reports a number in the high nineties. That number is true and it is
# about one assembly of two: core/ is the pure half (parsers, maths, netcode, data), and src/ is everything
# that touches a Node. Until the runtime suite existed there was no way to execute src/ under test at all,
# so every file in it carried [ExcludeFromCodeCoverage] and the gate could not see it. This script is what
# makes that half visible, so it can be paid down file by file instead of being permanently invisible.
#
# Two numbers come out, and BOTH matter:
#
#   covered   — of the src/ lines that opted in (no [ExcludeFromCodeCoverage]), how many run under test
#   opted out — how much of src/ is still excluded, i.e. not in the denominator at all
#
# Reporting only the first would let src/ show 100% while one file opted in and seventy-one hid. The goal is
# a high first number with a ZERO second one.
#
# Usage:
#   ./scripts/check-src-coverage.sh                # measure and report
#   ./scripts/check-src-coverage.sh --min 40       # also fail below 40% of the opted-in lines
#   ./scripts/check-src-coverage.sh --files        # per-file breakdown, worst first
#
#   GODOT=/path/to/godot ./scripts/check-src-coverage.sh
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
min=""
show_files=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --min) min="$2"; shift 2 ;;
        --files) show_files=1; shift ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

godot="${GODOT:-$("$repo_dir/scripts/install-godot.sh" --print-path)}"
if [[ ! -x "$godot" ]]; then
    echo "Godot not found at $godot; run ./scripts/install-godot.sh first (or set GODOT)." >&2
    exit 1
fi

# Godot loads the Debug assembly, and tests/Runtime compiles in Debug alone.
dotnet build "$repo_dir/unturned-godot.sln" -c Debug --nologo -v quiet
dotnet tool restore --tool-manifest "$repo_dir/.config/dotnet-tools.json" > /dev/null

assembly="$repo_dir/.godot/mono/temp/bin/Debug/unturned-godot.dll"
[[ -f "$assembly" ]] || { echo "No Debug assembly at $assembly." >&2; exit 1; }

result_dir="$(mktemp -d "${TMPDIR:-/tmp}/unturned-godot-src-coverage.XXXXXX")"
trap 'rm -rf -- "$result_dir"' EXIT
report="$result_dir/src-coverage.cobertura.xml"

# --include-test-assembly: coverlet treats the assembly it is pointed at as the test assembly and would
#   otherwise leave the very thing being measured uninstrumented.
# --coverage (GoDotTest's flag, not coverlet's): the engine tears the process down without running the
#   managed shutdown hook that flushes coverlet's hit counts, so without it every module reports 0%.
dotnet coverlet "$assembly" \
    --target "$godot" \
    --targetargs "--headless --audio-driver Dummy --path $repo_dir res://tests/Runtime/RuntimeTests.tscn --run-tests --quit-on-finish --coverage" \
    --format cobertura --output "$report" --include-test-assembly > "$result_dir/run.log" 2>&1 \
    || { echo "The runtime suite failed; coverage was not measured:" >&2; tail -40 "$result_dir/run.log" >&2; exit 1; }

[[ -s "$report" ]] || { echo "coverlet produced no report:" >&2; tail -20 "$result_dir/run.log" >&2; exit 1; }

SRC_COVERAGE_MIN="$min" SRC_COVERAGE_FILES="$show_files" python3 - "$report" "$repo_dir" <<'PY'
import collections
import os
import sys
import xml.etree.ElementTree as ET

report, repo_dir = sys.argv[1], sys.argv[2]
minimum = os.environ.get("SRC_COVERAGE_MIN") or ""
show_files = os.environ.get("SRC_COVERAGE_FILES") == "1"

# The Godot source generators emit a partial class per script (property/method dispatch tables). That code
# is not hand-written and no one can meaningfully test it, so it is not part of the target — the same
# reasoning coverlet.runsettings already applies to generated code in core/.
def is_generated(name):
    return "Godot.SourceGenerators" in name or "/obj/" in name

per_file = collections.defaultdict(lambda: [0, 0, 0, 0])
for pkg in ET.parse(report).getroot().iter("package"):
    if pkg.get("name") != "unturned-godot":
        continue
    for cls in pkg.iter("class"):
        name = cls.get("filename", "")
        if is_generated(name):
            continue
        entry = per_file[name]
        for line in cls.iter("line"):
            entry[1] += 1
            if int(line.get("hits", "0")) > 0:
                entry[0] += 1
            condition = line.get("condition-coverage")
            if condition and "(" in condition:
                covered, total = condition.split("(")[1].rstrip(")").split("/")
                entry[2] += int(covered)
                entry[3] += int(total)

hit = sum(e[0] for e in per_file.values())
total = sum(e[1] for e in per_file.values())
branches_hit = sum(e[2] for e in per_file.values())
branches = sum(e[3] for e in per_file.values())

# What is still invisible. An opted-out file contributes nothing to the numbers above, so counting it here
# is the only thing that stops "95% of src/" from meaning "95% of the one file that opted in".
opted_out = []
for root, _, files in os.walk(os.path.join(repo_dir, "src")):
    for f in sorted(files):
        if not f.endswith(".cs"):
            continue
        path = os.path.join(root, f)
        with open(path, errors="ignore") as handle:
            body = handle.read()
        if "ExcludeFromCodeCoverage" in body:
            loc = sum(1 for line in body.splitlines()
                      if line.strip() and not line.strip().startswith("//"))
            opted_out.append((os.path.relpath(path, repo_dir), loc))

if show_files and per_file:
    print("Per-file (opted in), worst first:")
    rows = sorted((h / t, h, t, n) for n, (h, t, _, _) in per_file.items() if t)
    for rate, h, t, name in rows:
        print(f"  {rate * 100:6.1f}%  {h:5}/{t:<6} {name}")
    print()

line_rate = hit / total * 100 if total else 0.0
branch_rate = branches_hit / branches * 100 if branches else 0.0
print(f"src/ coverage (opted in): {line_rate:.2f}% lines, {branch_rate:.2f}% branches "
      f"({hit:,}/{total:,} lines across {len(per_file)} files)")

out_loc = sum(loc for _, loc in opted_out)
if opted_out:
    print(f"src/ still opted OUT of measurement: {len(opted_out)} files, ~{out_loc:,} code lines "
          f"(carrying [ExcludeFromCodeCoverage], so none of it is in the figure above)")
else:
    print("src/ opted out of measurement: none — every file is in the figure above.")

if minimum:
    floor = float(minimum)
    if line_rate < floor:
        print(f"Coverage gate failed: {line_rate:.2f}% of opted-in src/ lines is below the {floor:.2f}% floor.",
              file=sys.stderr)
        raise SystemExit(1)
    print(f"Gate passed: {line_rate:.2f}% >= {floor:.2f}%.")
PY
