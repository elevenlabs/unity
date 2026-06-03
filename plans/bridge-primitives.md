# Bridge Primitives — Detailed Plan

## Context

The bridge primitives are the foundation of the WebGL side of the ElevenLabs Unity SDK. They handle every interaction between C# and JavaScript via Unity's jslib interop. The RFC defines three primitives (Promise-as-Task, Observer, JS-initiated Promise), all sharing a "signal-then-retrieve" shape. This plan de-risks the key assumptions, proposes detailed interfaces, and defines a validation strategy.

## Key Findings That Refine the RFC's Assumptions

### 1. SendMessage timing — assumed synchronous from jslib scope

The RFC states SendMessage is "delivered on the next Unity frame." This is **not explicitly documented** by Unity — the official docs don't specify timing. However, community evidence strongly suggests that **SendMessage called from within jslib plugin code (including from `Promise.then()`, `setTimeout`, or `WebSocket.onmessage` callbacks) executes synchronously** — the C# method runs inline before the next JS statement.

Evidence: Unity WebGL WebSocket bridges commonly call `_free(ptr)` immediately after `SendMessage` in the same callback. If delivery were deferred, the freed memory would corrupt the payload.

**Impact:** The bridge is simpler than the RFC assumed. No polling MonoBehaviour `Update()` loop needed. No per-observer queues on the JS side. The C# handler runs immediately within the JS callback and can complete an `AwaitableCompletionSource` or dispatch an event handler directly.

**Decision:** Assume synchronous. Include a lightweight validation test in the first WebGL build (log frame number in SendMessage handler vs. caller) to confirm. If wrong, adding a one-frame queue is a localized change — the registry/dispatch layer doesn't change.

### 2. `Awaitable` over `Task` or `UniTask`

Standard `Task` has known issues on WebGL: `Task.Delay()` doesn't work, and continuations after `await` may lose the Emscripten call context (breaking subsequent `DllImport` calls).

**Recommendation: Use Unity's `Awaitable` / `AwaitableCompletionSource<T>`** (available since Unity 2023.1):

- Zero external dependencies (critical for a library SDK)
- Built-in, pooled (reduced GC), runs on main thread
- Has `SetResult` / `SetException` / `SetCanceled`
- UniTask maintainers themselves recommend Awaitable for library code
- Consumers who want UniTask can wrap via `ToUniTask()`

**Implication:** Minimum Unity version becomes **2023.1** (effectively Unity 6 LTS). This resolves RFC open question #5 in favor of the newer floor.

### 3. Finalizers are unreliable on WebGL

GC finalizers run but with unpredictable timing. The RFC's "finalizer-based `ReleaseResult`" for abandoned Tasks is a safety net, not a primary mechanism.

**Mitigation:** Use `IDisposable` as primary cleanup. Finalizer remains as a last-resort leak detector that logs a warning, not as a relied-upon resource release path.

---

## Risk Inventory

| #   | Risk                                                | Severity            | Validation                                                            |
| --- | --------------------------------------------------- | ------------------- | --------------------------------------------------------------------- |
| R1  | SendMessage timing (sync vs deferred)               | **High**            | Assumed sync; lightweight frame-number assertion in first WebGL build |
| R2  | Awaitable continuations can call DllImport on WebGL | **High**            | Validated during initial implementation (first round-trip test)       |
| R3  | Background tab throttling delays event delivery     | Medium              | Acceptable for voice agent use case; document the behavior            |
| R4  | JS exceptions don't propagate to C# via DllImport   | Medium              | Mitigated by try/catch in all jslib functions                         |
| R5  | HEAPU8 views invalidated on heap growth             | Low–Med             | Coding discipline: always access `Module.HEAPU8` fresh                |
| R6  | String marshaling overhead on high-frequency calls  | Low (control plane) | Binary path deferred to v0.3                                          |

---

## Validation Tests (built into implementation, not a separate spike)

Rather than a standalone spike, these validations are embedded into the first WebGL build as assertion tests. They confirm assumptions as we go, with minimal overhead.

### V1: SendMessage Timing Assertion

In the first jslib round-trip: log `Time.frameCount` in C# before the DllImport call and inside the SendMessage handler. If they differ, we know delivery is deferred and add a queue. This is a `Debug.Assert`, not a separate project.

### V2: Awaitable + DllImport Continuation

The first Promise-as-Task implementation (`BridgePromise.Call<T>`) naturally validates this: consumer `await`s the result and then calls another DllImport function. If this breaks under IL2CPP WebGL, we'll see it immediately.

