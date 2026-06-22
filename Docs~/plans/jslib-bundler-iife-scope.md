# `.jslib` bundler IIFE scope leakage + smoke-test coverage gap

**Status:** Discovered 2026-06-22 while wiring an external consumer demo (Getting Started Unity project) against the SDK as a `file:` package. Two narrow fixes shipped in the same session; the architectural fix described here remains.
**Driver:** First end-to-end test of `Conversation.StartSessionAsync` from a consumer project (not the in-tree smoke test) surfaced three SDK stripping issues. The third is structural and needs design.
**Related plans:** [generic-bridge-primitives.md](./generic-bridge-primitives.md) (overall bridge design), [dyncall-migration.md](./dyncall-migration.md) (Emscripten interop choices), [client-sdk-exports-for-plan-b.md](./client-sdk-exports-for-plan-b.md) (what we re-export from `@elevenlabs/client/internal/unity`).

---

## Summary

`Bridge~/build/bundle-jslib.ts` wraps each .jslib's TypeScript sources in a Rolldown IIFE (`var library = (function () { /* SDK classes + factory closures + side effects */ return library; })(); mergeInto(LibraryManager.library, library);`). Emscripten's library evaluation runs the IIFE at build time to populate `LibraryManager.library`, then emits the runtime by serializing only the library entries it can statically reach. The IIFE body — including the bundled `@elevenlabs/client/internal/unity` classes that the factory closures close over — is **not** emitted into the runtime. Factory closures arrive at the browser with `WebSocketConnection`, `WebRTCConnection`, etc. all `undefined`, and the first `StartSessionAsync` call fails with `WebSocketConnection is not defined`.

This is invisible in CI because the conversation smoke test's `[ConvSmoke] CONFIG MISSING` skip path returns success before exercising the failing code, and the harness treats that skip as a pass.

## Why we noticed

The Getting Started visual demo wires a `TalkingBox` MonoBehaviour into a Unity 6 starter project that consumes the SDK as a local `file:` package. None of the in-tree samples (`Samples/BridgeSmokeTest`, `Samples/ConversationSmokeTest`) hit the same stripping behaviour because their asmdefs explicitly reference `ElevenLabs.Agents.WebGL`. The Getting Started project doesn't, so the consumer-side path exposed three layered issues at once:

1. `BridgedSessionLauncher` getting stripped because nothing in the consumer reference graph reaches it.
2. `$EL_ConnectionFactories` / `$EL_AudioGlueFactories` getting stripped by Emscripten because no library entry depended on them.
3. The IIFE-scope leak described above — present in every build, including the in-tree smoke test, but masked by the CONFIG-MISSING skip.

Fixes (1) and (2) shipped this session. Fix (3) is what this plan covers.

## Evidence (what we measured)

In a freshly-built `Getting Started` WebGL build (post-fixes 1 & 2) and a freshly-built `TestProject/Build/WebGLConversationSmoke/`:

```
brotli -dc Web.framework.js.br | grep -c "class WebSocketConnection" → 0
brotli -dc Web.framework.js.br | grep -c "setWebRTCAudioAdapterFactory" → 0
brotli -dc Web.framework.js.br | grep -c "createWebSocketConnection"  → 1
```

That last `1` is the serialized factory closure `(config) => WebSocketConnection.create(config)`. The closure is in the output; the class it closes over is not. The IIFE side-effects (`setWebRTCAudioAdapterFactory(() => new WebAudioAdapter())`, `installIosAudioUnlockListener()`) are also gone, so the WebRTC adapter is never installed and iOS audio unlock never arms.

Decompressed `Web.framework.js` is ~415 KB; the bundled `ElevenLabsConnection.jslib` is ~23,500 lines. Most of the bundle never reaches the browser.

## Root cause

Two interacting design choices:

