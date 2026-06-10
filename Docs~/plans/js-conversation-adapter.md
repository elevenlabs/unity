# Plan: JS Conversation Adapter — wiring `@elevenlabs/client` to the bridge primitives

> **Superseded by [plan-b.md](./plan-b.md) — see [ARCHITECTURE.md](../ARCHITECTURE.md).**
> Plan B bridges `@elevenlabs/client`'s connection + input/output controllers
> instead of its `Conversation` class. Kept for historical reference; the
> Tasks list below is dropped.

## Context

All three bridge primitives (Promise-as-Task, Observer, Handler Invocation) are complete on the JS side with Vitest coverage. The C# halves are blocked on Unity license.

To keep moving without Unity, build the **JS-side adapter** that wraps `@elevenlabs/client`'s `Conversation` and exposes it through the primitives. This crystallises the wire format, produces a tested reference implementation, and gives the eventual C# façade (and any future codegen) a stable target.

Everything in this plan is fully testable with Vitest. No Unity required.

## Prerequisites (completed in `bridge-primitives.md`)

Two pieces of cleanup landed before adapter work began. Both are tracked in [bridge-primitives.md](./bridge-primitives.md); they're listed here so this plan reads stand-alone:

- **Primitive renames.** `$EL_FireObserverEvent` → `$EL_EmitEvent`; `$EL_CreateInvocation` → `$EL_InvokeHandler`. The C# SendMessage target `OnObserverEvent` keeps its qualifier.
- **TypeScript + Rolldown bundling.** Primitives now live under `Bridge~/src/primitives/` and are bundled to `Plugins/WebGL/ElevenLabsBridge.jslib` by `Bridge~/build/bundle-jslib.ts`. `pnpm run verify:primitives` enforces freshness. The adapter reuses both the primitives and the bundler script.

## Goals

- Add `@elevenlabs/client` as a `Bridge~/` dependency and bundle it into the WebGL build alongside the adapter.
- Author the adapter in TypeScript, type-checked against `@elevenlabs/client`'s declarations, and bundle into a jslib-compatible artifact under `Plugins/WebGL/`.
- Bridge every public `Conversation` surface area through the primitives.
- Cover the adapter with Vitest, mocking `@elevenlabs/client` the same way the primitives mock the Unity runtime today.

## Non-goals (deferred)

- Any C# code (blocked on Unity).
- Code generation. Hand-write the adapter to discover the right patterns; codegen from `@elevenlabs/client`'s `.d.ts` is a strong follow-up but only worth it once the wire format is stable.
- Audio interception / Unity-routed audio (v0.3).
- Visualizer polling APIs (`getInputByteFrequencyData`, etc.) — these want the binary path; out of scope here.

## Working from the Conversation surface

The shape of this adapter is dictated by `@elevenlabs/client`, not by the primitives. Each row below names a piece of the Conversation API, which primitive carries it, and any contract notes.

### DllImport signatures

All entrypoints take `sessionId` first for consistency. Promise-as-Task variants take `promiseId` second; the C# lambda passed to `BridgePromise.Call` closes over `sessionId` and receives `promiseId` from the registry.

| Entrypoint | Signature |
|---|---|
| `EL_StartSession` | `(sessionId: number, promiseId: number, optionsJsonPtr: number)` |
| `EL_EndSession` | `(sessionId: number, promiseId: number)` |
| `EL_SendUserMessage` | `(sessionId: number, promiseId: number, textPtr: number)` |
| `EL_SendContextualUpdate` | `(sessionId: number, promiseId: number, textPtr: number)` |
| `EL_SendUserActivity` | `(sessionId: number, promiseId: number)` |
| `EL_SendFeedback` | `(sessionId: number, promiseId: number, isPositiveInt: number)` |
| `EL_SetVolume` | `(sessionId: number, promiseId: number, volumeStringPtr: number)` |
| `EL_RegisterClientToolName` | `(sessionId: number, toolNamePtr: number)` |
| `EL_UnregisterClientToolName` | `(sessionId: number, toolNamePtr: number)` |

### Lifecycle

C# owns the bridge `sessionId` (allocated by `BridgeIdGenerator`, opaque to JS). JS keys its in-memory session map by the C#-supplied `sessionId` and never allocates one.

