#!/bin/bash
# Run Unity Edit Mode tests in headless batchmode.
# NOTE: do NOT pass -quit; the test runner exits the editor once tests finish.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="/Applications/Unity/Hub/Editor/6000.3.6f1/Unity.app/Contents/MacOS/Unity"

"$UNITY" \
  -batchmode \
  -nographics \
  -projectPath "$REPO_ROOT/TestProject" \
  -runTests \
  -testPlatform editmode \
  -testResults "$REPO_ROOT/TestProject/test-results.xml" \
  -logFile -
