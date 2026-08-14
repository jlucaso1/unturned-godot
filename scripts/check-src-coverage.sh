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
#   ./scripts/check-src-coverage.sh --ratchet      # fail if it dropped below the recorded reference
#   ./scripts/check-src-coverage.sh --record       # write the current numbers as the new reference
#   ./scripts/check-src-coverage.sh --files        # per-file breakdown, worst first
#   ./scripts/check-src-coverage.sh --with-game-run  # also measure real game sessions and merge them
#   ./scripts/check-src-coverage.sh --with-gpu-run   # ...including the ones that need a display
#
# --ratchet is the gate, and it is deliberately NOT --min.
#
# The reasoning against a fixed floor is sound and is written above: src/ is being brought under
# measurement file by file, and opting a large uncovered file in drops the rate before the tests for it
# land — so a percentage bar would punish exactly the move it exists to encourage. But the consequence
# was that src/ could regress by any amount with no signal at all, which is the other failure.
#
# A ratchet rewards the migration instead of punishing it. It compares against a reference committed to
# the repo (src-coverage-reference.json) and fails only when the number falls more than a tolerance
# below it, so:
#
#   * covering more of src/ raises the reference on the next --record and the bar rises with it;
#   * opting a big uncovered file IN lowers the rate, which is expected — and is a deliberate,
#     reviewable edit to the reference file rather than a gate to argue with;
#   * deleting tests, or letting a covered path rot, drops the rate against a reference nobody
#     changed, and that fails.
#
# The opted-out line count is ratcheted too, in the other direction: it may fall but never rise. That
# is what stops the headline percentage from being gamed by adding [ExcludeFromCodeCoverage] to
# whatever is dragging it down — the move that makes both numbers look better while measuring less.
#
# --with-game-run exists because some of src/ cannot be reached from a test at all. Main.cs is the entry
# point: it loads a map, builds a world and then ENDS THE PROCESS, so a test that called it would end the
# suite. The benchmark tiers do the same to report their exit status. Those files are not untestable
# because nobody wrote tests — they are unreachable from inside a test run, and the only honest way to
# measure them is to run the game and measure THAT.
#
# It needs the game's content, so it is opt-in rather than default.
#
# --with-gpu-run adds the runs that need a real rendering driver: the screenshot family and the GPU
# benchmark tier. Headless is not merely slower for these — the display server is literally named
# "headless" and the loader leaves before any capture code runs, so measuring them there records the same
# lines the plain loader already did. gamescope's headless backend supplies a driver without putting a
# window on anyone's screen, which is how this repo renders anyway (see scripts/perf-screenshots.sh).
# Implies --with-game-run.
#
#   GODOT=/path/to/godot ./scripts/check-src-coverage.sh
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
min=""
show_files=0
with_game_run=0
with_gpu_run=0
ratchet=0
record=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --min) min="$2"; shift 2 ;;
        --files) show_files=1; shift ;;
        --ratchet) ratchet=1; shift ;;
        --record) record=1; shift ;;
        --with-game-run) with_game_run=1; shift ;;
        --with-gpu-run) with_game_run=1; with_gpu_run=1; shift ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done

# The reference is the HERMETIC measurement — the suite alone, no game content, no extra sessions —
# because that is the one a pull request can reproduce. --with-game-run merges real sessions on top and
# necessarily reports a different (higher) number, so ratcheting the two against one reference would
# compare two different measurements and fail whichever ran second.
if (( (ratchet || record) && with_game_run )); then
    echo "--ratchet/--record measure the hermetic suite; they cannot be combined with --with-game-run." >&2
    exit 2
fi

reference_file="$repo_dir/scripts/src-coverage-reference.json"

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
# UG_COVERAGE_KEEP=1 leaves the working directory behind, which is the only way to read the per-run logs
# when a merged number comes out wrong: every run's stdout lands there and nothing else records it.
if [[ -n "${UG_COVERAGE_KEEP:-}" ]]; then
    echo "keeping $result_dir" >&2
else
    trap 'rm -rf -- "$result_dir"' EXIT
fi
report="$result_dir/src-coverage.cobertura.xml"

# --include-test-assembly: coverlet treats the assembly it is pointed at as the test assembly and would
#   otherwise leave the very thing being measured uninstrumented.
# --coverage (GoDotTest's flag, not coverlet's): the engine tears the process down without running the
#   managed shutdown hook that flushes coverlet's hit counts, so without it every module reports 0%.
# The suite's own pass. With a game run to follow it this emits json so the second pass can merge into it;
# on its own it emits the cobertura the reader below reads.
suite_format="cobertura"
suite_output="$report"
if (( with_game_run )); then
    suite_format="json"
    suite_output="$result_dir/suite.json"
fi