| Conversation surface | Primitive | Notes |
|---|---|---|
| `Conversation.startSession(opts)` | Promise-as-Task | Order of operations is load-bearing. Adapter (1) parses opts, (2) **inserts the `SessionRecord` (observers, `toolNames`, `toolDispatcherKey`, `instance: null`) into the session map under the C#-supplied `sessionId` first**, (3) builds the SDK options object (the Proxy and observer callbacks close over the record), (4) awaits `Conversation.startSession`. On success, attaches the SDK instance to the record and resolves with `""`. On failure, drops the map entry and rejects with the error. Installing before the await means callbacks firing mid-start — and any `EL_RegisterClientToolName` racing in from C# — find the record. |
| `conv.endSession()` | Promise-as-Task | Resolves with `""`. Adapter disposes session-scoped observers via the primitives' `EL_DisposeObserver`, drops the session record (including its `toolNames` set and `toolDispatcherKey`), and forgets the SDK instance. |
| `conv.getId()` | n/a | No separate API and no `conversationId` in the `startSession` resolve. C# subscribes to `onConnect` (which delivers `{ conversationId }` before `startSession` resolves) and lifts it from there. |

### Methods

Each is a Promise-as-Task. The wire input is whatever the SDK expects, marshalled the simplest way that round-trips through `UTF8ToString`.

| Conversation surface | Wire input |
|---|---|
| `sendUserMessage(text)` | bare string pointer |
| `sendContextualUpdate(text)` | bare string pointer |
| `sendUserActivity()` | none |
| `sendFeedback(isPositive)` | int (0/1) |
| `setVolume({ volume })` | bare float-as-string pointer |

Bare values where there's nothing to envelope; JSON only when the SDK actually takes a structured object that the C# side needs to compose. This avoids premature JSON overhead on the simple methods.

### Events (callbacks)

The SDK reads its callbacks at construction time and changes behaviour based on which ones are present (e.g. presence of `onAudio` activates raw-audio delivery). The adapter therefore **must not** install a callback the C# side did not explicitly subscribe to.

Concretely: `StartSessionOptions` carries a sparse `observers: { [eventName]: observerId }` map. The adapter walks that map and only installs callbacks for the keys actually present. Anything not requested is left undefined in the SDK's options.

For each requested observer the adapter does two things at session start: installs the matching SDK callback, and registers the observer ID with the primitives' `$EL_RegisterObserver(observerId, detachFn)`. `detachFn` swaps the captured SDK callback for a no-op so dispatch stops cleanly. This means `endSession` (and individual `EL_DisposeObserver` calls, should the C# side ever want to drop one mid-session) tear down the SDK callbacks uniformly through the agnostic primitives lifecycle — the conversation owns the callbacks once they cross into it. Future SDK iterations that expose a real observable pattern can swap the detach implementation without changing this contract.

| Conversation event | Payload shape emitted to C# |
|---|---|
| `onConnect` | `{ conversationId }` |
| `onDisconnect` | pass-through of SDK's disconnect details |
| `onMessage` | `{ message, source }` |
| `onError` | `{ message, context }` |
| `onStatusChange` | `{ status }` |
| `onModeChange` | `{ mode }` |
| `onCanSendFeedbackChange` | `{ canSendFeedback }` |
| `onVadScore` | `{ vadScore }` |

`onAudio` is intentionally absent — see "Out-of-scope surface" below; it's deferred to the v0.3 audio path along with the visualizer methods.

Each installed callback JSON-stringifies its argument and calls `_EL_EmitEvent(observerId, payload)`.

### Client tools (Proxy + name set, single shared dispatcher)

**Primitive separation.** Tools go through the Handler Invocation primitive only — `$EL_InvokeHandler` on the JS side, `BridgeHandler.Register` on the C# side. The Observer primitive is for SDK events (`onMessage`, `onStatusChange`, etc.) and is never reused for tool dispatch. The `toolNames` set below is adapter-private session state, not a primitive — it exists solely to answer the synchronous `getOwnPropertyDescriptor` Proxy trap that `@elevenlabs/client` requires.

