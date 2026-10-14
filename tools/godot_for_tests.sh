#!/bin/sh
# Godot as GdUnit4's test processes run it (GODOT_BIN in build.py test): the real binary, except for the adapter's
# editor pass before every run (-e --headless --quit-after N), which would build and import a project build.py has
# already built and imported. The pass takes seconds per suite, and the editor rewrites caches under .godot that a
# test process starting beside it reads.
for argument in "$@"; do
    [ "$argument" = "-e" ] && exit 0
done
exec "$REAL_GODOT" "$@"
