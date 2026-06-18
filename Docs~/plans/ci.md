# CI Plan — Test Surface, Credentials, Workflow Shape

## Status — 2026-06-18

**Unity lanes are UNBLOCKED — Option A (Industry serial) is active.**
Repo secrets `UNITY_EMAIL` / `UNITY_PASSWORD` / `UNITY_SERIAL` are
configured. The Service Account placeholder secrets are deleted.
Task 5.3.2 is complete; 5.3.3 is the next workflow to land.

History (kept terse so the trap-paths don't get re-walked):

- **Round 1, 2026-06-17.** Asked Unity whether the Industry org
  could host a CI bot user without consuming a paid seat.
- **Round 2 reply, 2026-06-18.** Unity pointed at **Build Server
  licenses** (included in the Industry trial), distributed via a
  self-hosted Unity **Licensing Server**. That's Option C below, and
  it's infeasible at this project's scale — single maintainer,
  GitHub-hosted runners only, no always-on infrastructure (the
  License Server is hardware-bound to its registration machine, so
  the "boot inside the Actions job" pattern doesn't work either).
- **Resolution, 2026-06-18 (independent of Unity's reply).** The
  Industry trial exposes a serial via the Unity ID web dashboard
  (Unity ID → My Seats → reveal serial). That serial works with
  game-ci's standard Pro/Plus serial-activation flow — same shape
  the action has supported for years. No further Unity
  correspondence needed; a previously drafted round-3 follow-up
  asking about ephemeral / Unity-hosted Licensing Server delivery
  was never sent because the trial serial closed the loop.

One trap-path confirmed dead and documented below so it doesn't
get re-walked:

- **Service Accounts** (Key ID + Secret) cannot activate the Editor
  — wrong credential system entirely (see "Credential-type detour 1").

A second documented-but-corrected detour:

- **Industry uses Named User Licensing in normal Hub operation**,
  which initially looked like it ruled out the serial flow entirely.
  It doesn't, for the Industry **trial** specifically — see
  "Credential-type detour 2" for the corrected reasoning and the
  open question about what happens at trial conversion.

The **lint lane** (no Unity, no secrets) has landed as task 5.3.1
and is unaffected.

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

## Credential-type detour 1 — Unity Service Accounts are NOT for Editor activation

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

## Credential-type detour 2 — Industry uses NUL in normal operation, but the trial exposes a serial

A second detour that surfaced on 2026-06-18 and was then partially
corrected the same day. Unity has two licensing **models**
(orthogonal to the credential-system distinction above):

| Model | How it activates | Tiers using it |
|---|---|---|
| **Named User Licensing (NUL)** | Sign in via Unity Hub → entitlement auto-binds to the Unity ID. | Industry, Enterprise, modern Pro (normal day-to-day Hub flow) |
| **Serial / ULF-based licensing** | Activate via serial key (`XX-XXXX-XXXX-XXXX-XXXX-XXXX`) through Unity Hub or CLI; Personal seats export a ULF instead. | Legacy Pro/Plus, the Industry **trial** (serial revealed in the Unity ID web dashboard), Personal (ULF) |

The initial reading was "Industry is NUL-only, so no serial exists
for `UNITY_SERIAL` to hold, so Option A doesn't apply." That's true
of normal Industry Hub sign-in, but it missed an empirical fact:
the Industry **trial** in fact does expose a serial via the Unity ID
web dashboard (Unity ID → My Seats → reveal serial). The serial
activates the seat through game-ci's standard Pro/Plus serial flow,
same as legacy Pro. Option A is therefore viable for the trial
window; the maintainer has confirmed this by configuring the secret
and the activation path opens for the duration of the trial.

**Open question for the next revisit:** when the trial converts to
paid Industry (or expires unconverted), does the same Unity ID
still expose a serial in the web dashboard, or does the
entitlement flip to strict NUL-only operation that requires a
Licensing Server? If the latter, this section's earlier conclusion
turns out to be correct for paid Industry — and the project would
need to fall back to Option B or accept Option C's infrastructure
cost. The calendar reminder around the trial expiry (2026-07-19) is
the gate.

Personal seats still use the older ULF export path, which is why
Option B (separate CI Unity ID holding a free Personal seat) is
documented as the fallback if the trial-serial path closes.

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

### Option A — Industry trial serial + maintainer's Unity ID (ACTIVE)

**Resolution 2026-06-18:** This is the active path. The Industry
trial exposes a serial via the Unity ID web dashboard, which
game-ci's standard Pro/Plus serial flow activates against. Secrets
are configured on the repo; the Service Account placeholders are
deleted. Trial-conversion behaviour is the open question — see
"Credential-type detour 2" for the gate.

For the trial window, we're using the **maintainer's** Unity ID
directly rather than provisioning a separate CI Unity ID — the trial
is single-seat and adding a second account doesn't help. If we later
convert to paid Industry with multiple seats, splitting to a
dedicated CI identity becomes worthwhile (smaller blast radius if a
secret leaks; 2FA can stay on for the personal account).

Secrets configured on the repo:

| Secret | Source |
|---|---|
| `UNITY_SERIAL` | Serial key from the Unity ID web dashboard, format `XX-XXXX-XXXX-XXXX-XXXX-XXXX`. Treat as sensitive — anyone with the serial + email + password can activate against the seat. |
| `UNITY_EMAIL` | Maintainer's Unity ID email. |
| `UNITY_PASSWORD` | Maintainer's Unity ID password — **must be alphanumeric mixed-case only** (game-ci docs explicitly call out failures on special characters). |

The three Service Account placeholder secrets that previously
occupied these slots (`UNITY_AUTHORIZATION_HEADER`, `UNITY_KEY_ID`,
`UNITY_SECRET_KEY`) have been deleted — they were for the wrong
credential system (see "Credential-type detour 1").

Operational caveats:

- **Trial expiry 2026-07-19.** When the trial ends, CI breaks until
  the license converts to paid Industry (which may or may not rotate
  the serial / may or may not flip to NUL-only — see "Credential-type
  detour 2"), is downgraded to Pro/Plus, or the workflow falls back
  to Option B (Personal seat on a dedicated CI Unity ID). Set a
  calendar reminder for ~3 days before expiry to revisit.
- **Concurrency: 2.** Pro/Industry seats allow up to 2 simultaneous
  activations. The workflow can run two PRs in parallel before
  hitting `LICENSE_ALREADY_IN_USE`. Use
  `concurrency.group: unity-tests-${{ github.ref }}`,
  `cancel-in-progress: true` so a force-push doesn't fight against
  itself. No global guard needed for low-throughput cadence; tighten
  to `group: unity-tests` (no ref suffix) if seat-fighting appears.
- **Activation eats one seat slot for the duration of the job.** Pair
  game-ci/unity-test-runner / unity-builder with
  game-ci/unity-return-license at the end of the workflow so the seat
  is freed promptly even when later steps fail (use `if: always()`).
  Without it, an aborted run can leave a seat locked until the
  activation TTL expires.
- **Using the maintainer's Unity ID directly (trial scope only).**
  No separate CI Unity ID is provisioned for the trial — the single
  seat means a second account doesn't help. Blast radius if the
  secrets leak: anyone with the trio can activate the seat (consuming
  the one-of-two slot) and use the Unity Editor under the
  maintainer's identity until the password is rotated. At paid
  Industry conversion, revisit splitting to a dedicated CI Unity ID.
- **2FA on the maintainer's account.** game-ci has no app-password
  flow, so 2FA can't be enabled on the Unity ID whose credentials
  are in the secrets. If 2FA becomes a security requirement, the
  workaround is to provision a dedicated CI Unity ID (2FA-off) and
  invite it into the org — same shape as the Option B fallback,
  but with the trial serial instead of a Personal ULF.

### Option B — Free Personal seat on a dedicated CI Unity ID (documented fallback, not active)

**Fallback if the trial-serial path closes.** Option A is currently
active. If trial conversion flips the entitlement to strict NUL-only
(see "Credential-type detour 2"), or if the trial expires without
conversion, the next viable path is a separate CI-only Unity ID
holding a free Personal seat. game-ci uses the standard ULF
activation flow against `UNITY_LICENSE` / `UNITY_EMAIL` /
`UNITY_PASSWORD`.

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

### Option C — Unity Licensing Server (the official Industry-CI path, INFEASIBLE for this project)

**Confirmed by Unity 2026-06-18 as the only sanctioned path for
using Industry entitlements in CI.** Build Server licenses are
included in the Industry trial seat, are **floating-only by design**,
and can only be distributed via a Unity Licensing Server that the
customer operates. game-ci's `unityLicensingServer: <url>` input
acquires a seat before the build and returns it after.

| Secret | Source |
|---|---|
| `UNITY_LICENSING_SERVER_URL` | URL of the self-hosted licensing server (must be reachable from CI runners) |
| `UNITY_SERVICES_CONFIG` | Base64-encoded `services-config.json` issued by the licensing server during setup |

Operational reality for this project (single maintainer, public repo,
GitHub-hosted runners only):

- The Licensing Server is an **ASP.NET HTTP/S service** the customer
  installs, registers with the Unity ID portal, and operates. There
  is no Unity-hosted / SaaS version.
- Setup binds the server license to the **hardware fingerprint**
  (MAC, CPU) of the registration machine. Cloud-VM hosting is
  officially unsupported because instance restarts can invalidate
  the binding and require a Unity Customer Service ticket to reset.
- Two viable hosting shapes, both blocked at our infrastructure
  scale:
  - **Self-hosted GitHub runner** on always-on local hardware (Mac /
    workstation) with the Licensing Server co-located. We have no
    self-hosted runner pool and committing to one for a small OSS
    package isn't justified.
  - **Publicly addressable Licensing Server** with our own TLS cert,
    firewall, uptime monitoring. Same maintenance burden plus
    public-attack-surface concerns.
- Ephemeral pattern ("boot the Licensing Server inside the GitHub
  Actions job, acquire a seat, tear down") **does not work** on
  GitHub-hosted runners — every job runs on a fresh VM with a
  different hardware fingerprint, and the license archive is bound
  to the registration machine.

This option is the **right end-state** if the project ever justifies
self-hosted CI infrastructure (paid Industry conversion + multiple
maintainers + higher PR throughput), and **not viable now**.

### Option D — Defer Unity CI (obsolete; was the state while Options A/B were unresolved)

Kept as a historical anchor only. While the licensing question was
open (2026-06-17 → 2026-06-18), Unity CI stayed manual via the local
`pnpm --dir TestProject run test` + `bash TestProject/build-webgl.sh`
flow and only the lint workflow ran in GitHub Actions. Superseded by
Option A on 2026-06-18.

### Recommendation

**Option A — Industry trial serial + maintainer's Unity ID.** Active
as of 2026-06-18; secrets configured. Decision tree at trial expiry
(2026-07-19) or if conversion flips to NUL-only:

- **Option B** if the trial-serial path closes (free Personal seat
  on a dedicated CI Unity ID). Lower throughput (concurrency:1) and
  the ergonomic cost of maintaining a second Unity ID, but free,
  infrastructure-light, and works on GitHub-hosted runners out of
  the box.
- **Option C** if the project ever justifies self-hosted CI
  infrastructure (paid Industry conversion + multiple maintainers +
  higher PR throughput). Right long-term shape, infeasible now.

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

1. ~~**License type.**~~ **Resolved 2026-06-18:** Option A active
   via the Industry trial serial; `UNITY_EMAIL` / `UNITY_PASSWORD` /
   `UNITY_SERIAL` configured; Service Account placeholders deleted.
   Re-decide at trial expiry (2026-07-19): if paid Industry
   conversion still exposes a serial, stay on Option A; if it flips
   to strict NUL-only, fall back to Option B (provision a CI-only
   Unity ID, generate a Personal ULF, swap the secrets).

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

- [x] **5.3.1 — Lint workflow.** Landed `.github/workflows/lint.yml`. No secrets needed.
- [x] **5.3.2 — Repo secrets configured.** `UNITY_EMAIL` / `UNITY_PASSWORD` / `UNITY_SERIAL` configured against the maintainer's Unity ID + Industry trial serial; Service Account placeholders (`UNITY_AUTHORIZATION_HEADER`, `UNITY_KEY_ID`, `UNITY_SECRET_KEY`) deleted.
- [ ] **5.3.3 — Unity Edit Mode workflow.** `.github/workflows/unity-tests.yml` using game-ci/unity-test-runner@v4.
- [ ] **5.3.4 — Integration workflow.** `.github/workflows/integration.yml` chaining unity-builder + Playwright. Depends on 5.3.3 (proves the license activation works).

Each task lands as its own commit and is independently revertable.
