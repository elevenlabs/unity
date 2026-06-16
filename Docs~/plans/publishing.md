# Publishing — UPM distribution plan

## Context

[`plan-b.md`](./plan-b.md) covers the C#/JS architectural pivot but says
nothing about how the SDK is shipped to consumers — it only notes that "the
RFC's distribution plan … stays the same." That distribution plan lives in
[`initial-rfc.md`](./initial-rfc.md) (the "Distribution" section), and so
far the only execution against it is **HP.8** in
[`generic-bridge-primitives.md`](./generic-bridge-primitives.md): the
`package.json` `"files"` allowlist that scopes the npm-pack payload to
`Runtime/ Editor/ Tests/ Plugins/ CONTRIBUTING.md` (npm auto-includes
`package.json`, `README.md`, `CHANGELOG.md`, `LICENSE`).

This plan fills the gap. It captures the three distribution channels the
RFC ranks (git URL → scoped registry → Asset Store), the
[OpenUPM acceptance criteria](https://openupm.com/docs/adding-upm-package.html#upm-package-criteria)
we'd need to clear if we ever list there, and the concrete state of
`package.json` / `LICENSE` / `CHANGELOG.md` / `README.md` against both.

The intended outcome: a clear v0.1 release path (git URL), a clean
graduation path to a scoped registry, and a checklist of what's still
missing before OpenUPM is a real option.

## Current state (verified 2026-06-16)

| Asset | State | Notes |
|---|---|---|
| `package.json` `name` | `io.elevenlabs.agents` | Valid reverse-DNS for the real `elevenlabs.io` domain (≥3 segments, no reserved scope). |
| `package.json` `version` | `0.1.0` | Matches `CHANGELOG.md`. |
| `package.json` `unity` | `6000.0` | Unity 6 LTS floor. |
| `package.json` `files` | allowlist present (HP.8) | Payload scoped; tilde dirs (`Bridge~/ Codegen~/ Docs~/`) excluded by Unity convention regardless. |
| `package.json` `repository` | **missing** | OpenUPM doesn't strictly require it but `npm publish` / Unity Package Manager UI surface it; add it. |
| `LICENSE` | MIT, ©2026 ElevenLabs | OpenUPM Criterion 3 ✅ (SPDX `MIT`). |
| `CHANGELOG.md` | Keep-a-Changelog | `[0.1.0]` still marked `Unreleased`; needs a date at tag time. |
| `README.md` | 2-line stub | No install / quickstart yet — required for any channel. |
| `Samples~/` | absent | OK for v0.1; revisit when Components package lands (RFC open Q3). |
| `.github/workflows/` | none | No tag-driven release automation today. |
| Git remote | `git@github.com:elevenlabs/unity.git` | Matches `package.json` URLs. |

## Phase 1 — UPM via Git URL (v0.1 launch channel)

Per the RFC, the launch channel. Zero infrastructure; version pinning via
git tags.

Consumer line in their `Packages/manifest.json`:

```json
"io.elevenlabs.agents": "https://github.com/elevenlabs/unity.git#v0.1.0"
```

What's needed to cut v0.1.0:

1. **Add `repository` field** to `package.json` (`{"type":"git","url":"https://github.com/elevenlabs/unity.git"}`).
2. **Date the `0.1.0` entry** in `CHANGELOG.md` (replace "Unreleased").
3. **Expand `README.md`** with an Install section (git URL one-liner +
   Unity-version note) and a 30-second quickstart that imports
   `ElevenLabs.Agents` and starts a session.
4. **Tag `v0.1.0`** on `main` once Plan B Phase 6 lands. Tag name must
   match `package.json#version` exactly (OpenUPM Criterion 5 — also UPM
   git-URL convention).
5. **Smoke-install** from a clean Unity 6 project against the tag and
   confirm the package resolves, compiles, and runs the sanity test.

No CI required for this channel — tagging is manual.

## Phase 2 — UPM scoped registry (post-stabilisation)

Per the RFC, the graduation channel "once the SDK stabilizes." Gives
semver discoverability and the standard Package Manager UI install flow.

Decision points (defer until v0.2 closes; capture rationale here):

- **Registry host.** Three options the RFC names:
  - **npmjs.com** — zero infra, free public publishes, but anyone can
    grab the scope. Need to register `@elevenlabs` (or equivalent) and
    publish under `io.elevenlabs.*` package names; npm scoping and UPM
    package naming are independent axes.
  - **GitHub Packages** — uses GitHub auth, lives next to the repo,
    requires consumers to add a `~/.upmconfig.toml` auth token (rough UX).
  - **Self-hosted** (Verdaccio etc.) — most control, most infra. Skip
    unless we hit a constraint the hosted options can't meet.
  - **Recommendation:** npmjs.com. Simplest install UX for consumers; the
    HP.8 `files` allowlist already constrains the published tarball.
- **Publish trigger.** GitHub Action on tag push (`v*`) that runs
  `npm publish --access public` after the existing `verify:*` gates
  (`csharpier check`, `Bridge~ verify:primitives`,
  `Bridge~ verify:connection`, `Codegen~ verify:protocol-dtos`,
  `TestProject/run-tests.sh`). New file:
  `.github/workflows/release.yml`.
- **README install section** gains a scoped-registry stanza for
  `Packages/manifest.json`:

  ```json
  {
    "scopedRegistries": [
      { "name": "ElevenLabs", "url": "https://registry.npmjs.org",
        "scopes": ["io.elevenlabs"] }
    ],
    "dependencies": { "io.elevenlabs.agents": "0.2.0" }
  }
  ```

Nothing in the package layout itself needs to change between Phase 1 and
Phase 2 — the `files` allowlist (HP.8) already produces the same tarball
that `npm publish` will upload.

## Phase 3 — OpenUPM (optional future channel)

OpenUPM is a community registry that auto-builds from GitHub tags. Listing
costs nothing but commits us to its
[13 acceptance criteria](https://openupm.com/docs/adding-upm-package.html#upm-package-criteria).
This section is the compliance walkthrough — keep it current as we move
toward listing.

| # | OpenUPM criterion | Our status | Gap to close |
|---|---|---|---|
| 1 | Reverse-DNS name, ≥3 segments, no reserved scope | ✅ `io.elevenlabs.agents` | None (avoid `com.unity`/`com.github`/`com.example` — we do). |
| 2 | Complies with Unity's ToS + Package Guidelines | ✅ believed | Re-read [Unity Package Guidelines](https://unity.com/legal/terms-of-service/software) once before submission. |
| 3 | Open source, GitHub-hosted, SPDX license | ✅ MIT on github.com/elevenlabs/unity | None. |
| 4 | Functional, tested, not a test package | ⏳ | Land Plan B Phases 4–6 first; ship v0.1 with a passing edit-mode sanity test + at least one end-to-end smoke. |
| 5 | Semver; git tag matches `package.json#version` | ✅ semver | Enforce in release workflow (Phase 2 above). |
| 6 | Package <512 MB | ✅ | The `files` allowlist keeps the tarball minimal (kB-scale). |
| 7 | Legal / content compliance | ✅ | None. |
| 8 | Fork policy | n/a | Not a fork. |
| 9 | Not already on Unity's official registry | ✅ | None. |
| 10 | NuGet uplinks via UnityNuGet | n/a today | Our only NuGet-shaped dep is `com.unity.nuget.newtonsoft-json` (Unity-supplied). If we add another NuGet dep later, route via UnityNuGet rather than a direct OpenUPM publish. |
| 11 | ≥1 release per 3 months | ⏳ | Trivially met during active dev; flag if the project goes dormant. |
| 12 | No mass re-branding | ✅ | Original work. |
| 13 | Accurate `topics` on the GitHub repo | ⏳ | Set repo topics to e.g. `unity`, `unity-package`, `elevenlabs`, `voice-ai`, `webgl`, `upm` once the repo goes public. |

**Net:** the only structural gap is **Criterion 4** (functionality
proof) — which lands naturally with Plan B completion. Everything else is
either already true or a checklist item at submission time. No code change
in this repo is needed *for OpenUPM specifically*; the Phase 1 + Phase 2
work already moves us into compliance.

## Phase 4 — Asset Store (deferred per RFC)

Per RFC: "Worth considering once we have a paying Unity customer base."
Out of scope for this plan; revisit when there's commercial pull. The
publishing toolchain (HP.8 + tagged releases) is a precondition either
way.

## Files to touch (Phase 1)

| File | Change |
|---|---|
| `package.json` | Add `repository` field. |
| `CHANGELOG.md` | Replace `[0.1.0] - Unreleased` with the tag date; add `## [Unreleased]` placeholder above it. |
| `README.md` | Add Install (git URL) and Quickstart sections. |

Phase 2 adds `.github/workflows/release.yml` and a scoped-registry README
section; no other file changes.

## Verification

For Phase 1 (git URL):

1. `npm pack` in a clean checkout — inspect the resulting tarball and
   confirm only the allowlisted directories + auto-included metadata
   files are present (HP.8 already verified the empirical behaviour;
   re-run after any `files` change).
2. In a throwaway Unity 6 project, add the git-URL line against the
   `v0.1.0` tag, let UPM resolve, and run the bundled sanity test via
   `TestProject/run-tests.sh` adapted to the consumer project (or run
   the package's `Tests/` assembly through the Test Runner UI).
3. WebGL smoke: `TestProject/build-webgl.sh` from the consumer project
   to confirm the `Plugins/WebGL/*.jslib` artefacts ship and link.

For Phase 2 (scoped registry):

4. `npm publish --dry-run --access public` locally before wiring CI; the
   reported file list must match the `npm pack` tarball from step 1.
5. After the first real publish, install the package by version (not git
   URL) in a fresh Unity project via the manifest stanza above; confirm
   Package Manager shows the package under "In Project" with the right
   metadata.

For Phase 3 (OpenUPM): no Unity-side verification beyond the above —
OpenUPM listing is a one-time submission. Re-run the compliance table
above before opening the listing PR.
