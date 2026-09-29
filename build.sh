#!/usr/bin/env bash
# One script for players and developers (Fedora first, works on any x86_64 Linux with the listed packages).
#
#   ./build.sh play      Play the game from source. Downloads Godot on first use.
#   ./build.sh dev       Set up everything for development: system packages, Godot, export templates,
#                        the Python virtualenv (.venv) for the asset generators, then imports the project.
#   ./build.sh test      Run the automated tests (tests/run_tests.sh, cases in tests/TEST_CASES.md).
#   ./build.sh build     Run the tests, then export a standalone executable to build/ThisScalpelIsMine.x86_64.
#                        SKIP_TESTS=1 exports without testing.
#   ./build.sh editor    Open the project in the Godot editor.
#   ./build.sh assets    Regenerate models and sounds (needs ./build.sh dev first).
#   ./build.sh lint      Lint and type-check the Python tools.
#
# Godot lives in ./.tools, export templates where Godot expects them (~/.local/share/godot).
# Set GODOT_BIN to use your own Godot binary of the same version instead.
set -euo pipefail

GODOT_VERSION="4.7.2"
PYTHON_VERSION="3.11"
ROOT="$(cd "$(dirname "$0")" && pwd)"
TOOLS="$ROOT/.tools"
VENV="$ROOT/.venv"
GODOT="${GODOT_BIN:-$TOOLS/godot-$GODOT_VERSION}"
TEMPLATES="${XDG_DATA_HOME:-$HOME/.local/share}/godot/export_templates/$GODOT_VERSION.stable"
BASE_URL="https://github.com/godotengine/godot/releases/download/$GODOT_VERSION-stable"
OUTPUT="$ROOT/build/ThisScalpelIsMine.x86_64"

# Installs missing system packages with dnf (asks for sudo), or says what to install elsewhere.
# Arguments are "command:fedora-package" pairs.
require() {
	local missing=() pair
	for pair in "$@"; do
		command -v "${pair%%:*}" >/dev/null 2>&1 || missing+=("${pair#*:}")
	done
	[[ ${#missing[@]} -eq 0 ]] && return
	if command -v dnf >/dev/null 2>&1; then
		echo "Installing system packages: ${missing[*]}"
		sudo dnf install -y "${missing[@]}"
	else
		echo "Please install these packages with your package manager, then run this again: ${missing[*]}" >&2
		exit 1
	fi
}

fetch_godot() {
	[[ -x "$GODOT" ]] && return
	require curl:curl unzip:unzip
	echo "Downloading Godot $GODOT_VERSION..."
	mkdir -p "$TOOLS"
	curl -fL -o "$TOOLS/godot.zip" "$BASE_URL/Godot_v$GODOT_VERSION-stable_linux.x86_64.zip"
	unzip -o -q "$TOOLS/godot.zip" -d "$TOOLS"
	mv "$TOOLS/Godot_v$GODOT_VERSION-stable_linux.x86_64" "$GODOT"
	rm "$TOOLS/godot.zip"
}

fetch_templates() {
	[[ -f "$TEMPLATES/linux_release.x86_64" ]] && return
	require curl:curl unzip:unzip
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

setup_python() {
	require "python$PYTHON_VERSION:python$PYTHON_VERSION"
	if [[ ! -x "$VENV/bin/python" ]]; then
		echo "Creating the Python virtualenv in .venv..."
		"python$PYTHON_VERSION" -m venv "$VENV"
	fi
	"$VENV/bin/pip" install --quiet --upgrade pip
	"$VENV/bin/pip" install --quiet -r "$ROOT/requirements-dev.txt"
}

need_venv() {
	[[ -x "$VENV/bin/python" ]] || { echo "Run ./build.sh dev first." >&2; exit 1; }
}

case "${1:-}" in
	play | run)
		fetch_godot
		import_project
		exec "$GODOT" --path "$ROOT"
		;;
	dev)
		# git for version control, Xvfb for the headless screenshot tool.
		require git:git xvfb-run:xorg-x11-server-Xvfb
		fetch_godot
		fetch_templates
		setup_python
		import_project
		echo
		echo "Ready. ./build.sh play to play, ./build.sh editor to edit, ./build.sh test to run the tests."
		;;
	test)
		fetch_godot
		GODOT="$GODOT" exec "$ROOT/tests/run_tests.sh"
		;;
	build)
		fetch_godot
		[[ "${SKIP_TESTS:-0}" == 1 ]] || GODOT="$GODOT" "$ROOT/tests/run_tests.sh"
		fetch_templates
		import_project
		mkdir -p "$ROOT/build"
		"$GODOT" --headless --path "$ROOT" --export-release "Linux" "$OUTPUT"
		echo "Done: $OUTPUT"
		;;
	editor)
		fetch_godot
		exec "$GODOT" --editor --path "$ROOT"
		;;
	assets)
		need_venv
		cd "$ROOT"
		"$VENV/bin/python" -m tools.assetgen
		"$VENV/bin/python" -m tools.blender
		;;
	lint)
		need_venv
		cd "$ROOT"
		"$VENV/bin/ruff" check tools
		"$VENV/bin/ruff" format --check tools
		"$VENV/bin/mypy" tools
		;;
	*)
		sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'
		exit 1
		;;
esac
