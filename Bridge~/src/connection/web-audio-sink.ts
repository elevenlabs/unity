// DOM lib is opted into here (not project-wide) because the sink is the only
// module that legitimately reaches for browser-native Web Audio APIs at the
// type level. Keeping DOM out of the default tsconfig leaves the primitives
// layer free of accidental browser globals.
/// <reference lib="dom" />

// JS-side Web Audio sink used by the WebGL build's bridged WebSocket transport
// when the C# caller supplies an OutputAudioSource via ConversationOptions.
//
// Satisfies `attachConnectionToOutput`'s `{ playAudio(chunk: ArrayBuffer) }`
// contract so the existing audio-glue wiring still works — audio bytes still
// flow connection → sink entirely JS-side. The bridge does not carry PCM.
//
// Property setters (volume, position, spatial blend, …) are invoked from C#
// via the generic JsObject primitive once per Unity frame, mirroring a
// curated subset of UnityEngine.AudioSource onto the Web Audio graph below:
//
//   AudioWorkletNode (PCM intake, queue) → masterGain → analyser
//                                                       ├─ monoGain → StereoPanner ─┐
//                                                       └─ spatialGain → Panner ────┴→ destination
//
// `monoGain` and `spatialGain` form the wet/dry crossfade driven by
// `spatialBlend`: 0 = full mono, 1 = full spatial, anything in between mixes
// both — matching Unity's AudioSource.spatialBlend semantics on the
// constrained subset of properties that have Web Audio analogues.
//
// Listener model: Web Audio's own AudioListener stays pinned at the origin
// facing default (-Z forward, +Y up). All of Unity's listener translation +
// rotation is folded into the source position C#-side via
// `Transform.InverseTransformPoint` + a Z flip (Unity LH +Z forward → Web
// Audio RH -Z forward). That keeps the JS surface small and avoids
// re-publishing the listener orientation every frame.

// AudioWorklet processors must be loaded from a separate JS module — there's
// no inline-function constructor. We embed the processor source as a string
// and load it via Blob URL at runtime; this keeps the bundle self-contained
// and avoids shipping a sidecar .js file under Plugins/WebGL/.
const WORKLET_PROCESSOR_NAME = "el-pcm-feeder";
const WORKLET_SOURCE = `
class PcmFeeder extends AudioWorkletProcessor {
  constructor() {
    super();
    this.queue = [];
    this.consumed = 0;
    this.port.onmessage = (event) => {
      const msg = event.data;
      if (msg.type === "pcm") {
        this.queue.push(msg.data);
      } else if (msg.type === "clear") {
        this.queue.length = 0;
        this.consumed = 0;
      }
    };
  }
  process(_inputs, outputs) {
    const out = outputs[0][0];
    if (!out) return true;
    let i = 0;
    while (i < out.length) {
      if (this.queue.length === 0) {
        out[i++] = 0;
        continue;
      }
      const head = this.queue[0];
      const take = Math.min(out.length - i, head.length - this.consumed);
      for (let j = 0; j < take; j++) out[i + j] = head[this.consumed + j];
      i += take;
      this.consumed += take;
      if (this.consumed >= head.length) {
        this.queue.shift();
        this.consumed = 0;
      }
    }
    return true;
  }
}
registerProcessor("${WORKLET_PROCESSOR_NAME}", PcmFeeder);
`;

export interface WebAudioSinkConfig {
  sampleRate: number;
}

