// Ambient declarations for @elevenlabs/client platform-web sub-paths that are
// not in the package's "exports" map. TypeScript's bundler moduleResolution
// enforces the exports field; Rolldown (the production bundler) does not.
// These declarations give tsc the minimal shape it needs to type-check the
// factory wrappers without requiring the SDK to add new export conditions.

declare module "@elevenlabs/client/dist/platform/web/input.js" {
  import type {
    FormatConfig,
    InputConfig,
    AudioWorkletConfig,
  } from "@elevenlabs/client";
  type CreateConfig = FormatConfig & InputConfig & AudioWorkletConfig;

  export class MediaDeviceInput {
    static create(config: CreateConfig): Promise<MediaDeviceInput>;
    isMuted(): boolean;
    getVolume(): number;
    getByteFrequencyData(buffer: Uint8Array): void;
    close(): Promise<void>;
    setMuted(isMuted: boolean): Promise<void>;
    setDevice(config?: object): Promise<void>;
  }
}

declare module "@elevenlabs/client/dist/platform/web/output.js" {
  import type { FormatConfig, AudioWorkletConfig } from "@elevenlabs/client";
  type CreateConfig = FormatConfig &
    AudioWorkletConfig & {
      outputDeviceId?: string;
    };

  export class MediaDeviceOutput {
    static create(config: CreateConfig): Promise<MediaDeviceOutput>;
    setVolume(volume: number): void;
    interrupt(resetDuration?: number): void;
    getVolume(): number;
    getByteFrequencyData(buffer: Uint8Array): void;
    close(): Promise<void>;
    setDevice(config?: object): Promise<void>;
  }
}