### V3: Observer Ordering

The first observer consumer (conversation events) validates ordering. Add a sequence number check in debug builds.

### V4: JS-Initiated Promise Round-Trip

The first client tool registration validates the full JS → C# → JS round-trip.

---

## Detailed Interface Design

### The interop asymmetry that shapes everything

The two directions of the bridge have fundamentally different capabilities:

- **C# → JS (DllImport):** Synchronous. Can pass multiple typed arguments (`int`, `float`, `string` as pointer). Can return primitives and pointers. Strings require heap marshaling (`_malloc` + `stringToUTF8`) in both directions regardless. Used to *initiate* async operations and to *resolve* JS-held promises.
- **JS → C# (SendMessage):** Accepts a single string (or number, or nothing). No return value. No typed parameters. This is the only mechanism for JS to call into C# (short of `dynCall`, deferred to later).

This asymmetry is why all three primitives share the "signal-then-retrieve" shape: JS packs an `id:payload` string into SendMessage because that's all it can carry. The pattern isn't a workaround for timing — it's a structural consequence of the interop model. Even the C# → JS direction involves string marshaling via heap pointers for anything beyond a number, so serialization is unavoidable in both directions. We won't have per-result-type return functions on the JS side; jslib functions that kick off async work return `void`, and results flow back through SendMessage.

### Architecture (assuming synchronous SendMessage)

```
C# calls jslib (synchronous DllImport, args: int id + string pointer)
  → JS executes, starts async operation (returns void)
    → async callback fires (Promise.then / onmessage / etc.)
      → JS calls SendMessage with single "id:status:payload" string
        → C# handler receives call, parses string, completes AwaitableCompletionSource
          → or dispatches to observer handler / calls back into jslib to resolve
```

The `id:payload` packing in SendMessage is dictated by the single-string constraint — not by timing concerns. Binary/large payloads (v0.3 audio path) will use heap pointers (`_malloc` + `Marshal.Copy`) passed as `id:ptr,length` strings through the same SendMessage channel. This adds a fourth parser variant when we get there; out of scope for v0.1.

### File Layout

The repo root *is* the UPM package — `package.json` at the top level, customers install via UPM git URL with no subfolder path.

```
package.json                       — UPM package manifest
Runtime/
  ElevenLabs.Agents.WebGL.asmdef  — assembly definition (WebGL + Editor for testing)
  WebGL/
    WebGLBridge.cs                — MonoBehaviour singleton, message dispatch
    BridgePromise.cs              — Promise-as-Task primitive + registry
    BridgeObserver.cs             — Observer primitive + registry + handle
    BridgeRequestHandler.cs       — JS-initiated promise primitive + registry
    BridgeIdGenerator.cs          — Monotonic int ID generation
    BridgeMessageParser.cs        — "id:payload" string parsing
    BridgeException.cs            — JS-originated error type
    ElevenLabsBridgeNative.cs     — DllImport declarations + non-WebGL throwing stubs
Plugins/
  WebGL/
    ElevenLabsBridge.jslib        — JS side of all three primitives
Tests/
  Editor/
    ElevenLabs.Agents.WebGL.Tests.asmdef
    BridgePromiseTests.cs
    BridgeObserverTests.cs
    BridgeRequestTests.cs
    BridgeMessageParserTests.cs
```

**Editor / non-WebGL behavior.** The primitives are WebGL-only. `ElevenLabsBridgeNative` declares real `DllImport`s under `#if UNITY_WEBGL && !UNITY_EDITOR` and throwing stubs (`throw new PlatformNotSupportedException("WebGL bridge is not available outside WebGL builds")`) otherwise. This means calling `BridgePromise.Call(...)` in the Editor or on native platforms surfaces a clear error instead of returning an `Awaitable` that never completes. Conversation consumers compile-time-gate on `UNITY_WEBGL` to pick between the bridged and native implementations (per the RFC).

### WebGLBridge (singleton MonoBehaviour)

