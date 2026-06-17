// Factory registrations for the five SDK classes the C# side drives via the
// generic JsObject primitive. Each entry in $EL_ConnectionFactories is a
// closure over the bundled SDK class; the __postset registers them all with
// the primitives layer.
//
// Naming convention reminder: $-prefixed library entries are emitted by
// Emscripten with the `$` stripped (no underscore added). So $EL_ConnectionFactories
// is accessed at runtime as EL_ConnectionFactories, and $EL_RegisterFactory as
// EL_RegisterFactory. See globals.d.ts for the full convention.
//
// Return shape is not declared here — each C# call site picks it per-call via
// the generic <T> parameter (e.g. JsBridge.InvokeFactoryAsync<JsObject>(...)).

import {
  WebSocketConnection,
  WebRTCConnection,
  createConnection,
  MediaDeviceInput,
  MediaDeviceOutput,
  type SessionConfig,
  type MediaDeviceInputConfig,
  type MediaDeviceOutputConfig,
  type WebRTCConnectionConfig,
} from "@elevenlabs/client/internal/unity";

// $EL_ConnectionFactories is a jslib library entry (non-function). Emscripten
// hoists it as EL_ConnectionFactories in the runtime so the __postset below
// can iterate and register each factory with EL_RegisterFactory.
//
// The factory functions are closures over the SDK classes bundled into the IIFE
// by Rolldown; they remain live after the IIFE executes because the library
// object holds references to them.
//
// Config args arrive as rehydrated JSON from C# — the first arg is the config
// object. We cast to the SDK's expected type; the C# caller is responsible for
// sending the correct shape.
export const $EL_ConnectionFactories: Record<
  string,
  (config: unknown) => unknown
> = {
  createWebSocketConnection: (config) =>
    WebSocketConnection.create(config as SessionConfig),
  createWebRTCConnection: (config) =>
    WebRTCConnection.create(config as WebRTCConnectionConfig),
  createConnection: (config) => createConnection(config as SessionConfig),
  createMediaDeviceInput: (config) =>
    MediaDeviceInput.create(config as MediaDeviceInputConfig),
  createMediaDeviceOutput: (config) =>
    MediaDeviceOutput.create(config as MediaDeviceOutputConfig),
};

// Tells Emscripten that $EL_RegisterFactory must be defined before the
// __postset below runs. The runtime name is EL_RegisterFactory (no underscore —
// see globals.d.ts naming convention).
export const $EL_ConnectionFactories__deps = ["$EL_RegisterFactory"];

// Inline code emitted by Emscripten after the library symbol is defined.
// Iterates EL_ConnectionFactories and calls EL_RegisterFactory for each key so
// the C# JsBridge can invoke them by name via EL_InvokeFactoryAsync.
export const $EL_ConnectionFactories__postset =
  "Object.keys(EL_ConnectionFactories).forEach(function(k){EL_RegisterFactory(k,EL_ConnectionFactories[k]);});";
