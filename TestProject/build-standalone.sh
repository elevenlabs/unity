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

# Wipe any prior output before kicking off the build. The TestProject/
# embedded host project is also the package root that Unity scans for
# assets/DLLs (manifest.json: "file:../.."); a stripped UnityEngine.dll
# left behind by a failed prior build under
# TestProject/Build/Standalone/<name>_BackUpThisFolder_ButDontShipItWithYourGame/Managed/
# gets re-imported as an additional reference, shadowing the real
# UnityEngine.dll and producing CS0117 "Application doesn't contain
# dataPath" errors during the next compile.
rm -rf "$REPO_ROOT/TestProject/Build/Standalone"

# Build target is picked at runtime from Application.platform inside
# HostBuild.BuildStandalone — same script works on macOS / Windows / Linux
# as long as the matching Standalone module is installed.
set +e
"$UNITY" \
  -batchmode \
  -nographics \
  -projectPath "$REPO_ROOT/TestProject" \
  -executeMethod ElevenLabs.WebGL.Editor.HostBuild.BuildStandalone \
  -logFile -
UNITY_EXIT=$?
set -e

# Restore ProjectSettings.asset. HostBuild.BuildStandalone restores the
# in-memory settings before EditorApplication.Exit, but PlayerSettings'
# GetScriptingBackend reads the platform default (Standalone: 0 = Mono)
# even when the file had {} on disk — so the save-back writes
# "Standalone: 0" explicitly, polluting the working tree. A targeted
# git checkout is simpler and more reliable than fighting the API.
if git -C "$REPO_ROOT" rev-parse --git-dir > /dev/null 2>&1; then
  git -C "$REPO_ROOT" checkout -- TestProject/ProjectSettings/ProjectSettings.asset 2>/dev/null || true
fi

exit "$UNITY_EXIT"