```csharp
namespace ElevenLabs.WebGL
{
    public sealed class WebGLBridge : MonoBehaviour
    {
        internal const string GameObjectName = "__ElevenLabsBridge__";
        private static WebGLBridge _instance;

        // Ensure the bridge GameObject exists before any JS code can SendMessage to it.
        // Without this, lazy creation risks silent message drops if JS fires first.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void EnsureCreated() => _ = Instance;

        public static WebGLBridge Instance {
            get {
                if (_instance == null) {
                    var go = new GameObject(GameObjectName);
                    DontDestroyOnLoad(go);
                    go.hideFlags = HideFlags.HideAndDontSave;
                    _instance = go.AddComponent<WebGLBridge>();
                }
                return _instance;
            }
        }

        // SendMessage targets — public instance methods
        public void OnPromiseSettled(string message) {
            var (id, status, payload) = BridgeMessageParser.ParseIdStatusPayload(message);
            BridgePromiseRegistry.Settle(id, status == "ok" ? payload : null,
                                               status == "err" ? payload : null);
        }

        public void OnObserverEvent(string message) {
            var (id, payload) = BridgeMessageParser.ParseIdPayload(message);
            BridgeObserverRegistry.Dispatch(id, payload);
        }

        public void OnRequest(string message) {
            var (id, type, payload) = BridgeMessageParser.ParseIdTypePayload(message);
            BridgeRequestRegistry.HandleRequest(id, type, payload);
        }

        private void OnDestroy() {
            // Verbs differ on purpose:
            //   - Promises were initiated by C#, so we cancel them (consumer's awaits throw OperationCanceledException).
            //   - Observers are C#-owned subscriptions, so we dispose them.
            //   - Requests came from JS expecting a reply, so we reject them with a reason.
            BridgePromiseRegistry.CancelAll();
            BridgeObserverRegistry.DisposeAll();
            BridgeRequestRegistry.RejectAll("Bridge destroyed");
        }
    }
}
```

### Primitive 1: Promise-as-Task

The primitive is JSON-library-agnostic. It returns a raw payload string; consumers (typically generated façade code) hand it a deserializer. Lets each consumer pick the serializer that fits its data shape, and keeps the primitive reusable beyond this SDK.

**C# API:**

```csharp
// Raw primitive — returns the payload string from JS.
public static Awaitable<string> Call(
    Action<int> jsCall,
    CancellationToken ct = default);

// Convenience overload with a deserialization hook.
public static async Awaitable<T> Call<T>(
    Action<int> jsCall,
    Func<string, T> deserialize,
    CancellationToken ct = default)
{
    var raw = await Call(jsCall, ct);
    return deserialize(raw);
}
```

Consumer (typically generated):

```csharp
// Generated façade code knows the type and picks the deserializer:
var session = await BridgePromise.Call(
    id => ElevenLabsBridgeNative.EL_StartSession(id, JsonConvert.SerializeObject(args)),
    JsonConvert.DeserializeObject<SessionInfo>);
```

**Registry:** `Dictionary<int, AwaitableCompletionSource<string>>` mapping ID → completion source. On settle: remove entry, call `SetResult(payload)` on success or `SetException(new BridgeException(error))` on failure. Cancellation: when the consumer's `CancellationToken` fires, the primitive removes the entry and calls `SetCanceled()`. The JS side may still settle later — the registry lookup misses and the result is silently dropped.

