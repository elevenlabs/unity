# Plan B — Bridge into the connection + I/O controllers, not the Conversation

## Context

The original [RFC](./initial-rfc.md) proposed a hybrid: one `Conversation` C#
interface, with a native implementation on desktop/mobile/XR and a WebGL
implementation that is a thin façade over `@elevenlabs/client`'s `Conversation`.
The [JS Conversation adapter plan](./js-conversation-adapter.md) was the
WebGL-side detailing of that approach.

After several days implementing toward that plan, the level of abstraction
feels wrong:

1. **DX divergence.** Two Conversation implementations means two timing models, two event-shape sources of truth, two places bugs can live. Even with codegen, the consumer sees subtle differences between platforms.
2. **JS-bridge surface bigger than expected.** A faithful WebGL `Conversation` façade has to mirror the full Conversation API (lifecycle, methods, callbacks, client tools, overrides, feedback) — and every change to that surface in the JS SDK ripples through the bridge.
3. **Audio routing concession.** Plan A's "default mode" runs audio entirely in JS, which is fast but means WebGL conversations don't go through Unity's audio pipeline by default. That's an asymmetry game developers hit immediately.

**Plan B** lowers the bridge by one layer. The public Conversation lives in
C# only and is identical on every platform. The bridge wraps the three
classes the JS SDK already factors out — `BaseConnection`, `InputController`,
`OutputController` — and the same C# `Conversation` orchestrates them on
every target.

See [ARCHITECTURE.md](../ARCHITECTURE.md) for the to-be shape.

## What changes vs Plan A