dotnet coverlet "$assembly" \
    --target "$godot" \
    --targetargs "--headless --audio-driver Dummy --path $repo_dir res://tests/Runtime/RuntimeTests.tscn --run-tests --quit-on-finish --coverage" \
    --format "$suite_format" --output "$suite_output" --include-test-assembly > "$result_dir/run.log" 2>&1 \
    || { echo "The runtime suite failed; coverage was not measured:" >&2; tail -40 "$result_dir/run.log" >&2; exit 1; }

if (( with_game_run )); then
    # Every automation flag this project reads, cleared for every run below.
    #
    # One list, expanded everywhere, because the hazard is uniform: an exported flag redirects a run into
    # a mode it was never measuring, and the redirected run still writes coverage — so it is accepted.
    # An exported BOT_JOIN turns the tier-1 run into the scripted bot client; an exported
    # UG_HEADLESS_INTERACTIVE turns the loader run into a session with nothing to end it. Each run then
    # sets only the flags it means, after this list has taken the caller's out.
    clear_modes=(
        UG_HEADLESS_INTERACTIVE= SOLO= FREECAM= OPEN_LAN= OPEN_LAN_AFTER= JOIN= MAP=
        BOT_JOIN= BOT_SECONDS= BOT_NAME= STEP_PROBE= UG_RUNTIME_BENCH_SECS= QUIT_AFTER=
        SCREENSHOT_PATH= MENU_SHOT= REPRO_AUTO= REPRO_CAPTURE_AT=
    )

    # How long any one run may take before it is treated as wedged.
    #
    # QUIT_AFTER only bounds a run that reaches the session; a failed join or a world build that gives up
    # returns before that timer is ever created. Without this the only backstop is the workflow's own
    # job timeout, which kills everything and throws away the log tail these guards print.
    run_limit="${UG_COVERAGE_RUN_TIMEOUT:-600}"

    # Run one measured pass and merge it onto the previous one. Fails loudly if the process wedged, and
    # checks the REPORT rather than the exit status: several of these modes are supposed to exit nonzero.
    measure() {
        local name="$1" previous="$2" output="$3" format="$4"
        shift 4
        timeout --kill-after=30s "$run_limit" env UG_COVERAGE=1 "${clear_modes[@]}" "$@" \
            dotnet coverlet "$assembly" \
                --target "$godot" \
                --targetargs "--headless --audio-driver Dummy --path $repo_dir" \
                --merge-with "$previous" \
                --format "$format" --output "$output" --include-test-assembly \
                > "$result_dir/$name.log" 2>&1 || true

        if [[ ! -s "$output" ]]; then
            echo "The $name run produced no coverage at all (wedged, crashed, or left through the" >&2
            echo "engine rather than the runtime):" >&2
            tail -40 "$result_dir/$name.log" >&2
            exit 1
        fi
    }

    # One real session. UG_HEADLESS_INTERACTIVE runs the ordinary interactive path — player, session,
    # zombies, streaming — with no display driver, and QUIT_AFTER leaves through AppShutdown, the single
    # way out of a loaded world. The screenshot mode would be quicker and is deliberately NOT used: it
    # quits straight out of the loader, so it never runs the session it is supposed to measure.
    measure session "$result_dir/suite.json" "$result_dir/session.json" json \
        UG_HEADLESS_INTERACTIVE=1 SOLO=1 QUIT_AFTER=45

    # Tier 1, which is a whole program of its own: it builds the world synchronously, measures it, writes
    # a report and leaves. Nothing about it is reachable from a session, because it never starts one.
    measure_with_args() {
        local name="$1" previous="$2" output="$3" format="$4" args="$5"
        shift 5
        timeout --kill-after=30s "$run_limit" env UG_COVERAGE=1 "${clear_modes[@]}" "$@" \
            dotnet coverlet "$assembly" \
                --target "$godot" \
                --targetargs "--headless --audio-driver Dummy --path $repo_dir -- $args" \
                --merge-with "$previous" \
                --format "$format" --output "$output" --include-test-assembly \
                > "$result_dir/$name.log" 2>&1 || true

        if [[ ! -s "$output" ]]; then
            echo "The $name run produced no coverage at all:" >&2
            tail -40 "$result_dir/$name.log" >&2
            exit 1
        fi
    }

    measure_with_args tier1 "$result_dir/session.json" "$result_dir/tier1.json" json --benchmark

    # The runs that need a rendering driver. gamescope's headless backend supplies one without a window.
    #
    # The exit backtrace these print is expected and harmless: leaving through the runtime (UG_COVERAGE)
    # tears the process down while the GPU resources are still live, so the engine's native teardown
    # complains on the way out. The screenshot lands and the hit counts are written before any of that —
    # both verified — and this mode exists only to measure.
    gpu_previous="$result_dir/tier1.json"
    if (( with_gpu_run )); then
        if ! command -v gamescope > /dev/null; then
            echo "--with-gpu-run needs gamescope (its headless backend supplies a driver without a window)." >&2
            exit 2
        fi

        measure_rendered() {
            local name="$1" previous="$2" output="$3" format="$4" args="$5"
            shift 5
            timeout --kill-after=30s "$run_limit" gamescope --backend headless -- \
                env UG_COVERAGE=1 "${clear_modes[@]}" "$@" \
                    dotnet coverlet "$assembly" \
                        --target "$godot" \
                        --targetargs "--audio-driver Dummy --path $repo_dir $args" \
                        --merge-with "$previous" \
                        --format "$format" --output "$output" --include-test-assembly \
                        > "$result_dir/$name.log" 2>&1 || true

            # gamescope forwards the child's status on a clean shutdown and does not on a hung one, so
            # the report is what is checked here too.
            if [[ ! -s "$output" ]]; then
                echo "The $name rendered run produced no coverage at all:" >&2
                tail -40 "$result_dir/$name.log" >&2
                exit 1
            fi
        }

        measure_rendered shot "$gpu_previous" "$result_dir/gpu1.json" json "" \
            SCREENSHOT_PATH="$result_dir/shot.png"
        measure_rendered player "$result_dir/gpu1.json" "$result_dir/gpu2.json" json "" \
            SCREENSHOT_PATH="$result_dir/player.png" PLAYER=1

        # Tier 2, which is the only caller GpuBenchmark has: real frames from authored camera poses.
        measure_rendered tier2 "$result_dir/gpu2.json" "$result_dir/tier2.json" json "-- --benchmark --gpu"
        gpu_previous="$result_dir/tier2.json"
    fi

    # The loader's own early exit: it reads the level, builds the world and leaves without ever starting
    # a session. Nothing else reaches it, because every other run here goes on to be a session.
    #
    # The SCREENSHOT family that sits behind the same entry point is NOT driven, and cannot be from here:
    # with --headless the display server is named "headless", and the loader takes this early-exit branch
    # before any capture code is reached. Verified rather than assumed — running it with SCREENSHOT_PATH,
    # PLAYER=1 and FREECAM=1 produced three identical measurements. Capturing needs a display, which is
    # what --with-gpu-run supplies.
    measure loader "$gpu_previous" "$result_dir/loader.json" json

    # The session's other shapes. Each is an automation mode that exists because a human at a keyboard
    # cannot be part of a verification run, and each takes a branch of the world build nothing else does:
    # opening the port a second player joins through, attempting a join that has nowhere to land, walking
    # a player-shaped body at a sill to answer "can the player get over that", and the scripted client
    # that plays the other side of a multiplayer check.
    #
    # They are short on purpose. What is being measured is that the code runs, not what it concluded —
    # and a nonzero exit is expected from several of them, which is why `measure` checks the report.
    measure lan "$result_dir/loader.json" "$result_dir/mode1.json" json \
        UG_HEADLESS_INTERACTIVE=1 SOLO=1 OPEN_LAN=1 QUIT_AFTER=20
    measure join "$result_dir/mode1.json" "$result_dir/mode2.json" json \
        UG_HEADLESS_INTERACTIVE=1 JOIN=127.0.0.1:27099 QUIT_AFTER=20
    measure step "$result_dir/mode2.json" "$result_dir/mode3.json" json \
        UG_HEADLESS_INTERACTIVE=1 SOLO=1 STEP_PROBE="0,40,0>4,40,0" QUIT_AFTER=25
    measure bot "$result_dir/mode3.json" "$result_dir/mode4.json" json \
        UG_HEADLESS_INTERACTIVE=1 BOT_JOIN=127.0.0.1:27099 BOT_SECONDS=5

    # Tier 3, the runtime tier: a real session measured over a few seconds of frames, which is the only
    # caller RuntimeBenchmark has. Kept short — what is being measured here is that the code runs, not
    # what it measured. This one emits the cobertura the reader below reads.
    measure tier3 "$result_dir/mode4.json" "$report" cobertura \
        UG_HEADLESS_INTERACTIVE=1 SOLO=1 UG_RUNTIME_BENCH_SECS=5
