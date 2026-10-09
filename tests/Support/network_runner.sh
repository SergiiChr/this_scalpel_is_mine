#!/usr/bin/env bash
# Two real Godot processes over ENet on localhost, for tests/Network/NetworkTest.cs. It prints what's wrong and
# exits non-zero on the first problem.
# Usage: network_runner.sh GODOT ROOT sync|stall
# The drivers are tests/Support/NetDriver.cs (sync) and NetStallDriver.cs (stall), each started from its scene.
set -uo pipefail

godot=$1
root=$2
mode=$3
logs="$root/build/test-logs"
mkdir -p "$logs"

# Stops a driver still running too, so it doesn't hold the port until its timeout.
fail() {
	echo "FAIL: $*"
	kill $(jobs -p) 2>/dev/null
	exit 1
}

# Godot keeps going after script errors and unhandled C# exceptions. Engine leak reports printed while quitting are noise.
clean_log() {
	local errors
	errors=$(grep -E "SCRIPT ERROR|Parse Error|ERROR:|Unhandled exception|^FAIL:" "$1" | grep -vE "at exit|leaked")
	[[ -z "$errors" ]] || fail "$(basename "$1"): $errors"
}

# Waits for a driver: errors or FAIL lines in its log fail the run, and so does any exit status but 0 (a crash, a
# failed check, 124 for the timeout).
finished() {
	local status=0
	wait "$1" || status=$?
	clean_log "$logs/$2.log"
	[[ $status -eq 0 ]] || fail "$2 exited with status $status$([[ $status -eq 124 ]] && echo ' (timed out)'), see $logs/$2.log"
}

expect() {
	grep -q "$2" "$1" || fail "$(basename "$1") lacks '$2'"
}

run() {
	# exec: started in the background, the job is the timeout wrapper itself, with Godot as its only child.
	exec timeout 180 "$godot" --headless --path "$root" "res://tests/Support/$1.tscn" -- "--role=$2" >"$logs/$3.log" 2>&1
}

if [[ "$mode" == sync ]]; then
	run NetDriver host net_host &
	host=$!
	sleep 2
	run NetDriver client net_client &
	client=$!
	finished "$client" net_client
	finished "$host" net_host
	expect "$logs/net_host.log" "\[host\] surgeon wounds"
	expect "$logs/net_client.log" "\[client\] surgeon wounds"
	expect "$logs/net_host.log" "\[host\] client squat has bent knees and grounded heels"
	if grep -q "partner handed me: nothing" "$logs/net_host.log"; then
		fail "tool handoff between players did not arrive"
	fi
	# Both peers end with the same painted wound map, exactly the same cut, stitched and torn springs and the same
	# site. The site is measured on the body model by each peer: its shape must come out the same for both.
	state() {
		grep -o "painted texels=[0-9]*, severed springs=[0-9]*, topology=-*[0-9]*, site shape=-*[0-9]*" "$1"
	}
	host_state=$(state "$logs/net_host.log")
	client_state=$(state "$logs/net_client.log")
	if [[ -z "$host_state" || "$host_state" != "$client_state" ]]; then
		fail "host and client disagree: '$host_state' vs '$client_state'"
	fi
	exit 0
fi

# Spotty connection: freeze the client for 10 s mid-surgery (longer than ENet's default timeout), then let it go on.
run NetStallDriver host net_stall_host &
host=$!
sleep 2
run NetStallDriver client net_stall_client &
client=$!
for _ in $(seq 1 600); do
	grep -q "\[client\] running" "$logs/net_stall_client.log" && break
	sleep 0.1
done
grep -q "\[client\] running" "$logs/net_stall_client.log" || fail "the client never got into surgery within 60 s"
# Freeze Godot itself, not the timeout wrapper around it.
godot_pid=$(pgrep -P "$client") || fail "the client never started"
kill -STOP "$godot_pid"
sleep 10
kill -CONT "$godot_pid"
finished "$client" net_stall_client
finished "$host" net_stall_host
expect "$logs/net_stall_host.log" "input paused: true, partner still here: true"
expect "$logs/net_stall_client.log" "still in surgery: true"
