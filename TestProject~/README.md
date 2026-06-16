# TestProject~

This is the dev host Unity project for the `io.elevenlabs.agents` package — **not a sample**.

It exists so Unity has a project to open for C# compilation checks, the Edit Mode
Test Runner, and WebGL build smoke tests. The `~` suffix makes Unity ignore this
directory when the package is installed by a downstream consumer (so it doesn't
ship in the UPM payload).

## What lives here

- `Packages/manifest.json` — references the local package via `file:../..` so
  edits to `Runtime/` and `Tests/` take effect immediately without reinstalling.
- `ProjectSettings/ProjectVersion.txt` — pinned to Unity **6000.3.6f1** (Unity 6 LTS).

Unity generates `Library/`, `Temp/`, `Obj/`, `Logs/`, `UserSettings/`, and an empty
`Assets/` on first open; all are gitignored.

## Implementation plan

See [`Docs~/plans/generic-bridge-primitives.md`](../Docs~/plans/generic-bridge-primitives.md),
tasks HP.1–HP.7, for the full rationale and sequencing.
