# Research: JS-to-C# callback mechanisms in Unity WebGL

**Status:** Research note  
**Purpose:** Capture the comparison of `SendMessage` vs `DynCall` + `MonoPInvokeCallback` for future architecture decisions

---

## Context

Unity WebGL builds run a single-threaded WebAssembly module compiled via Emscripten + IL2CPP. C# code can call JavaScript synchronously via `[DllImport("__Internal")]`, but the reverse — JavaScript calling into C# — has two distinct mechanisms with very different characteristics. Choosing between them is a foundational decision because the choice ripples through registry design, async patterns, and debugging ergonomics.

This note captures the comparison so future decisions (e.g. moving from a control-plane bridge to a streaming/audio path) can be made on the same evidence base.

---

## The two mechanisms

### 1. `SendMessage`

JavaScript calls `unityInstance.SendMessage(gameObjectName, methodName, singleArg)`. The argument is a single string, number, or null. The target must be a public instance method on a MonoBehaviour attached to a named GameObject in the scene. No return value.

Available since Unity 5.x (introduction of the WebGL platform). Considered the "default" JS→C# path.

### 2. `DynCall` + `[MonoPInvokeCallback]`

JavaScript calls a wasm function pointer directly: `Module.dynCall_vi(funcPtr, arg)` (signature `v` = void return, `i` = int arg). The pointer targets a static C# method decorated with `[MonoPInvokeCallback(typeof(SomeDelegate))]`, obtained at startup via `Marshal.GetFunctionPointerForDelegate`.

DynCall is an Emscripten primitive; `MonoPInvokeCallback` is a Unity attribute that became foundational with IL2CPP AOT (Unity 5.4+). Both are stable, neither is deprecated.

---

## Comparison

