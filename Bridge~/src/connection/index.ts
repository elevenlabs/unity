// Entry point for the ElevenLabsConnection.jslib bundle.
//
// The bundler (`Bridge~/build/bundle-jslib.ts`) embeds this module's compiled
// output as a JSON-escaped string in a single `$EL_ConnectionInit` library
// entry. Emscripten emits that entry verbatim at framework.js top scope via
// the `;`-prefix string trick (see emscripten/src/jsifier.js — a `$Foo: ';X'`
// entry becomes `var Foo;X;` in the runtime). That means the bundled
// @elevenlabs/client/internal/unity SDK classes, the side-effects below, and
// the factory registrations all run at runtime startup, in scope, instead of
// being trapped inside a Rolldown IIFE whose body Emscripten dropped on the
// floor.
//
// Background: see Docs~/plans/jslib-bundler-iife-scope.md for the
// architectural rewrite. The pre-fix design exported `$EL_*` library entries
// from `factories.ts` / `audio-glue.ts`, but the SDK classes the factory
// closures referenced were never reachable in the runtime emit — Emscripten
// only preserves declared library entries, and the IIFE wrapper was
// discarded.

import {
  installIosAudioUnlockListener,
  setWebRTCAudioAdapterFactory,
  WebAudioAdapter,
} from "@elevenlabs/client/internal/unity";
import { connectionFactories } from "./factories.js";
import { audioGlueFactories } from "./audio-glue.js";

// Side-effects that the SDK's `platform/web/index.js` entrypoint would
// normally fire at module-init time. We invoke them explicitly so the bridge
// owns the timing.
//
// `setWebRTCAudioAdapterFactory` only stores the factory; the closure runs
// later when a WebRTC connection actually needs an adapter, so it's safe to
// call unconditionally.
setWebRTCAudioAdapterFactory(() => new WebAudioAdapter());

// `installIosAudioUnlockListener` reads `navigator.platform` synchronously
// (via `isIosDevice()`). Guard with `typeof navigator` so the bundle survives
// any environment where `navigator` isn't defined (e.g. unit tests in Node).
// At browser runtime the guard is a no-op.
if (typeof navigator !== "undefined") {
  installIosAudioUnlockListener();
}

// Wire every factory into the primitives layer. `EL_RegisterFactory` is the
// runtime name of `$EL_RegisterFactory` from the primitives bundle (the `$`
// prefix is stripped on Emscripten emit — see Bridge~/src/primitives/globals.d.ts).
// The `$EL_ConnectionInit__deps: ['$EL_RegisterFactory']` declaration in the
// bundler output ensures the primitives' library entries are emitted before
// this code runs.
for (const [name, fn] of Object.entries(connectionFactories)) {
  EL_RegisterFactory(name, fn);
}
for (const [name, fn] of Object.entries(audioGlueFactories)) {
  EL_RegisterFactory(name, fn);
}
