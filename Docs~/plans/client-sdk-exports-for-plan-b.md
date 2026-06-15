# `@elevenlabs/client` exports needed for the Unity SDK (Plan B)

> **Status: ✅ Complete (2026-06-15).** Upstream PR
> [elevenlabs/packages#835](https://github.com/elevenlabs/packages/pull/835)
> shipped in `@elevenlabs/client@1.11.0` adding the `./internal/unity`
> sub-path export. Unity-side cleanup landed: `factories.ts` imports from
> `@elevenlabs/client/internal/unity` (with the three named config-type
> aliases), `Bridge~/src/connection/platform-web.d.ts` is deleted, the
> `resolve.alias` block is gone from `Bridge~/vitest.config.ts`, the
> `deepImportAliases` block is gone from `Bridge~/build/bundle-jslib.ts`,
> and `audio-glue.ts` composes `withoutAudioPayload` + `attachDefaultAudio`
> over the SDK's `attachInputToConnection` / `attachConnectionToOutput`.
> Document retained as the record of the upstream surface contract and the
> rationale for `./internal/unity` as the entrypoint name.

> **Audience.** An agent working in the `elevenlabs/packages` monorepo
> (`packages/client/`). The author of this document is the Unity SDK
> (`elevenlabs/unity`), maintained by the same person who
> maintains the client SDK. Treat this as a work order.

## Scope

This PR exposes everything the Unity SDK's WebGL bridge needs from
`@elevenlabs/client` across **all phases of [Plan B](https://github.com/elevenlabs/unity/blob/main/Docs~/plans/plan-b.md)**
(v0.1 critical path *and* v0.3 Unity-routed audio). The intent is one PR,
one bump, then no more "expose this please" round-trips.

The deliverable is **pure surface change — no new behavior in the SDK.**
Specifically:

1. A new `./internal/unity` sub-path export aggregating seven existing
   symbols (five runtime values + two types) and three new ones (zero
   runtime values + three types), plus a strict discipline: only
   re-export what the Unity bridge actually consumes at runtime or
   names at the type level — no speculative surface.
2. Three new named config-type aliases that make `MediaDevice*.create()`
   and `WebRTCConnection.create()` call sites tractable from downstream
   code. These are pure renames/extractions of existing anonymous
   intersections; no runtime behavior changes.

Composition and filtering (the audio-payload-stripping wrapper, the
`attachDefaultAudio` factory) live on the Unity side, not in the SDK.
Rationale in step 4 ("No new helper files") below.

After it ships, the Unity SDK deletes its three deep-import workarounds
(ambient `.d.ts`, Vite alias, Rolldown alias) in a single follow-up commit.

---

## Why this matters (context)

The Unity SDK ships a single C# `Conversation` API that runs on Desktop,
Mobile, XR (native) and WebGL (browser). On WebGL the C# `Conversation`
delegates to the client SDK via Emscripten jslib — but **not** by wrapping
`Conversation` / `VoiceConversation`. Instead, the C# layer plays the role
of `Conversation` itself, and drives three smaller objects on the JS side:

```
C# Conversation
  ├── IConnection            ← BridgedWebSocketConnection / BridgedWebRTCConnection
  │                            wraps WebSocketConnection / WebRTCConnection
  ├── IInputController       ← BridgedInputController
  │                            wraps MediaDeviceInput
  └── IOutputController      ← BridgedOutputController
                               wraps MediaDeviceOutput
```

The four classes are already factored out cleanly. They just need to be
reachable through `package.json#exports` so the Unity bridge can `import`
them without escape hatches.

Plan and rationale: [Plan B — Bridge into the connection + I/O controllers,
not the Conversation](https://github.com/elevenlabs/unity/blob/main/Docs~/plans/plan-b.md).

---

## Why `./internal/unity` (and not a flat name)

Three alternatives were considered and rejected:

- **Extend the existing flat `./internal`.** Real bloat risk — `./internal`
  aggregates scribe setup, location parsers, source-info hooks, etc., and
  the package doesn't declare `"sideEffects": false`, so a bundler asked
  to import one symbol may conservatively include upstream side-effecty
  modules (scribe code the Unity bundle doesn't need).
- **Generic flat name like `./bridge`.** Too ambiguous for an external
  reader — "bridge" doesn't signal who this is for, nor that the surface
  is stability-tied to a specific consumer.
- **Flat consumer name like `./unity`.** Names the consumer well, but
  reads like the export *is* the Unity SDK (it's not — it's pure
  `@elevenlabs/client` code that the Unity SDK happens to consume), and
  promotes the surface to a top-level entrypoint that implies the same
  stability contract as `.`.

`./internal/unity` does all three jobs at once:

- **Inherits the `/internal` "no semver guarantees" signal** by
  convention — anything under `/internal/...` is understood to be
  shape-stable only for the SDK's own use and named consumers.
- **Names the consumer** without claiming the export *is* that consumer.
  A reader of `from "@elevenlabs/client/internal/unity"` correctly
  reads "internal SDK surface shaped for Unity SDK consumption".
- **Lives in its own file** — guaranteed-minimal export surface, no
  bundler risk of pulling unrelated SDK code into the Unity bundle.

If a future second bridge consumer (React Native, another game engine)
needs similar shape, add a parallel sub-path
(`./internal/react-native`, `./internal/godot`, etc.). Reversible.

---

## Changes to make

### 1. `package.json` — add `./internal/unity` to `exports`

```jsonc
"exports": {
  ".":                { /* unchanged */ },
  "./internal":       "./dist/internal.js",
  "./internal/unity": {
    "types":   "./dist/internal/unity.d.ts",
    "default": "./dist/internal/unity.js"
  }
}
```

> Layout note: the entrypoint is a **flat file**
> (`src/internal/unity.ts` → `dist/internal/unity.js`), not a directory.
> The aggregator only re-exports symbols that live in `src/utils/`,
> `src/platform/web/`, etc. — there is no Unity-specific source code
> living next to it that would justify a directory.

### 2. `src/internal/unity.ts` — the new entrypoint

> **Discipline.** Every export below is either a runtime value
> (`export { ... }`) **or** a type-only re-export (`export type { ... }`)
> — never accidentally promoting a type to a runtime symbol. Type-only
> re-exports get erased by `tsc` and never reach the consumer's bundle,
> which matters here because the Unity bridge runs in a size-sensitive
> WebGL build.

```ts
// ── Runtime values ────────────────────────────────────────────────────

// Concrete platform classes the Unity bridge instantiates from C#.
// (Unity calls .create() on these from its JS factory glue.)
export { MediaDeviceInput  } from "../platform/web/input.js";
export { MediaDeviceOutput } from "../platform/web/output.js";

// I/O ↔ connection wiring primitives. Unity's audio-glue.ts composes
// these (plus a Unity-local audio-payload-stripping wrapper) into its
// own single `attachDefaultAudio` factory exposed to C# — the SDK does
// not ship that composition.
export { attachInputToConnection  } from "../utils/attachInputToConnection.js";
export { attachConnectionToOutput } from "../utils/attachConnectionToOutput.js";

// v0.3: Unity-routed audio adapter slot. Already exported from ./internal —
// re-exported here so all Unity-consumed symbols live behind one import.
export { setWebRTCAudioAdapterFactory } from "../WebRTCAudioAdapter.js";

// ── Types only (use `export type` so tsc erases at compile time) ──────

// Adapter slot's interface + result shape. v0.3 Unity-routed audio.
export type { WebRTCAudioAdapter, AnalysisResult } from "../WebRTCAudioAdapter.js";

// Named convenience aliases for the MediaDevice* config shapes — today
// they're anonymous intersections at the .create() call site, which forces
// downstream consumers to reconstruct the type by hand.
export type { MediaDeviceInputConfig  } from "../platform/web/input.js";
export type { MediaDeviceOutputConfig } from "../platform/web/output.js";

// WebRTCConnection's config type is anonymous in the .d.ts today;
// re-export it under a stable name so the bridge can drop its
// `Parameters<typeof WebRTCConnection.create>[0]` workaround.
export type { WebRTCConnectionConfig } from "../utils/WebRTCConnection.js";
```

> If a future addition is a type alias or interface, use `export type
> { ... }`. If it's a class, function, constant, or enum, use the plain
> `export { ... }`. Mixing the two in one statement (`export { Foo, type
> Bar }`) is fine but the split-by-block layout above makes the surface
> easier to audit at a glance.

The existing symbols on `.` (`WebSocketConnection`, `WebRTCConnection`,
`createConnection`, `SessionConfig`, `FormatConfig`, `InputConfig`,
`OutputConfig`, `AudioWorkletConfig`, `DisconnectionDetails`,
`ConnectionType`, `IncomingSocketEvent`, `OutgoingSocketEvent`,
`InputController`, `OutputController`, `postOverallFeedback`,
`SessionConnectionError`) are NOT duplicated here — the Unity bridge
imports them from `.` as today.

### 3. Promote the anonymous config types to named exports

In `src/platform/web/input.ts`, change:

```ts
static create({ ... }: FormatConfig & InputConfig & AudioWorkletConfig): Promise<MediaDeviceInput>;
```

to:

```ts
export type MediaDeviceInputConfig = FormatConfig & InputConfig & AudioWorkletConfig;

// ...
static create(config: MediaDeviceInputConfig): Promise<MediaDeviceInput>;
```

Same treatment in `src/platform/web/output.ts`:

```ts
export type MediaDeviceOutputConfig =
  FormatConfig & OutputConfig & AudioWorkletConfig & { audioContext?: AudioContext };

static create(config: MediaDeviceOutputConfig): Promise<MediaDeviceOutput>;
```

And in `src/utils/WebRTCConnection.ts`, rename the existing `ConnectionConfig`
to a stable, exported `WebRTCConnectionConfig`:

```ts
export type WebRTCConnectionConfig = SessionConfig & {
  onDebug?: (info: unknown) => void;
};

static create(config: WebRTCConnectionConfig): Promise<WebRTCConnection>;
```

(Or keep `ConnectionConfig` as an alias if any internal call sites depend
on the name.)

### 4. No new helper files

Two earlier drafts of this doc proposed SDK-side helpers
(`attachDefaultAudio`, then `withoutAudioPayload`). Both were dropped:

- `attachDefaultAudio` — coupled three concerns (input wiring, output
  wiring, subscriber filtering) and monkey-patched `connection.onMessage`.
  The "subscribers added before vs. after see different events" semantics
  were surprising.
- `withoutAudioPayload` — pure wrapper, fine in isolation, but ~8 lines
  of trivial code. The `Audio` event shape it depends on is reachable
  from the public `IncomingSocketEvent` union (via
  `Extract<IncomingSocketEvent, { type: "audio" }>`), so the Unity SDK
  can define and own this wrapper itself with no SDK additions. If the
  SDK ever refactors the audio event shape, `tsc` fails loud on the
  Unity side — no silent drift.

The SDK's job is to expose stable primitives; composition and filtering
live on the consumer side.

### 5. Document the entrypoint

The `./internal/unity` entrypoint needs a written stability contract so:

- Future SDK changes know not to break it casually (a Unity SDK
  consumer depends on it).
- Other developers stumbling across the export know whether they're
  allowed to use it.
- Anyone adding a sibling (`./internal/react-native`, etc.) knows the
  pattern to follow.

Add documentation in two places:

- **README (`packages/client/README.md`).** A short subsection under
  "Entrypoints" (create the section if it doesn't exist) listing all
  three paths with one-line descriptions:

  | Path | Stability | Audience |
  |---|---|---|
  | `@elevenlabs/client` | Public, semver-stable | All users |
  | `@elevenlabs/client/internal` | No semver guarantees | SDK internals + advanced consumers |
  | `@elevenlabs/client/internal/unity` | No semver guarantees | The [ElevenLabs Unity SDK](https://github.com/elevenlabs/unity)'s WebGL bridge |

- **Maintainer-facing docs.** If `packages/client/` has a
  `CONTRIBUTING.md`, `MAINTAINERS.md`, or similar, add a paragraph
  explaining:
  - What `./internal/unity` is (a curated low-level surface shaped to
    the Unity SDK's needs).
  - Why it exists (the Unity SDK runs `@elevenlabs/client` in a WebGL
    context wrapped by C# — it needs concrete `MediaDevice*` classes
    plus the `attach*` helpers, none of which belong on the public `.`
    surface).
  - How to evolve it (additions are fine; removals/renames require a
    coordinated change with `elevenlabs/unity`).
  - Link back to this document (Plan B, the Unity SDK's plan it
    serves).

  If no maintainer-facing doc exists, a header comment block at the top
  of `src/internal/unity.ts` covering the same points is acceptable
  shorthand.

### 6. Tests

No new runtime code → no new tests required in this PR. The five
runtime symbols re-exported on `./internal/unity` are existing,
presumably already covered.

Optional sanity test — add a one-file Vitest that imports each symbol
from `@elevenlabs/client/internal/unity` and asserts shape (e.g.
`typeof MediaDeviceInput.create === "function"`, etc.). Cheap insurance
against `package.json` exports drift in future refactors.

---

## Acceptance criteria

The PR is good when, after publishing:

1. The following are importable from `@elevenlabs/client/internal/unity`:

   | Symbol | Kind | New or existing? |
   |---|---|---|
   | `MediaDeviceInput` | class (runtime) | existing (move to public path) |
   | `MediaDeviceOutput` | class (runtime) | existing (move to public path) |
   | `attachInputToConnection` | function (runtime) | existing (move to public path) |
   | `attachConnectionToOutput` | function (runtime) | existing (move to public path) |
   | `setWebRTCAudioAdapterFactory` | function (runtime) | existing (also on `./internal`) |
   | `WebRTCAudioAdapter` | type | existing (also on `./internal`) |
   | `AnalysisResult` | type | existing (also on `./internal`) |
   | `MediaDeviceInputConfig` | type | **new** (named alias) |
   | `MediaDeviceOutputConfig` | type | **new** (named alias) |
   | `WebRTCConnectionConfig` | type | **new** (named alias / rename of `ConnectionConfig`) |

2. `tsc` resolves the imports under both `moduleResolution: "bundler"` and
   `"node16"` for downstream packages.
3. `src/internal/unity.ts` has no top-level side effects (it must be
   tree-shakable); ideally also marked at the module level so bundlers
   without `sideEffects: false` in package.json can still drop unused
   exports.
4. The entrypoint is documented (README entrypoint table + maintainer-facing
   note per step 5 above).
5. A version is published (a `next`-tagged release is enough — the Unity
   SDK pins via `patchedDependencies` until stable).

---

## What does NOT change

- The four wrapped classes themselves (`WebSocketConnection`,
  `WebRTCConnection`, `MediaDeviceInput`, `MediaDeviceOutput`). Their
  existing public methods cover everything the bridge needs.
- `createConnection`, `SessionConfig`, `FormatConfig`, `InputConfig`,
  `OutputConfig`, `AudioWorkletConfig`, `IncomingSocketEvent`,
  `OutgoingSocketEvent`, `InputController` / `OutputController` interface
  types, `DisconnectionDetails`, `ConnectionType`, `postOverallFeedback`,
  `SessionConnectionError`, `DEFAULT_INPUT_CHUNK_DURATION_MS` — already
  public on `.`, the bridge consumes them from there.
- The `Conversation` / `VoiceConversation` / `TextConversation` surface —
  the Unity SDK does not wrap these.
- The `./internal` export — leaving it intact preserves any other
  consumers. The bridge symbols that currently live there
  (`setWebRTCAudioAdapterFactory`, `WebRTCAudioAdapter`, `AnalysisResult`)
  stay there *and* gain a `./internal/unity` re-export.
- The IIFE bundle (`dist/lib.iife.js`).

---

## Cleanup on the Unity side (after publish)

Single follow-up commit in `elevenlabs/unity`:

1. In [`Bridge~/src/connection/factories.ts`](https://github.com/elevenlabs/unity/blob/main/Bridge~/src/connection/factories.ts):
   - Replace deep imports of `MediaDeviceInput` / `MediaDeviceOutput` with `from "@elevenlabs/client/internal/unity"` (plain `import { ... }` — these are classes).
   - Replace the local `InputCreateConfig` / `OutputCreateConfig` intersections with imported `MediaDeviceInputConfig` / `MediaDeviceOutputConfig`, using the `import { ..., type MediaDeviceInputConfig, type MediaDeviceOutputConfig }` modifier so the types are erased at compile time.
   - Replace `Parameters<typeof WebRTCConnection.create>[0]` with `type WebRTCConnectionConfig`, imported the same way.
2. Delete [`Bridge~/src/connection/platform-web.d.ts`](https://github.com/elevenlabs/unity/blob/main/Bridge~/src/connection/platform-web.d.ts) (ambient declarations).
3. Delete the `resolve.alias` block in [`Bridge~/vitest.config.ts`](https://github.com/elevenlabs/unity/blob/main/Bridge~/vitest.config.ts).
4. Delete the `deepImportAliases` block in [`Bridge~/build/bundle-jslib.ts`](https://github.com/elevenlabs/unity/blob/main/Bridge~/build/bundle-jslib.ts).
5. In [`Bridge~/src/connection/audio-glue.ts`](https://github.com/elevenlabs/unity/blob/main/Bridge~/src/connection/audio-glue.ts), implement (a) a Unity-local `withoutAudioPayload` callback wrapper and (b) an `attachDefaultAudio(connection, input, output, bridgeCallback)` factory that composes the two SDK `attach*` primitives with the local wrapper (this is [Plan B task 2.3](https://github.com/elevenlabs/unity/blob/main/Docs~/plans/plan-b.md#L164)). Sketch:

   ```ts
   import {
     attachInputToConnection,
     attachConnectionToOutput,
   } from "@elevenlabs/client/internal/unity";
   import type { IncomingSocketEvent } from "@elevenlabs/client";

   type OnMessage = (event: IncomingSocketEvent) => void;

   function withoutAudioPayload(callback: OnMessage): OnMessage {
     return (event) => {
       if (event.type === "audio") {
         const { audio_base_64: _stripped, ...restAudioEvent } = event.audio_event;
         callback({ ...event, audio_event: restAudioEvent as typeof event.audio_event });
       } else {
         callback(event);
       }
     };
   }

   export function attachDefaultAudio(connection, input, output, bridgeCallback) {
     const detachIn  = attachInputToConnection(input, connection);
     const detachOut = attachConnectionToOutput(connection, output);
     connection.onMessage(withoutAudioPayload(bridgeCallback));
     return () => {
       detachIn();
       detachOut();
       // connection.onMessage is single-callback in the SDK;
       // teardown of the subscription happens via connection.close().
     };
   }
   ```
6. Run `pnpm --dir Bridge~ run verify:connection` and confirm parity.

Tracking: the "Pending Human Approval" section at the bottom of [`Docs~/plans/plan-b.md`](https://github.com/elevenlabs/unity/blob/main/Docs~/plans/plan-b.md).
