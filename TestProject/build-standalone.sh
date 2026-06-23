#!/bin/bash
# Run a headless desktop standalone smoke build with IL2CPP + High managed
# stripping to verify Runtime/Native/link.xml preserves ElevenLabs.Protocol.*
# under AOT. Builds for the host OS; the produced binary opens a real-agent
# session (or logs CONFIG MISSING and exits cleanly).
#
# Exits 0 on build success, non-zero on failure.
#
# NOTE: The build method calls EditorApplication.Exit() itself, so -quit is
# not needed here (unlike -runTests where -quit causes silent early exit).
# Expect this to take several minutes — IL2CPP compilation is the bottleneck.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="/Applications/Unity/Hub/Editor/6000.3.6f1/Unity.app/Contents/MacOS/Unity"

# Build target is picked at runtime from Application.platform inside
# HostBuild.BuildStandalone — same script works on macOS / Windows / Linux
# as long as the matching Standalone module is installed.
"$UNITY" \
  -batchmode \
  -nographics \
  -projectPath "$REPO_ROOT/TestProject" \
  -executeMethod ElevenLabs.WebGL.Editor.HostBuild.BuildStandalone \
  -logFile -