export interface WebAudioSink {
  /** Satisfies `attachConnectionToOutput`'s contract. Bytes are 16-bit LE PCM. */
  playAudio(chunk: ArrayBuffer): void;
  setVolume(volume: number): void;
  /**
   * Source position in the listener's local frame, Web Audio handedness
   * (+X right, +Y up, -Z forward). The C# wrapper folds Unity's listener
   * translation + rotation into this value via
   * `Transform.InverseTransformPoint` + a Z flip, so Web Audio's own
   * `AudioListener` stays pinned at the origin facing default.
   */
  setPosition(x: number, y: number, z: number): void;
  setSpatialBlend(blend: number): void;
  setMinDistance(distance: number): void;
  setMaxDistance(distance: number): void;
  /** 0 → "linear", 1 → "exponential" (Web Audio's closest analog to Unity's Logarithmic). */
  setRolloffMode(mode: number): void;
  setPanStereo(pan: number): void;
  /** Web Audio has no doppler scalar; recorded but unused until a sampled-position pre-emphasis lands. */
  setDopplerLevel(level: number): void;
  /** Fade master gain to 0 over `durationMs`, then flush the queued PCM. */
  interrupt(durationMs: number): void;
  close(): Promise<void>;
  /**
   * RMS over the analyser's most recent time-domain samples, normalised to
   * `[0, 1]`. Pre-step-6 this was `mean(getByteFrequencyData)/255`, which is
   * a spectral-magnitude average not an audible-envelope reading and
   * produced values on a different scale than the native backend's RMS
   * over the playback ring. Time-domain RMS matches the native semantics
   * (`Mathf.Sqrt(mean(sample^2))`) so the same `volumeSensitivity` constant
   * works on both backends. See "Cross-platform volume-reading consistency"
   * in Docs~/plans/output-audio-source.md.
   */
  getVolume(): number;
  /**
   * Returns the analyser's byte-frequency bins as an array of length
   * `length`. The bridge call site passes the desired length; the JS sink
   * allocates a Uint8Array, fills it from the analyser, and returns
   * `Array.from(buffer)` so the bridge marshalling pipeline (Newtonsoft on
   * the C# side) can deserialise it as `byte[]`. Returning the Uint8Array
   * directly serialises as `{"0":..,"1":..}` which Newtonsoft does NOT
   * decode as a byte array — the array-of-numbers shape is the one that
   * round-trips cleanly.
   */
  getByteFrequencyData(length: number): number[];
}

// Debug logger. Defaults to OFF so production builds don't spam the browser
// console; flip on per-page via `globalThis.__elevenLabsWebAudioDebug__ = true`
// (the Playwright debug driver at IntegrationTests~/src/debug-driver.ts does
// exactly that via page.addInitScript so cross-checking C# Debug.Log against
// JS console.log lines stays trivial). Strict `=== true` comparison so any
// other truthy value (e.g. accidental string) doesn't accidentally enable it.
function debugEnabled(): boolean {
  return (
    (globalThis as { __elevenLabsWebAudioDebug__?: boolean })
      .__elevenLabsWebAudioDebug__ === true
  );
}
function debugLog(...args: unknown[]): void {
  if (debugEnabled()) {
    // eslint-disable-next-line no-console
    console.log("[WebAudioSink]", ...args);
  }
}

