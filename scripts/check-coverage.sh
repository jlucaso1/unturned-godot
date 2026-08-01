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
