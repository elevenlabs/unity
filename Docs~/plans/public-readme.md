# Public-readme + Getting Started plan

**Status:** Proposed, 2026-06-29
**Driver:** Repo is approaching public-release readiness. The current
[`README.md`](../../README.md) opens with requirements, then jumps into the
exception/error taxonomy — it reads as API reference, not as a landing page.
The only "how do I start" pointer is one paragraph about importing the
`QuickStart` sample, which is a single-GameObject scene with an `OnGUI`
transcript — functional, but not the demo that gets a new user excited.

We have a much better story already live: the `Getting Started` Unity project
at `/Users/kraenhansen/UnityProjects/Getting Started/` (Unity's official "Get
Started with Unity" template) with a [`TalkingBox`](file:///Users/kraenhansen/UnityProjects/Getting%20Started/Assets/Scripts/TalkingBox.cs)
MonoBehaviour layered on. Walk into a cube, it greets you in character; walk
out, the session ends. Dynamic variables, spatial audio, volume-driven bob,
all in ~200 lines.

This plan converts that into the public on-ramp: a slim README that points to
a single getting-started doc, with a copy of the Getting-Started-template +
TalkingBox vendored as an importable sample.

---

## Scope

In:

- Rewrite [`README.md`](../../README.md) as a landing page.
- Add [`Docs~/GETTING_STARTED.md`](../GETTING_STARTED.md) walkthrough.
- Add `Samples/GettingStarted/` — condensed copy of the Unity Getting-Started
  template + `TalkingBox` + `TalkingBoxAgentConfig` + a working scene.
- Add [`Docs~/ERROR_HANDLING.md`](../ERROR_HANDLING.md) — the current README's
  "Error types" section verbatim.
- Delete the README's "Mapping from the `@elevenlabs/client` JS SDK" table.
- Repo-wide public-readiness audit (separate pass — see "Audit" below).

Out:

- OpenUPM publishing — tracked as a separate issue, GETTING_STARTED.md leads
  with UPM git URL only.
- API reference docs beyond what already exists in XML doc comments.
- Marketing / screen recording with audio (open question — see below).

---

## Open question — landing media

A 30-second screen recording with audio is the natural lead for the README,
since the demo is intrinsically audio. A muted GIF would show the bob but
not the conversation, which undersells it. Options:

1. **Record with audio, embed as a video file** (GitHub renders `.mp4` /
   `.mov` inline in markdown when uploaded via the issues/PR drag-and-drop
   flow). This is the strongest demo but needs us to actually record it.
2. **Record video, ship as GIF + add transcript captions to the HUD in the
   sample.** Adds scope to TalkingBox (UI bottom-of-screen transcript) but
   keeps the README inline-playable everywhere.
3. **No media for v0.1.** Lead with a screenshot of the scene and let the
   sample carry the demo.

Default: option 1, recorded once the sample is in place. Revisit if the
recording flow turns out to be flaky.

---

## Task list

### 1. Vendor the sample

- [x] Create `Samples/GettingStarted/` with these files, scoped to just
      what's needed to walk up to a cube and talk to it:
  - [x] `Scenes/GettingStarted.unity` — condensed scene built from
        primitives (ground plane, directional light, two TalkingBox
        cubes, a player capsule with [`SimplePlayerController`](../../Samples/GettingStarted/Scripts/SimplePlayerController.cs)).
        Authored via Unity batchmode in a staging folder, then copied
        into the package so the GUIDs in the scene reference the
        sample's shipped scripts and materials. The original live
        project's PlayerRobot and Wall_Light prefabs were not vendored
        — they're Unity Getting-Started template assets and would
        bloat the sample. The capsule + WASD/mouse-look controller
        delivers the same walk-up flow with zero external dependencies.
  - [x] `Scripts/TalkingBox.cs` — adapted from the live project, wrapped
        in the `ElevenLabs.Agents.Samples.GettingStarted` namespace.
  - [x] `Scripts/TalkingBoxAgentConfig.cs` — same.
  - [x] `Scripts/SimplePlayerController.cs` — minimal WASD + mouse-look
        controller (added beyond the plan because we couldn't ship the
        PlayerRobot prefab).
  - [x] `Prefabs/TalkingBox.prefab` — cube with `SphereCollider`
        (`isTrigger=true`), `AudioSource`, `TalkingBox` component, all
        wired, referencing the shipped `TalkingBox_Yellow` material.
  - [x] `Materials/TalkingBox_Yellow.mat`, `TalkingBox_Red.mat`,
        `Ground.mat` — URP Lit materials shipped with the sample so the
        prefab and scene don't depend on the consumer project's
        default-material GUIDs.
  - [x] `ElevenLabs.Agents.Samples.GettingStarted.asmdef`.
  - [x] `README.md`.
- [x] Add `Samples/GettingStarted` to [`package.json`](../../package.json)'s
      `samples` array and `files` array. Added
      `com.unity.modules.physics` to the package's `dependencies` while
      we were there — the sample uses `SphereCollider` /
      `CharacterController`, and the previous TestProject manifest
      didn't transitively pull physics in.
- [ ] Decide what to do with `Samples/QuickStart/`. Options: keep as a
      minimal "no graphics, just transcript" debug sample; fold into
      GettingStarted; or delete. Lean toward keeping — it's the smallest
      possible smoke test and that's valuable.
- [ ] Decide what to do with `Samples/BridgeSmokeTest/`,
      `Samples/ConversationSmokeTest/`, `Samples/StandaloneSmokeTest/`.
      These exist for our integration tests. Likely move to a non-shipped
      location (they're under `files` today via `Samples.meta`; the package
      manifest only ships `Samples/QuickStart` explicitly, so they may
      already be excluded — verify).
- [ ] **Follow-up — move `Samples/` → `Samples~/`.** Today the
      package's sample sources live at `Samples/` (no tilde), so they're
      visible to the AssetDatabase of any consumer project. When the
      user imports a sample via Package Manager, Unity copies it into
      `Assets/Samples/...` — both copies now exist, with the same
      asmdef name, which produces a hard "Assembly with name X already
      exists" error and GUID-conflict warnings on every imported
      asset. Renaming to `Samples~/` is the Unity convention to hide
      the sources from AssetDatabase while keeping Package Manager
      able to copy them on demand. Affects QuickStart too; out of
      scope for task 1.

### 2. Write `Docs~/GETTING_STARTED.md`

- [ ] Prereqs section: Unity 6.3 LTS, free ElevenLabs account, an agent
      created in the dashboard with authentication disabled.
- [ ] **Option A — start from the SDK sample (recommended).** Import
      `GettingStarted` via UPM Samples, open the scene, press Play.
      ~3 steps end-to-end.
- [ ] **Option B — add to your own project.** Walkthrough:
  1. Install the SDK (UPM git URL).
  2. WebGL-only collapsible section: Use WebAssembly.Table + API
     Compatibility .NET Standard 2.1.
  3. Add `TalkingBoxAgentConfig.cs` (paste block).
  4. Create the asset under `Resources/` and fill in agent ID.
  5. Add `TalkingBox.cs` (paste block) and walk through each region:
     dynamic variables, overrides, `OutputAudioSource`, lifecycle.
  6. **Add a `SphereCollider` (Is Trigger = on)** to the cube the
     `TalkingBox` is attached to — this is what fires
     `OnTriggerEnter` / `OnTriggerExit`. The component logs a warning at
     Awake if missing, but the doc should say so explicitly so users
     don't get stuck.
  7. Make sure the player GameObject has a non-trigger collider (the
     Unity Getting-Started template's PlayerRobot already does).
  8. Press Play, walk into the cube.
- [ ] "What to try next" section: change `mood`, add a second box with a
      different `color`, link to client-tool docs once they exist.
- [ ] Link back to [`COMPATIBILITY.md`](../../COMPATIBILITY.md) and
      [`ERROR_HANDLING.md`](../ERROR_HANDLING.md) at the end.

### 3. Rewrite `README.md`

- [ ] Lead with 1–2 sentence pitch + landing media (see open question).
- [ ] Install section: UPM via git URL. One-liner pointing to the
      Compatibility doc for Player Settings details.
- [ ] "Get started" → link to [`Docs~/GETTING_STARTED.md`](../GETTING_STARTED.md).
- [ ] "Samples" → short table of the shipped samples
      (GettingStarted as the headline, QuickStart as the minimal one if
      kept).
- [ ] "Documentation" → links to Architecture, Compatibility, Error
      handling, Contributing, Changelog.
- [ ] Move "Error types" section → [`Docs~/ERROR_HANDLING.md`](../ERROR_HANDLING.md).
- [ ] Delete "Mapping from the `@elevenlabs/client` JS SDK" section.

### 4. Add `Docs~/ERROR_HANDLING.md`

- [ ] Move the README's current "Error types" section verbatim.
- [ ] No structural changes — that section is in good shape; it's just
      misplaced.

### 5. Track follow-ups as issues

- [ ] File "Publish to OpenUPM" issue.
- [ ] File "Record landing media" issue if we pick option 1 or 2 above.

### 6. Public-readiness audit (parallel pass — separate plan if it grows)

- [ ] Confirm [`LICENSE`](../../LICENSE) matches the org's preferred license
      for public OSS.
- [ ] [`.github/`](../../.github/) — verify CI workflow status, add issue +
      PR templates if missing.
- [ ] [`CONTRIBUTING.md`](../../CONTRIBUTING.md) — confirmed OK for now per
      user.
- [ ] [`Docs~/plans/`](.) — these are working design docs. Decide whether
      they ship publicly (fine — they tell the story of how we got here) or
      move to internal. Lean toward keeping public.
- [ ] Grep for internal-only references: Slack channels, internal monorepo
      links, personal usernames in non-commit pointers, hardcoded paths
      under `/Users/`.
- [ ] Confirm [`package.json`](../../package.json) `documentationUrl` lands
      somewhere useful after the README rewrite.

---

## Sequencing

The vendored sample (task 1) is the load-bearing piece — both the README and
the getting-started doc reference it. Order:

1. Sample first (1).
2. Getting-started doc (2) — can be drafted in parallel with the sample once
   the file layout is decided.
3. Error handling doc (4) — trivial cut/paste, do alongside the README.
4. README rewrite (3) — last, once it has stable targets to link to.
5. Audit (6) — parallel, separate PRs.
6. Follow-up issues (5) — file as we go.

One PR per task is fine; the sample + getting-started doc could land
together since they're tightly coupled.
