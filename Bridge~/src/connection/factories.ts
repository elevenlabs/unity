// Factory functions for the five SDK classes the C# side drives via the
// generic JsObject primitive. Each factory is a closure over the bundled SDK
// class. They're collected here as a plain object so:
//
//   * `index.ts` can iterate them and register each with the primitives layer
//     via `EL_RegisterFactory(name, fn)` (the runtime name of the
//     `$EL_RegisterFactory` library entry — see globals.d.ts naming convention).
//   * Tests can pre-register them directly into the dispatcher's factory
//     registry without going through the .jslib's runtime postset.
//
// Why no $-prefix on the export name anymore: the connection `.jslib` no
// longer surfaces these factories as Emscripten library entries. Instead the
// entire bundle (including this map) lands at framework.js top scope via a
// single `$EL_ConnectionInit` postset; registration happens inline at startup.
// The earlier IIFE-wrapped library-entries approach surfaced a stripping leak
// (the IIFE body never reached the runtime); commit `14d7601` ports the
// .jslib to postset mode — git log there for the full design rationale.
//
// Return shape is not declared here — each C# call site picks it per-call via
// the generic <T> parameter (e.g. JsBridge.InvokeFactoryAsync<JsObject>(...)).

import {
  WebSocketConnection,
  WebRTCConnection,
  createConnection,
  MediaDeviceInput,
  type SessionConfig,
  type MediaDeviceInputConfig,
  type WebRTCConnectionConfig,
} from "@elevenlabs/client/internal/unity";

// MediaDeviceOutput is intentionally absent. The WebSocket arm now uses our
// own `createWebAudioSink` (Bridge~/src/connection/web-audio-sink.ts) so the
// supplied `AudioSource` can drive Web Audio's PannerNode + StereoPannerNode
// from C#-side property polling. The WebRTC arm has its own coupled output
// (read via `BridgedWebRTCConnection.GetCoupledOutput`) and never used this
// factory.

// Config args arrive as rehydrated JSON from C# — the first arg is the config
// object. We cast to the SDK's expected type; the C# caller is responsible for
// sending the correct shape.
export const connectionFactories: Record<string, (config: unknown) => unknown> =
  {
    createWebSocketConnection: (config) =>
      WebSocketConnection.create(config as SessionConfig),
    createWebRTCConnection: (config) =>
      WebRTCConnection.create(config as WebRTCConnectionConfig),
    createConnection: (config) => createConnection(config as SessionConfig),
    createMediaDeviceInput: (config) =>
      MediaDeviceInput.create(config as MediaDeviceInputConfig),
  };
