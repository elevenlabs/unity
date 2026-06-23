# StandaloneSmokeTest

IL2CPP-built desktop standalone smoke test that drives
`Conversation.StartSessionAsync` against a real ElevenLabs agent over the
native `ClientWebSocket` transport. Mirrors `Samples/ConversationSmokeTest/`
but exercises the non-WebGL session path so the
[`Runtime/Native/link.xml`](../../Runtime/Native/link.xml) AOT preservation
is verified end-to-end before shipping.

## What it asserts

- **Build / link survives IL2CPP managed stripping:** the binary produced
  by `HostBuild.BuildStandalone` runs to `Application.Quit` without a
  `JsonReaderException` from a stripped `[JsonProperty]` setter. The
  `link.xml` preserves the `ElevenLabs.Protocol.*` namespace explicitly;
  this smoke catches regressions in either the preserve directive or the
  consumer-side `[AlwaysLinkAssembly]` glue.
- **Native transport handshake completes:** the conversation_initiation
  round-trip via Newtonsoft.Json over `IncomingSocketEventConverter`
  populates `Conversation.ConversationId` before the first user message.
- **Agent reply is non-empty:** the smoke fails fast if
  `AgentResponded.AgentResponse` deserializes to an empty string —
  symptomatic of a stripped property setter on the corresponding
  protocol DTO.

## Local setup

1. Configure agent credentials:
   - In the Unity Editor: **Assets → Create → ElevenLabs → Standalone Smoke Config**.
   - Move the asset into any folder named `Resources` somewhere under
     `Assets/` (e.g. `Assets/Resources/StandaloneSmokeConfig.asset`).
   - Fill in `agentId` from your ElevenLabs dashboard.
   - The asset is gitignored — it never leaves your machine.

2. Build the IL2CPP standalone (~5-10 minutes on a cold cache; subsequent
   incremental builds are faster):

   ```bash
   bash TestProject/build-standalone.sh
   ```

   Output lands under `TestProject/Build/Standalone/`. On macOS this is a
   `.app` bundle; on Windows / Linux it's a flat directory with the
   executable + Player data.

   > **After the smoke, before running `pnpm --dir TestProject run test`
   > again, remove `TestProject/Build/Standalone/`.** The IL2CPP build
   > leaves stripped `UnityEngine.*.dll` files under
   > `<name>_BackUpThisFolder_ButDontShipItWithYourGame/Managed/` that
   > Unity then re-imports as part of the package on the next batchmode
   > invocation (the package is referenced as `file:../..`, so the
   > embedded TestProject is also part of the package tree). Symptoms:
   > stale CS0117 errors or a hung AssemblyUpdater.

3. Run the binary. On macOS, an `ELEVENLABS_AGENT_ID` env var overrides the
   asset's `agentId`:

   ```bash
   ELEVENLABS_AGENT_ID=agent_xxx \
     open -W TestProject/Build/Standalone/StandaloneSmoke.app
   ```

   `-W` blocks the shell on the binary's exit so the shell exit code
   reflects the smoke result. Player.log paths vary per OS:

   - macOS: `~/Library/Logs/<Company>/<Product>/Player.log`
     (`~/Library/Logs/DefaultCompany/unity-minimal-test/Player.log` for
     the in-repo TestProject)
   - Windows: `%USERPROFILE%\AppData\LocalLow\<Company>\<Product>\Player.log`
   - Linux: `~/.config/unity3d/<Company>/<Product>/Player.log`

   Grep the log for `[StandaloneSmoke]` lines to see the run trail.

   > **macOS first-run microphone prompt.** The native launcher always
   > opens the microphone (`UnityMicrophoneInput` is the default I/O
   > controller — see [`Runtime/Native/NativeSessionLauncher.cs`](../../Runtime/Native/NativeSessionLauncher.cs)),
   > so the first run on a fresh macOS user requires interactively
   > accepting the microphone permission popup. CI needs either:
   > a self-hosted runner with TCC pre-granted, an environment flag to
   > route the smoke through `NullInputController` (TODO), or a manual
   > first-run on the runner before the automated job. Hosted GitHub
   > runners cannot accept TCC prompts and will hang.

## CI

No automated CI integration ships at v0.1 — the smoke is a manual
gate before tagging. To wire it into CI later:

1. Materialise `TestProject/Assets/Resources/StandaloneSmokeConfig.asset`
   from a secret (the file is a standard Unity `.asset` YAML — see the
   locally-created file for the exact shape).
2. Invoke `bash TestProject/build-standalone.sh` on a runner with a
   Unity 6 LTS installation and Mac standalone IL2CPP support.
3. Run the produced binary with `open -W` (macOS) and assert the binary
   exits 0; tail the Player.log for the `All standalone smoke checks
   passed!` line as an additional sanity check.

Without the config asset the binary logs `[StandaloneSmoke] CONFIG MISSING`
and exits 0, so a clean clone still produces a buildable + runnable
artifact for build-pipeline validation.
