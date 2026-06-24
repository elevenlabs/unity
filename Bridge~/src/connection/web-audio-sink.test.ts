// Unit tests for createWebAudioSink. Node's vitest environment has no Web
// Audio implementation, so we stub the constructors + node types this module
// reaches for and assert on the calls made against the stubs.
//
// Scope is the main-thread JS surface only: graph construction, property
// setters, gain ramps, and worklet port wiring. The worklet processor itself
// is not exercised here — it runs only in a real browser audio thread and is
// covered end-to-end by IntegrationTests~/ once that harness lands.

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createWebAudioSink, type WebAudioSink } from "./web-audio-sink.js";

// ---------------------------------------------------------------------------
// Stubs — minimal Web Audio API shape the sink touches.
// ---------------------------------------------------------------------------

interface StubAudioParam {
  value: number;
  cancelScheduledValues: ReturnType<typeof vi.fn>;
  setValueAtTime: ReturnType<typeof vi.fn>;
  linearRampToValueAtTime: ReturnType<typeof vi.fn>;
}

function makeParam(initial = 1): StubAudioParam {
  const param: StubAudioParam = {
    value: initial,
    cancelScheduledValues: vi.fn(),
    setValueAtTime: vi.fn((v: number) => {
      param.value = v;
    }),
    linearRampToValueAtTime: vi.fn((v: number) => {
      param.value = v;
    }),
  };
  return param;
}

interface StubAudioNode {
  connect: ReturnType<typeof vi.fn>;
  disconnect: ReturnType<typeof vi.fn>;
}

function makeNode(): StubAudioNode {
  return { connect: vi.fn(), disconnect: vi.fn() };
}

interface StubGainNode extends StubAudioNode {
  gain: StubAudioParam;
}

interface StubAnalyserNode extends StubAudioNode {
  frequencyBinCount: number;
  getByteFrequencyData: ReturnType<typeof vi.fn>;
}

interface StubStereoPannerNode extends StubAudioNode {
  pan: StubAudioParam;
}

interface StubPannerNode extends StubAudioNode {
  positionX: StubAudioParam;
  positionY: StubAudioParam;
  positionZ: StubAudioParam;
  refDistance: number;
  maxDistance: number;
  distanceModel: string;
}

interface StubAudioListener {
  positionX: StubAudioParam;
  positionY: StubAudioParam;
  positionZ: StubAudioParam;
}

interface StubAudioWorkletNode extends StubAudioNode {
  port: { postMessage: ReturnType<typeof vi.fn> };
  __name: string;
  __options: unknown;
}

interface StubAudioContext {
  currentTime: number;
  sampleRate: number;
  destination: StubAudioNode;
  listener: StubAudioListener;
  audioWorklet: { addModule: ReturnType<typeof vi.fn> };
  createGain: ReturnType<typeof vi.fn>;
  createAnalyser: ReturnType<typeof vi.fn>;
  createStereoPanner: ReturnType<typeof vi.fn>;
  createPanner: ReturnType<typeof vi.fn>;
  close: ReturnType<typeof vi.fn>;
  __constructed: { sampleRate: number };
  __gains: StubGainNode[];
  __analysers: StubAnalyserNode[];
  __stereoPanners: StubStereoPannerNode[];
  __panners: StubPannerNode[];
  __worklets: StubAudioWorkletNode[];
}

