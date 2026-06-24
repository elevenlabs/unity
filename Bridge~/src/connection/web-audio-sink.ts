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
  /** RMS-ish scalar derived from the analyser node. */
  getVolume(): number;
  getByteFrequencyData(buffer: Uint8Array<ArrayBuffer>): void;
}

export async function createWebAudioSink(
  config: WebAudioSinkConfig,
): Promise<WebAudioSink> {
  const context = new AudioContext({ sampleRate: config.sampleRate });
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

  // Defaults match Unity's AudioSource: full mono (spatialBlend = 0), unit
  // volume, listener + source at origin. C# pushes deltas at the start of the
  // session, but a defensive default keeps the graph audible if those updates
  // are dropped or delayed.
  masterGain.gain.value = 1;
  monoGain.gain.value = 1;
  spatialGain.gain.value = 0;

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
      if (chunk.byteLength < 2) return;
      // int16-LE → Float32, matching UnityAudioSourceOutput.DecodePcm16's
      // asymmetric scale (32768 for negatives, 32767 for positives) so a
      // silent buffer round-trips through Web Audio with no DC offset.
      const view = new Int16Array(chunk);
      const samples = new Float32Array(view.length);
      for (let i = 0; i < view.length; i++) {
        const s = view[i];
        samples[i] = s < 0 ? s / 32768 : s / 32767;
      }
      resetGainForPlayback();
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
      // Mean of all frequency bins, normalised to [0, 1] — matches the
      // SDK's @elevenlabs/client `calculateVolume` helper so the value
      // returned through GetOutputVolume stays consistent with the previous
      // BridgedOutputController behaviour. RMS would overweight transient
      // peaks and produce visibly different readings on the same audio.
      const data = new Uint8Array(analyser.frequencyBinCount);
      analyser.getByteFrequencyData(data);
      if (data.length === 0) return 0;
      let sum = 0;
      for (let i = 0; i < data.length; i++) sum += data[i];
      const volume = sum / data.length / 255;
      return volume < 0 ? 0 : volume > 1 ? 1 : volume;
    },
    getByteFrequencyData(buffer: Uint8Array<ArrayBuffer>): void {
      analyser.getByteFrequencyData(buffer);
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