| Dimension                | `SendMessage`                                                                            | `DynCall` + `MonoPInvokeCallback`                                                                                                     |
| ------------------------ | ---------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------- |
| **Latency per call**     | ~200x slower (still <1ms; reflection + GameObject lookup)                                | Direct wasm function pointer call                                                                                                     |
| **GC allocations**       | String allocation on each call + reflection overhead                                     | Zero for primitives; heap alloc + UTF-8 marshal for strings                                                                           |
| **Arguments**            | One: string, number, or null                                                             | Multiple typed primitives (`i`/`f`/`d`) and pointers                                                                                  |
| **Return values**        | None (fire-and-forget)                                                                   | Supported (first char of signature)                                                                                                   |
| **Method type**          | Public instance method on a named GameObject MonoBehaviour                               | Static method only (IL2CPP AOT constraint)                                                                                            |
| **String passing**       | Native — string crosses the boundary as-is                                               | Manual: `_malloc` + `stringToUTF8` (C#→JS) or `UTF8ToString` (JS→C#)                                                                  |
| **Boilerplate**          | None — name the GameObject and method                                                    | `[MonoPInvokeCallback]` attribute, delegate type declarations, function pointer registration at startup, delegate lifetime management |
| **Error handling**       | C# exceptions caught silently; logged to console                                         | C# exceptions caught at IL2CPP boundary; logged but not propagated                                                                    |
| **Failure mode**         | `SendMessage: object X not found!` / `does not have receiver`; silent drop               | Cryptic linker errors at build time if specialization missing; null pointer crash at runtime if delegate GC'd                         |
| **Debugging**            | Clear stack traces, console-testable (`unityInstance.SendMessage(...)`), readable errors | Opaque function pointers, harder to test from console, requires logging pointer at startup                                            |
| **IL2CPP compatibility** | Full                                                                                     | Required (WebGL always uses IL2CPP); generic specializations may need explicit attributes                                             |
| **Editor compatibility** | Works in Editor with Mono                                                                | WebGL-only path; needs `#if UNITY_WEBGL && !UNITY_EDITOR` gating                                                                      |
| **Stability outlook**    | Stable, not deprecated                                                                   | Stable, not deprecated; possible future evolution via WebAssembly function references                                                 |

---

## When each one wins

### `SendMessage` is the right choice when:

- Call rate is low (< ~10–100/sec): the latency difference doesn't show up
- Payloads are single strings: SendMessage's native string passing is simpler than DynCall's manual marshaling
- Developer ergonomics matter: clearer debugging, easier console testing, less ceremony
- A central registry/dispatch pattern fits the design: one `OnXxx` handler per category, ID-keyed lookup inside
- The team is new to Unity WebGL interop: lower onboarding cost

### `DynCall` + `MonoPInvokeCallback` is the right choice when:

- Call rate is high (> ~100/sec): audio frames, video texture updates, high-frequency event streams
- Payloads are primitive or binary: avoids the JSON round-trip
- Per-call function pointers fit naturally: each callback has its own pointer rather than dispatching through a central handler
- Return values are needed: SendMessage can't return; DynCall can
- The architecture already has a JS object proxy model: function pointers stored on JS-side objects map cleanly to dynCall

---

## Failure modes worth knowing

**Both mechanisms drop C# exceptions silently.** The runtime catches them at the language boundary and logs to console, but neither propagates to JS. Any reliable error reporting needs to be done in-band — typically by serializing errors into the payload (`id:err:message`) and resolving the JS Promise with rejection on that signal.

**`SendMessage` has a startup race**: calling it within `createUnityInstance().then(...)` may fail because the target GameObject doesn't exist yet. Fix: use `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]` to ensure the bridge GameObject exists as early as possible.

**`SendMessage` crashes on very large strings** (>1MB empirically; documented Unity Issue Tracker entry). Binary payloads should never go through SendMessage's string channel.

**`DynCall` delegate lifetime**: if the delegate backing a function pointer is GC'd, calling the pointer crashes with no clear error. Delegates must be held in static fields for the lifetime of the bridge.

**`DynCall` generic specializations**: IL2CPP generates code per `<T>` instantiation. If a needed specialization isn't reachable via `[MonoPInvokeCallback]` attributes, the linker fails at build time. Multiple attributes can declare multiple specializations.

---

## Real-world usage patterns

- `SendMessage` dominates general-purpose Unity WebGL bridges. The default React/JS integration libraries use it almost exclusively.
- `DynCall` shows up in performance-critical paths: production WebRTC SDKs, WebSocket bridges with high event rates, audio pipelines, video texture updates.
- Hybrid approaches are common: `SendMessage` for control plane, `DynCall` for hot paths.

---

## Migration cost

A bridge designed around a central registry/dispatch pattern (one `OnXxx` handler per category, ID-keyed lookup) is mechanism-agnostic: the registry and async machinery stay the same; only the delivery layer changes. Migrating a specific primitive from `SendMessage` to `DynCall` is local — the public API doesn't change, the registries don't change, only the jslib and a single C# entry point change.

This means "start with `SendMessage`, migrate hot paths later" is a low-risk strategy _if_ the registry/dispatch layer is genuinely delivery-mechanism-agnostic from day one.

---

## Decision for the ElevenLabs Unity SDK (v0.1)

**Adopt `SendMessage` for all three bridge primitives.** Reasoning:

- Control-plane call rates are well below where the latency difference matters (~1–10/sec for promises, observers, requests)
- Native string passing fits the JSON payload model without manual marshaling
- Debugging is significantly better during early development
- The registry/dispatch layer is mechanism-agnostic — a future swap is localized

**Plan to revisit at v0.3 (audio path).** PCM frames at 48 kHz require either DynCall + heap pointers or a Worklet-based path that bypasses the bridge entirely. This is a known trigger, not a "profile and decide" — the rate is high enough that DynCall is essentially mandatory.

The bridge primitives are designed so this future migration touches only the jslib and a single C# entry point per primitive, not the public API or the registries.
