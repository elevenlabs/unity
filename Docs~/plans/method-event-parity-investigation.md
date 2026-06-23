# Method + event parity — JS SDK ↔ Unity SDK investigation

**Status:** Proposed, 2026-06-23
**Scope:** Doc-only deliverable for [v0.1 parity plan #11a](./v0.1-parity.md#11a--methodevent-parity-investigation-doc-only).
Every subsequent #11b (method) and #11c (event) PR cites a row in
[The parity matrix](#the-parity-matrix) below.

---

## Why this doc exists

The v0.1 parity plan calls out a load-bearing partition: each missing
JS-SDK surface needs classifying as **wire-event-driven** (mechanical
once the wire DTO exists), **derived state** (router-logic port from
JS to C#), or **hybrid** (both). Without that partition every #11 PR
re-litigates "is this a codegen problem?" — and the answer turns out
to depend on the AsyncAPI spec snapshot at
[`Codegen~/schemas/convai-asyncapi.yml`](../../Codegen~/schemas/convai-asyncapi.yml),
not on the JS SDK shape.

The matrix below combines methods and callbacks in one table (a `kind`
column distinguishes them) so #11b and #11c PRs cite a single
artefact, and so the audit of "which AsyncAPI wire types are missing"
is visible in one place.

## Source material

This doc reads the JS SDK from `Bridge~/node_modules/@elevenlabs/client/`:

- [`BaseConversation.d.ts`](../../Bridge~/node_modules/@elevenlabs/client/dist/BaseConversation.d.ts)
  — abstract public surface (methods + abstract audio accessors).
- [`BaseConversation.js`](../../Bridge~/node_modules/@elevenlabs/client/dist/BaseConversation.js)
  — the `onMessage` router and every per-wire-event `handleX` helper.
- [`types.d.ts`](../../Bridge~/node_modules/@elevenlabs/client/dist/types.d.ts)
  — the `Callbacks` type and `CALLBACK_KEYS` runtime list (the
  authoritative callback set the JS SDK promises).
- [`utils/BaseConnection.d.ts`](../../Bridge~/node_modules/@elevenlabs/client/dist/utils/BaseConnection.d.ts)
  / [`.js`](../../Bridge~/node_modules/@elevenlabs/client/dist/utils/BaseConnection.js)
  — connection-level subscriptions (`onMessage` / `onDisconnect` /
  `onModeChange` / `onDebug`).
- [`utils/WebSocketConnection.js`](../../Bridge~/node_modules/@elevenlabs/client/dist/utils/WebSocketConnection.js)
  — WebSocket-specific `disconnect` triggers (close codes, error
  events).
- [`utils/WebRTCConnection.js`](../../Bridge~/node_modules/@elevenlabs/client/dist/utils/WebRTCConnection.js)
  — LiveKit-event-driven `disconnect` and `updateMode` triggers; sets
  up `audio_element_ready` `onDebug` notification.
- [`utils/overrides.js`](../../Bridge~/node_modules/@elevenlabs/client/dist/utils/overrides.js)
  — `constructOverrides` (auto-injected `source_info`,
  `tool_mock_config` passthrough).
- [`utils/errors.js`](../../Bridge~/node_modules/@elevenlabs/client/dist/utils/errors.js)
  — JS error class hierarchy used by `onError` callsites.

C# surface read from
[`Runtime/Core/Conversation.cs`](../../Runtime/Core/Conversation.cs),
[`Runtime/Core/ConversationOptions.cs`](../../Runtime/Core/ConversationOptions.cs),
and the generated dispatcher at
[`Runtime/Core/Protocol/IncomingEventDispatcher.g.cs`](../../Runtime/Core/Protocol/IncomingEventDispatcher.g.cs).

## Classification taxonomy

| Class | Meaning | PR shape |
|---|---|---|
| `wire-codegen` | Pure wire-event payload that the C# side can surface as soon as the AsyncAPI schema emits a DTO for it. No router logic; one `Raise*` helper and one public event. | Trivial: depends on the spec re-vendor PR (#7) or a hand-rolled spec patch + regen. |
| `wire-existing` | Wire DTO already generated; only the C# event + `Raise*` helper + dispatcher subscription are missing. | Mechanical: lands as soon as the PR is opened. |
| `derived` | No wire payload — derives from C# router state (status flips, mode flips, can-send-feedback bookkeeping). Already largely covered by `Conversation`; gaps here are usually "missing enum value" or "missing transition". | Logic-only, no codegen dependency. |
| `hybrid` | Surface that fires on **either** a wire event **or** a derived trigger (e.g., `ErrorOccurred` from a transport failure or from an incoming `error` frame). | Combines `wire-codegen` and `derived`; the wire side waits on schema, but the derived half can ship today. |
| `transport-only` | Surface owned by the connection layer (`IConnection`), not by `Conversation` — `onDebug` lives here. | Out-of-scope for #11; rolls into #9 (native) since the bridged side has its own debug path. |
| `http-side-channel` | Not a wire event at all — an HTTP request alongside the WebSocket. Codegen unaffected. | Standalone `HttpClient` (native) or `JsObject` HTTP fetch (bridged) PR. |

## Upstream-spec gaps surfaced by the inventory

These items are **`wire-codegen` rows that are blocked on the
AsyncAPI spec** — every #11c PR for them must either wait on #7's spec
re-vendor or land an upstream PR against `elevenlabs/xi` first.

| Wire type (JS) | JS field name | Used by JS SDK in | Status in vendored spec |
|---|---|---|---|
| `internal_tentative_agent_response` | `tentative_agent_response_internal_event` | `handleTentativeAgentResponse` → `onDebug` | Absent |
| `mcp_tool_call` | `mcp_tool_call` | `handleMCPToolCall` → `onMCPToolCall` | Absent |
| `mcp_connection_status` | `mcp_connection_status` | `handleMCPConnectionStatus` → `onMCPConnectionStatus` | Absent |
| `agent_tool_request` | `agent_tool_request` | `handleAgentToolRequest` → `onAgentToolRequest` | Absent |
| `agent_tool_response` (non-full-payload variant) | `agent_tool_response` | `handleAgentToolResponse` → `onAgentToolResponse` (and `end_call` shortcut) | Absent (only `agent_tool_response_full_payload` is in the spec) |
| `asr_initiation_metadata` | `asr_initiation_metadata_event` | `handleAsrInitiationMetadata` → `onAsrInitiationMetadata` | Absent |
| `agent_chat_response_part` | `text_response_part` | `handleAgentChatResponsePart` → `onAgentChatResponsePart` | Absent |
| `error` | `error_event` | `handleErrorEvent` → `onError` + `max_duration_exceeded` shortcut | Absent |
| `guardrail_triggered` | (no inner object) | `handleGuardrailTriggered` → `onGuardrailTriggered` | Absent |
| `agent_typing` | `agent_typing_event` | `handleAgentTyping` → `onAgentTyping` | Absent |
| `external_agent_connected` | (no inner object) | `handleExternalAgentConnected` → `onExternalAgentConnected` | Absent |
| `audio_event_alignment` (`AudioAlignmentEvent`) | n/a (delivered via `onAudioAlignment`, called from WebRTC audio adapter) | `onAudioAlignment` callback typed in `Callbacks` but never invoked from `BaseConversation` | Absent; also unused in `BaseConversation` — confirm whether this is dead code in JS SDK |
| `tool_mock_config` (outgoing, in `conversation_initiation_client_data`) | n/a — `constructOverrides` adds it client-side | Sent by client when `ConversationOptions.ToolMockConfig` is set | Absent in `ConversationInitiationClientData` schema |
| `mcp_tool_approval_result` (outgoing) | `tool_call_id`, `is_approved` | `sendMCPToolApprovalResult` | Absent in `OutgoingSocketEvent` union |
| `source_info.source` enum value `unity_sdk` | n/a — enum widening | `constructOverrides` auto-injects `{ source: "js_sdk", version: <SDK_VERSION> }`; we need a corresponding `unity_sdk` slot | Enum at [`convai-asyncapi.yml:962-980`](../../Codegen~/schemas/convai-asyncapi.yml#L962-L980) lacks `unity_sdk` |

**Action**: per [v0.1-parity.md:165](./v0.1-parity.md#L165), file each
of these as an upstream PR against `elevenlabs/xi` before promising the
matching C# method/event. The dependency is unidirectional — the spec
PR has to merge first, then a re-vendor here, then the #11c PR.

## The parity matrix

One row per JS-SDK callback (`kind: callback`) or method (`kind: method`).
"Existing C# surface" cites the current
[`Conversation.cs`](../../Runtime/Core/Conversation.cs) declaration.
"Class" uses the [classification taxonomy](#classification-taxonomy).

| # | Kind | JS name | JS source loc | Class | Existing C# surface | Proposed C# signature / placement | Blocker |
|---|---|---|---|---|---|---|---|
| 1 | callback | `onConnect` | `BaseConversation.markConnected` → `updateStatus("connected")` (and the bridged session's explicit `RaiseConnected` after the handshake) | `derived` | `event Action<string>? Connected` | ✅ shipped | none |
| 2 | callback | `onDisconnect` | `BaseConversation.endSessionWithDetails`, fired from transport `disconnect()` or user `endSession()` | `derived` | `event Action<DisconnectionDetails>? Disconnected` | ✅ shipped | none |
| 3 | callback | `onError` | `BaseConversation.onError`; called by client-tool dispatch, by `handleErrorEvent` (server `error` frame), and by `max_duration_exceeded` end-of-session shortcut | `hybrid` | `event Action<string>? ErrorOccurred` (transport / client-tool only — see [Resolved gap: error type hierarchy](#resolved-gap-error-type-hierarchy)) | Keep `event Action<string>? ErrorOccurred`. Wire-`error`-frame integration lands with the upstream `error` event PR. | Wire `error` frame absent from spec (upstream PR) |
| 4 | callback | `onMessage` (user) | `BaseConversation.handleUserTranscript` → `onMessage({ role: "user", ... })` | `wire-existing` | `event Action<UserTranscriptArgs>? UserTranscriptReceived` | ✅ shipped (kept C#-idiomatic `UserTranscriptReceived` + `AgentResponded` split rather than mirroring JS's combined `onMessage`) | none |
| 5 | callback | `onMessage` (agent) | `BaseConversation.handleAgentResponse` → `onMessage({ role: "agent", ... })` | `wire-existing` | `event Action<AgentResponseArgs>? AgentResponded` | ✅ shipped | none |
| 6 | callback | `onAudio` | `BaseConversation.handleAudio` (no-op in base; `WebSocketConnection.handleMessage` and `WebRTCConnection.setupAudioCapture` emit the actual `audio` frames) | `wire-existing` | `event Action<AudioResponseArgs>? AudioReceived` | ✅ shipped | none |
| 7 | callback | `onModeChange` | Connection-level `updateMode()`; WebRTC uses `RoomEvent.ActiveSpeakersChanged`; WebSocket flips inside `handleAudioResponse` / `handleInterruption` | `derived` | `event Action<Mode>? ModeChanged` (currently flips in `HandleAudioResponse` + `HandleInterruption` only — bridged path inherits JS behaviour via `IConnection.OnModeChange`) | ✅ shipped | none |
| 8 | callback | `onStatusChange` | `BaseConversation.updateStatus` | `derived` | `event Action<Status>? StatusChanged` | ✅ shipped | none |
| 9 | callback | `onCanSendFeedbackChange` | `BaseConversation.updateCanSendFeedback` | `derived` | `event Action<bool>? CanSendFeedbackChanged` | ✅ shipped | none |
| 10 | callback | `onUnhandledClientToolCall` | `BaseConversation.handleClientToolCall` else-branch | `derived` | None (the C# side raises `ErrorOccurred` for the same case at [`Conversation.cs:413-418`](../../Runtime/Core/Conversation.cs#L413-L418), without giving handlers a chance to opt in) | Add `event Action<ClientToolCallArgs>? UnhandledClientToolCall`. When a subscriber exists, suppress the error-and-tool-error response and let the subscriber decide; when no subscriber, keep today's behaviour. | none (`ClientToolCall` already generated) |
| 11 | callback | `onVadScore` | `BaseConversation.handleVadScore` | `wire-existing` | `event Action<VadScoreArgs>? VadScoreUpdated` | ✅ shipped | none |
| 12 | callback | `onMCPToolCall` | `BaseConversation.handleMCPToolCall` | `wire-codegen` | None | `event Action<MCPToolCallArgs>? MCPToolCallReceived` once the DTO is generated. | Upstream spec PR (`mcp_tool_call`) |
| 13 | callback | `onMCPConnectionStatus` | `BaseConversation.handleMCPConnectionStatus` | `wire-codegen` | None | `event Action<MCPConnectionStatusArgs>? MCPConnectionStatusChanged` | Upstream spec PR (`mcp_connection_status`) |
| 14 | callback | `onAgentToolRequest` | `BaseConversation.handleAgentToolRequest` | `wire-codegen` | None | `event Action<AgentToolRequestArgs>? AgentToolRequested` | Upstream spec PR (`agent_tool_request`) |
| 15 | callback | `onAgentToolResponse` | `BaseConversation.handleAgentToolResponse` **and** `handleAgentToolResponseFullPayload` — both invoke the same callback; the C# router already handles only the `_full_payload` variant via `OnAgentToolResponseFullPayload` for the `end_call` shortcut, without surfacing it as an event | `hybrid` | None | `event Action<AgentToolResponseArgs>? AgentToolResponded`. C# surface should unify both variants behind one args record (JS does the same). | Upstream spec PR (`agent_tool_response` non-full variant) |
| 16 | callback | `onConversationMetadata` | `BaseConversation.handleConversationMetadata` | `wire-existing` | `event Action<ConversationInitiationMetadataArgs>? InitiationMetadataReceived` | ✅ shipped | none |
| 17 | callback | `onAsrInitiationMetadata` | `BaseConversation.handleAsrInitiationMetadata` | `wire-codegen` | None | `event Action<AsrInitiationMetadataArgs>? AsrInitiationMetadataReceived` | Upstream spec PR (`asr_initiation_metadata`) |
| 18 | callback | `onInterruption` | `BaseConversation.handleInterruption` | `wire-existing` | `event Action<InterruptionArgs>? Interrupted` | ✅ shipped | none |
| 19 | callback | `onAgentResponseCorrection` | `BaseConversation.handleAgentResponseCorrection` | `wire-existing` | `event Action<AgentResponseCorrectionArgs>? AgentResponseCorrected` | ✅ shipped | none |
| 20 | callback | `onAgentChatResponsePart` | `BaseConversation.handleAgentChatResponsePart` | `wire-codegen` | None | `event Action<AgentChatResponsePartArgs>? AgentChatResponsePartReceived` | Upstream spec PR (`agent_chat_response_part`) |
| 21 | callback | `onAudioAlignment` | `Callbacks` type only — **not invoked from `BaseConversation`**. Likely wired separately by an internal adapter (WebRTC) or unused in JS SDK proper | `wire-codegen` | None | Defer: confirm whether the JS SDK invokes this anywhere (we couldn't find a callsite in `BaseConversation`). If yes, mirror; if no, drop from the parity surface entirely. | Confirm JS callsite + upstream spec PR (`audio_event_alignment`) |
| 22 | callback | `onGuardrailTriggered` | `BaseConversation.handleGuardrailTriggered` | `wire-codegen` | None | `event Action? GuardrailTriggered` (no payload — JS callback takes no args) | Upstream spec PR (`guardrail_triggered`) |
| 23 | callback | `onAgentTyping` | `BaseConversation.handleAgentTyping` | `wire-codegen` | None | `event Action<AgentTypingArgs>? AgentTyping` | Upstream spec PR (`agent_typing`) |
| 24 | callback | `onExternalAgentConnected` | `BaseConversation.handleExternalAgentConnected` | `wire-codegen` | None | `event Action? ExternalAgentConnected` (no payload) | Upstream spec PR (`external_agent_connected`) |
| 25 | callback | `onDebug` | `BaseConnection.debug` (transport-internal: parse errors, invalid events, audio_element_ready, send-message errors) **and** `BaseConversation.onMessage` default arm (unknown wire types) | `transport-only` (transport half) + `derived` (unknown-wire half) | None | Per [Resolved gap: `onDebug` policy](#resolved-gap-ondebug-policy), bridge to `UnityEngine.Debug.Log` behind `ConversationOptions.EnableDebugLogging`. Unknown-wire arm is already covered by `IncomingEventDispatcher.OnUnhandled` (internal). | none — recipe is "do it without adding a public event" |
| 26 | callback | (none) | n/a | `wire-existing` | `event Action<AgentResponseCompleteArgs>? AgentResponseCompleted` | ✅ shipped — note this is **not** in JS SDK's `Callbacks` (the spec defines `agent_response_complete` but the JS router has no `handle*` for it). C# surfaces it because the wire payload is useful. | none — already shipped |
| 27 | method | `setVolume({ volume })` | `BaseConversation` abstract; `VoiceConversation.setVolume`, `WebRTCConnection.output.setVolume` | `derived` | `void SetVolume(float)` | ✅ shipped | none |
| 28 | method | `setMicMuted(isMuted)` | `BaseConversation` abstract; `VoiceConversation.setMicMuted`, `WebRTCConnection.input.setMuted` | `derived` | `Awaitable SetMicMuted(bool)` | ✅ shipped | none |
| 29 | method | `getInputByteFrequencyData()` | `BaseConversation` abstract; `VoiceConversation` / `WebRTCConnection.input.getByteFrequencyData` | `derived` | `void GetInputByteFrequencyData(byte[])` | ✅ shipped (C# takes a caller-allocated buffer; JS returns a fresh `Uint8Array` per call) | none |
| 30 | method | `getOutputByteFrequencyData()` | `BaseConversation` abstract; `VoiceConversation` / `WebRTCConnection.output.getByteFrequencyData` | `derived` | `void GetOutputByteFrequencyData(byte[])` | ✅ shipped | none |
| 31 | method | `getInputVolume()` / `getOutputVolume()` | `VoiceConversation` / `WebRTCConnection.input.getVolume` etc. | `derived` | `float GetInputVolume()` / `float GetOutputVolume()` | ✅ shipped | none |
| 32 | method | `sendFeedback(like)` | `BaseConversation.sendFeedback` | `derived` (gate: `canSendFeedback`) | `void SendFeedback(bool)` | ✅ shipped | none |
| 33 | method | `sendContextualUpdate(text, options?)` | `BaseConversation.sendContextualUpdate` | `derived` (outgoing) | `void SendContextualUpdate(string)` | Extend to accept the optional `context_id` to match JS's `ContextualUpdateOptions`. One-line addition. | none — `ContextualUpdate` DTO already generated; add the optional field on the wire DTO if missing |
| 34 | method | `sendUserMessage(text)` | `BaseConversation.sendUserMessage` | `derived` (outgoing) | `void SendUserMessage(string)` | ✅ shipped | none |
| 35 | method | `sendUserActivity()` | `BaseConversation.sendUserActivity` | `derived` (outgoing) | `void SendUserActivity()` | ✅ shipped | none |
| 36 | method | `sendMCPToolApprovalResult(toolCallId, isApproved)` | `BaseConversation.sendMCPToolApprovalResult` | `wire-codegen` | None | `void SendMCPToolApprovalResult(string toolCallId, bool isApproved)` | Upstream spec PR (`mcp_tool_approval_result` outgoing) |
| 37 | method | `sendMultimodalMessage({ text?, fileId? })` | `BaseConversation.sendMultimodalMessage` | `wire-existing` | None | `void SendMultimodalMessage(string? text = null, string? fileId = null)` — `MultimodalMessage` DTO already generated; one-line forward into `_connection.Send(...)` | none |
| 38 | method | `uploadFile(file)` → `{ fileId }` | `BaseConversation.uploadFile` — HTTP POST to `${origin}/v1/convai/conversations/${conversationId}/files` | `http-side-channel` | None | `Awaitable<string> UploadFileAsync(byte[] bytes, string mimeType, string? filename = null)`. Returns `fileId` consumable by `SendMultimodalMessage`. | Investigate whether `Bridge~/internal/unity` re-exports the helper (so bridged path doesn't have to duplicate the HTTP call) |
| 39 | method | `endSession()` | `BaseConversation.endSession` | `derived` | `Awaitable EndSession()` | ✅ shipped | none |
| 40 | method | `getId()` | `BaseConversation.getId` (`connection.conversationId`) | `derived` | `string ConversationId` (property) | ✅ shipped — exposed as a property to match the C# convention | none |
| 41 | method | `isOpen()` | `BaseConversation.isOpen` (`status === "connected"`) | `derived` | None (subsumed by `Status` property + `StatusChanged` event) | Decide: do we add a convenience `bool IsOpen => Status == Status.Connected;` property, or document that game code should read `conversation.Status`? Lean toward **don't add** — one more way to express the same thing is API surface noise. | none (decision, not work) |
| 42 | method (input controller) | `setDevice(InputDeviceConfig)` | `MediaDeviceInput.setDevice` (WebGL), `WebRTCConnection.input.setDevice` | `derived` | Already on [`IInputController`](../../Runtime/Core/IInputController.cs); not exposed on `Conversation` | `Awaitable ChangeInputDevice(InputDeviceConfig, FormatConfig?)` — forwards to `_inputController.SetDevice(...)`. Per [v0.1-parity.md:162](./v0.1-parity.md#L162). | none |
| 43 | method (output controller) | `setDevice(OutputDeviceConfig)` | `MediaDeviceOutput.setDevice` (WebGL), `WebRTCConnection.output.setDevice` | `derived` | Already on [`IOutputController`](../../Runtime/Core/IOutputController.cs); not exposed on `Conversation` | `Awaitable ChangeOutputDevice(OutputDeviceConfig, FormatConfig?)` — same shape | none |

## Resolved gaps from the parent plan

The v0.1 parity plan listed five "uncertain gaps" (see
[v0.1-parity.md:147-156](./v0.1-parity.md#L147-L156)). Each gets a
decision below; #11b / #11c PRs cite these decisions rather than
re-opening them.

### Resolved gap: reconnection / retry

**Decision**: punt to v0.2.

**Wire-flow audit**: the JS SDK's `WebSocketConnection` and
`WebRTCConnection` **do not retry** on their own — both emit
`disconnect({ reason: ... })` on the first transport failure and rely on
the host application to call `Conversation.startSession` again. The
React SDK layers a host-level retry on top, but it's not part of
`@elevenlabs/client` proper. The Unity SDK can match the SDK-core
behaviour today (it already does); auto-retry would be a *new* opinion,
not parity work. Defer.

**Action**: add an XML doc comment above
[`Conversation.EndSessionWithDetails`](../../Runtime/Core/Conversation.cs#L216)
when #11c lands, noting "no auto-retry; consumers re-call
`StartSessionAsync` after observing `Disconnected`." No code change in
this investigation PR.

### Resolved gap: session resumption (`ConversationToken`)

**Decision**: confirmed the wire flow; #10 already plumbs the field; the
remaining work is a regression test.

**Wire-flow audit**:
[`utils/WebRTCConnection.js:163-167`](../../Bridge~/node_modules/@elevenlabs/client/dist/utils/WebRTCConnection.js#L163-L167)
accepts `conversationToken` directly without further negotiation —
LiveKit handles the resumption semantics inside the room handshake.
WebSocket transport doesn't currently support it
(`PrivateWebRTCSessionConfig` keys it to `connectionType: "webrtc"`);
the launcher validation at #10's `BridgedSessionLauncher` already
rejects `ConversationToken` on WebSocket transport.

**Action**: #11c adds an Edit Mode test that
`Conversation.StartSessionAsync(options with ConversationToken)` builds
the right WebRTC handshake payload. No new public surface required.

### Resolved gap: error type hierarchy

**Decision**: don't mirror JS error classes. Keep
[`BridgeException`](../../Runtime/WebGL/Bridged/BridgeException.cs)
(transport) + [`ClientToolException`](../../Runtime/Core/ClientTools.cs)
(user-code) as the only public exception types. Server-side `error`
frames raise `ErrorOccurred` with a structured args record (next
paragraph) once the upstream spec PR lands.

**Why**: the JS SDK's `errors.ts` defines four classes —
`ConversationError`, `SessionConnectionError`,
`AudioConcatProcessorError`, `RawAudioProcessorError` — that exist
because JS lacks structured error types and needs class identity for
`instanceof` discrimination. C# has nullable args, sealed types, and
pattern matching; mirroring the hierarchy adds noise without adding
discrimination power.

**Proposed args record for `ErrorOccurred` when wire `error` frames
land**: `record ErrorArgs(string Message, string? ErrorType, int? Code,
string? DebugMessage, object? Details, ErrorSource Source)` where
`ErrorSource` is `Transport | ClientTool | Server`. Today
`ErrorOccurred` carries just a string — widening to this record is a
breaking API change but it's pre-v0.1, so fine. README in #12 documents
the JS-error → C#-error mapping for users migrating from JS.

### Resolved gap: `Status` / `Mode` completeness audit

**Decision**: the current enums **are complete** against JS today.

**Audit**:
[`types.d.ts:10-14`](../../Bridge~/node_modules/@elevenlabs/client/dist/types.d.ts#L10-L14)
defines `Mode = "speaking" | "listening"` and `Status =
"disconnected" | "connecting" | "connected" | "disconnecting"`.
[`Runtime/Core/Mode.cs`](../../Runtime/Core/Mode.cs) and
[`Runtime/Core/Status.cs`](../../Runtime/Core/Status.cs) match
one-for-one.

**Conditional addition**: if `Reconnection / retry` ever ports (it
doesn't, see above), add `Status.Reconnecting`. For v0.1, no change.

### Resolved gap: `onDebug` policy

**Decision**: do **not** add a public C# event for debug noise. Add
`ConversationOptions.EnableDebugLogging` (default `false`) which routes
the equivalent JS-side `onDebug({type, ...})` payloads to
`UnityEngine.Debug.Log` with an `[ElevenLabs debug]` prefix. The
unknown-wire-event arm (today's `IncomingEventDispatcher.OnUnhandled`)
gates on the same flag.

**Why**: every game project that ever subscribes to `onDebug` in the JS
SDK does so for diagnostic logging, not for a runtime branch. Surfacing
it as a C# `event` would imply an API contract over what's actually
unstable opaque diagnostic JSON.

**Action**: bundled into the same #11b PR as
`UnhandledClientToolCall` (both touch the dispatcher + options surface).

## Out of scope (won't appear in #11)

- `BaseConversation.markConnected` / `endSessionWithDetails` private
  helpers — internal lifecycle, not public surface.
- `getRoom()` on WebRTC — leaks the LiveKit `Room` reference; Unity SDK
  doesn't expose the underlying transport.
- `attachConnectionToOutput` / `attachInputToConnection` from
  `Bridge~/node_modules/@elevenlabs/client/dist/utils/` — these are
  WebGL bridge plumbing that the `@elevenlabs/client` SDK exposes for
  the React SDK's transitive consumers; the Unity SDK's equivalent is
  the `IInputController` / `IOutputController` interfaces, which are
  internal.

## Dependencies and PR order

```
#11a (this doc)                ── lands now ──────────────────────┐
                                                                  │
#11b (methods)                                                    ▼
  ├─ ChangeInputDevice + ChangeOutputDevice         ── independent
  ├─ SendMultimodalMessage                          ── independent
  ├─ SendContextualUpdate(contextId)                ── independent
  ├─ UnhandledClientToolCall + onDebug flag         ── independent
  ├─ UploadFileAsync                                ── investigate first
  └─ SendMCPToolApprovalResult                      ── waits on upstream PR
                                                                  │
#11c (events)                                                     ▼
  ├─ ErrorOccurred widening to ErrorArgs            ── waits on upstream `error` event
  ├─ AgentToolResponded (unifies both variants)     ── waits on upstream `agent_tool_response`
  ├─ MCPToolCallReceived                            ── waits on upstream `mcp_tool_call`
  ├─ MCPConnectionStatusChanged                     ── waits on upstream `mcp_connection_status`
  ├─ AgentToolRequested                             ── waits on upstream `agent_tool_request`
  ├─ AsrInitiationMetadataReceived                  ── waits on upstream `asr_initiation_metadata`
  ├─ AgentChatResponsePartReceived                  ── waits on upstream `agent_chat_response_part`
  ├─ AgentTyping                                    ── waits on upstream `agent_typing`
  ├─ GuardrailTriggered                             ── waits on upstream `guardrail_triggered`
  ├─ ExternalAgentConnected                         ── waits on upstream `external_agent_connected`
  └─ AudioAlignment(deferred)                       ── confirm JS callsite exists first
```

The `independent` branches under #11b can land in any order today —
none depend on the spec sync. Everything on the `waits on upstream PR`
list is gated on either #7 (spec re-vendor) or a `elevenlabs/xi` PR
authored alongside it.

## Open question carried forward

The `SourceInfo` deferral at
[v0.1-parity.md:347-352](./v0.1-parity.md#L347-L352) was flagged for
this investigation. The upstream-spec audit above
([row in the gaps table](#upstream-spec-gaps-surfaced-by-the-inventory))
confirms: the `source` enum at
[`convai-asyncapi.yml:962-980`](../../Codegen~/schemas/convai-asyncapi.yml#L962-L980)
has no `unity_sdk` slot. **Action for #9 (native transport)**: file an
upstream PR adding `unity_sdk` to the enum, then native can populate
`source_info` directly with `{ source: "unity_sdk", version:
<PackageVersion> }`. The bridged path inherits the JS SDK's
auto-injected `js_sdk` source today and stays as-is; #9 changes the
native default but leaves bridged unchanged.

The `ToolMockConfig` deferral at
[v0.1-parity.md:357-362](./v0.1-parity.md#L357-L362) gets the same
disposition: file an upstream spec PR adding `tool_mock_config` to
`ConversationInitiationClientData`, then a re-vendor surfaces the typed
DTO and the #10 `BuildSessionConfig` plumbing becomes mechanical. Track
under #11c's "upstream spec PR" list above.
