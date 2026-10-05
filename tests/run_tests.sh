#!/usr/bin/env bash
# Runs the GUT test scripts under tests/ (helpers in tests/support aren't tests). Exits non-zero if any fails.
# Every test script declares its tags on one line: const TAGS = ["smoke", ...]. A script can add Godot options the same
# way: const GODOT_ARGS = ["--fixed-fps", "60"] runs every frame as one physics step of 1/60 s as fast as the machine
# goes, for long surgeries (code timed by the wall clock, like a sedated surgeon's input delay, then runs too slow).
# Usage: run_tests.sh [--all] [--tag TAG]... [--skip TAG]... [--case TEXT] [--with-key-frames] [--ci-run] [--list]
#                     [--jobs N]
#   Without --all or --tag it runs the smoke tag. Several --tag pick the scripts that have all of them, --skip leaves
#   out the scripts that have the tag.
#   --case TEXT runs only the test functions whose name contains TEXT.
#   --with-key-frames: visual_confirmation scripts save their key frames and check the frame budget (WITH_KEY_FRAMES=1,
#   see tests/support/key_frames.gd). They get a display (xvfb-run without one) and the compatibility renderer, and run
#   alone after the others. Without it they run headless like the rest, skipping the screenshots.
#   --ci-run: frame times are reported, not checked against the budget (CI_RUN=1, see tests/support/frame_budget.gd).
# GODOT must point at the Godot binary (./build.sh test sets it up). Logs and JUnit XML go to build/.
# ISOLATE_CASES = true gives each top-level case its own game process and JUnit report.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GODOT="${GODOT:?set GODOT to the Godot binary}"
LOGS="$ROOT/build/test-logs"
RESULTS="$ROOT/build/test-results"
mkdir -p "$LOGS" "$RESULTS"
# Screenshots land under build/ too: Godot doesn't import anything there.
touch "$ROOT/build/.gdignore"

all=0
list=0
case_filter=""
jobs=1
key_frames=0
tags=()
skips=()
while [[ $# -gt 0 ]]; do
	case "$1" in
		--all) all=1 ;;
		--tag) tags+=("$2"); shift ;;
		--tag=*) tags+=("${1#*=}") ;;
		--skip) skips+=("$2"); shift ;;
		--skip=*) skips+=("${1#*=}") ;;
		--case) case_filter="$2"; shift ;;
		--case=*) case_filter="${1#*=}" ;;
		--jobs) jobs="$2"; shift ;;
		--jobs=*) jobs="${1#*=}" ;;
		--with-key-frames) key_frames=1 ;;
		--ci-run) export CI_RUN=1 ;;
		--list) list=1 ;;
		*) echo "Unknown test option: $1" >&2; exit 2 ;;
	esac
	shift
