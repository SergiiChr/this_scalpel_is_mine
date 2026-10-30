#!/bin/sh
# Runs a command on an X server of its own drawn by the machine's GPU, as xvfb-run does in software: a rootful Xwayland,
# one window on the Wayland desktop with its own screen inside. No window manager runs in it, so nothing on the desktop
# resizes the command's window, takes its focus or moves the pointer over it.
# Usage: tools/with_gpu_display.sh WIDTHxHEIGHT command...
size=$1
shift
fifo=$(mktemp -u)
mkfifo "$fifo"
Xwayland -geometry "$size" -displayfd 3 3>"$fifo" >/dev/null 2>&1 &
server=$!
trap 'kill $server 2>/dev/null; rm -f "$fifo"' EXIT INT TERM
read -r number <"$fifo"
rm -f "$fifo"
[ -n "$number" ] || { echo "with_gpu_display: Xwayland didn't start" >&2; exit 1; }
DISPLAY=":$number" WAYLAND_DISPLAY= "$@"
