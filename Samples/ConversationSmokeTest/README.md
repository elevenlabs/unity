# ConversationSmokeTest

End-to-end WebGL smoke test that drives `Conversation.StartSessionAsync`
against a real ElevenLabs agent. Mirrors `Samples/BridgeSmokeTest/` but
exercises the full bridged stack (the C# Conversation, the four JS SDK
classes, and the wire protocol) rather than the low-level JS↔C# primitives.

## What it asserts

- **Happy path:** connect → send a user message → receive at least one
  `AgentResponded` event → receive at least one `AudioReceived` event →
  `EndSession` → receive `Disconnected`.
- **V2 (sync after await):** a sync `SendContextualUpdate` is callable
  immediately after the awaited `Conversation.StartSessionAsync` returns —
  no extra frame yield.
- **V3 (audio ordering):** every audio chunk's `EventId` is strictly
  monotonically increasing across the agent's full turn.
- **V4 (multi-send):** the same connection handle serves multiple outgoing
  sends in a single session (two `SendContextualUpdate` calls + one
  `SendUserMessage`).
- **Audio default-mode:** the scene contains no Unity `AudioSource`
  (built as an empty scene with only this MonoBehaviour) AND audio events
  arrive — proves the JS-side `attachInputToConnection` /
  `attachConnectionToOutput` pipeline carried the turn end-to-end with no
  Unity audio component involvement.
- **No-leak:** `PromiseRegistry.Count` and `CallbackRegistry.Count` return
  to their pre-session baseline after `EndSession`.

The Playwright/Vitest harness at
[`IntegrationTests~/src/conversation-smoke.test.ts`](../../IntegrationTests~/src/conversation-smoke.test.ts)
additionally observes the actual WebSocket frames via
`page.on('websocket')` and asserts on the on-wire protocol shape:
`conversation_initiation_client_data` is the first frame sent, the
`user_message` payload's `text` field matches what the smoke test sent,
and the received frame sequence includes
`conversation_initiation_metadata` (before `agent_response`) plus at
least one `audio` frame.

## Local setup

1. Build the WebGL artifact:

   ```bash
   bash TestProject/build-webgl-conversation.sh
   ```

   (One-time; rebuild when C# or jslib sources change.)

2. Configure agent credentials:

   - In the Unity Editor: **Assets → Create → ElevenLabs → Conversation Smoke Config**.
     Move the asset into any folder named `Resources` somewhere under
     `Assets/` (e.g. `Assets/Resources/ConversationSmokeConfig.asset`).
   - Fill in `agentId` from your ElevenLabs dashboard.
   - The asset is gitignored — it never leaves your machine.

3. Run the harness:

   ```bash
   pnpm --dir IntegrationTests~ run test
   ```

   Without the config asset, the test reports as passing with
   `[ConvSmoke] CONFIG MISSING` in the output — a clean clone stays
   green in CI.

## CI

The harness treats `[ConvSmoke] CONFIG MISSING` as a green-but-no-op
outcome. To exercise the real-agent path in CI, a pre-build step must
materialise `TestProject/Assets/Resources/ConversationSmokeConfig.asset`
from a secret (the file is a standard Unity `.asset` YAML — see the
locally-created file for the exact shape) before running
`bash TestProject/build-webgl-conversation.sh`.