fi

[[ -s "$report" ]] || { echo "coverlet produced no report:" >&2; tail -20 "$result_dir/run.log" >&2; exit 1; }

SRC_COVERAGE_MIN="$min" SRC_COVERAGE_FILES="$show_files" \
SRC_COVERAGE_RATCHET="$ratchet" SRC_COVERAGE_RECORD="$record" \
SRC_COVERAGE_REFERENCE="$reference_file" \
python3 - "$report" "$repo_dir" <<'PY'
import collections
import json
import os
import sys
import xml.etree.ElementTree as ET

report, repo_dir = sys.argv[1], sys.argv[2]
minimum = os.environ.get("SRC_COVERAGE_MIN") or ""
show_files = os.environ.get("SRC_COVERAGE_FILES") == "1"
ratchet = os.environ.get("SRC_COVERAGE_RATCHET") == "1"
record = os.environ.get("SRC_COVERAGE_RECORD") == "1"
reference_file = os.environ.get("SRC_COVERAGE_REFERENCE", "")

# How far the rate may fall below the reference before this fails.
#
# Not zero. The figure moves a little on its own — a run that races differently, a line the JIT
# reaches on one machine and not another — and a gate that fires on noise is a gate people learn to
# re-run rather than read. A point and a half is comfortably above that and far below any real
# regression: deleting one covered file's tests moves this by much more.
TOLERANCE_POINTS = 1.5