function buildContext(options?: { sampleRate?: number }): StubAudioContext {
  const ctx: StubAudioContext = {
    currentTime: 0,
    sampleRate: options?.sampleRate ?? 48000,
    destination: makeNode(),
    listener: {
      positionX: makeParam(0),
      positionY: makeParam(0),
      positionZ: makeParam(0),
    },
    audioWorklet: { addModule: vi.fn().mockResolvedValue(undefined) },
    createGain: vi.fn(),
    createAnalyser: vi.fn(),
    createStereoPanner: vi.fn(),
    createPanner: vi.fn(),
    close: vi.fn().mockResolvedValue(undefined),
    __constructed: { sampleRate: options?.sampleRate ?? 48000 },
    __gains: [],
    __analysers: [],
    __stereoPanners: [],
    __panners: [],
    __worklets: [],
  };
  ctx.createGain.mockImplementation(() => {
    const node: StubGainNode = { ...makeNode(), gain: makeParam(1) };
    ctx.__gains.push(node);
    return node;
  });
  ctx.createAnalyser.mockImplementation(() => {
    const node: StubAnalyserNode = {
      ...makeNode(),
      frequencyBinCount: 32,
      getByteFrequencyData: vi.fn(),
    };
    ctx.__analysers.push(node);
    return node;
  });
  ctx.createStereoPanner.mockImplementation(() => {
    const node: StubStereoPannerNode = { ...makeNode(), pan: makeParam(0) };
    ctx.__stereoPanners.push(node);
    return node;
  });
  ctx.createPanner.mockImplementation(() => {
    const node: StubPannerNode = {
      ...makeNode(),
      positionX: makeParam(0),
      positionY: makeParam(0),
      positionZ: makeParam(0),
      refDistance: 1,
      maxDistance: 10000,
      distanceModel: "inverse",
    };
    ctx.__panners.push(node);
    return node;
  });
  return ctx;
}

// ---------------------------------------------------------------------------
// Globals lifecycle.
// ---------------------------------------------------------------------------

let activeContext: StubAudioContext;
let workletNodeOptions: unknown;
let workletNodeName: string;

