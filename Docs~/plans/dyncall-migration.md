# DynCall migration — JS→C# bridge delivery

**Status:** Design decision, 2026-06-12
**Supersedes:** the "Decision for the ElevenLabs Unity SDK (v0.1)" section of [webgl-js-to-csharp-callbacks.md](./webgl-js-to-csharp-callbacks.md)
**Implementation tasks:** Phase 2.5 + revised Phase 3 of [generic-bridge-primitives.md](./generic-bridge-primitives.md)

---

## Summary

The JS→C# delivery path for the ElevenLabs Unity WebGL bridge moves from
Unity's `SendMessage` primitive to wasm function pointers invoked via
Emscripten's `{{{ makeDynCall('sig', 'fnVar') }}}` compile-time macro. The
public C# API (`JsBridge` / `JsObject` / `JsFunction` / `BridgeCallback`)
does not change; only the two settlement entry points (`$EL_Settle`,
`$EL_InvokeCallback`) and the matching C# dispatch site change.

Consumers gain a hard requirement: **Player Settings → WebGL → Publishing
Settings → Use WebAssembly.Table** must be enabled. This is enforced by a
build-time `IPreprocessBuildWithReport` that fails the build with the fix
instructions, plus a runtime first-call try/catch as defence-in-depth.

## Why now

The original [research note](./webgl-js-to-csharp-callbacks.md) explicitly
designed the bridge primitives for a cheap future migration to DynCall: the
registry and async machinery are mechanism-agnostic; only the jslib edge and
a single C# entry point per primitive change. The plan was "ship SendMessage
for v0.1, migrate hot paths at v0.3 (audio)."

Three things changed between that decision and this revision:

1. **The legacy `Module.dynCall_*` API is deprecated in Unity 6** in favour
   of the `makeDynCall` macro — see Unity's [deprecated browser-interaction
   APIs page](https://docs.unity3d.com/6000.0/Documentation/Manual/web-interacting-browser-deprecated.html).
2. **Enabling `Use WebAssembly.Table`** (which Unity now recommends for
   new projects) **makes the old `dynCall_*` API unavailable** — see the
   [PlayerSettings WebGL doc](https://docs.unity3d.com/6000.3/Documentation/Manual/class-PlayerSettingsWebGL.html).
   Going straight to `makeDynCall` is forward-compatible; staying on the
   old API would mean a forced rewrite as soon as we (or any consumer)
   want the modern setting on.
3. **We are at the cheapest possible inflection point.** Phase 2 of
   [generic-bridge-primitives.md](./generic-bridge-primitives.md) (the JS
   side) is done with exactly **two** SendMessage call sites; Phase 3 (the
   C# side) has not started. Migrating now costs one JS rewrite plus a
   shift in the not-yet-written C# design. Migrating after Phase 3 ships
   costs the same work plus a teardown of the MonoBehaviour dispatch path,
   the `BridgeMessageParser` `"id:status:payload"` string-encoding, and
   the corresponding Edit-mode tests.

A Unity engineering contact also cited "up-to-500ms latency spikes" for
`SendMessage` as motivation; **that figure is not corroborated by any
Unity documentation we reviewed** and is not part of the justification
here. The case for switching now rests entirely on the deprecation
direction and the cheap-migration-window argument.

## What changes

### Wire protocol

`SendMessage` carries one string argument, so v0.1 string-encoded payloads
as `"promiseId:status:payload"` and `"handle:payload"`. DynCall lets us pass
multiple typed primitives, so the new protocol is:

| Channel | Signature | Args |
|---|---|---|
| Promise settlement (`$EL_Settle`) | `viii` | `promiseId: int`, `statusCode: int` (`0`=ok / `1`=err), `payloadPtr: int` (UTF-8 heap pointer) |
| Callback invocation (`$EL_InvokeCallback`) | `vii` | `handle: int`, `payloadPtr: int` (UTF-8 heap pointer) |

`BridgeMessageParser` becomes unused and is removed. The C# side reads each
payload string via `Marshal.PtrToStringUTF8(payloadPtr)`; the JS side owns
the buffer lifetime and `_free`s it after the synchronous DynCall returns.

### JS side — macro injection at bundle time

The `{{{ makeDynCall('sig', 'fnVar') }}}` macro is an Emscripten compile-time
text substitution, not a runtime function. To keep the TypeScript source
unit-testable in Node, the new bundler step (`Bridge~/build/substitute-make-dyncall.ts`)
is a small Rolldown transform plugin that uses `this.parse(code)` to walk
the AST, locates `CallExpression` nodes whose callee matches `/^dynCall_[vif]+$/`
and whose first argument is an `Identifier` starting with `_EL_`, and
rewrites those calls to the macro form via `MagicString`.

AST-based rewriting (vs a regex or text walker) survives nested calls,
multi-line arguments, comments inside the arg list, and any future format
changes. The plugin lives in its own file with isolated unit tests covering
each shape (plain int args, nested `JSON.stringify(...)`, zero-arg call,
multi-line call, negative case with non-`_EL_` identifier).

### C# side — static callback class, no MonoBehaviour

The `WebGLBridge` MonoBehaviour exists today purely as a `SendMessage`
target. With DynCall the GameObject earns nothing, so it is deleted. The
new entry point is a static `BridgeStaticCallbacks` class with:

- **Non-generic explicit delegate types** declared in-file:
  `internal delegate void SettleCallback(int promiseId, int statusCode, IntPtr payloadPtr);`
  and `internal delegate void InvokeCallback(int handle, IntPtr payloadPtr);`.
  IL2CPP rejects generic delegate types (`Action<...>`) with
  `[MonoPInvokeCallback]` — the marshaller silently emits a trampoline that
  misbehaves under stripping. Non-generic delegates are mandatory.
- **Static delegate fields** holding the references for GC safety. If the
  delegate backing a function pointer is GC'd, calling the pointer crashes
  opaquely. Fields must be static for the lifetime of the bridge.
- **Static methods** decorated with `[AOT.MonoPInvokeCallback(typeof(SettleCallback))]`
  / `[AOT.MonoPInvokeCallback(typeof(InvokeCallback))]`. Each reads the
  payload via `Marshal.PtrToStringUTF8(payloadPtr)` synchronously before
  returning — the IntPtr must never be captured into a continuation, because
  the JS side `_free`s the buffer the instant the DynCall returns.
- **`[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]`
  static initializer** (note: `SubsystemRegistration`, not `BeforeSceneLoad`
  — earlier, so no user code in earlier load phases can call into an
  unregistered callback). The initializer probes for `Use WebAssembly.Table`,
  registers both function pointers, and subscribes `Application.quitting`
  for best-effort teardown.

A `Runtime/WebGL/link.xml` preserves the whole `BridgeStaticCallbacks` type
so IL2CPP's stripping pass doesn't eat the function-pointer-only static
methods on release builds. This bug only surfaces in stripped release
builds — debug-build testing will not catch it.

### `Use WebAssembly.Table` enforcement — two layers

The setting being off is fatal: with WebAssembly.Table disabled, the
`{{{ makeDynCall(...) }}}` macro expands to `getWasmTableEntry(fnVar)(...args)`,
and `getWasmTableEntry` references a `wasmTable` that wasn't exported —
runtime `ReferenceError` on the *first* DynCall, **not a link-time error**.
A single runtime probe is also unreliable because Unity invokes Emscripten
with Closure Compiler enabled on release builds, which can rename
`getWasmTableEntry` and even `wasmTable` itself.

**Layer 1 (primary): build-time gate.** `Editor/BridgeBuildPreprocessor.cs`
implements `IPreprocessBuildWithReport`; on WebGL builds it reads
`PlayerSettings.WebGL.useWasmTable` and throws `BuildFailedException` with
the fix instructions if the setting is off. Editor-only code, no runtime
cost, no Closure renaming concerns.

**Layer 2 (defence-in-depth): runtime probe + first-call try/catch.** The
runtime probe checks `typeof Module.wasmTable !== "undefined"` and
**warns** (`BridgeLog.Warn`) rather than throws — a false negative under
Closure renaming should not abort the bridge. The authoritative runtime
signal is the first `EL_SetSettleCallback` DynCall itself, wrapped in a
`catch (Exception ex) when (IsReferenceError(ex))` that throws
`BridgeException` with the same setup instructions.

## Consumer-impact risk

Enabling `Use WebAssembly.Table` is incompatible with any other `.jslib`
that still uses the deprecated `Module.dynCall_*` API. A consumer shipping
such a plug-in alongside our SDK would be forced to either migrate their
other plug-in or drop ours — a real blocking position.

**Decision: ship DynCall as a hard requirement.** Rationale: `dynCall_*` is
deprecated in Unity 6 and the right migration direction for any consumer
regardless of our SDK; we won't add speculative complexity for holdouts.

**Mitigation: the SendMessage code paths are preserved in git history.**
The migration lands as one commit per implementation task (per Phase 2.5 and
revised Phase 3 of [generic-bridge-primitives.md](./generic-bridge-primitives.md));
the deleted/rewritten files stay reachable via `git log -p` /
`git show <commit>`. If a real consumer hits the blocker, reintroducing a
SendMessage fallback is a known-shape patch built from those commits rather
than a from-scratch redesign. **The migration PR must not squash** — the
per-task commit granularity is the mitigation.

A public GitHub Discussion tracks the contingency and the reactivation path;
consumers hitting the issue are invited to comment there.

## Risks acknowledged

- **IL2CPP stripping** of function-pointer-only static methods (mitigated by `link.xml` — release-build-only bug, debug-build testing will miss it).
- **`Application.quitting` is best-effort in WebGL** — tab close, page navigation, and crash all skip it. Acceptable because the WebAssembly heap dies with the page.
- **`Marshal.PtrToStringUTF8` requires .NET Standard 2.1.** Unity 2023.1+ default; a project forced to .NET Standard 2.0 will hit `MissingMethodException`. Documented as a package requirement.
- **Closure renaming of Emscripten internals** breaks naive runtime probes — mitigated by the two-layer check above.
- **Per-callback memory churn:** every `$EL_InvokeCallback` now does `stringToNewUTF8` + DynCall + `_free`. Negligible at the control-plane rates Plan B expects (10–100/sec). The v0.3 audio path uses binary-payload DynCall variants that bypass the string allocation entirely; the higher rates land on a different code path by design.

## What does not change

- The public C# API of `JsBridge`, `JsObject`, `JsFunction`, `BridgeCallback` — same method shapes, same return-shape codes, same per-call `<T>`-driven dispatch.
- The wire protocol's `$ref` / `$fn` / `$cb` marker conventions, the return-shape codes, the registry-and-marker machinery on either side of the bridge.
- The Phase 2 JS registries (`$EL_Objects`, `$EL_Functions`, `$EL_Factories`) and the connection-bundle factories in `ElevenLabsConnection.jslib`. Only `ElevenLabsBridge.jslib` is regenerated.
- The single-threaded synchronous-call assumption from the original research note. DynCall is a synchronous wasm function call by construction, so the "SendMessage timing — assumed synchronous" caveat in `generic-bridge-primitives.md`'s architecture-decisions section no longer applies and is rewritten as part of the Phase 3 documentation work.
