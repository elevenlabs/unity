// Entry point for the ElevenLabsConnection.jslib bundle. Aggregates the
// connection-side modules into a single library object that
// build/bundle-jslib.ts emits as the second argument to mergeInto.

import {
  installIosAudioUnlockListener,
  setWebRTCAudioAdapterFactory,
  WebAudioAdapter,
} from "@elevenlabs/client/internal/unity";
import * as factories from "./factories.js";
import * as audioGlue from "./audio-glue.js";

// Side-effects that the SDK's `platform/web/index.js` entrypoint would normally
// fire at module-init time. We invoke them explicitly here so the bridge owns
// the timing — and so nothing else in the bundle needs to read `navigator` at
// top-level (which would crash during emcc's link-time eval in its bundled
// Node.js).
//
// `setWebRTCAudioAdapterFactory` only stores the factory; the closure runs
// later when a WebRTC connection actually needs an adapter, so it's always
// safe to call.
setWebRTCAudioAdapterFactory(() => new WebAudioAdapter());

// `installIosAudioUnlockListener` reads `navigator.platform` synchronously
// (via `isIosDevice()`). At browser runtime that's fine; at emcc link time
// `navigator` is undefined, so we gate the call on the global being present.
// The IIFE re-runs in the browser when the jslib loads — that's the pass
// that actually arms the iOS gesture listener.
if (typeof navigator !== "undefined") {
  installIosAudioUnlockListener();
}

const library = {
  ...factories,
  ...audioGlue,
};

export default library;