done
if [[ $all -eq 0 && ${#tags[@]} -eq 0 ]]; then
	tags=(smoke)
fi

selected=()
while IFS= read -r file; do
	line="$(grep -m1 -E '^const TAGS' "$file" || true)"
	if [[ -z "$line" ]]; then
		echo "Test has no TAGS: ${file#"$ROOT/"}" >&2
		exit 2
	fi
	if [[ -n "$case_filter" ]] && ! grep -qE "^func test_[a-z0-9_]*${case_filter}" "$file"; then
		continue
	fi
	matched=1
	for tag in "${tags[@]}"; do
		[[ "$line" == *"\"$tag\""* ]] || matched=0
	done
	for tag in "${skips[@]}"; do
		[[ "$line" != *"\"$tag\""* ]] || matched=0
	done
	if [[ $matched -eq 1 ]]; then
		selected+=("res://${file#"$ROOT/"}")
	fi
done < <(find "$ROOT/tests" -type f -name 'test_*.gd' -not -path '*/support/*' | sort)

if [[ $list -eq 1 ]]; then
	printf '%s\n' "${selected[@]}"
	exit 0
fi
if [[ ${#selected[@]} -eq 0 ]]; then
	echo "No tests match tags '${tags[*]}' and case '$case_filter'." >&2
	exit 2
fi

log_name() {
	local name="${1#res://tests/}"
	name="${name%.gd}"
	echo "${name//\//_}"
}

# Godot keeps going after script errors, so the log decides too: a test passes only if GUT passed it, it ran at
# least one case and its log has no errors. Engine leak reports printed while quitting are noise, not failures.
ERRORS='SCRIPT ERROR|Parse Error|ERROR:|\[ERROR\]'
NOISE='at exit|leaked'

run_one() {
	local script=$1
	local name
	name="$(log_name "$script")"
	local log="$LOGS/$name.log"
	local xml="$RESULTS/$name.xml"
	rm -f "$xml" "$LOGS/$name.failed"
	local source="$ROOT/${script#res://}"
	local cmd=("$GODOT" --path "$ROOT")
	local extra
	extra="$(grep -m1 -E '^const GODOT_ARGS' "$source" || true)"
	if [[ -n "$extra" ]]; then
		mapfile -t extra_args < <(grep -oE '"[^"]*"' <<<"$extra" | tr -d '"')
		cmd+=("${extra_args[@]}")
	fi
	if [[ $key_frames -eq 1 ]] && grep -m1 -E '^const TAGS' "$source" | grep -q '"visual_confirmation"'; then
		cmd=(env WITH_KEY_FRAMES=1 "${cmd[@]}" --rendering-method gl_compatibility --audio-driver Dummy --resolution 1280x720)
		if [[ -z "${DISPLAY:-}" ]]; then
			if ! command -v xvfb-run >/dev/null; then
				echo "Visual tests need xvfb-run or a DISPLAY." >"$log"
				touch "$LOGS/$name.failed"
				return
			fi
			cmd=(xvfb-run -a -s "-screen 0 1280x720x24" "${cmd[@]}")
		fi
	else
		cmd+=(--headless)
	fi
	cmd+=(-s addons/gut/gut_cmdln.gd -gtest="$script" -gexit -gdisable_colors -glog=1)
	if grep -qE '^const ISOLATE_CASES = true$' "$source"; then
		: >"$log"
		local case_name case_log case_xml count=0
		while IFS= read -r case_name; do
			[[ -z "$case_filter" || "$case_name" == *"$case_filter"* ]] || continue
			count=$((count + 1))
			case_log="$LOGS/${name}__${case_name}.log"
			case_xml="$RESULTS/${name}__${case_name}.xml"
			local case_cmd=(env GUT_EXACT_CASE="$case_name" SURGERY_TRACE=1 "${cmd[@]}"
				-gpre_run_script=res://tests/support/exact_case.gd -gjunit_xml_file="$case_xml")
			if ! run_case "$case_log" "$case_xml" "${case_cmd[@]}"; then
				touch "$LOGS/$name.failed"
				echo "[ERROR] Isolated case $case_name failed; see $case_log" >>"$log"
			fi
			cat "$case_log" >>"$log"
		done < <(sed -nE 's/^func (test_[a-z0-9_]+)\(.*$/\1/p' "$source")
		if [[ $count -eq 0 ]]; then
			echo "[ERROR] No isolated cases matched." >>"$log"
			touch "$LOGS/$name.failed"
		fi
	else
		if [[ -n "$case_filter" ]]; then
			cmd+=(-gunit_test_name="$case_filter")
		fi
		if ! run_case "$log" "$xml" "${cmd[@]}" -gjunit_xml_file="$xml"; then
			touch "$LOGS/$name.failed"
		fi
	fi
}

# Preserve the process failure as well as GUT's result. A timeout can happen after earlier cases passed,
# before GUT writes its final XML; calling that "No test cases ran" hides the actual cause.
run_case() {
	local log=$1 xml=$2
	shift 2
	rm -f "$xml"
	local status=0
	timeout --kill-after=10s 2400 "$@" >"$log" 2>&1 || status=$?
	if [[ $status -eq 124 || $status -eq 137 ]]; then
		echo "[ERROR] Test process timed out or was killed (exit $status); limit 2400 seconds." >>"$log"
	elif [[ $status -ne 0 ]]; then
		echo "[ERROR] Test process exited with status $status." >>"$log"
	fi
	if [[ ! -f "$xml" ]] || ! grep -q '<testcase' "$xml"; then
		echo "[ERROR] No completed JUnit report was written." >>"$log"
		status=1
	fi
	if grep -E "$ERRORS" "$log" | grep -qvE "$NOISE"; then
		status=1
	fi
	[[ $status -eq 0 ]]
}

report() {
	local script=$1
	local name
	name="$(log_name "$script")"
	if [[ -f "$LOGS/$name.failed" ]]; then
		echo "FAIL $script"
		{
			grep -E "$ERRORS|\[Failed\]" "$LOGS/$name.log" | grep -vE "$NOISE" | head -20 || true
			tail -12 "$LOGS/$name.log"
		} | sed 's/^/     /'
	else
		echo "ok   $script"
	fi
}

# With key frames, visual tests check the frame-time budget against the wall clock: they run alone, after the others,
# so tests running beside them don't slow their frames down.
together=()
alone=()
for script in "${selected[@]}"; do
	if [[ $key_frames -eq 1 ]] && grep -m1 -E '^const TAGS' "$ROOT/${script#res://}" | grep -q '"visual_confirmation"'; then
		alone+=("$script")
	else
		together+=("$script")
	fi
done
if [[ $jobs -le 1 ]]; then
	alone=("${selected[@]}")
elif [[ ${#together[@]} -gt 0 ]]; then
	echo "Running ${#together[@]} test scripts, $jobs at a time."
	export ROOT GODOT LOGS RESULTS case_filter key_frames ERRORS NOISE
	export -f run_one run_case log_name
	printf '%s\0' "${together[@]}" | xargs -0 -n1 -P "$jobs" bash -c 'run_one "$1"' _
	for script in "${together[@]}"; do
		report "$script"
	done
fi
if [[ ${#alone[@]} -gt 0 ]]; then
	echo "Running ${#alone[@]} test scripts one at a time."
	for script in "${alone[@]}"; do
		run_one "$script"
		report "$script"
	done
fi

failed=0
for script in "${selected[@]}"; do
	if [[ -f "$LOGS/$(log_name "$script").failed" ]]; then
		failed=$((failed + 1))
	fi
done
echo "$((${#selected[@]} - failed)) passed, $failed failed."
[[ $failed -eq 0 ]]