# The Godot source generators emit a partial class per script (property/method dispatch tables). That code
# is not hand-written and no one can meaningfully test it, so it is not part of the target — the same
# reasoning coverlet.runsettings already applies to generated code in core/.
def is_generated(name):
    return "Godot.SourceGenerators" in name or "/obj/" in name

# The tests themselves compile into this same assembly (tests/Runtime, Debug only), so instrumenting it
# instruments them too. A test file runs end to end by definition and scores ~100%, which would pad the
# denominator with exactly the code that cannot be evidence of anything: adding a test file would raise
# "src/ coverage" on its own, before it asserted a thing. Only src/ counts.
def is_target(name):
    return name.startswith("src/")

per_file = collections.defaultdict(lambda: [0, 0, 0, 0])
for pkg in ET.parse(report).getroot().iter("package"):
    if pkg.get("name") != "unturned-godot":
        continue
    for cls in pkg.iter("class"):
        name = cls.get("filename", "")
        if is_generated(name) or not is_target(name):
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

if record:
    with open(reference_file, "w") as handle:
        json.dump(
            {
                "_comment": [
                    "Reference point for scripts/check-src-coverage.sh --ratchet, which is what keeps",
                    "src/ from regressing silently. Regenerate with --record and COMMIT the result:",
                    "raising it is how covering more of src/ raises the bar, and lowering it is a",
                    "deliberate, reviewable statement that a large uncovered file was just opted in.",
                    "Measured hermetically (the runtime suite alone, no game content), so a pull",
                    "request can reproduce it.",
                ],
                "lineRate": round(line_rate, 2),
                "branchRate": round(branch_rate, 2),
                "optedOutLines": out_loc,
                "optedOutFiles": len(opted_out),
            },
            handle,
            indent=2,
        )
        handle.write("\n")
    print(f"Recorded {line_rate:.2f}% lines / {branch_rate:.2f}% branches and {out_loc:,} opted-out "
          f"lines to {os.path.relpath(reference_file, repo_dir)}.")

if ratchet:
    try:
        with open(reference_file) as handle:
            reference = json.load(handle)
    except FileNotFoundError:
        print(f"Coverage ratchet failed: no reference at {reference_file}. Create it with "
              f"./scripts/check-src-coverage.sh --record and commit it.", file=sys.stderr)
        raise SystemExit(1)

    was_lines = float(reference["lineRate"])
    was_opted_out = int(reference["optedOutLines"])
    floor = was_lines - TOLERANCE_POINTS
    failed = False

    if line_rate < floor:
        print(f"Coverage ratchet failed: src/ line coverage is {line_rate:.2f}%, more than "
              f"{TOLERANCE_POINTS} points below the recorded {was_lines:.2f}%.", file=sys.stderr)
        print("  If this is a large uncovered file being opted IN, that is the move this ratchet exists",
              file=sys.stderr)
        print("  to allow: re-record with ./scripts/check-src-coverage.sh --record and commit the",
              file=sys.stderr)
        print("  change, so the drop is stated rather than silent.", file=sys.stderr)
        failed = True

    # The other direction, and the reason the headline number cannot be gamed: hiding a badly covered
    # file behind [ExcludeFromCodeCoverage] RAISES the percentage while measuring less of src/.
    if out_loc > was_opted_out:
        print(f"Coverage ratchet failed: src/ now excludes {out_loc:,} lines from measurement, up from "
              f"{was_opted_out:,}. Opting code OUT is the one direction this may not move.",
              file=sys.stderr)
        failed = True

    if failed:
        raise SystemExit(1)

    print(f"Ratchet passed: {line_rate:.2f}% lines against a recorded {was_lines:.2f}% "
          f"(floor {floor:.2f}%), {out_loc:,} opted-out lines against {was_opted_out:,}.")
    if line_rate > was_lines + TOLERANCE_POINTS or out_loc < was_opted_out:
        print("  This is comfortably ahead of the reference — re-record it "
              "(./scripts/check-src-coverage.sh --record) so the bar keeps up.")
PY
