#!/bin/bash
# Prepares a Claude Code cloud session for ./build.py setup, lint and test without manual steps.
# The cloud proxy blocks Microsoft's download host, so the .NET SDK comes from Ubuntu's packages instead of .tools/dotnet.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

packages=()
command -v dotnet >/dev/null || packages+=(dotnet-sdk-10.0)
command -v python3.11 >/dev/null || packages+=(python3.11 python3.11-venv)
command -v xvfb-run >/dev/null || packages+=(xvfb)
command -v xauth >/dev/null || packages+=(xauth)
if [ ${#packages[@]} -gt 0 ]; then
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq "${packages[@]}"
fi

# Dev setup covers setup and adds the virtualenv lint needs; each step skips what is already in place.
cd "$CLAUDE_PROJECT_DIR"
./build.py dev
