#!/usr/bin/env bash
# One-stop launcher and builder for Fedora (or any x86_64 Linux).
#   ./build.sh          Run the tests, then export a standalone executable to build/ThisScalpelIsMine.x86_64
#   ./build.sh test     Run the tests only (tests/run_tests.sh, cases in tests/TEST_CASES.md)
#   ./build.sh run      Run the game straight from source
#   ./build.sh editor   Open the project in the Godot editor
# SKIP_TESTS=1 ./build.sh exports without testing.
# Godot is downloaded into ./.tools on first use.
# Export templates (~1 GB download, only the Linux ones are kept) go where Godot expects them.
# Set GODOT_BIN to use your own Godot binary of the same version instead.
set -euo pipefail

GODOT_VERSION="4.7.2"
ROOT="$(cd "$(dirname "$0")" && pwd)"
TOOLS="$ROOT/.tools"
GODOT="${GODOT_BIN:-$TOOLS/godot-$GODOT_VERSION}"
TEMPLATES="${XDG_DATA_HOME:-$HOME/.local/share}/godot/export_templates/$GODOT_VERSION.stable"
BASE_URL="https://github.com/godotengine/godot/releases/download/$GODOT_VERSION-stable"
OUTPUT="$ROOT/build/ThisScalpelIsMine.x86_64"

fetch_godot() {
	[[ -x "$GODOT" ]] && return
	echo "Downloading Godot $GODOT_VERSION..."
	mkdir -p "$TOOLS"
	curl -fL -o "$TOOLS/godot.zip" "$BASE_URL/Godot_v$GODOT_VERSION-stable_linux.x86_64.zip"
	unzip -o -q "$TOOLS/godot.zip" -d "$TOOLS"
	mv "$TOOLS/Godot_v$GODOT_VERSION-stable_linux.x86_64" "$GODOT"
	rm "$TOOLS/godot.zip"
}

fetch_templates() {
	[[ -f "$TEMPLATES/linux_release.x86_64" ]] && return
	echo "Downloading export templates (large, one time only)..."
	mkdir -p "$TOOLS" "$TEMPLATES"
	curl -fL -o "$TOOLS/templates.tpz" "$BASE_URL/Godot_v$GODOT_VERSION-stable_export_templates.tpz"
	unzip -o -q -j "$TOOLS/templates.tpz" \
		templates/linux_release.x86_64 templates/linux_debug.x86_64 templates/version.txt -d "$TEMPLATES"
	rm "$TOOLS/templates.tpz"
}

import_project() {
	"$GODOT" --headless --path "$ROOT" --import >/dev/null 2>&1 || true
}

fetch_godot
case "${1:-build}" in
	run)
		import_project
		exec "$GODOT" --path "$ROOT"
		;;
	editor)
		exec "$GODOT" --editor --path "$ROOT"
		;;
	test)
		GODOT="$GODOT" exec "$ROOT/tests/run_tests.sh"
		;;
	build)
		[[ "${SKIP_TESTS:-0}" == 1 ]] || GODOT="$GODOT" "$ROOT/tests/run_tests.sh"
		fetch_templates
		import_project
		mkdir -p "$ROOT/build"
		"$GODOT" --headless --path "$ROOT" --export-release "Linux" "$OUTPUT"
		echo "Done: $OUTPUT"
		;;
	*)
		echo "Usage: $0 [build|test|run|editor]" >&2
		exit 1
		;;
esac
