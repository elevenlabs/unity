#!/bin/bash
# Run a headless WebGL build of the conversation smoke scene to verify the
# bridged Conversation stack compiles into WebGL end-to-end.
# Exits 0 on success, non-zero on failure.
#
# NOTE: The build method calls EditorApplication.Exit() itself, so -quit is
# not needed here (unlike -runTests where -quit causes silent early exit).
# Expect this to take several minutes — IL2CPP compilation is the bottleneck.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="/Applications/Unity/Hub/Editor/6000.3.6f1/Unity.app/Contents/MacOS/Unity"

"$UNITY" \
  -batchmode \
  -nographics \
  -projectPath "$REPO_ROOT/TestProject" \
  -buildTarget WebGL \
  -executeMethod ElevenLabs.WebGL.Editor.HostBuild.BuildConversation \
  -logFile -
