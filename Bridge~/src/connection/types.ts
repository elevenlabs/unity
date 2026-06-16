// Shapes the connection jslib accepts from C#. Each is composed from the
// `@elevenlabs/client` exported type so an upstream SDK bump ripples through
// tsc — if the SDK renames or removes a field, this file fails to compile and
// flags the bridge mismatch before any jslib gets shipped.
//
// Composition is intentional: we re-export `SessionConfig` etc. directly today,
// but as we discover places the bridge accepts a narrower / wider shape than
// the raw SDK (e.g. WebGL can't supply `useWakeLock`), this file is where the
// `Pick` / `Omit` / `extends` clauses land.

import type {
  SessionConfig as SdkSessionConfig,
  FormatConfig as SdkFormatConfig,
  InputConfig as SdkInputConfig,
  OutputConfig as SdkOutputConfig,
  DisconnectionDetails as SdkDisconnectionDetails,
  ConnectionType as SdkConnectionType,
} from "@elevenlabs/client/internal/unity";

export type SessionConfig = SdkSessionConfig;
export type FormatConfig = SdkFormatConfig;
export type InputConfig = SdkInputConfig;
export type OutputConfig = SdkOutputConfig;
export type DisconnectionDetails = SdkDisconnectionDetails;
export type ConnectionType = SdkConnectionType;
