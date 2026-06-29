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

**Resolution (2026-06-29):** README rewrite landed without media —
deferred to the existing "Record landing media" follow-up (task 5).
Plan stays on option 1; the README has an HTML comment marking where
the recording lands once captured.

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
- [x] Decide what to do with `Samples/QuickStart/`. **Keep** as the
      minimal "no graphics, just transcript" debug sample. It's the
      smallest possible smoke test and the README's samples table now
      lists it alongside `GettingStarted` as the minimal counterpart.
- [x] Decide what to do with `Samples/BridgeSmokeTest/`,
      `Samples/ConversationSmokeTest/`, `Samples/StandaloneSmokeTest/`.
      **Already excluded** from the shipped package — verified by
      reading `package.json`'s `files` array (only `Samples/QuickStart`
      and `Samples/GettingStarted` are listed) and its `samples` array
      (same two). No action needed; the smoke tests live in-repo for
      our integration tests but never reach UPM consumers.
- [x] **Follow-up — move `Samples/` → `Samples~/`.** Done for the two
      user-facing samples: `Samples/QuickStart` → `Samples~/QuickStart`
      and `Samples/GettingStarted` → `Samples~/GettingStarted`. The
      smoke harnesses (`BridgeSmokeTest/`, `ConversationSmokeTest/`,
      `StandaloneSmokeTest/`) stay under `Samples/` because Unity must
      compile them so `HostBuild` can load the MonoBehaviour types via
      `Type.GetType` (per the v0.1-parity plan's earlier decision); they
      were already excluded from `package.json#files`, so UPM consumers
      never saw them. Updated `package.json#files` + `#samples` paths,
      deleted the now-stale `Samples/QuickStart.meta` /
      `Samples/GettingStarted.meta`, and repointed README /
      GETTING_STARTED.md / COMPATIBILITY.md / CONTRIBUTING.md links at
      the new `Samples~/` paths.

### 2. Write `Docs~/GETTING_STARTED.md`

- [x] Prereqs section: Unity 6.3 LTS, free ElevenLabs account, an agent
      created in the dashboard with authentication disabled.
- [x] **Option A — start from the SDK sample (recommended).** Import
      `GettingStarted` via UPM Samples, open the scene, press Play.
      ~3 steps end-to-end.
- [x] **Option B — add to your own project.** Walkthrough:
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
- [x] "What to try next" section: change `mood`, add a second box with a
      different `color`, link to client-tool docs once they exist.
- [x] Link back to [`COMPATIBILITY.md`](../../COMPATIBILITY.md) and
      [`ERROR_HANDLING.md`](../ERROR_HANDLING.md) at the end.

### 3. Rewrite `README.md`

- [x] Lead with 1–2 sentence pitch. Landing media **deferred** to a
      follow-up (see open question + task 5) — the README has an HTML
      comment marking where the recording lands once captured.
- [x] Install section: UPM via git URL. One-liner pointing to the
      Compatibility doc for Player Settings details.
- [x] "Get started" → link to [`Docs~/GETTING_STARTED.md`](../GETTING_STARTED.md).
- [x] "Samples" → short table of the shipped samples
      (GettingStarted as the headline, QuickStart as the minimal one).
- [x] "Documentation" → links to Architecture, Compatibility, Error
      handling, Contributing, Changelog.
- [x] Move "Error types" section → [`Docs~/ERROR_HANDLING.md`](../ERROR_HANDLING.md).
      (Landed in an earlier commit; the trimmed-README pointer was
      replaced wholesale by the rewrite.)
- [x] Delete "Mapping from the `@elevenlabs/client` JS SDK" section.
      (Same — was already gone from the README before the rewrite; the
      mapping table lives in `ERROR_HANDLING.md`.)

### 4. Add `Docs~/ERROR_HANDLING.md`

- [x] Move the README's current "Error types" section verbatim. README
      now has a one-paragraph "Error handling" pointer in its place; full
      rewrite still pending under task 3.
- [x] No structural changes — that section is in good shape; it's just
      misplaced.

### 5. Track follow-ups as issues

- [x] File "Publish to OpenUPM" issue — [#18](https://github.com/elevenlabs/unity/issues/18).
- [x] File "Record landing media" issue — [#19](https://github.com/elevenlabs/unity/issues/19)
      (option 1: 30s recording with audio, embed as inline video).

### 6. Public-readiness audit (parallel pass — separate plan if it grows)

- [x] Confirm [`LICENSE`](../../LICENSE) matches the org's preferred license
      for public OSS. MIT — standard permissive OSS, ready as-is.
- [x] [`.github/`](../../.github/) — CI workflows (unity-tests, lint,
      integration) all present and functional. Issue + PR templates,
      CODEOWNERS, and SECURITY.md are missing; tracked as a follow-up
      ([#20](https://github.com/elevenlabs/unity/issues/20)) since they're
      polish, not blockers.
- [x] [`CONTRIBUTING.md`](../../CONTRIBUTING.md) — confirmed OK for now per
      user.
- [x] [`Docs~/plans/`](.) — these are working design docs. Decision: keep
      public. They tell the story of how we got here; the audit spot-checked
      the most recently modified plans and found no unredacted credentials,
      customer names, or internal gossip.
- [x] Grep for internal-only references: Slack channels, internal monorepo
      links, personal usernames in non-commit pointers, hardcoded paths
      under `/Users/`. Clean — the only `/Users/` paths are in
      [`.claude/CLAUDE.md`](../../.claude/CLAUDE.md) (Claude Code context
      file — not shipped, intentionally local) and inside this plan file
      (which now points at the vendored sample instead). No Slack channels,
      no internal monorepo URLs in shipped surfaces, no leaked secrets.
- [x] Confirm [`package.json`](../../package.json) `documentationUrl` lands
      somewhere useful after the README rewrite. Points at
      `https://github.com/elevenlabs/unity#readme` — the rewritten slim
      landing page, which is exactly the right destination.

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