beforeEach(() => {
  vi.stubGlobal(
    "AudioContext",
    vi.fn((opts?: { sampleRate?: number }) => {
      activeContext = buildContext(opts);
      return activeContext;
    }),
  );
  vi.stubGlobal(
    "AudioWorkletNode",
    vi.fn((ctx: StubAudioContext, name: string, options: unknown) => {
      workletNodeName = name;
      workletNodeOptions = options;
      const node: StubAudioWorkletNode = {
        ...makeNode(),
        port: { postMessage: vi.fn() },
        __name: name,
        __options: options,
      };
      ctx.__worklets.push(node);
      return node;
    }),
  );
  vi.stubGlobal("Blob", class {});
  vi.stubGlobal("URL", {
    createObjectURL: vi.fn(() => "blob:fake"),
    revokeObjectURL: vi.fn(),
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
});

// ---------------------------------------------------------------------------
// Helpers.
// ---------------------------------------------------------------------------

function lastWorklet(): StubAudioWorkletNode {
  return activeContext.__worklets[activeContext.__worklets.length - 1];
}
function masterGain(): StubGainNode {
  // First gain returned by createGain — wired between worklet and analyser.
  return activeContext.__gains[0];
}
function monoGain(): StubGainNode {
  return activeContext.__gains[1];
}
function spatialGain(): StubGainNode {
  return activeContext.__gains[2];
}
function panner(): StubPannerNode {
  return activeContext.__panners[0];
}
function stereoPanner(): StubStereoPannerNode {
  return activeContext.__stereoPanners[0];
}

function littleEndianBuffer(...samples: number[]): ArrayBuffer {
  const buf = new ArrayBuffer(samples.length * 2);
  const view = new DataView(buf);
  for (let i = 0; i < samples.length; i++) {
    view.setInt16(i * 2, samples[i], true);
  }
  return buf;
}

// ---------------------------------------------------------------------------
// Tests.
// ---------------------------------------------------------------------------

describe("createWebAudioSink — graph construction", () => {
  it("builds the expected node graph with the worklet, master gain, analyser, mono/spatial gains, and panners", async () => {
    await createWebAudioSink({ sampleRate: 16000 });

    expect(activeContext.__constructed.sampleRate).toBe(16000);
    expect(activeContext.audioWorklet.addModule).toHaveBeenCalledTimes(1);
    expect(workletNodeName).toBe("el-pcm-feeder");
    expect(workletNodeOptions).toMatchObject({
      numberOfInputs: 0,
      numberOfOutputs: 1,
      outputChannelCount: [1],
    });
    expect(activeContext.__gains).toHaveLength(3);
    expect(activeContext.__analysers).toHaveLength(1);
    expect(activeContext.__stereoPanners).toHaveLength(1);
    expect(activeContext.__panners).toHaveLength(1);

    // Worklet → masterGain → analyser → {monoGain, spatialGain} → {stereoPanner, panner} → destination.
    expect(lastWorklet().connect).toHaveBeenCalledWith(masterGain());
    expect(masterGain().connect).toHaveBeenCalledWith(
      activeContext.__analysers[0],
    );
    expect(activeContext.__analysers[0].connect).toHaveBeenCalledWith(
      monoGain(),
    );
    expect(activeContext.__analysers[0].connect).toHaveBeenCalledWith(
      spatialGain(),
    );
    expect(monoGain().connect).toHaveBeenCalledWith(stereoPanner());
    expect(spatialGain().connect).toHaveBeenCalledWith(panner());
    expect(stereoPanner().connect).toHaveBeenCalledWith(
      activeContext.destination,
    );
    expect(panner().connect).toHaveBeenCalledWith(activeContext.destination);

    // Default spatial blend = 0 → mono full, spatial muted.
    expect(monoGain().gain.value).toBe(1);
    expect(spatialGain().gain.value).toBe(0);
    expect(masterGain().gain.value).toBe(1);
  });
});

describe("setVolume", () => {
  it("updates the master GainNode value and clamps to [0, 1]", async () => {
    const sink = await createWebAudioSink({ sampleRate: 16000 });

    sink.setVolume(0.5);
    expect(masterGain().gain.value).toBe(0.5);

    sink.setVolume(-2);
    expect(masterGain().gain.value).toBe(0);

    sink.setVolume(7);
    expect(masterGain().gain.value).toBe(1);
  });
});

describe("setPosition", () => {
  it("updates PannerNode.positionX/Y/Z", async () => {
    const sink = await createWebAudioSink({ sampleRate: 16000 });

    sink.setPosition(3, -1, 7);
    expect(panner().positionX.value).toBe(3);
    expect(panner().positionY.value).toBe(-1);
    expect(panner().positionZ.value).toBe(7);
  });
});

describe("setListenerPosition", () => {
  it("updates the AudioListener position", async () => {
    const sink = await createWebAudioSink({ sampleRate: 16000 });

    sink.setListenerPosition(10, 20, 30);
    expect(activeContext.listener.positionX.value).toBe(10);
    expect(activeContext.listener.positionY.value).toBe(20);
    expect(activeContext.listener.positionZ.value).toBe(30);
  });
});

describe("setSpatialBlend", () => {
  it("mixes mono and spatial gains across 0, 0.5, 1", async () => {
    const sink = await createWebAudioSink({ sampleRate: 16000 });

    sink.setSpatialBlend(0);
    expect(monoGain().gain.value).toBe(1);
    expect(spatialGain().gain.value).toBe(0);

    sink.setSpatialBlend(0.5);
    expect(monoGain().gain.value).toBe(0.5);
    expect(spatialGain().gain.value).toBe(0.5);

    sink.setSpatialBlend(1);
    expect(monoGain().gain.value).toBe(0);
    expect(spatialGain().gain.value).toBe(1);
  });
});

describe("setRolloffMode", () => {
  it("maps Unity Linear (0) → 'linear' and Logarithmic (1) → 'exponential'", async () => {
    const sink = await createWebAudioSink({ sampleRate: 16000 });

    sink.setRolloffMode(0);
    expect(panner().distanceModel).toBe("linear");

    sink.setRolloffMode(1);
    expect(panner().distanceModel).toBe("exponential");
  });
});

describe("setMinDistance / setMaxDistance / setPanStereo", () => {
  it("forwards distance + pan setters to their underlying params", async () => {
    const sink = await createWebAudioSink({ sampleRate: 16000 });

    sink.setMinDistance(2);
    sink.setMaxDistance(20);
    expect(panner().refDistance).toBe(2);
    expect(panner().maxDistance).toBe(20);

    sink.setPanStereo(-0.5);
    expect(stereoPanner().pan.value).toBe(-0.5);

    sink.setPanStereo(5); // out-of-range → clamped to 1.
    expect(stereoPanner().pan.value).toBe(1);
  });
});

describe("playAudio", () => {
  it("decodes int16-LE PCM to Float32 and posts it to the worklet port", async () => {
    const sink = await createWebAudioSink({ sampleRate: 16000 });
    const port = lastWorklet().port;

    const chunk = littleEndianBuffer(0, 16383, -16384, 32767);
    sink.playAudio(chunk);

    expect(port.postMessage).toHaveBeenCalledTimes(1);
    const [msg, transfer] = port.postMessage.mock.calls[0];
    expect((msg as { type: string }).type).toBe("pcm");
    const samples = (msg as { data: Float32Array }).data;
    expect(samples).toBeInstanceOf(Float32Array);
    expect(samples[0]).toBe(0);
    expect(samples[1]).toBeCloseTo(16383 / 32767, 5);
    expect(samples[2]).toBe(-0.5); // -16384 / 32768
    expect(samples[3]).toBeCloseTo(1, 5);
    // Underlying buffer transferred to avoid main↔audio thread copy.
    expect(transfer).toEqual([samples.buffer]);
  });

  it("resets the master gain to the user volume before queueing a chunk", async () => {
    const sink = await createWebAudioSink({ sampleRate: 16000 });
    sink.setVolume(0.42);
    masterGain().gain.cancelScheduledValues.mockClear();
    masterGain().gain.setValueAtTime.mockClear();

    sink.playAudio(littleEndianBuffer(1, 2));

    expect(masterGain().gain.cancelScheduledValues).toHaveBeenCalledTimes(1);
    expect(masterGain().gain.setValueAtTime).toHaveBeenCalledWith(0.42, 0);
  });

  it("ignores empty or undersized chunks", async () => {
    const sink = await createWebAudioSink({ sampleRate: 16000 });
    const port = lastWorklet().port;

    sink.playAudio(new ArrayBuffer(0));
    sink.playAudio(new ArrayBuffer(1));

    expect(port.postMessage).not.toHaveBeenCalled();
  });
});

describe("interrupt", () => {
  it("ramps the master gain to 0 over the requested duration and clears the worklet queue", async () => {
    const sink = await createWebAudioSink({ sampleRate: 16000 });
    activeContext.currentTime = 2.5;
    masterGain().gain.value = 0.8;

    sink.interrupt(2000);

    expect(masterGain().gain.cancelScheduledValues).toHaveBeenCalledWith(2.5);
    expect(masterGain().gain.setValueAtTime).toHaveBeenCalledWith(0.8, 2.5);
    expect(masterGain().gain.linearRampToValueAtTime).toHaveBeenCalledWith(
      0,
      2.5 + 2,
    );

    const port = lastWorklet().port;
    const clearCall = port.postMessage.mock.calls.find(
      (call) => (call[0] as { type: string }).type === "clear",
    );
    expect(clearCall).toBeDefined();
  });
});

describe("close", () => {
  it("disconnects every node and closes the AudioContext", async () => {
    const sink: WebAudioSink = await createWebAudioSink({ sampleRate: 16000 });

    await sink.close();

    expect(lastWorklet().disconnect).toHaveBeenCalled();
    expect(masterGain().disconnect).toHaveBeenCalled();
    expect(monoGain().disconnect).toHaveBeenCalled();
    expect(spatialGain().disconnect).toHaveBeenCalled();
    expect(stereoPanner().disconnect).toHaveBeenCalled();
    expect(panner().disconnect).toHaveBeenCalled();
    expect(activeContext.close).toHaveBeenCalledTimes(1);
  });
});

describe("getByteFrequencyData", () => {
  it("forwards into the analyser node", async () => {
    const sink = await createWebAudioSink({ sampleRate: 16000 });
    const buffer = new Uint8Array(8);

    sink.getByteFrequencyData(buffer);

    expect(
      activeContext.__analysers[0].getByteFrequencyData,
    ).toHaveBeenCalledWith(buffer);
  });
});
