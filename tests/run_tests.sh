#!/usr/bin/env bash
# GUT is the source of truth. Every discovered test script declares a one-line TAGS constant.
# Usage: run_tests.sh [--all] [--tag TAG]... [--case TEXT] [--list] [--jobs N]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GODOT="${GODOT:?set GODOT to the Godot binary}"
LOGS="$ROOT/build/test-logs"
RESULTS="$ROOT/build/test-results"
mkdir -p "$LOGS" "$RESULTS"

all=0
list=0
case_filter=""
jobs=1
tags=()
while [[ $# -gt 0 ]]; do
	case "$1" in
		--all) all=1 ;;
		--tag) tags+=("$2"); shift ;;
		--tag=*) tags+=("${1#*=}") ;;
		--case) case_filter="$2"; shift ;;
		--case=*) case_filter="${1#*=}" ;;
		--jobs) jobs="$2"; shift ;;
		--jobs=*) jobs="${1#*=}" ;;
		--list) list=1 ;;
		*) echo "Unknown test option: $1" >&2; exit 2 ;;
	esac
	shift
done

if [[ $all -eq 0 && ${#tags[@]} -eq 0 ]]; then
	tags=(smoke)
fi

mapfile -t discovered < <(find "$ROOT/tests" -type f -name 'test_*.gd' -not -path '*/support/*' | sort)
selected=()
for file in "${discovered[@]}"; do
	rel="res://${file#$ROOT/}"
	line="$(grep -m1 -E '^const TAGS' "$file" || true)"
	if [[ -z "$line" ]]; then
		echo "Test has no TAGS metadata: $rel" >&2
		exit 2
	fi
	if [[ -n "$case_filter" && "$rel" != *"$case_filter"* ]]; then
		continue
	fi
	matched=1
	for tag in "${tags[@]}"; do
		if [[ "$line" != *"\"$tag\""* ]]; then
			matched=0
			break
		fi
	done
	if [[ $matched -eq 1 ]]; then
		selected+=("$rel")
	fi
done

if [[ $list -eq 1 ]]; then
	printf '%s\n' "${selected[@]}"
	exit 0
fi
if [[ ${#selected[@]} -eq 0 ]]; then
	echo "No GUT tests matched tags '${tags[*]}' and case '$case_filter'." >&2
	exit 2
fi

printf 'GUT tests (%d):\n' "${#selected[@]}"
printf '  %s\n' "${selected[@]}"

run_one() {
	local script=$1
	local name="${script#res://tests/}"
	name="${name//\//_}"
	name="${name%.gd}"
	local source="$ROOT/${script#res://}"
	local log="$LOGS/$name.log"
	local xml="res://build/test-results/$name.xml"
	local runner=("$GODOT" --headless --path "$ROOT" -s addons/gut/gut_cmdln.gd -gconfig= -gtest="$script" -gexit -gdisable_colors -glog=1 -gjunit_xml_file="$xml")
	if grep -m1 -E '^const TAGS' "$source" | grep -q '"visual_confirmation"'; then
		runner=("$GODOT" --path "$ROOT" --rendering-method gl_compatibility -s addons/gut/gut_cmdln.gd -gconfig= -gtest="$script" -gexit -gdisable_colors -glog=1 -gjunit_xml_file="$xml")
		if command -v xvfb-run >/dev/null 2>&1; then
			runner=(xvfb-run -a "${runner[@]}")
		elif [[ -z "${DISPLAY:-}" ]]; then
			echo "Visual test requires xvfb-run or an active DISPLAY." >"$log"
			return 1
		fi
	fi
	if ! timeout 1200 "${runner[@]}" >"$log" 2>&1; then
		return 1
	fi
	# GUT exits successfully when a script cannot parse or no tests are collected.
	# Treat that as a runner failure instead of allowing a false green build.
	if [[ ! -f "$ROOT/${xml#res://}" ]] || ! grep -q '<testcase' "$ROOT/${xml#res://}"; then
		echo "No GUT test cases were executed." >>"$log"
		return 1
	fi
	if grep -qE 'SCRIPT ERROR:|GUT ERROR|(^|[[:space:]])FAIL:' "$log"; then
		return 1
	fi
}
export ROOT GODOT LOGS RESULTS
export -f run_one

if [[ "$jobs" -le 1 ]]; then
	for script in "${selected[@]}"; do
		echo "RUN  $script"
		if run_one "$script"; then
			echo "PASS $script"
		else
			name="${script#res://tests/}"
			name="${name//\//_}"
			name="${name%.gd}"
			echo "FAIL $script"
			tail -80 "$LOGS/$name.log" || true
			exit 1
		fi
	done
else
	printf '%s\0' "${selected[@]}" | xargs -0 -n1 -P "$jobs" bash -c 'run_one "$1"' _
fi