1. **`Bridge~/build/bundle-jslib.ts` uses Rolldown `format: "iife"`.** All bundled code — including SDK class definitions, helper functions, and the side-effectful imports from `@elevenlabs/client/internal/unity` — lives inside a single `(function () { ... })()` wrapper. The library object returned from the IIFE captures these via lexical scope.
2. **Emscripten library evaluation only preserves declared library entries.** When the .jslib file evaluates at build time, the IIFE runs and `mergeInto(LibraryManager.library, ...)` registers the entries. The runtime JS is then synthesized from `LibraryManager.library`: each entry's body is serialized (functions via `.toString()`, objects via inline JSON-ish emission). The IIFE wrapper itself is **not** copied into the output, so lexical references break and side-effects vanish.

The primitives `.jslib` (`Bridge~/src/primitives/`) doesn't hit this because every primitive function is self-contained (math, registry lookups, JSON parse/emit). The connection `.jslib` is the first one whose entries close over external SDK code, and that's what exposes the architectural mismatch.

## Proposed fixes

Three options, ordered by invasiveness. Pick one; do not stack.

### Option A — Promote SDK classes to library variables (recommended)

Stop wrapping in an IIFE. Bundle the connection module so that the SDK classes Rolldown pulls in are explicit library entries with the `$` prefix Emscripten reserves for non-function library values:

```ts
mergeInto(LibraryManager.library, {
  $WebSocketConnection: WebSocketConnection,
  $WebRTCConnection: WebRTCConnection,
  $createConnection: createConnection,
  $MediaDeviceInput: MediaDeviceInput,
  $MediaDeviceOutput: MediaDeviceOutput,

  $EL_ConnectionFactories__deps: [
    "$WebSocketConnection", "$WebRTCConnection", "$createConnection",
    "$MediaDeviceInput", "$MediaDeviceOutput", "$EL_RegisterFactory",
  ],
  $EL_ConnectionFactories: {
    createWebSocketConnection: (config) => WebSocketConnection.create(config),
    /* … */
  },
  $EL_ConnectionFactories__postset: "Object.keys(EL_ConnectionFactories).forEach(function(k){EL_RegisterFactory(k,EL_ConnectionFactories[k]);});",

  // Side effects need an explicit hook instead of free-floating IIFE statements.
  $EL_ConnectionInit__deps: ["$WebRTCConnection"],
  $EL_ConnectionInit__postset:
    "setWebRTCAudioAdapterFactory(() => new WebAudioAdapter()); if (typeof navigator !== 'undefined') installIosAudioUnlockListener();",
});
```

Now the SDK classes are runtime-emitted as top-level vars (Emscripten emits `var WebSocketConnection = …;`), and the factory closures resolve correctly. Side-effects move into `__postset` strings.

