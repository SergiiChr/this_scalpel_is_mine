#!/bin/sh
# Runs a command (CI's test runs) so that any process of it that crashes leaves a core dump, then prints the native and
# managed stacks of each core, also when a retry let the command pass.
# A crashed Godot prints nothing itself: its crash handler first sends the scene tree a crash notification, which runs
# C# inside the signal handler and crashes again.
# CI only: it changes the machine's core pattern with sudo, and each core takes hundreds of megabytes.
cores=/tmp/cores
sudo mkdir -p -m 777 "$cores"
sudo sysctl -q -w kernel.core_pattern="$cores/core.%p"
ulimit -c unlimited
# Private file-backed memory too: SOS needs the runtime's own mappings to walk managed stacks.
echo 0x3f >/proc/self/coredump_filter
"$@"
status=$?
set -- "$cores"/core.*
[ -e "$1" ] || exit "$status"
sudo apt-get install -y -qq lldb >/dev/null
sos=$(mktemp -d)
dotnet tool install --tool-path "$sos" dotnet-sos >/dev/null
plugin=$(find "$sos" -name libsosplugin.so | head -n 1)
for core; do
    echo "::group::$core: native stack of the crashed thread, then every managed stack"
    lldb --batch -c "$core" -o "plugin load $plugin" -o "bt 60" -o "clrstack -all"
    echo "::endgroup::"
done
exit "$status"