export async function createWebAudioSink(
  config: WebAudioSinkConfig,
): Promise<WebAudioSink> {
  debugLog("createWebAudioSink: requested sampleRate =", config.sampleRate);
  const context = new AudioContext({ sampleRate: config.sampleRate });
  debugLog(
    "AudioContext constructed: state =",
    context.state,
    "actual sampleRate =",
    context.sampleRate,
  );
  // Blob URL keeps the worklet source self-contained in the bundle and avoids
  // shipping a sidecar .js file under Plugins/WebGL/.
  const blob = new Blob([WORKLET_SOURCE], { type: "application/javascript" });
  const blobUrl = URL.createObjectURL(blob);
  try {
    await context.audioWorklet.addModule(blobUrl);
  } finally {
    URL.revokeObjectURL(blobUrl);
  }
  const worklet = new AudioWorkletNode(context, WORKLET_PROCESSOR_NAME, {
    numberOfInputs: 0,
    numberOfOutputs: 1,
    outputChannelCount: [1],
  });
  const masterGain = context.createGain();
  const analyser = context.createAnalyser();
  // 256-sample FFT window (frequencyBinCount = 128, fftSize = 256 time-domain
  // samples). At a 48 kHz AudioContext this is ~5 ms — the same envelope
  // resolution as `UnityAudioSourceOutput`'s native analysis buffer post-step-6.
  // getByteTimeDomainData's RMS over this window is what `getVolume` returns,
  // giving cross-platform parity for the `Conversation.GetOutputVolume()`
  // scalar without changing the public surface.
  analyser.fftSize = 256;
  const monoGain = context.createGain();
  const spatialGain = context.createGain();
  const stereoPanner = context.createStereoPanner();
  const panner = context.createPanner();

  worklet.connect(masterGain);
  masterGain.connect(analyser);
  analyser.connect(monoGain);
  analyser.connect(spatialGain);
  monoGain.connect(stereoPanner);
  spatialGain.connect(panner);
  stereoPanner.connect(context.destination);
  panner.connect(context.destination);

  // Browsers auto-suspend freshly-created AudioContexts until a user gesture
  // has occurred on the page. Without an explicit resume the AnalyserNode
  // never samples audio (returning all zeros from getByteFrequencyData) and
  // the worklet's queued PCM never plays — manifesting as silent agent voice
  // and a flatlined GetOutputVolume reading driving any AudioSource-bound
  // visualiser. Unity's WebGL loader unlocks its own AudioContext on the
  // initial click; ours is a separate instance and must be resumed
  // independently. The SDK's MediaDeviceOutput does the same — mirroring
  // here keeps GetOutputVolume parity with the pre-step-4 behaviour.
  await context.resume();
  debugLog("AudioContext after resume: state =", context.state);

  // Defaults match Unity's AudioSource: full mono (spatialBlend = 0), unit
  // volume, listener + source at origin. C# pushes deltas at the start of the
  // session, but a defensive default keeps the graph audible if those updates
  // are dropped or delayed.
  masterGain.gain.value = 1;
  monoGain.gain.value = 1;
  spatialGain.gain.value = 0;
  debugLog(
    "graph wired; analyser.fftSize =",
    analyser.fftSize,
    "frequencyBinCount =",
    analyser.frequencyBinCount,
  );

  // Test instrumentation hook (consumed by IntegrationTests~). If a function
  // is registered at globalThis.__elevenLabsWebAudioSinkHook__ before the
  // sink is created, it receives the key graph nodes so a Playwright test
  // can introspect the running graph from outside without sinks exposing
  // themselves through window globals by default. Production callers don't
  // define the hook, so this is a no-op at runtime.
  const testHook = (
    globalThis as {
      __elevenLabsWebAudioSinkHook__?: (info: {
        context: AudioContext;
        panner: PannerNode;
        stereoPanner: StereoPannerNode;
        masterGain: GainNode;
        monoGain: GainNode;
        spatialGain: GainNode;
        analyser: AnalyserNode;
      }) => void;
    }
  ).__elevenLabsWebAudioSinkHook__;
  if (typeof testHook === "function") {
    testHook({
      context,
      panner,
      stereoPanner,
      masterGain,
      monoGain,
      spatialGain,
      analyser,
    });
  }

  let userVolume = 1;
  let playAudioCallCount = 0;
  let getVolumeCallCount = 0;
  let lastGetVolumeLogTime = 0;
  let getByteFrequencyDataCallCount = 0;
  let lastGetByteFrequencyDataLogTime = 0;

  // Mirrors MediaDeviceOutput.playAudio's cancelScheduledValues + volume snap
  // before queueing a chunk. Without this, a chunk that arrives mid-interrupt
  // would play through the in-flight fade and emerge silent.
  function resetGainForPlayback(): void {
    const t = context.currentTime;
    masterGain.gain.cancelScheduledValues(t);
    masterGain.gain.setValueAtTime(userVolume, t);
  }

  function clamp01(v: number): number {
    if (Number.isNaN(v)) return 0;
    return v < 0 ? 0 : v > 1 ? 1 : v;
  }

  return {
    playAudio(chunk: ArrayBuffer): void {
      const inputByteLength = chunk.byteLength;
      if (inputByteLength < 2) {
        if (debugEnabled()) {
          debugLog(
            "playAudio: dropping undersized chunk (byteLength =",
            inputByteLength,
            ")",
          );
        }
        return;
      }
      // int16-LE → Float32, matching UnityAudioSourceOutput.DecodePcm16's
      // asymmetric scale (32768 for negatives, 32767 for positives) so a
      // silent buffer round-trips through Web Audio with no DC offset.
      const view = new Int16Array(chunk);
      const sampleCount = view.length;
      const samples = new Float32Array(sampleCount);
      let peak = 0;
      for (let i = 0; i < sampleCount; i++) {
        const s = view[i];
        const f = s < 0 ? s / 32768 : s / 32767;
        samples[i] = f;
        const a = f < 0 ? -f : f;
        if (a > peak) peak = a;
      }
      resetGainForPlayback();
      playAudioCallCount++;
      // Log BEFORE the postMessage transfers ownership. Reading samples.length
      // after the transfer returns 0 (buffer is detached), which was the
      // initial red-herring that made every chunk look empty. Captured into
      // sampleCount + peak above before the transfer so the log is honest.
      if (
        debugEnabled() &&
        (playAudioCallCount <= 5 || playAudioCallCount % 25 === 0)
      ) {
        debugLog(
          `playAudio #${playAudioCallCount}: inputByteLength =`,
          inputByteLength,
          "samples =",
          sampleCount,
          "peak =",
          peak.toFixed(4),
          "context.state =",
          context.state,
        );
      }
      // Transfer ownership of the underlying buffer to the worklet — avoids
      // copying the chunk across the main → audio thread boundary.
      worklet.port.postMessage({ type: "pcm", data: samples }, [
        samples.buffer,
      ]);
    },
    setVolume(volume: number): void {
      userVolume = clamp01(volume);
      masterGain.gain.value = userVolume;
    },
    setPosition(x: number, y: number, z: number): void {
      panner.positionX.value = x;
      panner.positionY.value = y;
      panner.positionZ.value = z;
    },
    setSpatialBlend(blend: number): void {
      const b = clamp01(blend);
      monoGain.gain.value = 1 - b;
      spatialGain.gain.value = b;
    },
    setMinDistance(distance: number): void {
      panner.refDistance = distance;
    },
    setMaxDistance(distance: number): void {
      panner.maxDistance = distance;
    },
    setRolloffMode(mode: number): void {
      // Unity: 0 = Linear, 1 = Logarithmic, 2 = Custom. Web Audio: "linear",
      // "exponential", "inverse". Linear → linear is direct; Logarithmic
      // → exponential is the closest match (both decay smoothly). Custom
      // curves aren't representable in Web Audio (no AnimationCurve) — the
      // C# layer emits a warn-once and falls back to mode = 1.
      panner.distanceModel = mode === 0 ? "linear" : "exponential";
    },
    setPanStereo(pan: number): void {
      // Unity panStereo is [-1, 1]; StereoPannerNode.pan is the same range.
      stereoPanner.pan.value = pan < -1 ? -1 : pan > 1 ? 1 : pan;
    },
    setDopplerLevel(_level: number): void {
      // Web Audio has no doppler scalar — Unity computes shift from per-frame
      // source velocity into PannerNode position pre-emphasis. Stubbed for
      // now; pre-emphasis lands when a regression demands it.
    },
    interrupt(durationMs: number): void {
      const t = context.currentTime;
      const fadeSec = Math.max(0, durationMs) / 1000;
      masterGain.gain.cancelScheduledValues(t);
      // Snap the current value so the ramp starts from where playback
      // currently is, not from whatever the last scheduled target was.
      masterGain.gain.setValueAtTime(masterGain.gain.value, t);
      masterGain.gain.linearRampToValueAtTime(0, t + fadeSec);
      worklet.port.postMessage({ type: "clear" });
    },
    async close(): Promise<void> {
      worklet.disconnect();
      masterGain.disconnect();
      analyser.disconnect();
      monoGain.disconnect();
      spatialGain.disconnect();
      stereoPanner.disconnect();
      panner.disconnect();
      await context.close();
    },
    getVolume(): number {
      // RMS over the analyser's most recent time-domain samples. Each byte
      // is a sample in [0, 255] where 128 represents silence; (b - 128) / 128
      // recovers a Float32 in [-1, 1]. Square-mean-sqrt of those values is
      // the audible envelope amplitude, matching native's
      // `Mathf.Sqrt(mean(_analysisBuffer^2))` so the two backends return
      // comparable scalars for the same audio. Pre-step-6 this computed
      // `mean(getByteFrequencyData)/255`, a spectral-magnitude average
      // that read on a different scale from native and made the shipped
      // TalkingBox sample's `volumeSensitivity` constant platform-dependent.
      const data = new Uint8Array(analyser.fftSize);
      analyser.getByteTimeDomainData(data);
      getVolumeCallCount++;
      if (data.length === 0) return 0;
      let sumSquares = 0;
      let minByte = 255;
      let maxByte = 0;
      for (let i = 0; i < data.length; i++) {
        const b = data[i];
        if (b < minByte) minByte = b;
        if (b > maxByte) maxByte = b;
        const s = (b - 128) / 128;
        sumSquares += s * s;
      }
      const rms = Math.sqrt(sumSquares / data.length);
      const clamped = rms < 0 ? 0 : rms > 1 ? 1 : rms;
      // Log the first 5 calls unconditionally so we can tell "getVolume is
      // never called" apart from "getVolume is called but the analyser is
      // silent". After that, rate-limit to ~once per second so a 60Hz
      // polling loop doesn't drown the console.
      if (debugEnabled()) {
        const now = Date.now();
        const verbose = getVolumeCallCount <= 5;
        if (verbose || now - lastGetVolumeLogTime > 1000) {
          lastGetVolumeLogTime = now;
          debugLog(
            `getVolume #${getVolumeCallCount}: samples =`,
            data.length,
            "min =",
            minByte,
            "max =",
            maxByte,
            "→ rms =",
            clamped.toFixed(4),
            "context.state =",
            context.state,
          );
        }
      }
      return clamped;
    },
    getByteFrequencyData(length: number): number[] {
      // Allocate JS-side and return Array.from(buffer) so the bridge's
      // Newtonsoft-based decoder on the C# end can deserialise it as
      // `byte[]`. Passing a Uint8Array through `JSON.stringify` yields
      // `{"0":..,"1":..}` (an object keyed by index) which Newtonsoft does
      // NOT decode as a byte array. See the WebAudioSink interface docs.
      const buffer = new Uint8Array(length);
      analyser.getByteFrequencyData(buffer);
      getByteFrequencyDataCallCount++;
      if (debugEnabled()) {
        const verbose = getByteFrequencyDataCallCount <= 5;
        const now = Date.now();
        if (verbose || now - lastGetByteFrequencyDataLogTime > 1000) {
          lastGetByteFrequencyDataLogTime = now;
          let max = 0;
          let nonZero = 0;
          let sum = 0;
          for (let i = 0; i < buffer.length; i++) {
            const v = buffer[i];
            if (v > max) max = v;
            if (v > 0) nonZero++;
            sum += v;
          }
          debugLog(
            `getByteFrequencyData #${getByteFrequencyDataCallCount}: length =`,
            length,
            "nonZero =",
            nonZero,
            "max =",
            max,
            "mean =",
            (sum / Math.max(1, buffer.length)).toFixed(2),
            "context.state =",
            context.state,
          );
        }
      }
      return Array.from(buffer);
    },
  };
}

// Factory-map export consumed by `index.ts` so the registration plumbing stays
// uniform across factories.ts / audio-glue.ts / this module.
export const webAudioSinkFactories: Record<
  string,
  (...args: unknown[]) => unknown
> = {
  createWebAudioSink: (...args) =>
    createWebAudioSink(args[0] as WebAudioSinkConfig),
};
