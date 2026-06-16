# Generic Bridge Primitives — Detailed Plan

## Goal

Build a small, generic set of primitives over Unity's WebGL jslib interop that
lets C# code in a Unity build drive arbitrary JavaScript objects — calling
their methods, reading their properties, subscribing to their event streams,
and managing their lifetime — without writing a hand-rolled jslib wrapper per
JS class. The primitives are domain-agnostic: any consumer that needs to reach
into the browser from a Unity WebGL build uses the same surface, and a JS-side
addition (a new method on a JS class, a new factory) requires zero new jslib
code.

## Context: the Unity WebGL interop model

Unity WebGL builds compile C# to WebAssembly via IL2CPP + Emscripten. The
interop between C# and the host JavaScript page has two directions with very
different capabilities:

- **C# → JS** is synchronous. C# declares functions with `[DllImport("__Internal")]` and Unity wires them to JS functions defined in a `.jslib` file. Calls are direct WebAssembly invocations. Multiple typed primitive arguments (`int`, `float`, `string` as pointer) are supported; return values are primitives or pointers. Strings cross via Emscripten heap allocations (`_malloc` + `stringToUTF8` outbound, `UTF8ToString` inbound).
- **JS → C#** is fire-and-forget. JavaScript calls `unityInstance.SendMessage(gameObject, methodName, singleArg)` — at most one string argument, no return value, delivered to a public instance method on a named `MonoBehaviour`. Empirically delivered synchronously when called from async JS callbacks (see "SendMessage timing" below).

Every JS interaction we build has to fit through these two narrow channels.
Anything async — a JS Promise resolving, a JS event firing — has to *signal*
into C# via `SendMessage` and then *be retrieved* by C# via a synchronous
`DllImport` getter on the way back through. That signal-then-retrieve shape
is the structural backbone of every primitive below.

## The primitive set

Three handle types — two invoked by C#, one invoked by JS — implemented with
the same registry-and-marker machinery.

### 1. `JsObject` — remote JS objects held by C#

