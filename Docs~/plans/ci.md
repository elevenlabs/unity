# CI Plan — Test Surface, Credentials, Workflow Shape

## Status — 2026-06-17

**Unity lanes are BLOCKED pending a response from Unity support** on
whether the ElevenLabs Industry org can host a CI bot user without
consuming a paid seat. Initial attempt used a Unity Service Account,
but those issue API-only credentials (Key ID + Secret, HTTP Basic)
that cannot activate the Unity Editor inside game-ci's Docker
container — game-ci needs a real Unity ID with email + password +
serial. A regular org member would consume a seat, which conflicts
with the 1-seat Industry trial. Email out to Unity contact 2026-06-17
asking about a build-user / CI-bot tier. See "Credential-type detour"
below for the full path through this trap so it doesn't get re-walked
on the next revisit.

The **lint lane** (no Unity, no secrets) remains unblocked and can
land independently as task 5.3.1.

## Goal

Run the existing local test surface in GitHub Actions on every PR and on
pushes to `main`. Land the workflow incrementally — the no-Unity checks
first (they're free and self-contained), then the Unity-gated jobs once
the license decision is made.

This doc is the gating analysis. No `.github/workflows/*.yml` exists
yet; landing one needs a license-type decision from a repo maintainer
(see "Open decisions" at the bottom).

## Test surface inventory

Six distinct check commands run today, with very different infra needs.

| # | Command | Working dir | Runtime needs | Wall time (local) | Needs Unity? |
|---|---|---|---|---|---|
| 1 | `dotnet csharpier check .` | repo root | .NET SDK + tools restore | ~3 s | No |
| 2 | `pnpm run typecheck && format:check && lint && test` | `Bridge~/` | Node 23+, pnpm | ~10 s | No |
| 3 | `pnpm run typecheck && format:check && lint && verify:protocol-dtos` | `Codegen~/` | Node 23+, pnpm, .NET (for round-trip) | ~15 s | No |
| 4 | `pnpm run typecheck` | `TestProject/` | Node 23+, pnpm | ~3 s | No |
| 5 | `pnpm --dir TestProject run test` (Edit Mode tests) | repo root | Unity 6000.3.6f1 + license | ~90 s | **Yes** |
| 6 | `pnpm --dir IntegrationTests~ run test` (browser-mode WebGL) | repo root | Node + Playwright + a fresh WebGL build at `TestProject/Build/WebGL/` | ~5 s + WebGL build (~3-5 min) | **Yes** (build only) |

(1)-(4) are the **lint lane** — pure JS/TS/C# tooling, runs in any
GitHub-hosted Linux/macOS runner, no secrets, no Docker. Should land
first since it's risk-free and immediately catches the bulk of
regressions (formatter drift, missing imports, codegen drift, broken
unit tests).

(5) is the **Unity Edit Mode lane** — needs `game-ci/unity-test-runner@v4`
which pulls a ~7 GB Docker image and activates a Unity license.

(6) is the **integration lane** — depends on (5)'s WebGL build artifact.
Once the WebGL bundle exists, the Playwright Chromium harness runs in
under 5 seconds.

## Credential-type detour — Unity Service Accounts are NOT for Editor activation

A trap worth documenting before the recommendation, because we walked
into it on 2026-06-17 and it ate a round-trip. Unity has two
unrelated credential systems:

| System | Auth shape | Used for |
|---|---|---|
| **Unity ID** | email + password (+ serial for activation) | Logging into Unity Hub, activating the Editor on a machine. This is what game-ci needs. |
| **Unity Service Account** | Key ID + Secret → HTTP Basic auth header | Unity Cloud / Services REST APIs (Build Automation, Asset Manager, Cloud Save, Authentication). Cannot activate the Editor. |

Service Accounts are free to create and do not consume seats — that
property tempted us into thinking they were the right CI identity.
They aren't. game-ci's unity-test-runner / unity-builder runs the
real Unity Editor in Docker and authenticates against Unity's
licensing server with the email/password/serial trio, not the API
key. A Service Account configured into `UNITY_EMAIL` /
`UNITY_PASSWORD` will fail activation at the first license check.

If we instead want to use those API keys for CI, the pivot is Unity
Build Automation (formerly Cloud Build) — Unity hosts the build
runners, GitHub Actions just triggers via REST and downloads the
artifact. That's a separate workflow shape (not game-ci) and is
build-only — it doesn't run Edit Mode tests. Path noted but not
expanded; not the current direction.

## Credentials required for the Unity lanes

The repo maintainer has an active **Unity Industry Trial** (org slug
`kraen_unity`, valid 2026-06-15 → 2026-07-19). The entitlement file at
`~/Library/Unity/licenses/UnityEntitlementLicense.xml` is tagged
`UnityPro` and grants full Pro features. The local cache does NOT
expose the serial, but a serial key IS available via the Unity ID
web dashboard (Unity ID → My Seats → reveal serial). This unlocks
game-ci's standard Pro/Plus serial activation path — same flow Pro
and Industry seats both use.

Three activation options follow, recommendation now ordered around the
serial-based path.

### Option A — Industry serial + dedicated CI Unity ID (recommended, BLOCKED on seat-cost confirmation)

The Industry trial is `UnityPro`-tagged, so game-ci's serial-based
activation works identically to a Pro/Plus seat. Two activations per
seat are allowed (Pro tier baseline), so concurrency:2 is the natural
ceiling — no Personal-seat single-machine contention.

Credentials should sit on a **dedicated CI Unity ID** invited to the
ElevenLabs org as a member, not the maintainer's personal Unity ID.
That isolates the credentials and avoids 2FA conflicts. **Open
question:** does Industry let us add such a CI member without
consuming a paid seat? A regular member normally does; Unity Service
Accounts don't help here (see detour above). Awaiting response from
Unity support (email out 2026-06-17). Until then, every secret-name
detail below is provisional.

Secrets to add (once unblocked):

| Secret | Source |
|---|---|
| `UNITY_SERIAL` | Serial key from the Unity ID web dashboard, format `XX-XXXX-XXXX-XXXX-XXXX-XXXX`. Treat as sensitive — anyone with the serial + email + password can activate against the seat. |
| `UNITY_EMAIL` | CI Unity ID email (e.g. `elevenlabs-unity-ci@…`), invited to the ElevenLabs Unity org as a member |
| `UNITY_PASSWORD` | CI Unity ID password — **must be alphanumeric mixed-case only** (game-ci docs explicitly call out failures on special characters). Easy to satisfy since this account is fresh and not used interactively. |

The three secrets currently configured on the repo
(`UNITY_AUTHORIZATION_HEADER`, `UNITY_KEY_ID`, `UNITY_SECRET_KEY`)
correspond to a Unity Service Account API key (see detour above) and
will need to be deleted once the real CI Unity ID is provisioned —
they're not usable for game-ci.

Operational caveats:

- **Trial expiry 2026-07-19.** When the trial ends, CI breaks until
  the license converts to paid Industry (rotates the serial), is
  downgraded to Pro, or the workflow falls back to Option B (Personal
  seat). Set a calendar reminder for ~3 days before expiry to revisit.
- **Concurrency: 2.** Pro/Industry seats allow up to 2 simultaneous
  activations. The workflow can run two PRs in parallel before
  hitting `LICENSE_ALREADY_IN_USE`. Use
  `concurrency.group: unity-ci-${{ github.ref }}`,
  `cancel-in-progress: true` so a force-push doesn't fight against
  itself. No global guard needed for low-throughput cadence; tighten
  to `group: unity-ci` (no ref suffix) if seat-fighting appears.
- **Activation eats one seat slot for the duration of the job.** Pair
  game-ci/unity-test-runner / unity-builder with
  game-ci/unity-return-license at the end of the workflow so the seat
  is freed promptly even when later steps fail (use `if: always()`).
  Without it, an aborted run can leave a seat locked until the
  activation TTL expires.
- **Service account scope.** Provision with only the rights CI needs
  — Unity org member, no admin, no billing, no project ownership. A
  leaked service-account secret then exposes only what the workflow
  could do anyway, not the broader Unity ID surface.
- **2FA.** Service accounts let you skip 2FA without weakening the
  maintainer's personal account. Don't enable 2FA on the service
  account — game-ci has no app-password flow and the activation will
  fail.

### Option B — Free Personal seat on a dedicated CI Unity ID

Fallback if Option A becomes unavailable (trial expiry without
renewal, organization-policy concerns about exposing the maintainer's
Unity ID to GitHub Actions secrets). A separate CI-only Unity ID holds
a free Personal seat; game-ci uses the ULF activation path.

A Unity ID can hold multiple license entitlements. The maintainer
keeps the Industry trial on their primary ID for local dev; a separate
**CI-only Unity ID** (e.g. `elevenlabs-unity-ci@<domain>`) holds a free
Personal seat, exported as a ULF and used by game-ci. The two seats
never collide because they're on different accounts.

Secrets to add:

| Secret | Source |
|---|---|
| `UNITY_LICENSE` | Contents of `Unity_v6000.x.ulf` generated against the CI Unity ID via Unity Hub → Preferences → Licenses → Add → Personal. Paste the entire XML as-is. |
| `UNITY_EMAIL` | CI Unity ID email |
| `UNITY_PASSWORD` | CI Unity ID password — **must be alphanumeric mixed-case only** (game-ci docs explicitly call out failures on special characters) |

Operational caveats:

- Personal seats are tied to one machine at a time. The CI Unity ID
  must not be used to open Unity Editor anywhere else while CI is
  running — separate account avoids this entirely.
- Multiple PRs landing concurrently will fight for the single CI seat
  and fail with `LICENSE_ALREADY_IN_USE`. Mitigation: pin the Unity
  workflow to `concurrency.group: unity-ci`, `cancel-in-progress: true`.
  Acceptable for a small-team cadence; revisit if PR throughput grows.
- Personal seats periodically need re-activation
  (game-ci/unity-license-activate handles this, or run the manual ULF
  refresh annually).

### Option C — Unity License Server (floating Industry seats)

The Unity-official path for Industry tier in CI: stand up a
self-hosted Unity License Server (Docker container) that issues
floating seats from the org's Industry pool. game-ci's
`unityLicensingServer: <url>` input acquires a seat before the build
and returns it after.

| Secret | Source |
|---|---|
| `UNITY_LICENSING_SERVER_URL` | URL of the self-hosted license server (must be reachable from GitHub runners — either publicly addressable, or a self-hosted runner inside the same network) |
| `UNITY_SERVICES_CONFIG` | Base64-encoded `services-config.json` issued by the license server admin console |

Operational caveats:

- Requires Unity Industry admin approval to issue floating seats from
  the org pool — depends on how the trial converts to a paid plan.
- Hosting the license server is a real operational concern (DNS, TLS,
  uptime). Public exposure of the server is the default; a self-hosted
  GitHub runner inside the same VPC sidesteps that.
- Costs scale with concurrent activations rather than per-developer.
- Overkill for the current single-maintainer / low-PR-throughput
  scenario but the right end state once Industry converts to paid.

### Option D — Defer Unity CI until the trial converts

The Industry trial expires 2026-07-19. If the path forward (paid
Industry, downgrade to Pro, switch to Personal-only) is undecided,
keep Unity CI manual (the existing local `pnpm --dir TestProject run test`
+ `bash TestProject/build-webgl.sh` flow) and only land the lint
workflow now. Revisit once the licensing model is stable.

### Recommendation

**Option A (Industry serial + dedicated CI Unity ID)** is the target
shape, pending Unity confirmation that the CI member can be added
without a seat cost. If Unity confirms a free build-user pattern,
proceed directly. If not, fall through to Option B (Personal seat on
a separate Unity ID, free, concurrency:1) — slightly worse ergonomics
but unblocks the Unity lanes immediately. Option C stays as the
long-term right-shape for paid Industry with parallel throughput.

## Recommended workflow shape

Three separate workflow files, each with its own runtime profile and
secrets requirements:

### `.github/workflows/lint.yml` (no secrets)

- Trigger: every PR + push to `main`
- Runner: `ubuntu-latest`
- Steps:
  1. Checkout
  2. Setup Node 23 + pnpm + .NET 9 (multi-tool job)
  3. `dotnet tool restore`
  4. Run all six lint/check commands across `Bridge~/`, `Codegen~/`,
     `TestProject/`, and `csharpier check .`
- Expected wall time: 1-2 minutes

### `.github/workflows/unity-tests.yml` (Unity secrets)

- Trigger: every PR + push to `main`
- Concurrency: `group: unity-${{ github.ref }}`, `cancel-in-progress: true`
  (so a force-push doesn't queue two activations)
- Runner: `ubuntu-latest` (game-ci needs Linux for the Docker images)
- Action: `game-ci/unity-test-runner@v4`
- Inputs:
  - `unityVersion: 6000.3.6f1`
  - `testMode: EditMode`
  - `projectPath: TestProject`
  - `customImage: unityci/editor:ubuntu-6000.3.6f1-webgl-3` (confirmed available, ~7 GB)
- Cache: `actions/cache@v4` on `TestProject/Library/` keyed by `Packages/packages-lock.json` + `ProjectSettings/ProjectSettings.asset`
- Expected wall time: 8-15 minutes (mostly the Library cache miss on first run, then ~3 minutes warm)

### `.github/workflows/integration.yml` (Unity secrets)

- Trigger: every PR + push to `main`
- Same concurrency guard as unity-tests
- Two jobs in sequence:
  1. **build-webgl** — `game-ci/unity-builder@v4` with `targetPlatform: WebGL`, uploads `TestProject/Build/WebGL/` as a workflow artifact (~7 MB compressed)
  2. **integration-test** — `needs: build-webgl`, downloads artifact, runs `pnpm --dir IntegrationTests~ install && run setup && run test`
- Expected wall time: ~15 minutes warm, ~25 minutes cold

These workflows can run in parallel after lint succeeds. Lint is the
fastest signal and should fail fast, so it doesn't gate the slower
lanes.

## Open decisions

1. **License type — BLOCKED on Unity response 2026-06-17.** Target
   shape is Option A (Industry serial + dedicated CI Unity ID). Open
   question to Unity: can the org host a CI bot member without
   consuming a paid seat? Falls back to Option B (Personal seat on a
   separate Unity ID) if the answer is no. Either way the maintainer
   will need to:
   - Provision the CI Unity ID with minimum rights (member only, no
     admin/billing); set an alphanumeric mixed-case password; leave
     2FA off
   - Delete the current placeholder secrets
     (`UNITY_AUTHORIZATION_HEADER`, `UNITY_KEY_ID`, `UNITY_SECRET_KEY`)
     — those are for the wrong credential system (see detour)
   - Add `UNITY_SERIAL` / `UNITY_EMAIL` / `UNITY_PASSWORD` as the
     real secrets
   - Calendar a 2026-07-16 (3 days pre-expiry) reminder to rotate
     `UNITY_SERIAL` if the trial converts to a paid Industry/Pro seat

2. ~~**Repo visibility.**~~ **Resolved 2026-06-17:** repo is going
   public. GitHub Actions minutes are unlimited on public repos, so
   the Unity Docker image cost is purely wall-clock latency, not
   billed minutes. Standard `ubuntu-latest` runners are fine.

3. **Branch protection rules.** Once workflows land, which checks
   should be required to merge a PR? Suggestion: lint required, Unity
   Edit Mode tests required, integration test optional (it's slow and
   the manual `bash TestProject/build-webgl.sh && pnpm --dir IntegrationTests~ run test`
   round-trip already gates the bridge end-to-end).

4. **What to commit.** Pre-built WebGL artifacts in the repo (via
   git-lfs) would let integration tests run without Unity in CI, but
   adds ~7 MB per build to repo size and a manual "remember to rebuild
   before pushing" gate. Rejected unless option 1 becomes blocked.

## Implementation order

- [x] **5.3.1 — Lint workflow.** Lands `.github/workflows/lint.yml`. No secrets needed; **unblocked**, can land independently of the Unity-credentials answer.
- [ ] **5.3.2 — Repo secrets configured.** Maintainer provisions the CI Unity ID (per Option A or B once Unity responds), deletes the current Service Account placeholder secrets, adds `UNITY_SERIAL` / `UNITY_EMAIL` / `UNITY_PASSWORD`. **Blocked on Unity response.**
- [ ] **5.3.3 — Unity Edit Mode workflow.** `.github/workflows/unity-tests.yml` using game-ci/unity-test-runner@v4. Depends on 5.3.2.
- [ ] **5.3.4 — Integration workflow.** `.github/workflows/integration.yml` chaining unity-builder + Playwright. Depends on 5.3.3 (proves the license activation works).

Each task lands as its own commit and is independently revertable.
