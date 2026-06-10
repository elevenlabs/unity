// Factory registrations for the four `@elevenlabs/client` classes the C# side
// drives via the generic `JsObject` primitive. Each entry below is a stub —
// real factory implementations land in task 2.2; this file currently only
// holds the export shape and a placeholder list of names so the bundling
// pipeline has something to consume.

export const FACTORY_NAMES = [
  "createWebSocketConnection",
  "createWebRTCConnection",
  "createConnection",
  "createMediaDeviceInput",
  "createMediaDeviceOutput",
] as const;

export type FactoryName = (typeof FACTORY_NAMES)[number];
