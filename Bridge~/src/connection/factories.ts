// Factory registrations for the five SDK classes the C# side drives via the
// generic JsObject primitive. Each entry in $EL_ConnectionFactories is a
// closure over the bundled SDK class; the __postset registers them all with
// the primitives layer once _EL_RegisterFactory is available in the runtime.
//
// Return shape is not declared here — each C# call site picks it per-call via
// the generic <T> parameter (e.g. JsBridge.InvokeFactoryAsync<JsObject>(...)).

import {
  WebSocketConnection,
  WebRTCConnection,
  createConnection,
  type SessionConfig,
  type AudioWorkletConfig,
  type InputConfig,
} from "@elevenlabs/client";
import { MediaDeviceInput } from "@elevenlabs/client/dist/platform/web/input.js";
import { MediaDeviceOutput } from "@elevenlabs/client/dist/platform/web/output.js";
import type { FormatConfig, OutputConfig } from "./types.js";

type InputCreateConfig = FormatConfig & InputConfig & AudioWorkletConfig;
type OutputCreateConfig = FormatConfig & OutputConfig & AudioWorkletConfig;

// $EL_ConnectionFactories is a jslib library entry (non-function). Emscripten
// hoists it as _EL_ConnectionFactories in the runtime so the __postset below
// can iterate and register each factory with _EL_RegisterFactory.
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
    WebRTCConnection.create(
      config as Parameters<typeof WebRTCConnection.create>[0],
    ),
  createConnection: (config) => createConnection(config as SessionConfig),
  createMediaDeviceInput: (config) =>
    MediaDeviceInput.create(config as InputCreateConfig),
  createMediaDeviceOutput: (config) =>
    MediaDeviceOutput.create(config as OutputCreateConfig),
};

// Tells Emscripten that _EL_RegisterFactory must be defined before the
// __postset below runs.
export const $EL_ConnectionFactories__deps = ["$EL_RegisterFactory"];

// Inline code emitted by Emscripten after the library symbol is defined.
// Iterates _EL_ConnectionFactories and calls _EL_RegisterFactory for each key
// so the C# JsBridge can invoke them by name via EL_InvokeFactoryAsync.
//
// The exact timing hook (postset vs Module.onRuntimeInitialized) that ensures
// _EL_RegisterFactory is already defined when this runs is resolved in task 2.4.
export const $EL_ConnectionFactories__postset =
  "Object.keys(_EL_ConnectionFactories).forEach(function(k){_EL_RegisterFactory(k,_EL_ConnectionFactories[k]);});";