The SDK uses `clientTools` in three ways ([BaseConversation.ts:282-340](https://github.com/elevenlabs/packages/blob/main/packages/client/src/BaseConversation.ts#L282-L340)):

1. `Object.prototype.hasOwnProperty.call(clientTools, name)` — existence check.
2. `clientTools[name](params)` — invocation on the "exists" branch.
3. On the "not exists" branch: emits `onError("Client tool with name X is not defined on client")`, sends `client_tool_result` with `is_error: true` and that exact message, and offers an `onUnhandledClientToolCall` override hook.

Path 3 is meaningfully different from "invocation threw" — agent-visible error message, dedicated `onError` text, the hook. To preserve it, the proxy's `getOwnPropertyDescriptor` trap has to answer honestly. But `getOwnPropertyDescriptor` is synchronous and the primitives' JS→C# channel (`SendMessage`) is void-returning, so JS can't sync-query the C# `BridgeHandlerRegistry`. Therefore C# has to push tool-name presence across at register time so the proxy can answer locally.

Design:

- **A single shared dispatcher per session.** Rather than registering one Handler Invocation entry per tool, C# registers **one** shared dispatcher under a session-scoped handler key (`toolDispatcherKey`) and routes individual tool calls internally on its own side. The JS adapter only needs to know that one key.
- `StartSessionOptions` carries `toolDispatcherKey: string` — an opaque C#-chosen handler-key string (GUID, session-scoped composite, anything) that C# also passes to its own `BridgeHandler.Register(toolDispatcherKey, sharedDispatcher)` call. The JS adapter stores it on the session record verbatim and never inspects or composes it. The handler-key namespace remains entirely C#-owned.
- Per `SessionRecord`, the adapter holds `toolNames: Set<string>` of bare tool names registered for this session. Starts empty; mutated by the two DllImports below.
- **`toolNames` shape — explicit:** each entry is the bare tool name as the agent sees it and as `@elevenlabs/client` passes to the Proxy traps. This is the property name the SDK uses in `clientTools[name](...)` and `hasOwnProperty.call(clientTools, name)`. The set's only purpose is to let `getOwnPropertyDescriptor` answer the SDK's existence check synchronously without an IPC round-trip.
- Two lightweight DllImports manage the set: `EL_RegisterClientToolName(sessionId, toolNamePtr)` adds; `EL_UnregisterClientToolName(sessionId, toolNamePtr)` removes. They carry only the bare tool name — no dispatcher payload, no callback wiring, no handler key. The C# façade calls them alongside whatever internal tool-table mutation it does.
- **Wire envelope for tool calls** (the only adapter-specific JSON shape on the tool path): the Proxy's `get` trap dispatches `$EL_InvokeHandler(session.toolDispatcherKey, JSON.stringify({ tool: String(name), params }))`. The C# shared dispatcher parses `{ tool, params }`, looks up the tool by name in its own internal table, invokes the implementation, and returns the serialised result. The bridge layer has no knowledge of individual tool implementations.
- `getOwnPropertyDescriptor(_, name)` consults the session's `toolNames` set: returns `{ value: undefined, writable: true, enumerable: true, configurable: true }` when `toolNames.has(String(name))`, `undefined` when absent. Absence routes the SDK into its dedicated not-defined branch.
- `endSession` drops the session record, which clears the set and forgets the dispatcher key.

This collapses per-tool C# registration to a single per-session registration, keeps JS-side state to a single `Set<string>`, and limits the bridge's tool-specific contract to one wire envelope (`{ tool, params }`) plus one C#-supplied dispatcher key per session.

### Out-of-scope surface (no jslib entries)

`getInputByteFrequencyData`, `getInputVolume`, `getOutputByteFrequencyData`, `getOutputVolume` are synchronous SDK getters that want the binary/polling route. Out of scope for v0.1 — no jslib entries are emitted. A short source-comment in `src/conversation/index.ts` marks the surface and references the v0.3 audio path. The C# façade independently throws `NotSupportedException` for these methods so the surface fails loudly without round-tripping through jslib. Reason: v0.3 will redesign these against the binary/heap-buffer path, so any v0.1 scaffold would be throwaway.

`onAudio` falls in the same bucket: the SDK delivers raw PCM that would force a base64/binary encoding decision the v0.3 audio path will obviate. The adapter does not install `onAudio` for v0.1 — it's omitted from the events table above, and the opt-in `observers` map mechanism remains intact for future events. If an `onAudio` observer ID still appears in `StartSessionOptions.observers` (a C# bug), the adapter logs a warning via `_EL_Log` and skips installation rather than wiring it.

## Behaviour contracts

1. **No eager event callbacks; observers are conversation-scoped.** The adapter installs exactly the SDK callbacks the C# side requested via `observers`, and registers each observer ID with `$EL_RegisterObserver(observerId, detachFn)` so the session owns the callback lifecycle end-to-end. `endSession` (or a stand-alone `EL_DisposeObserver` from C#) tears them down uniformly via the primitives. Client tools mirror C# state via a per-session `toolNames: Set<string>` kept in sync by the lightweight register/unregister pair; tool dispatch funnels through a single shared C# handler per session (key supplied in `StartSessionOptions`) with the wire envelope `{ tool, params }`.
2. **One session per ID.** Sessions are independent. Starting a second session while one runs is allowed; each gets its own ID, its own `clientTools` Proxy, its own `toolNames` set, and its own `toolDispatcherKey`.
3. **Stale signals are no-ops.** Mirrors the existing primitive convention: events fired after `endSession` don't reach `SendMessage`; tool invocations that arrive after the C# handler has been unregistered route to the Handler Invocation primitive's reject path (and the next `hasOwnProperty` check sees the tool name gone once C# has called `EL_UnregisterClientToolName`).
4. **JSON envelope only when there's structure to envelope.** Bare strings/ints/floats cross as bare strings. Multi-field payloads use JSON. Empty-success keeps the primitive's `""` convention.
5. **Adapter depends on primitives, never the inverse.** `src/conversation/` may consume `$EL_*` helpers from `src/primitives/` via `__deps`. The primitives layer must remain SDK-agnostic — no Conversation-shaped knowledge leaks back. Any helper a future second adapter (different SDK, custom WebRTC client) would also want belongs in primitives; everything else stays adapter-private.

## File layout (after adapter is in)

```
Plugins/WebGL/
  ElevenLabsBridge.jslib              (already shipping — bundled from src/primitives/)
  ElevenLabsConversation.jslib        (new — bundled from src/conversation/)

Bridge~/
  package.json                        (adds @elevenlabs/client)
  build/
    bundle-jslib.ts                   (shared Rolldown script, reused for the adapter)
  src/
    primitives/                       (shipping — no changes)
      bridge-name.ts
      log.ts
      call-promise.ts
      observer.ts
      handler.ts
      index.ts
      globals.d.ts
    conversation/                     (new — adapter)
      index.ts
      session.ts
      types.ts                        (StartSessionOptions — single source of truth)
  tests/
    ElevenLabsBridge.test.ts          (no changes)
    conversation/                     (new — adapter; split per concern for parallel agent loops)
      client-mock.ts                  (shared @elevenlabs/client stub; base mock only)
      client-mock-tool-branch.ts      (derived variant — simulates the SDK's not-defined tool branch end-to-end)
      session.test.ts                 (Phase 2 — lifecycle happy/error paths)
      methods.test.ts                 (Phase 3 — sendUserMessage etc.)
      events.test.ts                  (Phase 4 — callback fan-out)
      tools.test.ts                   (Phase 5 — Proxy + handler-key map)
      lifecycle.test.ts               (Phase 6 — endSession teardown)
```

Two separate jslib artifacts (rather than one combined) so primitives stay diff-friendly and consumers reading the build output can see the seam. Emscripten concatenates them at WebGL build time.

## Implementation phases

Each phase ends with green Vitest, lint, format, and typecheck.

### Phase 1 — Dependency + adapter scaffold

(`rolldown` and `build/bundle-jslib.ts` already exist; see [bridge-primitives.md](./bridge-primitives.md).)

- `pnpm add @elevenlabs/client`
- Add a `pnpm run build:conversation` script that reuses `bundle-jslib.ts` with `src/conversation/` as the entry, emitting `Plugins/WebGL/ElevenLabsConversation.jslib`.
- Confirm `tsconfig.src.json` already picks up `src/conversation/` (it currently globs `src/**/*.ts`); if not, extend the include.
- Define `StartSessionOptions` in `src/conversation/types.ts` mirroring the wire shape (agentId, agentKey?, observers map, SDK overrides). No client-tool field — tools are routed via the Handler Invocation primitive directly.
- Add a `verify:conversation` script paralleling `verify:primitives`, so the adapter jslib can't drift from its TS sources.

### Phase 2 — Session lifecycle

- Implement `EL_StartSession` / `EL_EndSession`. No `EL_GetConversationId` — consumers lift `conversationId` from the `onConnect` payload.
- Internal session map keyed by the **C#-supplied `sessionId`** (C# owns the namespace via `BridgeIdGenerator`; JS does not allocate); per-session record holds `{ instance, observers, toolNames: Set<string>, toolDispatcherKey }`.
- Vitest: start → end happy path; double-end no-op; SDK construction failure routes through Promise rejection and drops the map entry.

### Phase 3 — Methods

- Implement `sendUserMessage`, `sendContextualUpdate`, `sendUserActivity`, `sendFeedback`, `setVolume`.
- Vitest per method: happy path, missing session ID error, SDK rejection propagated.

### Phase 4 — Events

- Wire each requested observer ID into an SDK callback that emits via `_EL_EmitEvent`.
- Vitest: SDK calls each callback → `SendMessage` saw `id:payload` with the right JSON; unrequested events leave the SDK options untouched (assert absence).

### Phase 5 — Client tools (Proxy + name set)

- Build the `clientTools` Proxy in `EL_StartSession` per the "Client tools" design; back it with the session's `toolNames` set.
- Implement `EL_RegisterClientToolName` / `EL_UnregisterClientToolName` — name-only mutations on the set.
- Vitest: registered tool → `hasOwnProperty` true → invocation observed → SDK resolves; unregistered tool → `hasOwnProperty` false → SDK takes the not-defined branch and sends `is_error: true`; register then unregister then call → also takes not-defined branch.

### Phase 6 — Lifecycle hardening + out-of-scope stubs

- `endSession` disposes session observers, clears the `toolNames` set, and drops the session record.
- Stub the visualizer methods such that calling them throws a clear "not supported in v0.1" error.
- Vitest: events fired after end don't reach `SendMessage`; pending invocations from before end can still resolve.

## Decided

1. **Bridge `sessionId` is C#-owned.** Allocated by `BridgeIdGenerator` and passed as the first argument to every adapter DllImport. JS keys its session map by the C#-supplied ID and never allocates one. This keeps the bridge ID space coherent with the rest of the primitives (Promise / Observer / Handler IDs are all C#-allocated) and avoids a redundant round-trip just to return a JS-allocated session ID.
2. **`conversationId` is sourced from `onConnect`.** `EL_StartSession` resolves with `""` (the primitives' empty-success convention). Consumers that care about the server-side conversation ID subscribe to `onConnect` — which delivers `{ conversationId }` before `startSession` resolves — and lift it from there. No separate `EL_GetConversationId` entrypoint.
3. **`onAudio` deferred to v0.3.** Same bucket as the visualizer methods: the SDK delivers raw PCM that would force a base64/binary encoding decision the v0.3 audio path will obviate. The opt-in `observers` map mechanism remains intact for future events.
4. **Overrides.** Pass through verbatim inside the JSON `StartSessionOptions` blob. The TS adapter types them by composing `@elevenlabs/client`'s exported config types directly (e.g. via `Pick` or `extends`) so SDK bumps ripple through tsc. The C# DTOs are hand-written for v0.1 (the C# halves don't exist yet anyway).
5. **Bundling target shape.** Single self-contained jslib that bakes in `@elevenlabs/client`. Accept the bundle-size cost; iterate if it becomes a real issue. Emscripten concatenates the primitives jslib and the adapter jslib at WebGL build time.

## Follow-ups (not in this plan)

- **C# DTO codegen from `@elevenlabs/client`'s `.d.ts`.** Once the wire format is stable, generate the C# `StartSessionOptions` / overrides / session-result DTOs from the SDK's TypeScript declarations so the typed C# surface tracks the SDK without hand-maintenance. Lives in a separate plan.
- **Hand-written → generated adapter.** The whole adapter is hand-written first to discover the right patterns; codegen of the dispatcher itself comes later, gated on this implementation proving the wire shape.
- **Observer key naming on the C# side.** The wire format uses SDK callback names (`onMessage`, `onError`, …) verbatim for v0.1 to keep the adapter mapping-free. Consider stripping the `on` prefix and exposing event-style names (`message`, `error`, …) on the C# observer-style surface in a future iteration. Needs a story for events whose names aren't obvious from their callback names (e.g. `onCanSendFeedbackChange` → ?).

## Verification

```bash
pnpm --dir Bridge~ install
pnpm --dir Bridge~ run build:conversation        # adapter jslib
pnpm --dir Bridge~ run verify:primitives         # primitives jslib (sanity)
pnpm --dir Bridge~ run verify:conversation       # adapter jslib has no drift vs TS sources
pnpm --dir Bridge~ run format:check
pnpm --dir Bridge~ run lint
pnpm --dir Bridge~ run typecheck
pnpm --dir Bridge~ run test                      # primitives + adapter green
dotnet csharpier check .                         # no C# changed, should remain clean
```

Success criteria:
- Every adapter entrypoint has a happy-path and an error-path Vitest case.
- The `StartSessionOptions` interface compiles against `@elevenlabs/client`'s exported types.
- `ElevenLabsConversation.jslib` is committed and reproducible.

---

## Tasks

Each task is sized for an isolated agent loop: it names the design section it implements, the files it touches, and the verification command that gates "done." Phases are ordered by dependency; tasks within a phase are mostly parallelisable except where noted.

Before every task: read this plan in full plus the relevant design section. After every task, run each of the following separately (the local Claude permissions allowlist gates them one-by-one, so don't chain with `&&`):

```bash
pnpm --dir Bridge~ run typecheck
pnpm --dir Bridge~ run lint
pnpm --dir Bridge~ run test
```

If any file under `src/conversation/` changed, also run `pnpm --dir Bridge~ run verify:conversation`.

### Phase 1 — Dependency + adapter scaffold

These three tasks must land in order; everything after Phase 1 depends on them.

- [ ] **1.1 — Add `@elevenlabs/client` dependency.** Run `pnpm --dir Bridge~ add @elevenlabs/client` and commit the updated `Bridge~/package.json` + `pnpm-lock.yaml`. Verify with `pnpm --dir Bridge~ install --frozen-lockfile` then `pnpm --dir Bridge~ run typecheck` (should remain green; nothing imports the package yet).
- [ ] **1.2 — Create the adapter scaffold.** Create `Bridge~/src/conversation/{index.ts,session.ts,types.ts}` with the shape from the file layout above. `types.ts` exports `StartSessionOptions` by **composing `@elevenlabs/client`'s exported config types directly** (e.g. via `Pick<SessionConfig, …>` or `extends`) so SDK bumps ripple through tsc — no hand-redeclared field shapes. Adapter-specific fields are: `observers: Partial<Record<EventName, number>>` (presence is the opt-in signal), `toolDispatcherKey: string` (opaque C#-supplied handler key forwarded verbatim to `$EL_InvokeHandler`), and the agent identifier(s) the SDK requires. No client-tool field. The bridge `sessionId` is NOT part of `StartSessionOptions` — C# passes it as the first DllImport argument. `index.ts` is initially empty (default-export an empty object so the bundler has something to wrap). `tsconfig.src.json` already globs `src/**/*.ts` and `tsconfig.tests.json` already globs `tests/**/*.ts`, so both pick up the new folders without changes. Verify with `pnpm --dir Bridge~ run typecheck`.
- [ ] **1.3 — Wire the conversation jslib build.** First, **parametrize the banner in `Bridge~/build/bundle-jslib.ts`** so the generated header derives its source path and `pnpm run build:*` reference from the input/output args — today it hardcodes `Source: Bridge~/src/primitives/. Regenerate via pnpm run build:primitives`, which is wrong for any other entry. Then add `build:conversation` and `verify:conversation` scripts to `Bridge~/package.json` paralleling the existing `build:primitives` / `verify:primitives` pair, both pointing at `src/conversation/index.ts` → `../Plugins/WebGL/ElevenLabsConversation.jslib`. Commit the generated (initially near-empty) `ElevenLabsConversation.jslib`. Document `verify:conversation` alongside `verify:primitives` in `.claude/CLAUDE.md`. Verify with both `pnpm --dir Bridge~ run verify:primitives` and `pnpm --dir Bridge~ run verify:conversation` (both must exit 0 on a fresh clone — the primitives jslib must remain byte-identical after the banner change).

### Phase 2 — Session lifecycle

Depends on Phase 1. Tasks 2.1–2.2 share state; do them sequentially.

- [ ] **2.1 — Stub `@elevenlabs/client` for tests.** In `Bridge~/tests/conversation/`, add a base Vitest mock (`client-mock.ts`) that exposes a `Conversation` class with `startSession` (returning a fake instance) and the methods listed in the "Methods" table. Each method records calls so tests can assert on them. Use `vi.mock("@elevenlabs/client", …)` inside this shared helper so test files reuse it. Keep the base mock **minimal** — it does not need to model the SDK's `clientTools` not-defined branch (that lives in `client-mock-tool-branch.ts`, added in Phase 5). Verify by writing a single throwaway test that imports the helper and asserts `Conversation.startSession` is callable — `pnpm --dir Bridge~ run test`.
- [ ] **2.2 — Implement the session map + `EL_StartSession` / `EL_EndSession`.** In `src/conversation/session.ts`, build a module-scoped `sessions: Map<number, SessionRecord>` keyed by the **C#-supplied `sessionId`** — no JS-side ID generator (C# owns the namespace via `BridgeIdGenerator`). `SessionRecord` holds `{ instance: SdkConversation | null, observers: Partial<Record<EventName, number>>, toolNames: Set<string>, toolDispatcherKey: string }`: `instance` starts `null` and is attached on `startSession` success; the toolNames set starts empty and is mutated by Phase 5; the dispatcher key is captured from `StartSessionOptions` at session start. `EL_StartSession` signature: `(sessionId, promiseId, optionsJsonPtr)`; the adapter `JSON.parse`s the options into `StartSessionOptions`. **Order of operations is load-bearing**: (1) parse opts, (2) insert the `SessionRecord` into the map under the supplied `sessionId`, (3) build the SDK options object (the Proxy and observer callbacks close over the record), (4) `await Conversation.startSession(sdkOpts)`. On success, attach the returned instance to the existing record and resolve the Promise-as-Task with `""` — no payload, `conversationId` comes from `onConnect`. On failure, **remove the record from the map** then reject with the error message. Export `EL_StartSession` and `EL_EndSession` (signature `(sessionId, promiseId)`) with `__deps` on `$EL_BridgeName` so SendMessage routing works; wire each through the Promise-as-Task envelope (`promiseId:ok:` / `promiseId:err:message`). Have `index.ts` re-export them on the library object. Vitest: start → end happy path resolves with `""`; the session map is keyed by the supplied `sessionId`; double-end no-ops; constructor throw propagates as `promiseId:err:` and removes the record; the record retains the supplied `toolDispatcherKey` verbatim; a tool-name register racing in mid-start (between insert and SDK resolve) finds the record. Verify: `pnpm --dir Bridge~ run test`.

### Phase 3 — Methods (parallelisable)

Tasks in this phase only touch `session.ts` (or a sibling `methods.ts` if it grows) and one test file each — they can be done in parallel by separate loops once Phase 2 is in.

- [ ] **3.1 — `sendUserMessage` + `sendContextualUpdate`.** Two near-identical Promise-as-Task wrappers taking a bare string pointer. Per "Methods" table. Vitest each: happy path, missing session ID rejects via `id:err:`, SDK rejection propagated.
- [ ] **3.2 — `sendUserActivity`.** No-arg Promise-as-Task variant. Vitest: happy path; missing session ID rejects.
- [ ] **3.3 — `sendFeedback`.** Int (0/1) arg. Vitest: positive feedback, negative feedback, missing session, SDK rejection.
- [ ] **3.4 — `setVolume`.** Bare float-as-string pointer. Vitest: happy path, malformed float input rejects without crashing, missing session.

### Phase 4 — Events

Depends on Phase 2.

- [ ] **4.1 — Translate `StartSessionOptions.observers` into SDK callbacks AND register them with the Observer primitive.** In `EL_StartSession`, walk the requested `observers` map. For each present key, do two things: (a) install the matching SDK callback that JSON-stringifies its arg and calls `_EL_EmitEvent(observerId, payload)` via the primitive (depends on `$EL_EmitEvent`); (b) register the observer ID with the primitive via `_EL_RegisterObserver(observerId, detachFn)` where `detachFn` swaps the captured SDK callback for a no-op (so future SDK firings become silent). This makes `endSession` and any stand-alone `EL_DisposeObserver(id)` call from C# tear down SDK callbacks uniformly through the agnostic primitives lifecycle. For each absent key, leave the SDK option undefined and skip both (a) and (b) — the SDK reads "present" as opt-in. Cover every row in the "Events (callbacks)" table. Depends on `$EL_EmitEvent` and `$EL_RegisterObserver`.
- [ ] **4.2 — Vitest coverage per event.** Per event: stub `Conversation` so its constructor captures the callbacks object; simulate the SDK firing each callback with a representative payload; assert `SendMessage` saw the matching `id:payload` JSON. Also assert: (a) omitting an observer from the request leaves the SDK options without that key (use `expect(...).not.toHaveProperty(...)` against the captured options); (b) for each present observer, `_EL_RegisterObserver` was called with the matching ID; (c) after `EL_DisposeObserver(id)` runs, simulating a further SDK firing of that callback no longer reaches `SendMessage` (proves the `detachFn` swap took effect).

### Phase 5 — Client tools (Proxy + name set, single shared dispatcher)

Depends on Phase 2. Two tasks, sequential — the proxy needs the set and the dispatcher key; the DllImports mutate the set.

- [ ] **5.1 — Build the `clientTools` Proxy backed by the session's `toolNames` set and `toolDispatcherKey`.** Extend `EL_StartSession` so the `clientTools` option on the SDK constructor is `new Proxy({}, traps)` capturing the session's `toolNames: Set<string>` AND `toolDispatcherKey: string` in closure. The set holds bare tool names as `@elevenlabs/client` passes them to Proxy traps. The dispatcher key is the opaque C#-supplied handler key for tool dispatch on this session — forwarded verbatim to `$EL_InvokeHandler`, never interpreted or composed by the adapter. Implement two traps: `get(_, name)` returns `async (params) => $EL_InvokeHandler(toolDispatcherKey, JSON.stringify({ tool: String(name), params }))` (the return string is forwarded verbatim — the SDK passes it through `String(result)`); `getOwnPropertyDescriptor(_, name)` returns `{ value: undefined, writable: true, enumerable: true, configurable: true }` when `toolNames.has(String(name))`, otherwise `undefined`. Vitest (use `client-mock.ts`, the base mock): (a) name in the set → `hasOwnProperty` true; (b) name not in the set → `hasOwnProperty` false; (c) call with name in the set → `$EL_InvokeHandler` observed with the session's `toolDispatcherKey` and payload `{ tool, params }` JSON → resolve back → settled with the returned string.
- [ ] **5.2 — `EL_RegisterClientToolName` / `EL_UnregisterClientToolName` DllImports.** Signatures: `EL_RegisterClientToolName(sessionId: number, toolNamePtr: number)` and `EL_UnregisterClientToolName(sessionId: number, toolNamePtr: number)` — bare tool name only, no handler key (the dispatcher key is shared per session and supplied at `StartSessionOptions` time). Each looks up the session record and `add` / `delete`s on its `toolNames` set. Missing session ID is a `_EL_Log("warn", …)` + no-op (per the primitives' first-wins convention; `BridgeLog` is C#-side, not JS). Re-registering an existing name is idempotent. Also in this task: introduce `tests/conversation/client-mock-tool-branch.ts` — a derived variant of the base mock that re-implements the SDK's `hasOwnProperty` → emit-`onError` → send-`client_tool_result` with `is_error: true` branch end-to-end. Vitest: register → `hasOwnProperty` flips true; unregister → flips false; missing session → no throw; idempotent re-register; using the derived mock, the SDK's full not-defined branch fires (assert `is_error: true` payload shape) when calling an unregistered tool end-to-end.

### Phase 6 — Lifecycle hardening + out-of-scope stubs

Depends on Phases 2–5.

- [ ] **6.1 — `endSession` teardown clears session state.** On end, iterate the observer-ID map calling `EL_DisposeObserver(observerId)` on each (which triggers the `detachFn` registered in Phase 4.1, neutering the SDK callback), clear the `toolNames` set, drop the session record (which also forgets `toolDispatcherKey`), and forget the SDK instance. Vitest (using `lifecycle.test.ts`): events fired after end don't reach `SendMessage` (proves the detach took effect); in-flight handler invocations from before end can still resolve (per "Behaviour contracts" #3); post-end tool calls return `hasOwnProperty: false` because the session record is gone.
- [ ] **6.2 — Visualizer + `onAudio` surface marker.** No jslib entries are emitted for `getInputByteFrequencyData`, `getInputVolume`, `getOutputByteFrequencyData`, `getOutputVolume`, and no SDK callback is installed for `onAudio` even if an observer ID for it appears in `StartSessionOptions.observers` — the adapter logs a `_EL_Log("warn", …)` and skips installation (per the "Out-of-scope surface" decision; all five belong on the v0.3 binary/heap-buffer path). Add a short source comment in `src/conversation/index.ts` listing these names and noting "v0.3 audio path". Vitest: for the visualizer methods, no coverage (no jslib entries exist); for `onAudio`, a single case asserting that providing an `onAudio` observer ID logs and skips installation without throwing or wiring a callback. The C# façade independently handles the absent surface by throwing `NotSupportedException` from non-DllImport stubs.

### Done check

The plan is complete when:

- `pnpm --dir Bridge~ run verify:conversation` exits 0 on a fresh clone.
- Every entrypoint in the "Methods", "Events", and "Client tools" sections has at least one happy-path and one error-path Vitest case.
- The full verification block at the top of this section passes locally.
