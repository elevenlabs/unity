// DOM lib opted into here (not project-wide) so `page.evaluate` callback
// bodies and `page.addInitScript` script bodies can reference browser
// globals (window, AudioContext, PannerNode). Outside these blocks the
// code still runs in Node — DOM types are inert there.
/// <reference lib="dom" />

import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "vitest";
import type { WebSocket } from "playwright";
import { runWebGLSmoke } from "./playwright-harness.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// Spatial variant lives in its own build directory so it doesn't trample the
// non-spatial conversation smoke. See Editor/HostBuild.cs#BuildConversationSpatial
// + TestProject/build-webgl-conversation-spatial.sh.
const BUILD_DIR = path.resolve(
  __dirname,
  "..",
  "..",
  "TestProject",
  "Build",
  "WebGLConversationSpatialSmoke",
);

// Must match `SpatialPosition.x` in
// Samples/ConversationSmokeTest/ConversationSpatialSmokeTest.cs. The smoke
// puts the source at (3, 0, 0) and the scene has no AudioListener, so
// WebAudioBackedOutput's no-listener fallback pushes world position with
// Z flipped — which leaves x unchanged. The PannerNode's `positionX.value`
// must therefore equal this exactly.
const EXPECTED_X = 3;

const TEST_TIMEOUT_MS = 120_000;

// Shape that `__elevenLabsWebAudioSinkHook__` (in web-audio-sink.ts) pushes
// per sink construction. Mirrored here as a TypeScript type so the
// page.evaluate result can be typed without `any`.
interface CapturedSink {
  pannerPositionX: number;
  pannerPositionY: number;
  pannerPositionZ: number;
  monoGain: number;
  spatialGain: number;
  masterGain: number;
  sampleRate: number;
}

test(
  "conversation spatial smoke test passes in Chromium",
  async () => {
    let audioFrameCount = 0;
    let audioBase64Stripped = true;

    const outcome = await runWebGLSmoke({
      buildDir: BUILD_DIR,
      matcher: {
        pass: ["[SpatialSmoke] All spatial smoke tests passed!"],
        fail: ["ASSERTION FAILED"],
        skip: ["[SpatialSmoke] CONFIG MISSING"],
        streamPrefix: "[SpatialSmoke]",
      },
      onBeforeNavigate: async (page) => {
        // Install the sink-introspection hook BEFORE navigation. The script
        // runs in every fresh execution context (including iframes); the
        // sink calls __elevenLabsWebAudioSinkHook__ at construction time
        // and we capture the references in __capturedSinks__ for the
        // post-resolve assertion to read.
        //
        // `as any` because @types/dom doesn't model our custom globals; the
        // shape is the WebAudioSinkHook interface in web-audio-sink.ts.
        await page.addInitScript(() => {
          (window as unknown as { __capturedSinks__: unknown[] }).__capturedSinks__ = [];
          (
            window as unknown as {
              __elevenLabsWebAudioSinkHook__: (info: unknown) => void;
            }
          ).__elevenLabsWebAudioSinkHook__ = (info) => {
            (window as unknown as { __capturedSinks__: unknown[] }).__capturedSinks__.push(info);
          };
        });

        // Audio-frame observer: the SDK still pushes audio events end-to-end,
        // but audio_base_64 should be stripped by the bridge's
        // withoutAudioPayload wrapper before reaching the C# side. We can't
        // observe the bridge boundary directly, but we can verify the wire
        // frames from the agent still carry the original payload — proving
        // the spatial swap didn't break the upstream contract.
        page.on("websocket", (ws: WebSocket) => {
          ws.on("framereceived", (event) => {
            try {
              const obj = JSON.parse(event.payload.toString()) as {
                type?: string;
                audio_event?: { audio_base_64?: string };
              };
              if (obj.type === "audio" && obj.audio_event) {
                audioFrameCount++;
                if (!obj.audio_event.audio_base_64) {
                  // Server should always include this — if it's missing on
                  // the wire something has changed in the SDK contract.
                  audioBase64Stripped = false;
                }
              }
            } catch {
              // Non-JSON frames (binary) and parse errors are uninteresting
              // for this assertion; the wire-shape contract is text JSON.
            }
          });
        });
      },
      onResolved: async (page, resolved) => {
        if (resolved.status !== "pass") return;
        // Inspect every sink the page constructed during the run, projecting
        // the audio-thread `AudioParam.value` reads back into plain numbers
        // so they survive the JSON round-trip from the page context.
        const captured = await page.evaluate(() => {
          const sinks = (
            window as unknown as {
              __capturedSinks__?: {
                context: AudioContext;
                panner: PannerNode;
                masterGain: GainNode;
                monoGain: GainNode;
                spatialGain: GainNode;
              }[];
            }
          ).__capturedSinks__;
          if (!sinks) return [];
          return sinks.map((s) => ({
            pannerPositionX: s.panner.positionX.value,
            pannerPositionY: s.panner.positionY.value,
            pannerPositionZ: s.panner.positionZ.value,
            monoGain: s.monoGain.gain.value,
            spatialGain: s.spatialGain.gain.value,
            masterGain: s.masterGain.gain.value,
            sampleRate: s.context.sampleRate,
          }));
        });
        // Stash on the outer scope so post-resolve assertions can read it
        // after the harness tears down the page.
        capturedSinks = captured as CapturedSink[];
      },
    });

    if (outcome.status === "skip") {
      console.log(`  [skip] ${outcome.message}`);
      return;
    }

    expect(outcome.status, `Spatial smoke failed: ${outcome.message}`).toBe(
      "pass",
    );

    // ----- Web Audio graph assertions -----------------------------------

    expect(capturedSinks, "no WebAudio sink captured during run").toBeDefined();
    expect(
      capturedSinks!.length,
      "expected at least one createWebAudioSink invocation during the spatial smoke",
    ).toBeGreaterThan(0);

    const sink = capturedSinks![0];
    console.log(`  [spatial] captured sink: ${JSON.stringify(sink)}`);

    // Source position pushed by WebAudioBackedOutput's polling loop must
    // reach the underlying PannerNode. EXPECTED_X mirrors what the smoke
    // sets on the supplied AudioSource's transform.
    expect(
      sink.pannerPositionX,
      `panner.positionX should reflect AudioSource transform.x (=${EXPECTED_X})`,
    ).toBeCloseTo(EXPECTED_X, 5);
    expect(sink.pannerPositionY).toBeCloseTo(0, 5);
    expect(sink.pannerPositionZ).toBeCloseTo(0, 5);

    // spatialBlend=1 → mono path muted, spatial path full.
    expect(
      sink.spatialGain,
      "spatialBlend=1 should drive spatialGain to 1",
    ).toBeCloseTo(1, 5);
    expect(
      sink.monoGain,
      "spatialBlend=1 should drive monoGain to 0",
    ).toBeCloseTo(0, 5);

    // ----- Audio-frame regression check ---------------------------------

    expect(
      audioFrameCount,
      "expected at least one audio frame from the agent",
    ).toBeGreaterThan(0);
    expect(
      audioBase64Stripped,
      "agent audio frames should still carry audio_base_64 on the wire",
    ).toBe(true);
  },
  TEST_TIMEOUT_MS,
);

// File-scoped slot the onResolved hook writes into so the post-resolve
// assertions can read it after the harness has torn down the page.
let capturedSinks: CapturedSink[] | undefined;
