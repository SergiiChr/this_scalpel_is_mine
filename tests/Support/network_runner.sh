#!/usr/bin/env bash
# Two real Godot processes over ENet on localhost, for tests/Network/NetworkTest.cs. It prints what's wrong and
# exits non-zero on the first problem.
# Usage: network_runner.sh GODOT ROOT sync|pinch|tape|lobby|ambulance|stall LIMIT
# LIMIT: seconds the test case may take (tests/Support/Limits.cs). Each driver is stopped there, and gives up waiting on
# the other side 10 s earlier, which leaves room for the client starting later and both quitting.
# The drivers are tests/Support/NetDriver.cs (sync), NetPinchDriver.cs (pinch), NetTapeDriver.cs (tape),
# NetLobbyDriver.cs (lobby), NetAmbulanceFlowDriver.cs (ambulance) and NetStallDriver.cs (stall), each started from its scene.
set -uo pipefail

godot=$1
root=$2
mode=$3
limit=$4
logs="$root/build/test-logs"
mkdir -p "$logs"

# The drivers' logs: net_host and net_client for sync, net_MODE_host and net_MODE_client otherwise.
players="net_$([[ "$mode" == sync ]] || echo "${mode}_")"

# Stops a driver still running too, so it doesn't hold the port until its timeout.
# Both drivers' reports (their lines naming their role in brackets) follow, so a failure on one side shows how far the
# other got.
fail() {
	echo "FAIL: $*"
	kill $(jobs -p) 2>/dev/null
	grep -sh "\[host\]\|\[client\]" "$logs/${players}host.log" "$logs/${players}client.log"
	exit 1
}

# Godot keeps going after script errors and unhandled C# exceptions. Engine leak reports printed while quitting are noise.
clean_log() {
	local errors
	# With the lines after each: a C# exception's object name and stack follow its first line.
	errors=$(grep -E -A 12 "SCRIPT ERROR|Parse Error|ERROR:|Unhandled exception|^FAIL:" "$1" | grep -vE "at exit|leaked")
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
	# Each player's own user:// (Godot's under XDG_DATA_HOME), as on two machines: both write save.cfg on joining,
	# and a player reading it while the other writes it reads half a file.
	XDG_DATA_HOME=$(mktemp -d)
	export XDG_DATA_HOME
	# exec: started in the background, the job is the timeout wrapper itself, with Godot as its only child.
	exec timeout "$limit" "$godot" --headless --path "$root" "res://tests/Support/$1.tscn" -- "--role=$2" \
		"--give-up=$((limit - 10))" >"$logs/$3.log" 2>&1
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
	expect "$logs/net_host.log" "\[host\] the client's card is back on the hook"
	expect "$logs/net_client.log" "\[client\] both copies put back hang on the hook as one"
	expect "$logs/net_client.log" "\[client\] bleed sources and cut pools match the host's"
	expect "$logs/net_client.log" "\[client\] the dropped tool rests where it does on the host"
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

if [[ "$mode" == pinch ]]; then
	run NetPinchDriver host net_pinch_host &
	host=$!
	sleep 2
	run NetPinchDriver client net_pinch_client &
	client=$!
	finished "$client" net_pinch_client
	finished "$host" net_pinch_host
	expect "$logs/net_pinch_client.log" "\[client\] forceps over the bullet"
	expect "$logs/net_pinch_host.log" "\[host\] the client's forceps take the bullet"
	exit 0
fi

if [[ "$mode" == tape ]]; then
	run NetTapeDriver host net_tape_host &
	host=$!
	sleep 2
	run NetTapeDriver client net_tape_client &
	client=$!
	finished "$client" net_tape_client
	finished "$host" net_tape_host
	expect "$logs/net_tape_host.log" "\[host\] holds the gauze over the cut"
	expect "$logs/net_tape_client.log" "\[client\] my strip tapes the host's gauze down flat where it held it, still in their hand"
	expect "$logs/net_tape_host.log" "\[host\] taped down, the gauze stays in my hand, pinned flat on it"
	expect "$logs/net_tape_host.log" "\[host\] let go of, the gauze stays taped where it was and my hand walks away"
	expect "$logs/net_tape_client.log" "\[client\] let go of, the host's gauze stays taped where it was"
	exit 0
fi

if [[ "$mode" == lobby ]]; then
	run NetLobbyDriver host net_lobby_host &
	host=$!
	sleep 2
	run NetLobbyDriver client net_lobby_client &
	client=$!
	finished "$client" net_lobby_client
	finished "$host" net_lobby_host
	expect "$logs/net_lobby_client.log" "\[client\] the client waits for the host in an empty lobby"
	expect "$logs/net_lobby_host.log" "\[host\] the host still reads the report while the client waits"
	for side in host client; do
		expect "$logs/net_lobby_$side.log" "\[$side\] back in the lobby, still connected"
		expect "$logs/net_lobby_$side.log" "\[$side\] the next surgery is appendectomy with both surgeons"
	done
	exit 0
fi

if [[ "$mode" == ambulance ]]; then
	run NetAmbulanceFlowDriver host net_ambulance_host &
	host=$!
	sleep 2
	run NetAmbulanceFlowDriver client net_ambulance_client &
	client=$!
	finished "$client" net_ambulance_client
	finished "$host" net_ambulance_host
	for side in host client; do
		expect "$logs/net_ambulance_$side.log" "\[$side\] both cooperating surgeons receive the game's successful surgery report"
		expect "$logs/net_ambulance_$side.log" "\[$side\] $side remains on its assigned aisle through discharge"
	done
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
