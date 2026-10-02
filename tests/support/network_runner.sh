#!/usr/bin/env bash
set -euo pipefail

godot=$1
root=$2
mode=$3
logs="$root/build/test-logs"
mkdir -p "$logs"

clean_log() {
	! grep -E "SCRIPT ERROR|Parse Error|^FAIL:" "$1" >/dev/null
}

if [[ "$mode" == sync ]]; then
	timeout 180 "$godot" --headless --path "$root" res://tests/support/net_driver.tscn -- --role=host >"$logs/net_host.log" 2>&1 &
	host=$!
	sleep 2
	timeout 180 "$godot" --headless --path "$root" res://tests/support/net_driver.tscn -- --role=client >"$logs/net_client.log" 2>&1
	wait "$host"
	clean_log "$logs/net_host.log"
	clean_log "$logs/net_client.log"
	grep -q "\[host\] surgeon wounds" "$logs/net_host.log"
	grep -q "\[client\] surgeon wounds" "$logs/net_client.log"
	! grep -q "partner handed me: nothing" "$logs/net_host.log"
	host_state=$(grep -o "painted texels=[0-9]*, severed springs=[0-9]*, topology=-*[0-9]*" "$logs/net_host.log")
	client_state=$(grep -o "painted texels=[0-9]*, severed springs=[0-9]*, topology=-*[0-9]*" "$logs/net_client.log")
	[[ -n "$host_state" && "$host_state" == "$client_state" ]]
	exit
fi

timeout 180 "$godot" --headless --path "$root" res://tests/support/net_stall_driver.tscn -- --role=host >"$logs/net_stall_host.log" 2>&1 &
host=$!
sleep 2
timeout 180 "$godot" --headless --path "$root" res://tests/support/net_stall_driver.tscn -- --role=client >"$logs/net_stall_client.log" 2>&1 &
client=$!
for _attempt in $(seq 1 600); do
	grep -q "\[client\] running" "$logs/net_stall_client.log" && break
	sleep 0.1
done
godot_pid=$(pgrep -P "$client")
kill -STOP "$godot_pid"
sleep 10
kill -CONT "$godot_pid"
wait "$client"
wait "$host"
clean_log "$logs/net_stall_host.log"
clean_log "$logs/net_stall_client.log"
grep -q "input paused: true, partner still here: true" "$logs/net_stall_host.log"
grep -q "still in surgery: true" "$logs/net_stall_client.log"