Represents a JavaScript object whose lifetime is owned by C#. C# obtains an
object by calling a registered **factory** (e.g. "give me a new
WebSocketConnection") and from then on calls methods, reads properties, and
eventually releases it. The C# side holds an opaque `int` handle; the JS side
maintains a `Map<handle, object>` registry.

Operations:

- **Factory invocation** — call a named, pre-registered JS factory function with arguments, get a result. Sync or async per the factory's registered shape. The result is either a primitive value, a fresh `JsObject` handle, or a `JsFunction` handle.
- **Method call (async)** — call a named method on the held object whose return value comes via a `Promise`. C# awaits the resolved value through the settle channel.
- **Method call (sync)** — call a named method that returns synchronously; result comes back as the return value of the `DllImport`. Used for hot paths where the async settle round-trip would add a frame of latency.
- **Property read (sync)** — get a named property's value via a `DllImport`. JSON-encoded; deserialised on the C# side.
- **Dispose** — drop the JS-side registry entry. Implemented via `IDisposable` (and a finalizer-based safety log).

### 2. `JsFunction` — remote JS functions held by C#

When a JS method or factory returns a function value (e.g. `addListener` returning
its corresponding `removeListener`, a factory returning a `detach` callback), C#
captures it as a `JsFunction` handle. Later C# invokes it via the same async or
sync `DllImport` channel as `JsObject` method calls. This is the dual of
`JsObject`: same lifetime model, different lookup table on the JS side.

Operations: **Call (async)**, **Call (sync)**, **Dispose**. No "register" step
— `JsFunction` handles are only obtained by JS returning a function value from
some other call.

### 3. `BridgeCallback` — C# delegates exposed to JS

The inverse of `JsFunction`: a C# delegate that JS holds as a callable. C# wraps
a delegate in a `BridgeCallback`, passes it as an argument to any JS method (see
"Wire protocol" below for how the argument is encoded), and JS sees a plain
function. When JS calls it, `SendMessage` routes back to the bridge, the registry
looks up the handle, and the delegate runs on Unity's main thread.

This covers the "JS event stream → C# handler" pattern — a connection's
`onMessage` callback, a worklet's port message listener, anything shaped like
`addListener(callback)`. The C# side wraps a delegate as a callback and passes
it as the argument; the JS side just sees a function and stores or invokes it
as it would any other.

Operations:

- **Wrap** — wrap a C# delegate in a `BridgeCallback` with an allocated handle.
- **Invocation** — happens from JS only; C# never invokes a `BridgeCallback` directly. JS-side invocation fires `SendMessage` → C# bridge dispatches to the registered delegate on the main thread.
- **Dispose** — remove the C# delegate from the registry. JS calls arriving after dispose miss the registry and no-op silently. The JS-side teardown (removing the listener from the JS event source) is the consumer's concern — typically a paired call to whatever `JsFunction` was returned alongside the registration (e.g. `removeListener`).

`BridgeCallback` is fire-and-forget — JS doesn't receive a return value back
from C#. A future `AsyncBridgeCallback` (noted under "Possible extensions")
would let JS `await` a result from C#, covering patterns like remote tool
dispatch. Out of scope for v0.1.

## Wire protocol

Every async entry point has the signature `(handleOrFactoryName, argsJsonPtr, returnShape, promiseId)`
and settles through one shared `OnPromiseSettled` SendMessage handler. Every
sync entry point has the signature `(handle, argsJsonPtr, returnShape)` returning
a heap-string pointer. `returnShape` is a small int (see the table under "Return
shape codes" below). The same JSON marker conventions (`$ref`, `$fn`, `$cb`)
apply to all arguments and all returns. Adding a new entry point reuses the
registries, the settle channel, the marker conventions, and the error-routing
template — only the marshalling differs.

### Identifiers

- **Object handle** — `int`, allocated by C# via `BridgeIdGenerator`, registered with the JS object map at object-creation time.
- **Function handle** — `int`, allocated by the JS side at the moment it materialises a function value to hand back to C#. Communicated to C# in the JSON-encoded return value of whatever call produced it.
- **Callback handle** — `int`, allocated by C# via `BridgeIdGenerator` at the moment C# wraps a delegate in a `BridgeCallback`.
- **Promise ID** — `int`, allocated by C# per async operation; used to route the eventual settle SendMessage back to the right `AwaitableCompletionSource<T>`.

All four ID spaces are disjoint by allocator and consumer; collisions are
impossible.

### Argument encoding

C# methods that drive the primitives accept `params object[]` and serialise
to a JSON array. Three types of argument get wrapped in **typed markers** so
the JS side can rehydrate them into the live JS value before the call:

| C# value | JSON shape | JS rehydration |
|---|---|---|
| `JsObject` reference | `{ "$ref": <handle> }` | Look up object in registry |
| `JsFunction` reference | `{ "$fn": <handle> }` | Look up function in registry |
| `BridgeCallback` | `{ "$cb": <handle> }` | Materialise `function(arg) { _EL_InvokeCallback(handle, JSON.stringify(arg)); }` |
| Anything else | Plain JSON | As-is |

Return values follow the inverse convention:

| JS value | JSON shape | C# decoding |
|---|---|---|
| A registered JS object | `{ "$ref": <newlyAllocatedHandle> }` | Wrap in `JsObject` |
| A function | `{ "$fn": <newlyAllocatedHandle> }` | Wrap in `JsFunction` |
| `undefined` / void | `null` | Discard |
| Anything else | JSON-serialised | Deserialise per C#-side expected type |

Whether a returned JS object becomes a `JsObject` (allocated and registered),
a `JsFunction`, or gets JSON-serialised in full is a **per-call decision**
driven by the C# call site. Every dispatcher entry point takes a `returnShape`
int argument (`0` = value / `1` = object / `2` = function / `3` = void); the C#
side derives it from the generic `<T>` parameter — `CallAsync<JsObject>` sends
`1`, `CallAsync<JsFunction>` sends `2`, the non-generic `CallAsync` sends `3`,
everything else sends `0`. No JS-side hint table is needed — the C# generic
is the single source of truth, and mismatch is impossible by construction.

### Return shape codes

A small numeric enum shared between the C# `BridgeReturnShape` and the JS
dispatcher's `decodeReturnShape`:

| Code | Name | Meaning |
|---:|---|---|
| `0` | `value` | Plain JSON round-trip; the default for primitives, arrays, and data objects. |
| `1` | `object` | Allocate a JsObject handle from the JS return value; encode as `{ "$ref": handle }`. |
| `2` | `function` | Allocate a JsFunction handle from the JS return value; encode as `{ "$fn": handle }`. |
| `3` | `void` | Discard the return value; settle `null`. |

Unknown codes fall back to `value`. The JS side does not validate the type of
the actual return against the shape — if C# asks for `function` but JS returns
an object, the allocator stores whatever it got and C# gets a `JsFunction` that
breaks on first invocation. That mismatch is impossible when C# derives the
code from `<T>` automatically.

### Settlement channel

All async operations settle through one SendMessage handler — `OnPromiseSettled`
— with the payload `"<promiseId>:ok:<resultJson>"` or `"<promiseId>:err:<message>"`.
A single C# registry maps promise IDs → `AwaitableCompletionSource<string>`;
the raw payload string is returned to the caller's awaiting `Awaitable` and
deserialised on the C# side per the expected return type.

Synchronous operations (sync method calls, property reads) bypass this channel
entirely — the `DllImport` returns a heap-pointer-to-UTF8 string that C#
copies and frees.

## Architecture decisions

### `Awaitable` over `Task` / `UniTask`

Unity's [`Awaitable` / `AwaitableCompletionSource<T>`](https://docs.unity3d.com/2023.1/Documentation/ScriptReference/Awaitable.html)
(Unity 2023.1+) is the right async primitive for a library SDK:

- Zero external dependencies
- Pooled (reduced GC), main-thread by default
- Continuations after `await` reliably preserve the Emscripten call context on WebGL, where `Task.Delay` and some `Task` continuations are known to misbehave

Consumers who want UniTask can wrap via `ToUniTask()`. This pins the
**minimum Unity version to 2023.1+ (Unity 6 LTS)**.

### JS→C# dispatch timing — synchronous by construction

**Superseded 2026-06-12** by the DynCall migration (see [dyncall-migration.md](./dyncall-migration.md) and Phase 2.5 below). The timing concern that motivated this subsection no longer applies: a wasm function-pointer call invoked via `{{{ makeDynCall('sig', 'fnVar') }}}` is a direct synchronous call into IL2CPP — the C# method runs inline before the next JS statement, by construction. The JS side `_free`s the UTF-8 payload buffer immediately after the DynCall returns, which depends on this synchronous-return contract; the contract is now a property of the wasm runtime, not an empirically observed `SendMessage` behaviour.

The original subsection (assumed-synchronous `SendMessage` with a planned frame-count assertion test) is preserved in git history at the commit prior to task 3 of the DynCall migration.

### Error handling

Every jslib function wraps its body in `try { ... } catch (e) { settle as err }`.
Errors are always routed through the same settlement channel so the consumer's
`await` throws; there's no separate error path. C# wraps JS error messages in
a `BridgeException`.

### Lifetime via `IDisposable` + finalizer safety net

Primary cleanup is `IDisposable` (and `using` blocks where appropriate).
Finalizers run on WebGL but with unpredictable timing — they're a last-resort
**leak detector** that logs a warning if a handle is GC'd without being
explicitly disposed, not the primary cleanup path.

### Registry rule: first-wins, lookup-then-remove

All registries (promises, callbacks, objects, functions) use the same pattern:
operations look up by ID, atomically remove the entry, then act on the
removed state. WebGL is single-threaded, but re-entrancy is possible — a
handler triggering another bridge operation can loop back into the same
registry, or `Dispose` can interleave with an in-flight dispatch. The
lookup-then-remove rule means stale signals (a settle arriving after a
cancel, a dispatch after a dispose) miss the registry and no-op silently,
rather than double-completing or double-disposing.

## C# API design

The generic `<T>` parameter on every call site is the single source of truth
for the return shape. The implementation translates `T` into the int code
sent across the DllImport boundary:

| C# `<T>` | `BridgeReturnShape` code |
|---|---:|
| `JsObject` | `1` (object) |
| `JsFunction` | `2` (function) |
| (non-generic overload returning `void` / `Awaitable`) | `3` (void) |
| anything else (primitives, data classes, arrays, …) | `0` (value) |

```csharp
namespace ElevenLabs.WebGL
{
    // Entry point for everything: invoke a JS-registered factory by name.
    public static class JsBridge
    {
        // Async factory call. T resolution drives the return shape code.
        public static Awaitable<T> InvokeFactoryAsync<T>(
            string factoryName,
            params object[] args);

        // Synchronous factory call. T resolution drives the return shape code.
        public static T InvokeFactory<T>(
            string factoryName,
            params object[] args);
    }

    // Handle to a remote JS object. Disposing releases the JS-side mapping.
    public sealed class JsObject : IDisposable, IAsyncDisposable
    {
        public int Handle { get; }

        // Async method call. T resolution drives the return shape code.
        public Awaitable<T> CallAsync<T>(string method, params object[] args);
        public Awaitable CallAsync(string method, params object[] args);

        // Sync method call. T resolution drives the return shape code.
        public T Call<T>(string method, params object[] args);
        public void Call(string method, params object[] args);

        // Sync property read (always value-shape — JSON round-trip).
        public T Get<T>(string property);

        public void Dispose();      // releases JS-side entry
        public ValueTask DisposeAsync();
    }

    // Handle to a JS function reference.
    public sealed class JsFunction : IDisposable, IAsyncDisposable
    {
        public int Handle { get; }
        public Awaitable<T> CallAsync<T>(params object[] args);
        public Awaitable CallAsync(params object[] args);
        public T Call<T>(params object[] args);
        public void Call(params object[] args);
        public void Dispose();
        public ValueTask DisposeAsync();
    }

    // A C# delegate exposed to JS as a callable. Held in a registry for its lifetime;
    // dispose to free the registry entry. C# never invokes a BridgeCallback directly —
    // JS calls it, SendMessage routes back, and the wrapped delegate runs on the main thread.
    public sealed class BridgeCallback : IDisposable
    {
        public int Handle { get; }

        // Wrap a C# delegate. Returns a BridgeCallback that JS can invoke.
        public static BridgeCallback Wrap(Action<string> handler);

        // Typed convenience overload.
        public static BridgeCallback Wrap<T>(Action<T> handler);

        public void Dispose();      // removes the C# delegate from the registry
    }

    // Wraps a JS-thrown error.
    public sealed class BridgeException : Exception { ... }
}
```

Argument auto-marshalling: `params object[] args` accepts plain values
(serialised as JSON), `JsObject` (encoded as `{$ref}`), `JsFunction`
(encoded as `{$fn}`), and `BridgeCallback` (encoded as `{$cb}`).

## JS API design

The JS side is one bundled `.jslib`. It exposes a small set of `EL_*`
DllImport targets, a registry of factories, and a uniform dispatcher.

```javascript
// State
$EL_Objects:      {},   // handle -> JS object
$EL_Functions:    {},   // handle -> JS function
$EL_Factories:    {},   // name   -> fn
$EL_NextHandleId: 1,    // shared object/function handle counter

// Registration (called from external JS at app boot, before any C# call)
$EL_RegisterFactory: function(name, fn) { ... },
```

There is no `$EL_RegisterMethods` and no per-handle method shape table. Each
dispatcher call carries its own `returnShape` int from the C# side; the JS
side just decodes it and runs.

DllImport entry points (every name follows the `EL_…` namespace):

```
EL_InvokeFactoryAsync(factoryNamePtr, argsJsonPtr, returnShape, promiseId)
EL_InvokeFactorySync(factoryNamePtr, argsJsonPtr, returnShape) -> stringPtr
EL_ObjectCallAsync(handle, methodPtr, argsJsonPtr, returnShape, promiseId)
EL_ObjectCallSync(handle, methodPtr, argsJsonPtr, returnShape) -> stringPtr
EL_ObjectGet(handle, propPtr) -> stringPtr
EL_ObjectRelease(handle)
EL_FunctionCallAsync(handle, argsJsonPtr, returnShape, promiseId)
EL_FunctionCallSync(handle, argsJsonPtr, returnShape) -> stringPtr
EL_FunctionRelease(handle)
```

`returnShape` is the int code from the "Return shape codes" table above
(`0` value / `1` object / `2` function / `3` void). `EL_ObjectGet` has no
shape parameter — property reads always JSON-roundtrip the value.

No DllImport for `BridgeCallback` release — the callback registry is C#-owned,
and `BridgeCallback.Dispose` simply removes the C# delegate. The JS-side
wrapper function may still be held by some JS listener; subsequent invocations
arrive, miss the registry lookup, and no-op silently. JS-side cleanup (calling
`removeListener`) is the consumer's concern.

The dispatcher is the same for every entry point:

```javascript
const RETURN_SHAPES = ["value", "object", "function", "void"];
function decodeReturnShape(code) { return RETURN_SHAPES[code] ?? "value"; }

function dispatch(target, argsJson) {
    var args = JSON.parse(argsJson || "[]").map(rehydrate);
    return target.apply(null, args);
}

function rehydrate(value) {
    if (value === null || typeof value !== "object") return value;
    if ("$ref" in value)  return _EL_Objects[value.$ref];
    if ("$fn"  in value)  return _EL_Functions[value.$fn];
    if ("$cb"  in value)  return makeBridgeCallback(value.$cb);
    // Plain object/array — recurse to rehydrate nested markers
    return mapValues(value, rehydrate);
}

function encodeReturn(value, returnShape) {
    if (returnShape === "object")   { var h = nextHandle(); _EL_Objects[h] = value;   return { $ref: h }; }
    if (returnShape === "function") { var h = nextHandle(); _EL_Functions[h] = value; return { $fn:  h }; }
    if (returnShape === "void")     return null;
    return value;
}
```

`makeBridgeCallback(handle)` returns a `function(arg) { _EL_InvokeCallback(handle, JSON.stringify(arg)); }`. The closure captures the handle int; the JS side never sees it as anything other than a plain function.

This is the entirety of the bridge runtime. Every domain-specific class (a
`WebSocketConnection`, an audio controller, anything else) is consumed via
its registered factory — no new jslib code per class, no JS-side declaration
of which methods return what.

## File layout

The package root is the UPM package (`io.elevenlabs.agents`). JS sources
under `Bridge~/` (Unity ignores `~`-suffixed dirs); generated jslib under
`Plugins/WebGL/` (committed; CI gates freshness).

```
Runtime/
  WebGL/
    JsBridge.cs                — public InvokeFactory entry point
    JsObject.cs                — handle + async/sync call/get/dispose
    JsFunction.cs              — function-ref handle
    BridgeCallback.cs          — C# delegate exposed to JS
    BridgeException.cs         — JS-originated error type
    WebGLBridge.cs             — singleton MonoBehaviour, SendMessage handlers
    BridgeIdGenerator.cs       — monotonic int IDs (C#-allocated)
    BridgeMessageParser.cs     — "id:status:payload" parsing
    BridgeLog.cs               — tagged logger
    Marshalling/
      BridgeArgEncoder.cs      — encodes object[] -> JSON with $ref/$fn/$cb markers
      BridgeValueDecoder.cs    — decodes returns into primitives, JsObject, JsFunction
    Internal/
      Registries.cs            — Promise / Callback / Object / Function registries
      ElevenLabsBridgeNative.cs — DllImport declarations + non-WebGL throwing stubs

Plugins/WebGL/
  ElevenLabsBridge.jslib       — generated from Bridge~/src/primitives/ (committed)

Bridge~/
  src/
    primitives/
      bridge-name.ts           — $EL_BridgeName + setter
      log.ts                   — $EL_Log
      registries.ts            — $EL_Objects / $EL_Functions / $EL_Factories tables, ID alloc
      marshalling.ts           — rehydrate / encodeReturn / makeBridgeCallback
      callbacks.ts             — $EL_InvokeCallback (SendMessage edge of the callback path)
      promise-settle.ts        — $EL_Settle (shared by every async entrypoint)
      dispatcher.ts            — the EL_* DllImport surface
      index.ts                 — aggregates the library object
      globals.d.ts             — ambient types for Unity-injected globals
  tests/
    primitives/                — Vitest coverage per file above
  build/
    bundle-jslib.ts            — shared Rolldown bundling script
```

The C# side stays in one assembly (`ElevenLabs.Agents.WebGL`); the JS side
ships as one `.jslib`. No per-domain JS code in this primitive layer — the
domain consumers (e.g. the connection wrappers) register their factories at
JS boot via `$EL_RegisterFactory`.

## Testing

Three layers, same as the existing repo conventions.

### JS unit tests (Vitest, Node) — `Bridge~/tests/`

The jslib source is bundled by Rolldown and the TypeScript modules are
imported directly in tests. Unity globals (`SendMessage`, `UTF8ToString`,
`_malloc`, etc.) are replaced with fakes; the dispatcher and registries are
exercised in isolation. Asserts:

- Factory registration and invocation
- Argument rehydration: `{$ref}`, `{$fn}`, `{$cb}` markers map to live values
- Return encoding: factory marked `"object"` allocates a handle; `"void"` returns null
- Callback fan-out: passing a `{$cb}` marker materialises a JS function that fires `SendMessage` with the correct format on invocation
- Lifetime: `EL_ObjectRelease` drops the registry entry; calls afterwards fail cleanly
- Error path: a JS-thrown error inside a method settles as `err`

### C# unit tests (Unity Test Runner, Edit Mode) — `Tests/Editor/`

Stubbed DllImports. Asserts:

- ID uniqueness, monotonic generation
- `BridgeArgEncoder` correctly encodes mixed args (primitives + JsObject + JsFunction + BridgeCallback)
- `BridgeValueDecoder` correctly produces JsObject / JsFunction handles from `{$ref}` / `{$fn}` returns
- Registry first-wins behaviour: double-settle, settle-after-cancel, dispose-mid-dispatch
- Message parsing edge cases: colons in payload, empty payload, Unicode

### Integration tests (WebGL build + Vitest browser mode) — Phase 5+

A minimal Unity scene exercises the full round-trip in Chrome / Firefox /
Safari. Vitest browser mode loads the WebGL build and asserts on the bridge
behaviour from the outside.

## Implementation phases

Ordered to frontload everything that doesn't require a Unity host project.
Unity 6000.3.6f1 is now installed locally (see "Phase 3 prelude" below) —
the C# phases unblock once the embedded `TestProject/` lands.

The first downstream consumer of this primitive layer is [plan-b.md](./plan-b.md)
(the connection / I/O controller bridge that the C# `Conversation` orchestrates).
Plan B's Phase 2 lands at the same time as this plan's Phase 2 — both produce
JS-side artifacts that bundle and test in isolation.

### Phase 1 — Existing scaffolding (already in repo)

These exist and are reused as-is. Listed for clarity, no new work:

- `WebGLBridge.cs` singleton MonoBehaviour with `[RuntimeInitializeOnLoadMethod]`
- `BridgeIdGenerator.cs` monotonic int ID generator
- `BridgeMessageParser.cs` (variants will be trimmed to just `id:status:payload` for the settle channel)
- `BridgeLog.cs` tagged logger
- `BridgeException.cs`
- `Bridge~/` pnpm + Vitest + Prettier + ESLint + Rolldown setup
- `Bridge~/build/bundle-jslib.ts` Rolldown bundling script
- The `$EL_BridgeName` + `$EL_Log` jslib helpers

Files from the prior bridge primitives implementation that don't fit the new
design are deleted in the Phase 1 cleanup step below — a single discrete
commit that leaves the repo on the kept scaffolding only, before any Phase 2
code lands.

### Phase 1 cleanup — Delete the prior primitive implementation (Unity-free, single commit)

Lands before any Phase 2 task. Rationale: starts the new work from a clean
slate so the new files don't have to coexist with their predecessors during
review or bisection.

- [x] **1c.1 — Delete superseded JS sources.** Remove `Bridge~/src/primitives/call-promise.ts`, `observer.ts`, `handler.ts`. (`$EL_CallPromise` is replaced by the per-entrypoint settle pattern in Phase 2.4; the observer primitive collapses into `BridgeCallback` in Phase 2's marshalling; handler invocation has no v0.1 consumer and is deferred to the future `AsyncBridgeCallback` extension point.)
- [x] **1c.2 — Delete superseded JS tests.** Remove `Bridge~/tests/ElevenLabsBridge.test.ts`. Per-file Vitest coverage for the new primitives returns alongside each Phase 2 task. Added `--passWithNoTests` to the `test` / `test:watch` scripts so `pnpm run test` exits 0 during the brief window before Phase 2 tests land.
- [x] **1c.3 — Trim `Bridge~/src/primitives/index.ts`.** Keep imports and spreads for only `bridge-name` and `log`. The next aggregation pass happens in Phase 2.6.
- [x] **1c.4 — Trim `Bridge~/src/primitives/globals.d.ts`.** Remove the ambient declarations for `_EL_Observers`, `_EL_PendingInvocations`, and `_EL_InvocationCounter`. Keep `SendMessage`, `_EL_BridgeName`, and `_EL_Log`.
- [x] **1c.5 — Trim `Runtime/WebGL/WebGLBridge.cs`.** Remove the three stub SendMessage handlers (`OnPromiseSettled`, `OnObserverEvent`, `OnHandlerInvoked`). They'll be replaced in Phase 3 with the real dispatch — but with different signatures and registries, so leaving them as stubs would create dead handlers that mis-suggest the new shape.
- [x] **1c.6 — Regenerate the jslib.** `pnpm --dir Bridge~ run build:primitives` rebuilds `Plugins/WebGL/ElevenLabsBridge.jslib` from the now-much-smaller source set (only `bridge-name` + `log`). Commit the regenerated file.
- [x] **1c.7 — Verify.** All of `pnpm --dir Bridge~ run typecheck`, `pnpm --dir Bridge~ run lint`, `pnpm --dir Bridge~ run verify:primitives` pass.

### Phase 2 — JS-side primitives (Unity-free, Vitest-covered)

All testable end-to-end in Node with Vitest mocking the Unity runtime.
Tasks within this phase are mostly parallelisable.

- [x] **2.1 — Registries.** `Bridge~/src/primitives/registries.ts` — `$EL_Objects`, `$EL_Functions`, `$EL_Factories`, JS-side function-handle allocator. Per-table `register`, `lookup`, `release` operations. Vitest: register/lookup/release; release-then-lookup misses cleanly; double-release is a no-op.
- [x] **2.2 — Marshalling.** `marshalling.ts` — `rehydrate(value)` and `encodeReturn(value, shape)`. Walks nested structures. Vitest: every marker shape round-trips; nested arrays/objects with mixed markers work; unknown markers pass through as-is.
- [x] **2.3 — Callback dispatch helper.** `callbacks.ts` — `$EL_InvokeCallback(handle, payload)` SendMessages with `handle + ':' + payload`. Used by the JS function returned from `makeBridgeCallback`. No JS-side release entry point — the C# registry is the authority; JS-side closures are the consumer's lifetime concern (typically via a paired `removeListener` `JsFunction`). Vitest: invocation fires `SendMessage` with the correct format.
- [x] **2.4 — Settle helper.** `promise-settle.ts` — `$EL_Settle(promiseId, status, payload)` SendMessages with `promiseId + ':' + status + ':' + payload`. Used by every async entrypoint. Vitest: ok/err round-trip; error message survives JSON escaping.
- [x] **2.5 — Dispatcher and entry points.** `dispatcher.ts` — the eight `EL_*` DllImport targets listed above. Each wraps its body in try/catch that routes to `$EL_Settle` on error (async) or returns a string-encoded error pointer (sync). Vitest:
  - `EL_InvokeFactoryAsync` resolves with the value encoded per the per-call `returnShape` int; missing factory rejects
  - `EL_ObjectCallAsync` calls the method with rehydrated args; settles with encoded return
  - `EL_ObjectCallSync` returns the heap-string-encoded value
  - `EL_ObjectGet` reads the property
  - `EL_ObjectRelease` drops the entry; subsequent calls reject with "unknown handle"
  - `EL_FunctionCallAsync` / `EL_FunctionCallSync` / `EL_FunctionRelease` parallel to object variants
  - unknown `returnShape` code falls back to `value`
- [x] **2.6 — Index aggregation + bundling.** `index.ts` re-exports via namespace imports + spread; `pnpm run build:primitives` emits `Plugins/WebGL/ElevenLabsBridge.jslib`. `pnpm run verify:primitives` rebuilds + `git diff --exit-code`. Existing CLAUDE.md command documentation updated to match the new shape.
- [x] **2.7 — Sample factory for end-to-end test.** A throwaway `mathFactory` registered in a Vitest setup file (returns an object with `add(a, b)` sync, `addAsync(a, b)` async, `pi` property, `addTickListener(callback)` returning a `removeListener` function). End-to-end Vitest exercises every primitive surface against it — factory invocation (shape `object`), sync method (shape `value`), async method (shape `value`), property read, callback registration with `BridgeCallback`, function-handle round-trip via `removeListener` (shape `function`), dispose. Not shipped; just used for cross-cutting coverage. Resolved open question #2 in the process: return shapes are passed per-call as ints rather than declared at registration time.

### Phase 2.5 — DynCall migration of the JS settlement and callback channels (Unity-free, Vitest-covered)

Lands between Phase 2 (done) and Phase 3 (not started). Replaces the two `SendMessage` call sites — `$EL_Settle` and `$EL_InvokeCallback` — with wasm function pointers invoked via the `{{{ makeDynCall('sig', 'fnVar') }}}` macro. The C# side does not change in this phase; the new entry points it will need (`EL_SetSettleCallback`, `EL_SetInvokeCallbackPtr`, `EL_ProbeWasmTable`) ship as JS stubs that will be wired in revised Phase 3.

See [dyncall-migration.md](./dyncall-migration.md) for the full design rationale (wire protocol, AST-based macro substitution, two-layer `Use WebAssembly.Table` enforcement, consumer-impact mitigations). Tasks in this phase are sized as one commit each — **do not squash**; per-task commit granularity is the SendMessage-fallback contingency mitigation.

- [x] **2.5.1 — Add `Bridge~/src/primitives/function-pointers.ts`.** New module exporting `$EL_SettlePtr = 0`, `$EL_CallbackPtr = 0` (hoisted to globals via Unity's `$` convention), plus `EL_SetSettleCallback(ptr)`, `EL_SetInvokeCallbackPtr(ptr)`, and `EL_ProbeWasmTable()` entry points. Vitest: each setter writes to the right global; probe returns 1 when `Module.wasmTable` is in scope, 0 otherwise.
- [x] **2.5.2 — Delete `Bridge~/src/primitives/bridge-name.ts`** and update `globals.d.ts`: remove `_EL_BridgeName` and `SendMessage` ambient declarations; add `dynCall_viii`, `dynCall_vii`, `_EL_SettlePtr`, `_EL_CallbackPtr`, `stringToNewUTF8`, `_free`. (`_EL_SettlePtr`, `_EL_CallbackPtr`, and `_malloc` were already added in 2.5.1.) The two source files that still call `SendMessage` (`promise-settle.ts`, `callbacks.ts`) carry temporary local `declare` stubs of those globals so the build stays green; the stubs vanish when 2.5.3 and 2.5.4 rewrite the file bodies onto the DynCall channel. `index.ts` drops the `bridge-name` import as a necessary side-effect of the deletion; the spread of the new `function-pointers` module lands in 2.5.5. The jslib was regenerated alongside (no `EL_SetBridgeName` symbol or `$EL_BridgeName` global emitted).
- [x] **2.5.3 — Rewrite `Bridge~/src/primitives/promise-settle.ts`.** New body uses `dynCall_viii(_EL_SettlePtr, promiseId, statusCode, payloadPtr)` with `statusCode` of `0` for ok / `1` for err; JS allocates the UTF-8 payload via `stringToNewUTF8`, calls DynCall, then `_free`s in `finally`. Vitest mocks `dynCall_viii` / `stringToNewUTF8` / `_free` / `_EL_SettlePtr`; asserts call args and free-after-call ordering.
- [x] **2.5.4 — Rewrite `Bridge~/src/primitives/callbacks.ts`.** Mirror of 2.5.3 with the `vii` signature (`handle`, `payloadPtr`). Same memory-lifetime contract and test shape.
- [x] **2.5.5 — Update `Bridge~/src/primitives/index.ts`.** Swap `import * as bridgeName from "./bridge-name"` for `import * as functionPointers from "./function-pointers"`; spread in place of `bridgeName`.
- [x] **2.5.6 — Add Rolldown transform plugin + regenerate `Plugins/WebGL/ElevenLabsBridge.jslib`.** New `Bridge~/build/substitute-make-dyncall.ts` exports a Rolldown plugin that uses `this.parse(code)` in `transform` to walk the AST, locates `CallExpression` nodes with `callee.name` matching `/^dynCall_[vif]+$/` and a first argument that's an `Identifier` starting with `_EL_`, and rewrites via `MagicString` to `{{{ makeDynCall('<sig>', '<varName>') }}}(<rest>)`. Wired into `bundle-jslib.ts`'s `rolldown({ plugins: [...] })` call. Plugin has its own unit tests (plain int args, nested `JSON.stringify`, multi-line, zero-arg, non-`_EL_` negative case). Then `pnpm --dir Bridge~ run build:primitives` regenerates the `.jslib`; commit it. `pnpm --dir Bridge~ run verify:primitives` passes. Spot-check: exactly **two** `{{{ makeDynCall(...) }}}` macro sites in the regenerated file, **zero** `SendMessage(...)` calls.

### Phase 3 prelude — Unity host project (gating prerequisite for Phase 3+)

**Status as of 2026-06-16:** Unity **6000.3.6f1** (Unity 6 LTS) is installed
locally at `/Applications/Unity/Hub/Editor/6000.3.6f1/Unity.app`. The
previous "blocked on Unity license" framing no longer applies — the only
remaining gating dependency for Phase 3+ is a Unity project Unity can
actually open. The package source today (asmdefs, jslib, Phase 1
scaffolding) has no consumer; Unity has nothing to compile.

**Layout decision: embedded `TestProject/` at repo root (no tilde).** The
dev host is a real Unity project we open locally for compile errors,
IntelliSense, the Test Runner, and WebGL builds. Two ideas got tried and
discarded on 2026-06-16 before landing on the current shape:

1. **`TestProject~/` (trailing tilde)** — original HP.1 design. Unity's
   asset-import folder-ignore rule (`~`-suffixed folders are skipped)
   would have kept the dev host out of downstream consumers' installed
   payload. **Doesn't work**: Unity's batchmode `-projectPath` validator
   refuses any path ending in `~`, including after symlink resolution.
   Blocks headless test runs and CI.
2. **Repo root as the Unity project** — `package.json` and
   `Packages/manifest.json` both at `/`, package self-referenced via
   `"io.elevenlabs.agents": "file:.."`. **Doesn't work**: Unity's
   `PackageManager::PackageAssets::RejectLocalPackagesInForbiddenFolders`
   crashes (segfault during `Project::Restore`) when a `file:` package
   resolves to the project root itself.

**Landed shape:** `TestProject/` (no tilde) at the repo root, with the
package referenced via `"io.elevenlabs.agents": "file:../.."` from
`TestProject/Packages/manifest.json`. The package source lives one
directory above the Unity project, satisfying the "forbidden folder"
constraint. The non-tilde name lets `-projectPath` open the project from
batchmode and CI. Mild overlap with the existing `Tests/` folder at the
package root is intentional: `Tests/` holds the test *source* that ships
with the package; `TestProject/` is the *runner host* Unity opens.

**UPM payload exclusion (follow-up):** Because `TestProject/` no longer
has the `~` suffix, Unity does NOT automatically exclude it from the
package when an end user installs `io.elevenlabs.agents` via git URL or
scoped registry. The directory will be visible inside the consumer's
`Packages/io.elevenlabs.agents/TestProject/`. To exclude it from
published payloads, add a `package.json` `"files"` allowlist OR an
`.npmignore` entry. Tracked as HP.8 below — not a Phase 3 blocker; the
practical effect on consumers is visual cruft, not broken behaviour.

- [x] **HP.1 — Create `TestProject/` skeleton (no tilde).** `TestProject/Packages/manifest.json` references the local package via `"io.elevenlabs.agents": "file:../.."`; `TestProject/ProjectSettings/` is seeded from a Unity 6000.3.6f1 `-createProject` run (all the standard `*.asset` files, including `ProjectSettings.asset`, `EditorBuildSettings.asset`, etc.); `TestProject/Assets/.gitkeep` keeps the empty Assets dir alive in fresh clones (Unity refuses to open a project without an existing `Assets/`).
- [x] **HP.2 — Update root `.gitignore`.** Add `TestProject/[Ll]ibrary/`, `TestProject/[Tt]emp/`, `TestProject/[Oo]bj/`, `TestProject/[Bb]uild/`, `TestProject/[Ll]ogs/`, `TestProject/[Uu]ser[Ss]ettings/`, `TestProject/[Aa]ssets/*` (with a negation for `.gitkeep`), and `TestProject/Packages/packages-lock.json`. Keep `TestProject/Packages/manifest.json` and `TestProject/ProjectSettings/*` tracked.
- [x] **HP.3 — First open verification.** Confirmed via `"/Applications/Unity/Hub/Editor/6000.3.6f1/Unity.app/Contents/MacOS/Unity" -batchmode -nographics -projectPath /path/to/repo/TestProject -logFile - -quit`: project path accepted, `io.elevenlabs.agents` resolves from `file:../..`, package source is scanned. **Known follow-ups surfaced:** (a) Unity creates `.meta` sidecar files for every file/folder it scans inside the package source (Editor/, Plugins/, Runtime/, Tests/, plus the repo-root markdown/json files); these are currently deleted on each open and need a real management strategy (tracked as HP.9). (b) The existing protocol DTOs fail to compile with `System.Text.Json` missing + `IsExternalInit` undefined — Unity's default API compat level doesn't include them; tracked as HP.10.
- [x] **HP.4 — Minimal Edit Mode sanity test.** A throwaway `Tests/Editor/SanityTest.cs` with `Assert.Pass()`. Proves the existing `ElevenLabs.Agents.WebGL.Tests.asmdef` reference graph resolves, the `UNITY_INCLUDE_TESTS` define is active, and Test Runner picks the assembly up. Verified 2026-06-16: `Unity -batchmode -nographics -projectPath TestProject -runTests -testPlatform editmode -testResults /tmp/test-results.xml -logFile /tmp/unity-test.log` exits 0 and emits `test-results.xml` with `result="Passed" total="1" passed="1" failed="0"` for `ElevenLabs.WebGL.Tests.SanityTest.Sanity`. **Critical gotcha learned:** do NOT pass `-quit` to a `-runTests` invocation — Unity will honor `-quit` BEFORE the test runner fires, exit 0 silently, and emit no results. The test runner exits the editor itself once tests finish. Remove or repurpose once a real Phase 3.2+ test lands.
- [x] **HP.5 — Headless local test runner.** `TestProject/run-tests.sh` — computes absolute project path from the script location and invokes `Unity -batchmode -nographics -projectPath <abs> -runTests -testPlatform editmode -testResults <abs>/test-results.xml -logFile -`. Deliberately omits `-quit` (see HP.4 critical gotcha — Unity honors `-quit` before the test runner fires). `TestProject/test-results.xml` added to `.gitignore`. `.claude/CLAUDE.md` "Unity-side verification" section updated with `bash TestProject/run-tests.sh` and a note explaining the `-quit` gotcha. Run via `bash TestProject/run-tests.sh`.
- [x] **HP.6 — WebGL build smoke.** A second wrapper invoking the same Unity binary with `-buildTarget WebGL -executeMethod` against a stub `Editor/HostBuild.cs` that calls `BuildPipeline.BuildPlayer(...)`. Confirms the IL2CPP WebGL pipeline is healthy before Phase 4 needs it. The scene that exercises the bridge for real lives in Phase 4.1 — HP.6 just proves the build target works. Implemented: `Editor/HostBuild.cs` (static Build() method with temporary scene creation + `EditorApplication.Exit()`), `Editor/ElevenLabs.Agents.WebGL.Editor.asmdef` (editor-only assembly), `TestProject/build-webgl.sh` (headless invoker via `-executeMethod ElevenLabs.WebGL.Editor.HostBuild.Build`). Build output goes to `TestProject/Build/WebGL/` (gitignored via existing `TestProject/[Bb]uild/` rule).
- [ ] **HP.7 — CI wiring (deferred).** GitHub Actions via `game-ci/unity-test-runner@v4` plus license activation (Personal seat ULF via repo secret, or organization seat). Tracked here; lands once HP.5 is stable locally. Not a Phase 3 blocker.
- [x] **HP.8 — UPM payload exclusion for `TestProject/`.** Add a `"files"` allowlist to `package.json` (or `.npmignore`) so `TestProject/` (and any other dev-only artefacts at the repo root) are excluded from the published package payload. Verify via `npm pack` on a clean clone that the resulting tarball contains only `Runtime/`, `Editor/`, `Tests/`, `Plugins/`, `Samples~/` (when it exists), and the documentation/license files. Not a Phase 3 blocker. **Empirical 2026-06-16 finding:** the `"files"` allowlist has NO effect on Unity's local `file:..` package scan — verified by running the test runner twice with and without the allowlist and diffing the resulting untracked `.meta` set (54 files, byte-identical sets, test passed in both cases). UPM only honors `"files"` at `npm pack` time (git URL installs, registry publishes), not for local file-references. The value of HP.8 is therefore strictly published-payload hygiene; it does NOT reduce `git status` clutter or Unity's recursive-scan footprint. That work belongs to HP.9. **Implemented 2026-06-16:** `package.json` `"files"` allowlist set to `["Runtime", "Runtime.meta", "Editor", "Editor.meta", "Tests", "Tests.meta", "Plugins", "Plugins.meta", "CONTRIBUTING.md"]`. npm auto-includes `package.json`, `README.md`, `CHANGELOG.md`, and `LICENSE`; everything else (TestProject/, Bridge~/, Codegen~/, Docs~/, stray *.meta files) is excluded from the published tarball.
- [x] **HP.9 — `.meta` file management strategy.** Decide which Unity-generated `.meta` files to commit. UPM convention is to commit `.meta` for every package-source file/folder (stable GUIDs for asset references); the recursive scan also creates `.meta` for the `TestProject/` subtree which should NOT be committed. Update `.gitignore` to draw the line cleanly and document the rule in the contributor guide. **Per the HP.8 finding above, this is the ONLY mechanism to reduce `git status` `.meta` noise** — there is no Unity-supported way to constrain the recursive scan that produces them. Three categories observed (2026-06-16, 54 files total): (1) **pkg-asset** (~13 files under `Runtime/`, `Plugins/`, `Tests/`) — real Unity assets, COMMIT these so GUIDs stay stable; (2) **repo-root standalone** (~10 files like `CHANGELOG.md.meta`, `LICENSE.meta`, `package.json.meta`) — Unity auto-stamps non-Unity files at the package root, IGNORE; (3) **test-host subtree** (~30 files under `TestProject/Assets.meta`, `TestProject/Packages.meta`, `TestProject/ProjectSettings/*.asset.meta`, etc.) — Unity scans the embedded TestProject as if it were package content, IGNORE the whole subtree.
- [x] **HP.10 — Set Unity API compat level + switch DTOs to Newtonsoft.Json.** The generated DTOs under `Runtime/Core/Protocol/*.g.cs` originally referenced `System.Text.Json.Serialization` and used `init`-only setters (which require `IsExternalInit`). The default Unity 6 API compat level provides neither, and Unity 6's .NET Standard 2.1 BCL does NOT bundle `System.Text.Json` (a 2026-06-16 batchmode test run aborted with ~70 `CS0246: 'JsonPropertyName' could not be found` errors after the first attempt at this task tried to rely on that). **Final shape:** (a) `TestProject/ProjectSettings/ProjectSettings.asset` switched from API compat level 6 (.NET Standard 2.0) to 9 (.NET Standard 2.1); (b) `Runtime/Core/IsExternalInit.cs` polyfill added for `init`-only setter support (Roslyn requires the type, .NET Standard 2.1 itself doesn't ship it); (c) codegen preset (`Codegen~/src/protocol-preset.ts`) rewritten to emit `[JsonProperty("…")]` + `using Newtonsoft.Json;` instead of `[JsonPropertyName]` + `System.Text.Json.Serialization`; (d) round-trip script swapped to `JsonConvert.SerializeObject/DeserializeObject`; (e) `com.unity.nuget.newtonsoft-json` added as a UPM dependency in `package.json`. Verified: clean Unity compile of both `ElevenLabs.Agents.WebGL.dll` and `ElevenLabs.Agents.WebGL.Tests.dll`, zero CS errors, round-trip serialization passes for every Convai DTO. Rationale for Newtonsoft over the alternatives: `JsonUtility` is structurally incompatible (no property/attribute support, no polymorphism, no snake_case→PascalCase mapping); `com.unity.serialization` uses an unrelated `[CreateProperty]` model aimed at DOTS workflows; vendoring `System.Text.Json` + dependencies under `Plugins/` invites IL2CPP/AOT trip-hazards on WebGL. `com.unity.nuget.newtonsoft-json@3.x` (wrapping Newtonsoft.Json 13.0.1) ships an AOT-friendly build out of the box.

**Unblocks what:** HP.1 through HP.4 + HP.10 are the minimum to start
Phase 3.0 work — after HP.4 the existing scaffolding compiles inside
Unity and new tests can be authored against it. HP.5–HP.7 are
quality-of-life and CI plumbing that don't gate any C# task
individually. HP.8 and HP.9 are repo-hygiene tasks that don't block
Phase 3 but should land before the package's first external release.

### Phase 3 — C# primitive layer (requires the Phase 3 prelude above)

Phase 3 was redesigned on 2026-06-12 around the DynCall path (see [dyncall-migration.md](./dyncall-migration.md)). Headline changes vs. the original v0.1 plan: the dispatch entry point is a static `BridgeStaticCallbacks` class with `[AOT.MonoPInvokeCallback]` static methods (not a MonoBehaviour with `SendMessage` targets); the `WebGLBridge` MonoBehaviour and `BridgeMessageParser` are deleted; consumers must enable `Use WebAssembly.Table` (enforced at build time + runtime).

- [x] **3.0 — `BridgeStaticCallbacks` static class.** New `Runtime/WebGL/BridgeStaticCallbacks.cs` with two non-generic explicit delegate types (`SettleCallback`, `InvokeCallback` — `Action<...>` is rejected by IL2CPP with `[MonoPInvokeCallback]`), two static delegate fields for GC safety, two `[AOT.MonoPInvokeCallback]`-decorated static methods reading payload via `Marshal.PtrToStringUTF8(payloadPtr)` before returning, and a `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` static initializer that registers both function pointers, runs the `EL_ProbeWasmTable` runtime probe (warn-only on false), wraps the first DynCall in a try/catch for the authoritative `ReferenceError`-as-`BridgeException` signal, and subscribes `Application.quitting` for best-effort teardown. A `Runtime/WebGL/link.xml` preserves the whole type so IL2CPP stripping doesn't eat the function-pointer-only static methods on release builds. Static-method payload-handler bodies are stubs — `_ = payload;` discards — until tasks 3.6 / 3.7 wire them into the callback and promise registries. The three DllImports the initializer needs (`EL_SetSettleCallback`, `EL_SetInvokeCallbackPtr`, `EL_ProbeWasmTable`) are added to `ElevenLabsBridgeNative.cs` as part of this commit; the rest of the `EL_*` surface and the `EL_SetBridgeName` removal land in task 3.1.
- [x] **3.1 — DllImport declarations.** `ElevenLabsBridgeNative.cs` — every `EL_*` entry point including the new `EL_SetSettleCallback(IntPtr)`, `EL_SetInvokeCallbackPtr(IntPtr)`, `EL_ProbeWasmTable() -> int` from Phase 2.5.1; the legacy `EL_SetBridgeName` is removed. `#if UNITY_WEBGL && !UNITY_EDITOR` real bodies and throwing stubs otherwise (matches the existing convention).
- [x] **3.2 — Registries (C# side).** `Runtime/WebGL/Internal/Registries.cs` — `PromiseRegistry` (`Dictionary<int, AwaitableCompletionSource<string>>`) with `Register` / `TrySettle(ok|err, payload)`; `CallbackRegistry` (`Dictionary<int, Action<string>>`) with `Register` / `TryDispatch` / `TryRemove`. Both use a `lock` around the dictionary; mutations happen inside the lock, handler invocation / completion-source signalling happens outside (so a re-entrant bridge call from the handler doesn't deadlock). Promise settle is terminal (lookup-then-remove, first-wins, idempotent second settle no-ops); callback dispatch is multi-shot (lookup-only); only `TryRemove` evicts the callback entry. `AssemblyInfo.cs` added at `Runtime/` exposing internals to `ElevenLabs.Agents.WebGL.Tests`. Edit-mode coverage in `Tests/Editor/Internal/PromiseRegistryTests.cs` (6 cases) and `CallbackRegistryTests.cs` (8 cases) — all 15 tests pass (1 sanity + 14 new). `BridgeStaticCallbacks.OnSettleFromJs` and `OnCallbackInvokedFromJs` still discard their payloads as before — wiring them to these registries lands in tasks 3.7 and 3.6.
- [x] **3.3 — Marshalling (C# side).** `Marshalling/BridgeArgEncoder.cs` and `BridgeValueDecoder.cs`. Encoder walks `params object[]`, emits JSON with `$ref` / `$fn` / `$cb` markers based on runtime type. Decoder reads JSON returns and produces primitives, `JsObject`, `JsFunction`. Edit-mode tests round-trip each shape.
- [x] **3.4 — `JsBridge` static entry point.** `InvokeFactoryAsync<T>` / `InvokeFactory<T>` async/sync overloads. Internally allocates a promise ID (async) or marshals and calls the sync DllImport. Returns deserialised `T` (including `JsObject` / `JsFunction` as `T`).
- [x] **3.5 — `JsObject` + `JsFunction`.** `CallAsync<T>` / `Call<T>` / `Get<T>` overloads; `Dispose` calls `EL_ObjectRelease` / `EL_FunctionRelease`. Finalizer logs a warning via `BridgeLog` if dispose wasn't called. Edit-mode tests against stubbed DllImports.
- [x] **3.6 — `BridgeCallback`.** `Wrap(Action<string>)` + typed overload allocates a handle and registers the delegate. `Dispose` removes the registry entry. Wires `BridgeStaticCallbacks.OnCallbackInvokedFromJs` (from task 3.0) to the callback registry's `Dispatch`. Edit-mode tests for wrap / invoke / dispose, including "invocation after dispose silently no-ops."
- [x] **3.7 — Wire `BridgeStaticCallbacks.OnSettleFromJs` to the promise registry.** Routes the `(promiseId, statusCode, payloadPtr)` DynCall args into the promise registry — `statusCode == 0` resolves the `AwaitableCompletionSource<string>` with the payload; `statusCode == 1` raises `BridgeException`. Replaces the v0.1-plan's `WebGLBridge.OnPromiseSettled` MonoBehaviour `SendMessage` target. Edit-mode tests cover both paths.
- [x] **3.8 — XML doc comments** on every public type and member.
- [x] **3.9 — `Editor/BridgeBuildPreprocessor.cs` — build-time `Use WebAssembly.Table` gate.** Implements `IPreprocessBuildWithReport`; on WebGL builds it reads `PlayerSettings.WebGL.useWasmTable` and throws `BuildFailedException` with the fix instructions (Player Settings → WebGL → Publishing Settings → Use WebAssembly.Table) when the setting is off. **Why:** with `Use WebAssembly.Table` disabled, the `{{{ makeDynCall(...) }}}` macro expands to `getWasmTableEntry(fnVar)(...args)`, and `getWasmTableEntry` references a `wasmTable` that wasn't exported — the failure mode is a runtime `ReferenceError` on the *first* DynCall, **not** a link-time error. A pure runtime probe is also unreliable because Unity invokes Emscripten with Closure Compiler on release builds, which can rename `getWasmTableEntry` and `wasmTable` themselves. This task is **Layer 1** (primary) of the two-layer enforcement designed in [dyncall-migration.md](./dyncall-migration.md#use-webassemblytable-enforcement--two-layers) — editor-only, no runtime cost, immune to Closure renaming. **Layer 2 (defence-in-depth)** is already shipped in task 3.0: a runtime `typeof Module.wasmTable !== "undefined"` probe that warns rather than throws (a false negative under Closure renaming should not abort the bridge), plus the first-call try/catch in `BridgeStaticCallbacks` that re-throws `ReferenceError` as `BridgeException` with the same setup instructions. New `Tests/Editor/BridgeBuildPreprocessorTests.cs` flips `PlayerSettings.WebGL.useWasmTable` (restoring it via `try`/`finally`) and asserts the preprocessor throws when off and is a no-op when on; non-WebGL build targets must be a no-op too.
- [x] **3.10 — Delete `Runtime/WebGL/WebGLBridge.cs`.** Remove the singleton `MonoBehaviour` and its companion `__ElevenLabsBridge__` GameObject. **Why:** `WebGLBridge` existed in v0.1 purely as a named GameObject so JS could target it via `SendMessage(gameObjectName, methodName, payload)`. The DynCall migration replaced that dispatch path with static `[AOT.MonoPInvokeCallback]`-decorated methods on `BridgeStaticCallbacks` (task 3.0) — there is no SendMessage target left to host, so the GameObject earns nothing in the new design. Keeping it would (a) leave a `DontDestroyOnLoad`, `HideAndDontSave` hidden GameObject in every consumer's scene for no functional reason, and (b) confuse readers about which dispatch path is live. Grep confirms zero external references — the type is self-referencing only. The file is intentionally preserved in git history (`git log -p Runtime/WebGL/WebGLBridge.cs`) per the SendMessage-fallback contingency documented in [dyncall-migration.md → Consumer-impact risk](./dyncall-migration.md#consumer-impact-risk); per-task commit granularity is what makes that fallback a known-shape patch rather than a from-scratch redesign.
- [ ] **3.11 — Delete `Runtime/WebGL/BridgeMessageParser.cs`.** Remove the colon-delimited message parser and its tests (if any remain). **Why:** the parser decoded the v0.1 wire format — `"promiseId:status:payload"` and `"handle:payload"` — because `SendMessage` can only carry a single string argument, forcing every typed field through string-encoding. The DynCall channels (`$EL_Settle` signature `viii`: `(int promiseId, int statusCode, IntPtr payloadPtr)`; `$EL_InvokeCallback` signature `vii`: `(int handle, IntPtr payloadPtr)` — see [dyncall-migration.md → Wire protocol](./dyncall-migration.md#wire-protocol)) pass typed primitives directly. The C# side now reads payloads via `Marshal.PtrToStringUTF8(payloadPtr)` — no string parsing, no colon-escaping edge cases, no `FormatException` paths. The parser is dead code; grep confirms zero external references. Same git-history preservation rationale as task 3.10.
- [ ] **3.12 — README updates: `Use WebAssembly.Table` requirement + .NET Standard 2.1 API compatibility level.** Two consumer-facing hard requirements landed with the DynCall migration and need to be discoverable without reading the plans. **(a) `Use WebAssembly.Table` is mandatory.** The build preprocessor (task 3.9) will fail builds with the setting off, but the README must explain the *why* — Unity 6 deprecated the legacy `Module.dynCall_*` API in favour of the `makeDynCall` macro (see Unity's [deprecated browser-interaction APIs page](https://docs.unity3d.com/6000.0/Documentation/Manual/web-interacting-browser-deprecated.html)), and enabling `Use WebAssembly.Table` is Unity's recommended forward-compatible setting that makes the macro path work (see the [PlayerSettings WebGL doc](https://docs.unity3d.com/6000.3/Documentation/Manual/class-PlayerSettingsWebGL.html)) — and document the **consumer-impact risk** spelled out in [dyncall-migration.md → Consumer-impact risk](./dyncall-migration.md#consumer-impact-risk): any other `.jslib` in the same Unity project that still uses `Module.dynCall_*` will break when the setting is on, forcing the consumer to migrate that plug-in or drop ours. **(b) `.NET Standard 2.1` API compatibility level.** `Marshal.PtrToStringUTF8`, used by `BridgeStaticCallbacks` to read DynCall payload pointers, is unavailable in .NET Standard 2.0. Unity 2023.1+ defaults to 2.1, so most projects are covered by default; a project explicitly downgraded to 2.0 will hit `MissingMethodException` on the first JS→C# callback. Documented as a package requirement (link to the [dyncall-migration.md → Risks acknowledged](./dyncall-migration.md#risks-acknowledged) entry for context).

### Phase 4 — WebGL smoke test (requires the Phase 3 prelude above + Phase 3 complete)

- [ ] **4.1 — Minimal scene.** A `BridgePrimitiveSmokeTest` MonoBehaviour: registers the `mathFactory` from Phase 2.7 in a `.jspre` (or its equivalent), then in C# `Start()` invokes the factory, exercises each surface (method, property, callback registration, function-handle round-trip), asserts on results, logs to console.
- [ ] **4.2 — Validation assertions.**
  - V1: `SendMessage` from an async JS callback delivers same-frame (`Time.frameCount` comparison)
  - V2: `Awaitable` continuation after `await JsObject.CallAsync(...)` can immediately call another `DllImport` without breakage
  - V3: `BridgeCallback` invocations arrive in order under rapid emission from JS
  - V4: A `JsFunction` returned from a method call survives across multiple sync calls
- [ ] **4.3 — Clean IL2CPP build.** No warnings, no missing symbols. Manual verification in Chrome and at least one of Firefox/Safari.

### Phase 5 — Automated integration (requires Phase 4 complete)

- [ ] **5.1 — Vitest browser-mode harness.** Playwright provider loads the WebGL build; the harness drives the smoke-test scene from JS and asserts on the bridge protocol.
- [ ] **5.2 — Edge cases.** Double-dispose, orphaned handles on bridge destroy, special chars in payload (colons, newlines, Unicode), large payloads (>100KB), rapid-fire callback invocations from JS.
- [ ] **5.3 — Cross-browser CI.** Chrome, Firefox, Safari.

## Definition of done

- Every `EL_*` entry point has Vitest coverage exercising its happy path, an error path, and a "missing handle" path.
- Every `JsBridge` / `JsObject` / `JsFunction` / `BridgeCallback` public method has an Edit-mode test exercising it against stubbed DllImports.
- The Vitest browser-mode integration suite passes on Chrome, Firefox, Safari in CI.
- Validation assertions V1–V4 pass in a real browser.
- A clean IL2CPP WebGL build succeeds with no warnings.
- `pnpm run verify:primitives` exits 0 on a fresh clone (jslib is reproducible from TS sources).
- Public C# API is documented with XML doc comments.
- The UPM package installs cleanly into a Unity 2023.1+ project via git URL.

## Open questions

1. **Argument auto-marshalling depth.** The encoder walks nested structures for `$ref` / `$fn` / `$cb` markers — is that needed in practice, or are object / function / callback args always top-level? Top-level-only is simpler and faster but locks out cases like `{ options: { onMessage: callback } }` payload shapes.
2. ~~**Return-shape declaration locality.**~~ **Resolved (2026-06-10): per-call.** Every dispatcher entry point takes a `returnShape` int derived from the C# `<T>` parameter. Eliminates the registration-time hint table (`$EL_RegisterMethods`) entirely — the C# generic is the single source of truth, and call-site / registration mismatch is impossible by construction. The one int per call across the DllImport boundary is negligible cost compared to splitting the source of truth across a separate registration block.
3. **Synchronous error reporting.** Async entry points settle errors through the promise channel. Sync entry points return a string pointer — how do errors look? Proposed: a sentinel prefix (`"!err:"` + message) that the C# decoder recognises and throws. Alternative: a separate `EL_LastSyncError` getter that C# polls after every sync call. First option is one fewer round trip.
4. **Auto-teardown helper for `BridgeCallback`.** A `BridgeCallback.Dispose` call removes the C# delegate, but the JS-side closure capturing the handle stays alive until the JS consumer drops it (e.g. by calling `removeListener`). A future helper could pair a `BridgeCallback` with a `JsFunction` at wrap time so `Dispose` invokes the teardown automatically. Defer until a real consumer makes the pattern repetitive enough to warrant it.

## Possible extensions

Noted to mark the primitive set as "complete on paper" — the design has a
natural place for each, but none are needed for v0.1.

- **`AsyncBridgeCallback`** — a `BridgeCallback` whose JS-side wrapper function returns a `Promise` that settles when C# resolves it (via a `EL_ResolveCallback(invocationId, resultJson)` DllImport). Lets JS-side code `await` a result from C#, covering patterns like remote tool dispatch (a JS module asking C# to execute a tool and waiting for the result). Reuses the existing settle channel plus a per-invocation registry on the JS side. With this in place the primitive set covers every interop direction symmetrically: C# can call sync or async into JS (via `JsObject` / `JsFunction`); JS can call sync-style fire-and-forget or async-with-result into C# (via `BridgeCallback` / `AsyncBridgeCallback`).
- **Binary-payload variants** — `EL_ObjectCallBytes(handle, methodPtr, bufferPtr, bufferLen, promiseId)` and a mirror return variant for hot paths (audio PCM frames at 40+ Hz). Avoids the JSON encode/decode of base64 strings. Slot-in alongside the existing async/sync call entry points without changing the wider protocol.
- **DynCall + `[MonoPInvokeCallback]` fast path** — for hot paths where even the SendMessage hop is too slow (PCM streaming at high sample rates), the C# side can register a static method via `[MonoPInvokeCallback]` whose function pointer JS calls via `Module.dynCall_*`. Out of scope; v0.1 stays on SendMessage as discussed in [webgl-js-to-csharp-callbacks.md](./webgl-js-to-csharp-callbacks.md).
