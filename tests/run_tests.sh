#!/usr/bin/env bash
# Runs every automated game test listed in tests/TEST_CASES.md. Exits non-zero if any fails.
# GODOT must point at the Godot binary (./build.sh test sets it up). Logs go to build/test-logs.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GODOT="${GODOT:?set GODOT to the Godot binary}"
LOGS="$ROOT/build/test-logs"
mkdir -p "$LOGS"
failed=0

run_scene() {
	timeout "${2:-900}" "$GODOT" --headless --path "$ROOT" "res://tests/$1.tscn" "${@:3}" >"$LOGS/$1.log" 2>&1
}

# Godot keeps going after script errors, so a test passes only if its log has no errors and reached its done marker.
# Engine leak reports printed while quitting are noise, not failures.
check() {
	local name=$1 log=$2 marker=$3
	local errors
	errors=$(grep -E "SCRIPT ERROR|Parse Error|^FAIL:|ERROR:" "$log" | grep -vE "at exit|leaked" || true)
	if [[ -n "$errors" ]]; then
		echo "FAIL $name"
		echo "$errors" | head -20
		failed=1
	elif ! grep -q "$marker" "$log"; then
		echo "FAIL $name: did not finish"
		tail -20 "$log"
		failed=1
	else
		echo "ok   $name"
	fi
}

"$GODOT" --headless --path "$ROOT" --import >"$LOGS/import.log" 2>&1
check import "$LOGS/import.log" "Godot Engine"

run_scene tissue_test 120
check tissue "$LOGS/tissue_test.log" "tissue_test: done"

run_scene models_test 120
check models "$LOGS/models_test.log" "models_test: done"

run_scene smoke_test 1200
check smoke "$LOGS/smoke_test.log" "smoke_test: done"

# Two real processes over ENet on localhost.
timeout 180 "$GODOT" --headless --path "$ROOT" res://tests/net_test.tscn -- --role=host >"$LOGS/net_host.log" 2>&1 &
host=$!
sleep 2
timeout 180 "$GODOT" --headless --path "$ROOT" res://tests/net_test.tscn -- --role=client >"$LOGS/net_client.log" 2>&1
wait "$host"
check net_host "$LOGS/net_host.log" "\[host\] surgeon wounds"
check net_client "$LOGS/net_client.log" "\[client\] surgeon wounds"
if grep -q "partner handed me: nothing" "$LOGS/net_host.log"; then
	echo "FAIL net: tool handoff between players did not arrive"
	failed=1
fi
# Both peers must end with the same painted wound map and exactly the same cut, stitched and torn springs.
state() { grep -o "painted texels=[0-9]*, severed springs=[0-9]*, topology=-*[0-9]*" "$1"; }
if [[ "$(state "$LOGS/net_host.log")" != "$(state "$LOGS/net_client.log")" ]]; then
	echo "FAIL net: host and client disagree: '$(state "$LOGS/net_host.log")' vs '$(state "$LOGS/net_client.log")'"
	failed=1
fi

# Spotty connection: freeze the client for 10 s mid-surgery (longer than ENet's default timeout), then let it go on.
timeout 180 "$GODOT" --headless --path "$ROOT" res://tests/net_stall_test.tscn -- --role=host >"$LOGS/net_stall_host.log" 2>&1 &
host=$!
sleep 2
timeout 180 "$GODOT" --headless --path "$ROOT" res://tests/net_stall_test.tscn -- --role=client >"$LOGS/net_stall_client.log" 2>&1 &
client=$!
for _ in $(seq 1 600); do
	grep -q "\[client\] running" "$LOGS/net_stall_client.log" && break
	sleep 0.1
done
# Freeze Godot itself, not the timeout wrapper around it.
godot_pid=$(pgrep -P "$client")
kill -STOP "$godot_pid"
sleep 10
kill -CONT "$godot_pid"
wait "$client"
wait "$host"
check net_stall_host "$LOGS/net_stall_host.log" "input paused: true, partner still here: true"
check net_stall_client "$LOGS/net_stall_client.log" "still in surgery: true"

exit $failed