**Race rule (first-wins).** On every transition (settle / cancel) the registry atomically removes the entry by ID before calling into the completion source. If two transitions race (e.g. JS settles in the same microtask the consumer's cancellation fires), only the one that successfully removed the entry calls into the ACS — the other finds no entry and no-ops. This avoids the `AwaitableCompletionSource` double-completion error and keeps semantics deterministic.

**JS side:** jslib function receives `promiseId`, starts async work, on settle calls `SendMessage('__ElevenLabsBridge__', 'OnPromiseSettled', promiseId + ':ok:' + resultJson)` or `':err:' + errMsg`. Every jslib function wraps its body in `try { ... } catch(e) { SendMessage(..., ':err:' + e.message) }`. The JS side serializes (`JSON.stringify` or whatever the generated jslib chooses); the C# side returns that string verbatim to the consumer for deserialization.

### Primitive 2: Observer

Same library-agnostic approach: the primitive's handler receives the raw payload string. Generated façade code wraps `Register` with a deserialization step.

**C# API:**

```csharp
// Raw primitive — handler receives the payload string.
public static BridgeObserverHandle Register(Action<string> handler);

// Convenience overload with deserialization.
public static BridgeObserverHandle Register<T>(
    Func<string, T> deserialize,
    Action<T> handler) =>
    Register(payload => handler(deserialize(payload)));
```

Consumer (typically generated):

```csharp
using var handle = BridgeObserver.Register(
    JsonConvert.DeserializeObject<ConversationEvent>,
    OnConversationEvent);
// handle.Id is passed to jslib when setting up the event source.
```

**Registry:** `Dictionary<int, Action<string>>` mapping ID → handler. `BridgeObserverHandle` implements `IDisposable`: on dispose, removes from registry. The handle does **not** call into jslib itself — the JS-side teardown is a separate concern handled by whoever set up the JS event source (typically via a paired `EL_DisposeObserver(id)` call in the generated façade).

**JS side:** `EL_RegisterObserver` hooks into a JS event source and stores an unsubscribe function keyed by observer ID. Each event calls `SendMessage` with `observerId + ':' + JSON.stringify(event)`. `EL_DisposeObserver` calls unsubscribe and deletes the entry.

### Primitive 3: JS-Initiated Promise

Same library-agnostic shape: raw handler takes/returns strings, generated façade wraps with (de)serializers.

**C# API:**

```csharp
// Raw primitive — handler receives/returns payload strings.
public static void Register(
    string toolName,
    Func<string, Awaitable<string>> handler);

// Convenience overload with (de)serialization hooks.
public static void Register<TArgs, TResult>(
    string toolName,
    Func<string, TArgs> deserialize,
    Func<TResult, string> serialize,
    Func<TArgs, Awaitable<TResult>> handler) =>
    Register(toolName, async payload => serialize(await handler(deserialize(payload))));
```

Consumer (typically generated):

```csharp
BridgeRequestHandler.Register<WeatherArgs, WeatherResult>(
    "tool:get_weather",
    JsonConvert.DeserializeObject<WeatherArgs>,
    JsonConvert.SerializeObject,
    async args => await FetchWeather(args.location));
```

**Registry:** `Dictionary<string, Func<string, Awaitable<string>>>` keyed by tool name. All handlers are async — there's no sync fast-path since results go back over the network anyway. On request: look up handler by tool name, `await` it, then call `EL_ResolveRequest(id, result)` or `EL_RejectRequest(id, error)`. If no handler is registered for the tool name, reject with `"No handler registered for tool: <name>"`.

**JS side:** `$EL_CreateRequest(toolName, payload)` returns a `Promise`. It generates a request ID, stores `resolve`/`reject` in `$EL_PendingRequests[id]`, and fires `SendMessage`. When C# calls `EL_ResolveRequest` or `EL_RejectRequest`, the stored resolve/reject is invoked, settling the Promise. The JS SDK (`@elevenlabs/client`) calls `$EL_CreateRequest` when it needs a client tool result.

### Error Handling Pattern

Every jslib function follows this template:

```javascript
EL_SomeFunction: function(promiseId, argPtr) {
    try {
        var arg = UTF8ToString(argPtr);
        // ... actual work ...
    } catch(e) {
        console.error('[ElevenLabs Bridge] EL_SomeFunction error:', e);
        SendMessage('__ElevenLabsBridge__', 'OnPromiseSettled',
            promiseId + ':err:' + (e.message || String(e)));
    }
}
```

Every jslib function that starts async work has a `promiseId` — errors are always routed back through the same settlement path. C# side: `BridgeException` wraps JS error messages, surfaced as a thrown exception when the consumer `await`s the bridge call.

---

## Testing Strategy

### C# unit tests (Unity Test Runner, Edit Mode)

Test registries, message parser, and bridge wiring via Unity Test Runner in Edit Mode (`-batchmode -nographics -runTests` in CI). This lets the primitives target `Awaitable` / `AwaitableCompletionSource<T>` directly without an abstraction layer to avoid Unity dependencies:

- ID uniqueness
- Register → settle → callback invoked with correct args
- Settle removes entry (double-settle is no-op)
- Observer dispatch → handler invoked
- Observer dispose → handler no longer invoked
- Message parsing with edge cases (colons in payload, empty payload, Unicode)

### JS tests (Vitest)

Extract jslib functions into testable form. Mock `SendMessage`, `UTF8ToString`, `_malloc`, `_free`. Use Vitest browser mode if we need real browser APIs. Assert:

- Successful async operation → `SendMessage` called with `ok` status
- Failed async operation → `SendMessage` called with `err` status
- Request resolve/reject settles the held Promise
- Observer dispose calls unsubscribe

### Integration tests (WebGL build + Vitest browser mode)

Minimal Unity project exercising each primitive end-to-end. Vitest browser mode loads the WebGL build and asserts on the results. Test matrix: Chrome, Firefox, Safari.

---

## Implementation Order

| Phase                       | Work                                                                                              | Depends on     |
| --------------------------- | ------------------------------------------------------------------------------------------------- | -------------- |
| **1. Foundation**           | UPM package skeleton, WebGLBridge (with stubbed handlers), ID gen, message parser, exception type, DllImport stubs, jslib skeleton, CI | —              |
| **2. Promise-as-Task**      | Registry, `BridgePromise.Call()`, wire `OnPromiseSettled`, jslib promise pattern, unit + JS tests | Foundation     |
| **3. Observer**             | Registry, handle, wire `OnObserverEvent`, jslib observer lifecycle, unit + JS tests               | Foundation     |
| **4. JS-Initiated Promise** | Registry, wire `OnRequest`, jslib request/resolve/reject, unit + JS tests                         | Foundation     |
| **5. WebGL smoke test**     | Minimal Unity scene exercising all 3 primitives, validation assertions (V1–V4) logged to browser console, manually verified in Chrome | All primitives |
| **6. Automated integration**| Vitest browser mode harness driving the WebGL build, edge cases, cross-browser CI                 | Smoke test     |

Phases 2, 3, and 4 are independent and can be parallelized. Phase 1's CI setup item (Unity license activation on the runner) is typically the slowest single task and may take longer than the C# work it gates.

Phase 7 (`WebGLBridgedConversation` and other consumers) is out of scope for this plan — see the separate conversation plan.

---

## Decided

- **Minimum Unity version:** 2023.1+ (Unity 6 LTS). Enables `Awaitable`/`AwaitableCompletionSource<T>` with zero external dependencies. Resolves RFC open question #5.
- **Async primitive:** `Awaitable`/`AwaitableCompletionSource<T>`. Consumers who want UniTask can wrap via `ToUniTask()`.
- **JS→C# delivery:** `SendMessage` for v0.1. `dynCall` deferred until profiling shows need. The registry/dispatch layer is delivery-mechanism-agnostic.
- **SendMessage timing:** Assumed synchronous. Lightweight assertion in first WebGL build to confirm.

## Still Open

1. **Payload size threshold**: At what point do we switch from inline `id:payload` strings to heap-pointer retrieval? Proposed: always inline for v0.1 (control plane only), add binary path in v0.3 for audio.

---

## Tasks

### Phase 1: Foundation

- [ ] Set up UPM package at repo root: `package.json`, `Runtime/`, `Editor/`, `Tests/`, `Plugins/` layout
- [ ] Assembly definitions (`Runtime` targeting WebGL + Editor for testability; `Tests/Editor` referencing Runtime)
- [ ] Create `WebGLBridge.cs` MonoBehaviour singleton with `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` auto-create + `DontDestroyOnLoad`. Wire `OnPromiseSettled` / `OnObserverEvent` / `OnRequest` as stubbed `Debug.Log` handlers — each primitive's phase wires its real handler
- [ ] Implement `BridgeIdGenerator.cs` (monotonic int)
- [ ] Implement `BridgeMessageParser.cs` (id:payload, id:status:payload, id:type:payload variants)
- [ ] Create `BridgeException.cs`
- [ ] Create `ElevenLabsBridgeNative.cs` with DllImport declarations under `#if UNITY_WEBGL && !UNITY_EDITOR`, throwing `PlatformNotSupportedException` stubs otherwise
- [ ] Create `ElevenLabsBridge.jslib` skeleton with `mergeInto(LibraryManager.library, {...})` boilerplate
- [ ] Edit-mode unit tests for ID generator and message parser
- [ ] **Set up Unity Test Runner in CI** (`-batchmode -nographics -runTests`) — includes Unity license activation on the runner; often the slowest single setup task
- [ ] Set up Vitest for JS tests

### Phase 2: Promise-as-Task

- [ ] Implement `BridgePromiseRegistry` (register `AwaitableCompletionSource<string>`, settle, remove, `CancelAll`)
- [ ] Implement `BridgePromise.Call(jsCall, ct)` returning `Awaitable<string>` with `CancellationToken` support
- [ ] Implement `BridgePromise.Call<T>(jsCall, deserialize, ct)` convenience overload
- [ ] Implement `WebGLBridge.OnPromiseSettled` handler with message parsing
- [ ] Try/catch wrapper template for all jslib functions, routing errors through `OnPromiseSettled` with the promise ID
- [ ] Edit-mode tests: register → settle → completion source resolved, settle removes entry, double-settle no-op, error path raises `BridgeException`, cancellation removes entry and stale settle is ignored
- [ ] Vitest tests: jslib promise wrapper calls `SendMessage` with correct format on resolve and reject

### Phase 3: Observer

- [ ] Implement `BridgeObserverRegistry` (register, dispatch, unregister, `DisposeAll`)
- [ ] Implement `BridgeObserverHandle : IDisposable` (removes from registry only — JS-side teardown is the caller's responsibility)
- [ ] Implement `BridgeObserver.Register(handler)` and `Register<T>(deserialize, handler)` public API
- [ ] Implement `WebGLBridge.OnObserverEvent` handler
- [ ] jslib `EL_RegisterObserver` / `EL_DisposeObserver` lifecycle + `$EL_Observers` store
- [ ] Edit-mode tests: register → dispatch → handler invoked, dispose unregisters, dispatch after dispose is no-op
- [ ] Vitest tests: observer registration stores unsubscribe, events fire `SendMessage`, dispose calls unsubscribe

### Phase 4: JS-Initiated Promise

- [ ] Implement `BridgeRequestRegistry` (single `Dictionary<string, Func<string, Awaitable<string>>>`, `RejectAll`)
- [ ] Implement `BridgeRequestHandler.Register(toolName, handler)` and typed `Register<TArgs, TResult>(...)` overload
- [ ] Implement `WebGLBridge.OnRequest` handler with async dispatch + resolve/reject call back into jslib
- [ ] Reject with clear error when no handler is registered for the tool name
- [ ] jslib `$EL_CreateRequest` helper, `EL_ResolveRequest` / `EL_RejectRequest` DllImport targets, `$EL_PendingRequests` store
- [ ] Edit-mode tests: register → request → handler invoked, async handler awaited, errors routed to reject, missing handler routed to reject
- [ ] Vitest tests: `$EL_CreateRequest` returns Promise that settles on resolve/reject from C#

### Phase 5: WebGL Smoke Test

Build a minimal Unity scene that exercises each primitive once, run it in a real browser, eyeball the console for the validation assertions. This is the moment of truth for the architecture — synchronous SendMessage and Awaitable+DllImport assumptions either hold or they don't.

- [ ] Minimal Unity scene with a `BridgeSmokeTest` MonoBehaviour exercising all three primitives end-to-end
- [ ] V1: SendMessage timing assertion (`Time.frameCount` comparison — passes if same frame, logs warning if not)
- [ ] V2: Awaitable + DllImport continuation test (consumer awaits a `BridgePromise.Call`, then calls another DllImport in the continuation)
- [ ] V3: Observer ordering test (sequence number check across rapid events)
- [ ] V4: JS-initiated promise round-trip test (full JS→C#→JS flow)
- [ ] Confirm clean IL2CPP WebGL build (no warnings, no missing symbols)
- [ ] Manual run in Chrome and at least one of Firefox/Safari, confirm validation assertions pass

### Phase 6: Automated Integration Tests

Turn the smoke test scene into an automated suite. Vitest browser mode loads the WebGL build, drives it via the JS bridge, asserts on the results.

- [ ] Vitest browser mode setup with Playwright provider
- [ ] Harness that loads the WebGL build and runs the primitive tests
- [ ] Test matrix: Chrome, Firefox, Safari
- [ ] Edge cases: double-dispose, orphaned promises on bridge destroy, special chars in payload (colons, newlines, Unicode), large payloads (>100KB), rapid-fire observer events
- [ ] CI wiring for the browser-mode tests

---

## Definition of Done

The bridge primitives are complete when:

- All three primitives have unit tests passing in Unity Test Runner (Edit Mode), executed headlessly in CI
- jslib JavaScript code has Vitest tests passing in CI
- A clean IL2CPP WebGL build succeeds with no warnings
- The Vitest browser-mode integration suite passes on Chrome, Firefox, and Safari in CI
- Validation assertions V1–V4 pass in a real browser (SendMessage timing confirmed synchronous; Awaitable continuations can call DllImport; observer events arrive in order; JS-initiated round-trip completes)
- No leaked GameObjects across scene transitions (bridge singleton survives, all observer/promise registries are cleaned up)
- Public C# API is documented with XML doc comments
- The package installs cleanly via `"com.elevenlabs.agents": "https://github.com/elevenlabs/elevenlabs-unity.git#<tag>"` into a fresh Unity 2023.1+ project