| Concern | Plan A | Plan B |
|---|---|---|
| C# Conversation implementations | Two (native + WebGL façade) | One (everywhere) |
| WebGL bridge target | `@elevenlabs/client` `Conversation` | `BaseConnection` + `InputController` + `OutputController` |
| Bridged JS classes count | 1 large | 4 small |
| Bridge primitives | 3 (Promise / Observer / Handler) | 3 generic handle types (`JsObject` / `JsFunction` / `BridgeCallback`) — see [generic-bridge-primitives.md](./generic-bridge-primitives.md) |
| Audio path on WebGL (default) | JS-only — Web Audio | JS-only — `attachInputToConnection` / `attachConnectionToOutput` glue stays in JS |
| Audio path on WebGL (Unity-routed) | v0.3 opt-in via PCM intercept hook | v0.3 opt-in — same milestone, same shape |
| Client tools | JS Proxy + name set + shared dispatcher key | C# table inside Conversation, no JS-side concept |
| Late event registration | Blocked on JS SDK supporting it | Already supported (C# Conversation owns its events) |
| Effort to write | Less C# message-routing code now, more bridge code | More C# message-routing code now, less bridge code |

The trade is "write the message router in C# once" vs "write the bridge
façade once" — and on net the C# router is the smaller surface, since the JS
SDK exposes 4 narrow classes vs. 1 sprawling `Conversation`.

## What stays the same

- **The Bridge~/build/bundle-jslib.ts toolchain.** Reused for the new `ElevenLabsConnection.jslib` artifact.
- **The RFC's codegen approach.** Protocol DTOs are still generated from the upstream spec (AsyncAPI — the RFC mislabeled it OpenAPI; vendored locally per Phase 3.1). They're now used by *both* implementations of `IConnection`, not just native.
- **The RFC's distribution plan, roadmap milestones, and risk inventory.** Plan B is an internal architectural pivot, not a product-level change.

## The bridging primitives this plan uses

Plan B is a pure consumer of [generic-bridge-primitives.md](./generic-bridge-primitives.md).
It adds zero new primitives and zero per-class `DllImport` entry points — every
interaction with the four JS SDK classes (`WebSocketConnection`, `WebRTCConnection`,
`MediaDeviceInput`, `MediaDeviceOutput`) and their composition glue routes
through the generic `JsObject` / `JsFunction` / `BridgeCallback` surface.

Mapped to the new consumers:

| Interaction | Primitive surface |
|---|---|
| Create a JS connection / input / output | `JsBridge.InvokeFactoryAsync<JsObject>("create...", config)` against factories registered at JS boot |
| Call a method on a JS object (with result) | `jsObject.CallAsync<T>("methodName", args)` |
| Call a method on a JS object (fire-and-forget) | `jsObject.Call("methodName", args)` (void overload) |
| Read a synchronous JS property | `jsObject.Get<T>("propertyName")` |
| Receive a stream of events | C# wraps a delegate in `BridgeCallback.Wrap(handler)` and passes it as an argument: `jsObject.Call("onMessage", callback)` |
| Hold a JS function returned from a call (e.g. a `removeListener` returned by `addListener`) | Receive as `JsFunction`; call later via `jsFunction.CallAsync(...)` |
| Dispose a JS object | `jsObject.Dispose()` (also via `using` blocks) |

What Plan B does *not* need beyond what the primitives provide:

- **No per-class jslib `EL_*` entry points.** The factory registrations (Phase 2.2) are the only JS-side code we add.
- **No async JS-to-C# round-trips for v0.1.** Client tools live entirely in the C# `Conversation` now, so the future `AsyncBridgeCallback` extension point is out of scope here.
- **No binary-payload variants for v0.1.** Default-mode audio bytes stay inside JS via `attachInputToConnection` / `attachConnectionToOutput` and never cross the bridge. Unity-routed audio (v0.3) is where binary payloads will land.

## Required `@elevenlabs/client` coordination

All four classes we need are already exported from the SDK's `index.ts`:
`WebSocketConnection`, `WebRTCConnection`, `createConnection`, plus the
`InputController` / `OutputController` interfaces (the concrete `MediaDeviceInput`
/ `MediaDeviceOutput` live under `platform/web/` and we import them from
there or from the `browser` entrypoint that triggers their registration).

Identified gaps:

1. **Concrete `MediaDeviceInput` / `MediaDeviceOutput` not in `package.json#exports`.** Task 2.2 needs the concrete platform classes (`MediaDeviceInput`, `MediaDeviceOutput`) plus the `attachInputToConnection` / `attachConnectionToOutput` audio-wiring helpers. None of these were in the SDK's `exports` map. **Resolved by [elevenlabs/packages#835](https://github.com/elevenlabs/packages/pull/835), shipped in `@elevenlabs/client@1.11.0`** as a new `./internal/unity` sub-path export. The PR also promotes the anonymous config-intersection types to named exports (`MediaDeviceInputConfig`, `MediaDeviceOutputConfig`, `WebRTCConnectionConfig`). Brief at [`client-sdk-exports-for-plan-b.md`](./client-sdk-exports-for-plan-b.md).
2. **Default-mode audio event stripping.** The C# Conversation needs `audio` events (for `event_id` / interruption / mode tracking) without the base64 payload crossing into the JS↔C# string channel on every chunk. **Status: solved Unity-side** — `audio-glue.ts` ships a pure `withoutAudioPayload` callback wrapper that's composed at subscribe time (`connection.onMessage(withoutAudioPayload(bridgeCallback))`) inside the `attachDefaultAudio` factory. No connection mutation, no ordering constraint, no SDK addition required beyond the primitives shipped by 1.11.0.
3. **WebRTC PCM extraction for Unity-routed audio (v0.3).** Already designed: `setWebRTCAudioAdapterFactory` is the slot (also re-exported from `@elevenlabs/client/internal/unity` since 1.11.0). **Status: no SDK change needed for v0.1**; a Unity-specific adapter lands with v0.3.
4. **Late event registration.** Not a gap — the C# `Conversation` owns the message router and exposes standard `.NET` events with `+=` / `-=` semantics from day one. The JS-side connection's single `onMessage` callback is wired once at session start via a `BridgeCallback`, and the C# router fans out into the user's subscribed events. (This was a Plan A constraint, where the C# façade had to pass callbacks into `@elevenlabs/client`'s `Conversation` constructor; Plan B's architecture removes the constraint entirely.)

No upstream PRs block Plan B's v0.1.

### Local SDK iteration workflow

Future Plan B tasks (e.g. any deeper integration discovered in Phases 4-5)
may want small tweaks to `@elevenlabs/client` before the change is ready
to upstream. To avoid maintaining a hard fork:

1. **While iterating**, use a pnpm `overrides` entry in
   `Bridge~/package.json` pointing at a local checkout (`link:../../packages/packages/client`
   or similar). Edits show up immediately without a publish loop. CI is
   guarded because the override path doesn't exist on the CI runner — the
   override is per-developer and must be removed before merging.
2. **Once a tweak is stable**, capture it as a `pnpm patch` under
   `Bridge~/patches/` and reference it via `patchedDependencies` in
   `Bridge~/package.json` so the lockfile is reproducible for everyone.
3. **Open the upstream PR** in parallel; once merged and a new
   `@elevenlabs/client` version ships, bump the dep version and delete
   the patch.

Where a tweak adds *new exports* rather than modifying existing ones,
the preferred upstream shape is to extend the existing
`@elevenlabs/client/internal/unity` sub-path entrypoint (added in 1.11.0
via [#835](https://github.com/elevenlabs/packages/pull/835); see
[`client-sdk-exports-for-plan-b.md`](./client-sdk-exports-for-plan-b.md)).
That entrypoint is shape-stable for Unity SDK use only, lives under the
`/internal/...` namespace by convention so it carries no semver
guarantees, and uses a dedicated source file so tree-shaking has no
chance of pulling unrelated SDK code into the WebGL bundle. Add the
symbol there with `export {}` for runtime values or `export type {}`
for type-only re-exports; bump the dep version once the SDK release
ships.

## Disposition of in-flight plans

- [`generic-bridge-primitives.md`](./generic-bridge-primitives.md) — **authoritative.** The primitive layer Plan B consumes. Reuses the scaffolding (WebGLBridge MonoBehaviour, ID generator, message parser, log helper, jslib bundler) already in the repo; the prior promise / observer / handler entry points get replaced in its Phase 2.
- [`plan-a-bridge-primitives.md`](./plan-a-bridge-primitives.md) — **superseded by generic-bridge-primitives.md** for the primitive design. Its Foundation phase tasks already landed (and are reused); the Promise-as-Task / Observer / Handler primitive designs no longer apply.
- [`js-conversation-adapter.md`](./js-conversation-adapter.md) — **superseded by this plan.** It is not yet committed; a `> Superseded by plan-b.md — see ARCHITECTURE.md` note will be added to its top and the file kept for historical reference. The Tasks list under it is dropped.
- [`webgl-js-to-csharp-callbacks.md`](./webgl-js-to-csharp-callbacks.md) — **still authoritative.** The SendMessage vs DynCall analysis is independent of which JS objects we bridge.

## Implementation phases

Phases are ordered so everything that can be done without a Unity host
project lands first. Phases 1–3 are Unity-free and can run in parallel by
separate agent loops. Phases 4–7 unblock once the embedded `TestProject/`
exists — see "Phase 3 prelude — Unity host project" in
[generic-bridge-primitives.md](./generic-bridge-primitives.md) (tasks
HP.1–HP.4 are the minimum). Unity **6000.3.6f1** is installed locally as
of 2026-06-16; the prior "Unity license" framing no longer applies.

### Phase 1 — Plan disposition and naming (no code)

- [x] Add a "Superseded by plan-b.md" header to `js-conversation-adapter.md`.
- [x] Cross-reference Plan B from `generic-bridge-primitives.md`'s "Implementation phases" intro so future readers find it as the canonical first consumer.
- [x] Confirm the names sketched in [ARCHITECTURE.md](../ARCHITECTURE.md) (`BridgedWebSocketConnection` etc.) — rename now if anything reads wrong, before code lands. Renamed `BridgeJs` → `JsBridge` (reads more naturally as a noun: "the bridge to JS"). `BridgedSession` left as-is but flagged as worth a second look — "Session" overlaps with the agent platform's session concept; could be `WebGLSessionLauncher` or inlined into `Conversation.StartSessionAsync`.

### Phase 2 — JS-side factory + audio-glue registrations (Unity-free, Vitest-covered)

The bulk of the frontloaded work. Output: a second `.jslib` artifact under
`Plugins/WebGL/`, bundled from new TypeScript sources, that registers
factories with the primitive layer at module init and exposes a Unity-local
`attachDefaultAudio` composition factory (which wraps the SDK's
`attach*` primitives + a pure audio-payload-stripping callback wrapper).
**No new `EL_*` DllImport entry points** — everything goes through
`$EL_RegisterFactory` and is consumed via the generic `JsObject` primitive
from C#.

- [x] **2.1 — Dependency + scaffold.** `pnpm --dir Bridge~ add @elevenlabs/client`. Create `Bridge~/src/connection/` with `factories.ts`, `audio-glue.ts`, `types.ts`, `index.ts`. `types.ts` mirrors the SDK's exported `SessionConfig`, `FormatConfig`, `InputConfig`, `OutputConfig`, `DisconnectionDetails` shapes via composition (`Pick`/`extends`) so SDK bumps ripple through tsc. Wire `pnpm run build:connection` / `verify:connection` paralleling the existing primitives scripts; commit the (initially near-empty) `Plugins/WebGL/ElevenLabsConnection.jslib`. Document the new commands in `.claude/CLAUDE.md`.
- [x] **2.2 — Factory registrations.** `factories.ts` calls `$EL_RegisterFactory(name, fn)` for each SDK class at module init:
  - `createWebSocketConnection(config)` → `WebSocketConnection.create(config)`
  - `createWebRTCConnection(config)` → `WebRTCConnection.create(config)`
  - `createConnection(config)` → dispatches per `connectionType`
  - `createMediaDeviceInput(config)` → `MediaDeviceInput.create(config)`
  - `createMediaDeviceOutput(config)` → `MediaDeviceOutput.create(config)`

  Return shape isn't declared here — each C# call site picks it via `<T>` (e.g. `JsBridge.InvokeFactoryAsync<JsObject>("createWebSocketConnection", config)`). The dispatcher's per-call `returnShape` int does the encoding. Same for object methods: `BridgedWebSocketConnection.SendAsync(...)` calls `connection.CallAsync("sendMessage", ...)` (non-generic, shape void); a method that returns a function-handle uses `CallAsync<JsFunction>(...)`. No JS-side method-shape table.

  Vitest: mock `@elevenlabs/client`, exercise each factory through `$EL_InvokeFactory` (or its TS-side equivalent in tests), assert the returned `JsObject` handle is valid and its methods dispatch through correctly.
- [x] **2.3 — `attachDefaultAudio` helper factory.** `audio-glue.ts` registers `attachDefaultAudio(connection, input, output, bridgeCallback)` as a factory. The function returns a JS detach closure; C# consumes it via `JsBridge.InvokeFactoryAsync<JsFunction>("attachDefaultAudio", ...)` so the dispatcher allocates a `JsFunction` handle for the returned closure. Implementation calls `attachInputToConnection(input, connection)`, `attachConnectionToOutput(connection, output)`, and `connection.onMessage(withoutAudioPayload(bridgeCallback))` — where `withoutAudioPayload` is a Unity-local pure callback wrapper that strips `audio_event.audio_base_64` from `audio` events (composed at subscribe time, no monkey-patching of the connection). The returned detach function reverses both `attach*` calls (the `onMessage` subscription tears down when the C# side disposes the connection, which calls `connection.close()`). Scope: WebSocket connections only — WebRTC has input/output pre-wired by livekit-client and skips this factory. Vitest: 8 tests across `audio-glue.test.ts` — pure-function correctness of `withoutAudioPayload` (strip, pass-through, no mutation, independent wrappers) plus dispatcher-driven factory cases (registration, composition, audio-stripping in the subscribed callback, detach handle teardown).
- [x] **2.4 — Bundling + lifecycle init.** `index.ts` aggregates the registrations into the library object — both `factories` and `audio-glue` modules are spread in, and the bundle ships their `__postset` declarations. Bundled via the shared `bundle-jslib.ts` toolchain to `Plugins/WebGL/ElevenLabsConnection.jslib`. **Timing mechanism verified via Emscripten source** (see [jsifier.mjs L576](https://github.com/emscripten-core/emscripten/blob/main/src/jsifier.mjs#L576), [L685](https://github.com/emscripten-core/emscripten/blob/main/src/jsifier.mjs#L685), [L860-885](https://github.com/emscripten-core/emscripten/blob/main/src/jsifier.mjs#L860-L885)): cross-`.jslib` `__deps` resolves through the merged `LibraryManager.library` object; `__postset` emits at module-init/parse time before `main`; ordering is dep-graph-driven (not CLI flag order), so our `__deps: ["$EL_RegisterFactory"]` declarations correctly enforce primitives-load-before-factory-register. **Remaining caveat:** `$EL_ConnectionFactories` and `$EL_AudioGlueFactories` are non-function library entries — they only land in the build if something `__deps`-references them. The C# `[DllImport]` chain reaches `EL_InvokeFactoryAsync → $EL_LookupFactory → $EL_Factories` but never touches our two registry constants, so without `INCLUDE_FULL_LIBRARY` the postsets may never fire. Known fix lands with Phase 5 C# code: a no-op `[DllImport] EL_ConnectionFactoriesInit()` declared with `__deps: ["$EL_ConnectionFactories", "$EL_AudioGlueFactories"]` to force inclusion. Phase 6 Unity smoke confirms.
- [x] **2.5 — End-to-end Vitest.** Mock `@elevenlabs/client`, mock the primitives' SendMessage. Drive a full happy-path session from a single test: invoke `createWebSocketConnection` → invoke `createMediaDeviceInput` + `createMediaDeviceOutput` → invoke `attachDefaultAudio` → fire mock incoming messages → assert the bridge callback receives correctly-stripped events → invoke `JsFunction.Call` for the detach handle → assert teardown. Implemented in `Bridge~/src/connection/e2e.test.ts` (7 tests): factory sequence produces monotonically-increasing handles; `attachDefaultAudio` returns a JsFunction handle and calls both SDK wiring helpers; audio events have `audio_base_64` stripped; non-audio events pass through unchanged; `EL_ObjectCallAsync` dispatches `sendMessage`; `EL_FunctionCallAsync` on the detach handle calls both SDK detach functions; `EL_ObjectRelease`/`EL_FunctionRelease` clear all handles after teardown.

### Phase 3 — Protocol DTO codegen (Unity-free, optional pre-Unity)

The C# Conversation in Phase 4 needs typed incoming/outgoing message DTOs.
We can defer this and use untyped JSON if codegen pushes the schedule, but
landing it now means Phase 4 starts with the right types in hand.

- [x] **3.1 — Vendor the AsyncAPI spec.** Copy `docs/convai-asyncapi.yml` from the private `elevenlabs/xi` repo into `Codegen~/schemas/convai-asyncapi.yml`. Record the source commit hash in a sibling `README.md` so future re-syncs are reproducible. Vendoring decouples Unity SDK releases from `@elevenlabs/client` release cadence — protocol changes can land here without waiting for a JS SDK publish. The cross-repo sync workflow that keeps this file fresh is listed under "Pending Human Approval". **Status:** vendored at xi PR [#37707](https://github.com/elevenlabs/xi/pull/37707) head `49a9dc2` (carries the `DynamicVariableNestedValueType` `$ref` fix needed for `@asyncapi/parser` to accept the document); re-pin to a merged `main` commit once the PR lands. See [`Codegen~/schemas/README.md`](../../Codegen~/schemas/README.md).
- [x] **3.2 — Generator scaffold.** New `Codegen~/` pnpm project (parallel to `Bridge~/` — own `package.json`, own deps, own `pnpm install`). TypeScript-based generator under `Codegen~/src/` reading the vendored AsyncAPI spec at `Codegen~/schemas/convai-asyncapi.yml` and emitting C# classes for `IncomingSocketEvent` / `OutgoingSocketEvent` unions and their nested payload types. Scripts mirror the `Bridge~` pattern: `pnpm --dir Codegen~ run generate` / `pnpm --dir Codegen~ run verify:protocol-dtos`. Document the new commands in `.claude/CLAUDE.md`. **Implementation notes:** `document.json()` from `@asyncapi/parser` returns the spec with all `$ref`s already inlined (no yaml package needed); `x-parser-schema-id` identifies top-level schemas; nested objects are generated as nested C# classes; class/property name collision (`csPropName` appends "Data"; `nestedClassName` appends "Payload") guards against invalid C#. `eslint.config.js` updated to only lint `.js`/`.mjs` (TypeScript covered by `tsc --noEmit`). `.claude/CLAUDE.md` update blocked by file-protection policy — the `verify:protocol-dtos` command needs to be manually added there.
- [x] **3.3 — Output target.** Generated files committed under `Runtime/Core/Protocol/`. CSharpier-formatted; freshness gate via `verify:protocol-dtos` script.
- [x] **3.4 — Allocation/AOT discipline.** Generator output uses `System.Text.Json` source-generated serialization (IL2CPP-friendly) or Newtonsoft.Json — decide once we can compile in Unity. Until then, the generator emits a debug-only `Console.WriteLine` round-trip test we can run with `dotnet run` to confirm parse/emit symmetry. **Decision deferred to Unity compile step; `System.Text.Json` attributes are already in place.** Round-trip test lives in `Codegen~/round-trip/` (`.csproj` committed, `Program.cs` regenerated by `pnpm run generate`); run with `pnpm --dir Codegen~ run round-trip`. Two generator bugs fixed in this task: (1) bare auto-properties must not emit trailing `;`; (2) C# CS0102 requires that a property and its nested class do not share the same name — a `Data` suffix is now appended to the property name when conflict detected.

**Per-event listener emission (Phase 4 follow-up). Status: done.** Emits `Runtime/Core/Protocol/IncomingEventDispatcher.g.cs` — an abstract base class with one `event System.Action<T>?` per incoming wire type, an `OnUnhandled` fallback (mirrors the JS SDK's `onDebug` arm in `BaseConversation.onMessage` — server-added event types ahead of an SDK refresh land here rather than raising as errors), and a `protected void Dispatch(IncomingSocketEvent)` switch. Conversation inherits from the dispatcher and calls `Dispatch(...)` from its message router; combining cases (e.g. `agent_tool_response{,_full_payload}`), suppression (e.g. `ping` auto-pong), and side effects (e.g. `end_call` shortcut) stay in Conversation so the dispatcher is a strict 1:1 fan-out from the wire schema. Template-string emission against the `incoming` payload list already collected in `Codegen~/src/generate-protocol-dtos.ts` — no Modelina preset extension; the per-event surface lives outside the DTO classes and Modelina stays scoped to model emission. The same change renamed the existing DTO outputs from `.cs` to `.g.cs` so the generated-vs-handwritten signal lives in the file name (CSharpier already runs with `--include-generated`, so formatting is unaffected).

**Per-event "args" emission for Conversation event surface (Phase 4 follow-up). Status: done.** Emits sibling generated file `Runtime/Core/Protocol/IncomingEventArgs.g.cs` containing one flat `record` per incoming wire type (e.g. `public record AgentResponseArgs(string AgentResponse, int EventId)` from the wire `AgentResponse { Type, AgentResponseEvent: { AgentResponse, EventId } }`) plus a static `IncomingEventArgsExtensions` class with one `ToArgs(this WireType e)` extension per type. Conversation subscribes to the dispatcher's wire-typed events and re-raises idiomatic user-facing events of its own — e.g. `base.OnAgentResponse += wire => AgentResponded?.Invoke(wire.ToArgs());` — so the args codegen provides building blocks while Conversation keeps full control over its event-surface naming, combining, suppression, and side-effect policy. **Transformation rule** per wire type: strip the redundant `type` property; if the remaining properties are exactly one nested-object property, flatten that inner object's fields onto the args record; otherwise copy the non-`type` properties as-is. Field names are preserved verbatim from the inner type — no renaming heuristic; Conversation can rename at its own surface if it cares (e.g. expose `AgentResponded` with a property aliased to `Text`). Records use a primary constructor for terse type definitions, and the generated `ToArgs()` body **always uses named arguments** at construction so the codegen is robust to schema field reordering or insertion (a future spec edit that adds a field in the middle of `AudioEvent` recompiles cleanly). Nested complex types (e.g. `AudioEventAlignment` referenced from `AudioResponseArgs`) are reused from the wire-side DTO file, not re-emitted. **Out of scope for the args generator** (all hand-written in Conversation per Phase 4.4): combining (e.g. `agent_tool_response{,_full_payload}` fanning into one user-facing event), suppression (`ping`), side effects (`end_call`), and the user-facing client-tool registration/unregistration API. **Known blocker for two types:** `ClientToolCall` and `AgentToolResponseFullPayload` hit the wire-codegen name-clash bug noted in 3.4 — the wrapper class collides with the envelope name, producing a self-referential property (`public ClientToolCall ClientToolCallData { get; set; }`). The args generator skips these two and Conversation hand-writes their mapping until the wire-side bug is fixed in a separate cleanup task. **Implementation choice:** introspect Modelina's already-emitted `ConstrainedObjectModel` per top-level wire payload (via `OutputModel.model.properties`, walking `ConstrainedReferenceModel.ref` for nested objects), reusing the same C# property names, types, and nullability rules the DTO preset emitted. Rejected alternatives — re-running Modelina against a transformed schema (would re-emit shared nested types like `AudioEventAlignment` and require deduplication against the DTO output) and hand-rolled JSON-Schema → C# mapping (would drift from the DTO preset's naming/nullability rules). The chosen approach guarantees zero drift by construction since both files render from the same model graph.

This phase can be deferred to overlap with Phase 4 if Phases 1–2 are slow.

### Phase 4 — C# Conversation + Core abstractions (requires Unity host project)

The Conversation logic itself. Mostly a port of `BaseConversation.ts`'s
`onMessage` switch (lines 446-569), state tracking (mode/status/feedback),
interruption handling, and client-tool dispatch into C#. Pure orchestration
— no jslib, no platform-specifics — so it's testable in Edit Mode against
mock `IConnection`/`IInputController`/`IOutputController`.

- [x] **4.1 — Define the three abstractions.** `Runtime/Core/IConnection.cs`, `IInputController.cs`, `IOutputController.cs` matching the shapes sketched in [ARCHITECTURE.md](../ARCHITECTURE.md). `IConnection.Send` takes typed `OutgoingSocketEvent`; `OnMessage` delivers typed `IncomingSocketEvent`. **Asmdef split:** Core ships as its own cross-platform asmdef `ElevenLabs.Agents.Core` (`Runtime/Core/ElevenLabs.Agents.Core.asmdef`); the existing `ElevenLabs.Agents.WebGL` asmdef now references it, as does the tests asmdef. Supporting types added alongside the interfaces: `Mode` (enum), `FormatConfig`, `InputDeviceConfig`, `OutputDeviceConfig`, `DisconnectionDetails` + `DisconnectionContext` + `DisconnectionReason`. All `public` for now — the architecture's "sketched as internal" question stays open and can be tightened in a follow-up by moving everything behind `InternalsVisibleTo`. `Close()` is sync on `IConnection` (matches JS `BaseConnection.close(): void`) and async (`Awaitable`) on the input/output controllers (matches JS `Promise<void>`); no `IDisposable` on the interfaces — Conversation owns the lifecycle and calls `Close()` from `EndSession`. `SetDevice` takes the device config + optional format override as separate parameters (cleaner than the JS `Partial<FormatConfig> & DeviceConfig` intersection). Verified via `dotnet csharpier check .` and `node TestProject/run-tests.ts` (78 tests pass, new asmdef compiles cross-platform).
- [x] **4.2 — `Conversation` class.** Public API (`StartSessionAsync`, `EndSession`, `SendUserMessage`, `SendContextualUpdate`, `SendUserActivity`, `SendFeedback`, `SetVolume`, `SetMicMuted`, `GetInputByteFrequencyData`, `GetOutputByteFrequencyData`, `GetInputVolume`, `GetOutputVolume`); events for all the callback shapes the JS SDK exposes. The signature is C#-idiomatic — `event` + args records, `Awaitable<T>` for async methods. **Visibility settled before this task landed:** `IConnection` / `IInputController` / `IOutputController` flipped from `public` to `internal`; supporting types (`Mode`, `FormatConfig`, `InputDeviceConfig`, `OutputDeviceConfig`, `DisconnectionDetails` + friends) stay `public` because they appear on Conversation's public surface — `Mode` in `ModeChanged`, the device configs in `ConversationOptions`, `DisconnectionDetails` in the `Disconnected` event. Rationale: ARCHITECTURE.md already sketched the three abstractions as internal; reducing the public semver surface matches Plan B's "the public Conversation lives in C# only" framing. New `Runtime/Core/AssemblyInfo.cs` exposes the internals to `ElevenLabs.Agents.WebGL` (Phase 5 bridged impls) and `ElevenLabs.Agents.WebGL.Tests`. **Skeleton scope:** events declared (lifecycle + args-typed protocol events); `StartSessionAsync` throws `NotImplementedException` (wired in Phase 5.4); `EndSession` closes the three impls; `SendUserMessage` / `SendContextualUpdate` / `SendUserActivity` forward to `IConnection.Send` with typed `OutgoingSocketEvent`; `SetVolume` / `SetMicMuted` / `Get*ByteFrequencyData` / `Get*Volume` forward to the controllers; `SendFeedback` deferred to Phase 4.3 (depends on `lastFeedbackEventId` state tracked by the router). Internal constructor takes the three interfaces so Phase 4.5 tests inject mocks directly. Inherits from `IncomingEventDispatcher` so Phase 4.3's router can `connection.OnMessage += base.Dispatch` and fan the wire-typed events out into the args-typed surface via the internal `Raise*` helpers.
- [ ] **4.3 — Message router.** Translate `BaseConversation.onMessage`'s switch into C#. Each case calls a small handler method, same as the JS SDK.
- [ ] **4.4 — Client tool dispatch.** A `Dictionary<string, ClientToolHandler>` owned by `Conversation`. The `client_tool_call` handler looks up, awaits, sends `client_tool_result`. No Proxy, no name-set, no shared dispatcher key.
- [ ] **4.5 — Edit-mode tests.** Mock IConnection / IInputController / IOutputController; drive every router branch and every public method. This is the regression baseline for the whole SDK.
- [ ] **4.6 — XML doc comments on the public surface.**

### Phase 5 — WebGL bridged implementations (requires Unity host project)

Thin C# wrappers around the `JsObject` handles returned by the Phase 2
factories. Each one is a class that holds a `JsObject`, implements the
matching `I…` interface from Phase 4, and translates every interface call
into a `jsObject.CallAsync(...)` / `Call(...)` / `Get<T>(...)`. **No new
`DllImport` declarations** — the generic primitives' `EL_Object*` entry
points carry everything.

- [ ] **5.1 — `BridgedWebSocketConnection` and `BridgedWebRTCConnection`.** Both implement `IConnection`. Constructor takes a `JsObject` already obtained via `await JsBridge.InvokeFactoryAsync<JsObject>("createWebSocketConnection", config)` (or `createWebRTCConnection`). Reads `ConversationId` / `InputFormat` / `OutputFormat` via `jsObject.Get<…>()`. `Send(OutgoingSocketEvent msg)` becomes `jsObject.Call("sendMessage", msg)`. Events: the C# event accessor wraps a delegate in `BridgeCallback.Wrap(handler)` and passes it via `jsObject.Call("onMessage", callback)`. The WebRTC variant additionally calls `jsObject.Get<JsObject>("input")` and `jsObject.Get<JsObject>("output")` to expose the input/output controllers bound to the same JS instance (per the SDK's coupling), wrapping them in `BridgedInputController` / `BridgedOutputController`.
- [ ] **5.2 — `BridgedInputController` and `BridgedOutputController`.** Each holds a `JsObject` and implements its interface as a 1:1 method-name mapping. Sync getters (`GetVolume`, `GetByteFrequencyData`) use `jsObject.Call<float>(...)` / sync calls; async ops (`SetDevice`, `Close`) use `jsObject.CallAsync(...)`. For `getByteFrequencyData(buffer)` where C# needs to write into its own buffer, the bridged side passes a `byte[]` argument and the generic primitive's binary-payload variant — flagged as v0.3 work in [generic-bridge-primitives.md](./generic-bridge-primitives.md). For v0.1, the JS method returns a fresh `Uint8Array` per call which crosses as a JSON-encoded number array; acceptable since visualizer frame data is read at most once per Unity frame (~60 Hz, ~1 KB per call).
- [ ] **5.3 — `BridgedSession` orchestration.** A small helper (consumed only by `Conversation.StartSessionAsync` under `#if UNITY_WEBGL`) does the full session setup: awaits the connection factory, awaits the input/output factories (WebSocket only — WebRTC already has them), calls `attachDefaultAudio` via the factory and stashes the returned `JsFunction` detach handle on the session, hands back the three bridged C# wrappers + the detach handle. The C# `Conversation` doesn't see any of this — it just receives `IConnection` + `IInputController` + `IOutputController`.
- [ ] **5.4 — Conversation factory selection.** Inside `Conversation.StartSessionAsync`: `#if UNITY_WEBGL` delegates to `BridgedSession`; `#else` calls native impls (Phase 7). The rest of `Conversation` is platform-unaware.
- [ ] **5.5 — Edit-mode tests for the bridged wrappers.** Stub `JsObject` / `JsFunction` / `BridgeCallback` (the generic primitives layer is its own test surface — here we only check that the bridged wrappers issue the right `CallAsync` / `Call` / `Get` invocations with the right method names and arguments, and that incoming `BridgeCallback` invocations route to the right C# events).

### Phase 6 — WebGL smoke + integration (requires Unity host project)

- [ ] **6.1 — Minimal scene.** A scene that calls `Conversation.StartSessionAsync` against a real test agent, sends a message, gets a response, ends. Manually verified in Chrome.
- [ ] **6.2 — Validation assertions.** Same V1–V4 list as the primitives plan, plus: audio default-mode flows correctly (mic in, speaker out, no Unity AudioSource involved); end-session tears down all three JS instances without leaks.
- [ ] **6.3 — Vitest browser-mode harness.** Drives the WebGL build from a JS test, asserting on the wire format.

### Phase 7 — Native implementations (deferred — post-Unity-license)

Out of scope for this plan's v0.1 critical path. Listed for completeness so
the abstractions in Phase 4 don't accidentally over-fit WebGL.

- `NativeWebSocketConnection` using `System.Net.WebSockets.ClientWebSocket`. Reuses the same protocol DTOs from Phase 3.
- `UnityMicrophoneInput` using `UnityEngine.Microphone` + a chunking loop.
- `UnityAudioSourceOutput` using `AudioClip` + `PCMReaderCallback` fed by a ring buffer.
- Edit-mode tests targeting the native impls behind the same `IConnection` contract used in Phase 4.

A v0.1 release tag may ship WebGL-only and call out native as v0.2, or wait
for native — that decision is at the milestone review, not in this plan.

## Verification (Unity-free phases)

After each Unity-free task:

```bash
pnpm --dir Bridge~ run typecheck
pnpm --dir Bridge~ run lint
pnpm --dir Bridge~ run test
```

If `src/connection/` changed, also `pnpm --dir Bridge~ run verify:connection`.

The local Claude permissions allowlist gates these one-by-one — don't chain
with `&&`.

## Open questions

1. **Factory registration timing in Emscripten — mostly resolved from source.** The `__postset` + `__deps` mechanism *does* work across separate `.jslib` files (Emscripten merges all `--js-library` files into one `LibraryManager.library` and resolves deps against the merged object; postsets emit at module-init/parse time before `main`; ordering is dep-graph-driven so our `__deps: ["$EL_RegisterFactory"]` puts the factory postsets after the primitives' definitions). The one open piece is whether `$EL_ConnectionFactories` and `$EL_AudioGlueFactories` (non-function library entries) get pulled into the build at all — they're not in the C# `[DllImport]` dep chain, so without `INCLUDE_FULL_LIBRARY` or an explicit `__deps` reference, they may be stripped silently. Fix is known and lands with Phase 5 (`EL_ConnectionFactoriesInit()` no-op DllImport whose JS body declares the dep); verification happens at Phase 6 Unity smoke.
2. **Should `IConnection.Send` take a typed event union, or a serialised string?** Typed gives compile-time safety, but means the C# Conversation builds the event then `BridgedWebSocketConnection.Send` serialises and the JS dispatcher rehydrates. Untyped means the Conversation serialises once and the JS side hands the raw string straight to `connection.sendMessage`. Lean typed for native parity, but worth a second look in Phase 4.
3. **What happens if the user calls a method during a transient disconnect?** JS SDK behaviour varies by method. Plan B's C# Conversation should make this consistent — likely "throw `InvalidOperationException`" rather than silently drop. Decide in Phase 4.
4. **Repository structure: in `elevenlabs/packages` or standalone?** Still open from the RFC. Plan B's WebGL bundle depends on `@elevenlabs/client`, which slightly tilts toward in-monorepo. Doesn't block any phase.

## Pending Human Approval

The tasks below are appended during implementation as new work is discovered.
They are not yet approved for execution — promote them into a numbered phase
task above once agreed.

- [ ] **Set up a spec-sync workflow in `elevenlabs/xi`.** GitHub Action on the `xi` repo that watches `docs/convai-asyncapi.yml` and opens a PR against this repo updating `Codegen~/schemas/convai-asyncapi.yml` (with the new source commit hash recorded in the sibling `README.md`). Lives outside this repo but the acceptance check is here: a change to the xi spec produces a PR here that, when merged, triggers Phase 3 codegen and updates the committed DTOs. Until this workflow is in place, the vendored spec is updated manually.
- [x] **Remove deep-import workaround once `@elevenlabs/client` exports `MediaDeviceInput` / `MediaDeviceOutput` upstream.** Resolved by [elevenlabs/packages#835](https://github.com/elevenlabs/packages/pull/835) shipping in `@elevenlabs/client@1.11.0` as a new `./internal/unity` sub-path export, then consumed Unity-side in the cleanup commit that also implements Task 2.3. The spec that drove the SDK PR is in [`client-sdk-exports-for-plan-b.md`](./client-sdk-exports-for-plan-b.md). All three workarounds (ambient `.d.ts`, Vite alias, Rolldown alias) deleted; `factories.ts` now imports from `@elevenlabs/client/internal/unity` along with the three named config-type aliases (`MediaDeviceInputConfig`, `MediaDeviceOutputConfig`, `WebRTCConnectionConfig`).
- [ ] **Spec the user-facing client-tool registration/unregistration API (Phase 4 follow-up on 4.4).** Phase 4.4 already specifies the *internal* dispatch (a `Dictionary<string, ClientToolHandler>` owned by Conversation that handles incoming `client_tool_call` and emits `client_tool_result`). What it doesn't specify is the *public* surface a game developer uses to add and remove tools — and that's the surface where type safety matters. Design space to nail down before promoting into a Phase 4 sub-task:
  - **Parameter shape on the handler side.** Strongly-typed parameter record (best DX, requires per-registration JSON deserialization), `System.Text.Json.JsonElement` (least friction, manual unpacking), or `Dictionary<string, object>` (worst type safety). The strongly-typed path likely means `Conversation.RegisterTool<TParams, TResult>(string name, Func<TParams, TResult> handler)` or similar; the JsonElement path leaves the consumer to validate fields themselves.
  - **Sync vs async handlers.** `Func<TParams, TResult>` for sync (calculator-style tools), `Func<TParams, Task<TResult>>` or `Func<TParams, Awaitable<TResult>>` for async (web-service-backed tools). Probably both, via overloads.
  - **Error propagation.** Handler exceptions must round-trip into `client_tool_result.is_error = true` plus an error string. Decide whether to expose a typed `ClientToolException` so handlers can attach a specific `error_type`, or rely on `ex.Message` plus an opt-in error-context callback.
  - **Removal / lifetime semantics.** `Conversation.UnregisterTool(string name)`, or return an `IDisposable` from `RegisterTool` that the caller disposes, or both. Mid-session re-registration: allowed (with overwrite warning) or rejected.
  - **Tool schema declaration.** The server side already knows the agent's tool schema, but the C# handler signature implies one — decide whether to validate at registration (require a JSON Schema or a `Type`-derived schema) or trust the server and surface mismatches as runtime errors.
  - **Registration timing.** The JS SDK conceptually requires tools be registered before `StartSession`. Plan B's C# Conversation owns the dispatch table directly and can support late registration; confirm we want that DX win before locking the API down.

  Out of scope for v0.1 — Phase 4.4's plain dictionary is enough to validate the internal flow. Promote into a numbered Phase 4 sub-task once the API shape is agreed.
- [ ] **Wire TypeScript-aware ESLint and enforce the `internal/unity` import boundary.** The current `Bridge~/eslint.config.js` is JS-only (just `@eslint/js` recommended), so `pnpm lint` effectively lints `eslint.config.js` itself and skips the TS sources entirely. The migration that closed [elevenlabs/packages#843](https://github.com/elevenlabs/packages/issues/843) — Unity bridge no longer imports anything from `@elevenlabs/client/.`, only from `@elevenlabs/client/internal/unity` — has no automated guardrail today; a regression would slip through CI. Add `typescript-eslint` to the flat config, enable a `no-restricted-imports` rule that bans `@elevenlabs/client` (suggesting `@elevenlabs/client/internal/unity` instead), and confirm `pnpm lint` actually walks `src/**/*.ts` + `build/**/*.ts`. Catches the regression at lint time so the WebGL build never re-acquires the navigator-at-IIFE-eval problem the shim used to mask.