Trade-off: requires updating `Bridge~/build/bundle-jslib.ts` to drop the IIFE wrap (use Rolldown's plugin API to transform the bundle into a `mergeInto`-friendly shape, OR shell out to a custom emitter). Also requires `Bridge~/src/connection/` to expose the bundled symbols differently. Touches the verify scripts (`pnpm verify:connection`) and the e2e test that imports `$EL_ConnectionFactories` from the source module (currently `factories.test.ts:37`, `e2e.test.ts:44`).

### Option B — `--pre-js` preamble

Keep the IIFE for what it's good at (bundling) but emit a separate `*.jspre` file containing the SDK class definitions and side-effects. Unity's WebGL build picks `.jspre` files up from `Plugins/WebGL/` and prepends them to the runtime, so they're always present. The factory closures then reference globally-available symbols.

Trade-off: two output artifacts per logical bundle, divergent naming convention from upstream Emscripten ecosystem (`.jspre` is Unity-specific), and the `.jslib` still has to declare imports that match what the `.jspre` provides — risking drift.

### Option C — Inline-string everything

Move the entire connection registration into a `__postset` string. Use Rolldown to bundle the SDK into a single self-contained JS string and embed it verbatim in the postset. Brutalist but works because `__postset` strings are emitted verbatim.

Trade-off: TypeScript-authored code loses tooling support inside the string, sourcemaps die, future migrations get harder. Reject unless A and B both prove infeasible.

## Smoke-test coverage gap

The conversation smoke test (`Samples/ConversationSmokeTest`) and its harness at `IntegrationTests~/src/conversation-smoke.test.ts` are wired correctly, but the failure mode `[ConvSmoke] CONFIG MISSING` (no `ConversationSmokeConfig.asset` in `Assets/Resources/`) is treated as a green-but-no-op pass to keep clean clones from breaking CI. The result: the session-open path is never exercised in CI, and the IIFE scope bug went undetected for the lifetime of the connection `.jslib`.

Two complementary mitigations:

1. **Add a CI job that provisions a real (low-quota) agent ID** from a repo secret. Materialize `TestProject/Assets/Resources/ConversationSmokeConfig.asset` in a pre-build step (the asset shape is a standard Unity `.asset` YAML). Already documented as a follow-up in `Samples/ConversationSmokeTest/README.md`.
2. **Add a static-shape smoke test** that doesn't need a real agent: build the WebGL conversation artifact, then in the Vitest harness, parse the decompressed `Web.framework.js` and assert specific symbols are present — at minimum `class WebSocketConnection` and `setWebRTCAudioAdapterFactory`. This is fast (no real network round-trip, no API quota) and catches stripping regressions without an ElevenLabs agent.

## What was fixed in this session (not this plan)

For provenance, the two fixes that shipped today and are required as preconditions for any future test of the IIFE fix:

1. **`Runtime/WebGL/AssemblyInfo.cs`** (new): `[assembly: AlwaysLinkAssembly]` on `ElevenLabs.Agents.WebGL`. Without it, IL2CPP drops the whole assembly from consumer builds whose asmdefs don't directly reference it. `link.xml` preservations only apply to assemblies the linker decided to keep.
2. **`Runtime/WebGL/link.xml`**: extended to preserve `BridgedSessionLauncher` alongside the pre-existing `BridgeStaticCallbacks` entry. The launcher's `[RuntimeInitializeOnLoadMethod]` would otherwise be stripped at IL2CPP time and `Conversation.SessionFactory` would stay `null`.
3. **`Runtime/WebGL/ElevenLabsBridgeNative.cs` + `BridgedSessionLauncher.cs` + `Bridge~/src/connection/factories.ts`**: a no-op `EL_EnsureConnectionFactoriesLoaded` DllImport called from the launcher. Its sole purpose is to give Emscripten a reachable reference to `$EL_ConnectionFactories` and `$EL_AudioGlueFactories`, so their `__postset` strings actually run at startup. Without it, the postsets are stripped along with the unreferenced library variables. This fix is necessary even after the Option-A bundler change — the new library variables would still need a reachable entry point unless they happen to be transitively reached by other library entries.

## Tasks

- [ ] **T1.** Add a stripping-regression test under `IntegrationTests~/` that parses the conversation-smoke `Web.framework.js` and asserts on at least: `class WebSocketConnection`, `setWebRTCAudioAdapterFactory`, `EL_ConnectionFactories`. Wire into the existing matrix.
- [ ] **T2.** Decide between Option A and Option B; spike the chosen approach in `Bridge~/build/`.
- [ ] **T3.** Update `Bridge~/src/connection/` (and `factories.test.ts` / `e2e.test.ts`) to match the new export shape from T2.
- [ ] **T4.** Once T1–T3 land, drop `EL_EnsureConnectionFactoriesLoaded` if the bundler change makes it redundant. (May still be needed depending on how library-variable reachability shakes out.)
- [ ] **T5.** Provision a CI secret-backed real-agent smoke run (documented in `Samples/ConversationSmokeTest/README.md`). Optional but valuable as defence-in-depth.

## Post-fix symptom: `source is not defined` (2026-06-22, after 14d7601)

After `14d7601 fix(webgl): emit connection jslib as a postset to escape IIFE scope leak` landed, the Getting Started demo gets further but hits a new error on every box-trigger entry:

```
[TalkingBox:01_Box_Yellow] StartSessionAsync failed: source is not defined
```

**Initial hypothesis (wrong).** The surrounding code in the bundled SDK includes a `const source = context.createMediaStreamSource(inputStream);` inside `MediaDeviceInput.create`, and the literal "source" appears in several nested closures, so the obvious guess was that postset emission had broken closure capture in the mic-input pipeline.

**Actual root cause** (captured 2026-06-22 by hooking `Promise.prototype.then` in a Playwright session against the Getting Started build):

```
ReferenceError: source is not defined
  at WebSocketConnection.create (Web.framework.js:9:47885)
  at createWebSocketConnection
  at _EL_InvokeFactoryAsync
```

The bundle source for `WebSocketConnection.create` is:

```js
let url;
const { name: source, version } = sourceInfo;
if (config.signedUrl) {
  const separator = config.signedUrl.includes("?") ? "&" : "?";
  url = `${config.signedUrl}${separator}source=${source}&version=${version}`;
} else url = `${origin}${WSS_API_PATHNAME}${config.agentId}&source=${source}&version=${version}`;
```

In the built `Web.framework.js` the destructuring declaration is **gone**, but the `${source}` / `${version}` template-literal references remain. Dangling references → `ReferenceError`.

Reproduced in isolation against Unity's bundled `tools/acorn-optimizer.js` (Emscripten ships acorn 8.7.1, terser 5.16.6). The JSDCE pass deletes:

- `const { x, y } = obj;` (shorthand)
- `const { a: x, b: y } = obj;` (renamed)
- `const [x, y] = arr;` (array)

…and keeps:

- `const x = obj.a;` (plain identifier id)
- `const { x } = sideEffect();` (RHS visibly side-effectful)

**The bug** is in [`runJSDCE`'s `VariableDeclarator` walker](https://github.com/emscripten-core/emscripten/blob/main/tools/acorn-optimizer.js#L392):

```js
VariableDeclarator(node, c) {
  const name = node.id.name;                     // undefined for ObjectPattern/ArrayPattern
  ensureData(scopes[scopes.length - 1], name).def = 1;
  if (node.init) c(node.init);
},
```

For destructuring, `node.id` is an `ObjectPattern` / `ArrayPattern` (no `.name`). The destructured identifiers are never visited (the walker doesn't recurse into `node.id`) and never recorded as `def`. In cleanup, the declaration ends up filed under the literal string key `"undefined"`, gets marked eliminateable, and is dropped — but the references in the function body remain because they DID get recorded as `use`.

Affected sites in the current connection bundle: `WebSocketConnection.create`, `WebRTCConnection.create` (both block session start today), plus two livekit-client `sortPresets` declarations (dormant until called).

**Fix that shipped** (commit 5241c66): a `lowerDestructuring()` Rolldown plugin in `Bridge~/build/lower-destructuring.ts`, wired into postset mode only. Walks the bundle AST after Rolldown finishes; rewrites every `const`/`let`/`var` whose declarator is an `ObjectPattern`/`ArrayPattern` with an `Identifier` RHS and only-Identifier bindings into plain `const x = obj.x, y = obj.y;` form. Anything fancier (defaults, rest, computed keys, nested patterns, non-Identifier RHS, multi-declarator) is left alone — the bundle ships verbatim and may still hit Emscripten's bug, but none of those shapes appear in the SDK or our own sources today. The plugin mirrors the existing `substitute-make-dyncall` Rolldown plugin and adds no new dependencies (acorn is already in Rolldown's tree).

**Why not just lower Rolldown's `transform.target`.** Tested: Rolldown's target floor is `es2015`, which is the same level destructuring became native syntax. There's no Rolldown-internal way to lower it out. Adding `oxc-transform` or `@babel/*` for one transform was disproportionate.

**Why this fix may not be sufficient long-term.** Emscripten's `acorn-optimizer.js` walker is wrong for *any* AST shape it parses but doesn't recurse into — destructuring is the one we caught because the SDK uses it heavily, but the same bug class could fire on patterns we haven't hit yet (rest parameters in arrow functions, exotic class field initializers, etc.). If a future SDK bump introduces a new pattern that JSDCE strips, the fix belongs in the same plugin. The static-shape regression test (T1) is the right tripwire: extend it with declaration-presence checks for whatever new SDK API we depend on, so the failure mode is "this exact symbol disappeared from framework.js" rather than a runtime `ReferenceError` no one expects.

### Upstream status — the bug is already fixed in Emscripten

After landing the workaround, we checked whether the underlying Emscripten bug had been reported and addressed upstream. It has been:

- **Issue:** [emscripten-core/emscripten#20393](https://github.com/emscripten-core/emscripten/issues/20393), "Invalid Acorn optimization", filed Sept 2023 against Emscripten 3.1.45 by @hoodmane (Pyodide). Same root cause: JSDCE lacks `ObjectPattern`/`ArrayPattern` support in `VariableDeclarator`.
- **Fix:** [PR #20402](https://github.com/emscripten-core/emscripten/pull/20402), "JSDCE: Add support for ObjectPattern declarations", merged Oct 5, 2023 as commit `db199c4`. The buggy `node.id.name` read was replaced with a `walkPattern(node.id, c, callback)` helper that handles `ObjectPattern`, `ArrayPattern`, and `AssignmentPattern`. (PR initially covered only ObjectPattern; ArrayPattern was added during review when a test caught it — exactly the breadth of our workaround.)
- **First Emscripten release with the fix:** **3.1.47** (Oct 9, 2023).

Unity's bundled Emscripten versions, for reference:

| Unity version           | Emscripten          | Workaround needed? |
| ----------------------- | ------------------- | ------------------ |
| 6.0 (6000.0)            | 3.1.38              | yes                |
| 6.3 (6000.3) — current  | 3.1.39-git          | **yes** (us)       |
| 6.4 (6000.4)            | (undocumented 3.1.x)| likely yes         |
| 6.5 (6000.5) — released | **4.0.19-unity**    | **no**             |
| 6.6 (6000.6) alpha      | 4.0.19+             | no                 |

Unity 6.5 bumped from Emscripten 3.x to 4.x in [the documented 6.5 release notes](https://docs.unity3d.com/6000.5/Documentation/Manual/WhatsNewUnity65.html) — "*Unity 6.5 and later uses the Emscripten compiler version `4.0.19-unity` to compile Unity Web Player builds*". Consumers on Unity 6.5+ get the fix automatically and don't need our workaround.

**Drop-when condition for this plugin:** when the SDK's minimum supported Unity moves to ≥6.5 (i.e. Emscripten ≥4.0.19, comfortably past the 3.1.47 fix), `lower-destructuring.ts` and its test can be deleted, the postset-mode plugin list in `bundle-jslib.ts` can drop back to `[substituteMakeDyncall()]`, the `Plugins/WebGL/ElevenLabsConnection.jslib` regenerates without the rewrites, and `conversation-shape.test.ts` accepts the original destructuring form (the test already does — `hasDestructuring || hasLowered`). Track this against whatever drives the Unity-minimum decision (Plan B sequencing, samples policy, customer commitments).

**Outstanding ask of Unity** (for the 6.3 LTS gap): backporting Emscripten 3.1.47+ into a `6000.3.x` patch would fix every consumer stuck on the current LTS without forcing them to a major Unity bump. The ask is unusually small because (a) Unity already validated a much larger Emscripten jump for 6.5, so cherry-picking PR #20402 alone is a tiny delta against the 3.1.39 baseline, and (b) it's a strict bug-fix patch — no behavioural surface change for existing consumers.

Self-contained writeup intended for a Unity-contact email (standalone repro, before/after stripping table, walker source, version matrix, suggested resolution) — does not reference this repo or our consumer-side pain so it can be forwarded freely: https://gist.github.com/kraenhansen/917d822cbbda818d27095ea80a470bf7.

## Open questions

- Should `lowerDestructuring` also handle `MemberExpression` RHS (with a temp binding to avoid duplicating the chain traversal)? No production site needs it today; revisit if a SDK bump introduces one.
- Is there a way to disable JSDCE on Unity's WebGL build without paying the framework.js size cost? `--no-js-opts` is one possible escape hatch — would let us delete this plugin entirely but the bundle would grow. Not worth chasing until the next quirk forces it.
- The cost of Option A vs B (above) should also weigh against eventual native (Plan B: non-WebGL) parity — if the native path uses a totally different transport, the Emscripten complexity here only matters for the WebGL phase.
