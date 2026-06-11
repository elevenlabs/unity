// Tests for connection factory registrations (task 2.2).
//
// Strategy:
//   1. Mock @elevenlabs/client and @elevenlabs/client/internal/unity so no
//      real WebSocket / WebRTC / MediaDevice code runs.
//   2. Set up the same Emscripten-global stubs used by the primitives e2e test.
//   3. Manually register the $EL_ConnectionFactories entries with the
//      primitives-layer $EL_RegisterFactory (bypassing the jslib __postset,
//      which is an Emscripten build-time concern resolved in task 2.4).
//   4. Drive each factory through EL_InvokeFactoryAsync and assert:
//      a) the SDK's static .create() method was called with the supplied config.
//      b) the settled result is a valid JsObject handle ({ $ref: N }).
//      c) methods on the returned handle dispatch correctly via EL_ObjectCallAsync.

import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  EL_InvokeFactoryAsync,
  EL_ObjectCallAsync,
  EL_ObjectRelease,
} from "../primitives/dispatcher.js";
import {
  $EL_AllocateFunction,
  $EL_AllocateObject,
  $EL_LookupFactory,
  $EL_LookupObject,
  $EL_RegisterFactory,
  $EL_ReleaseFunction,
  $EL_ReleaseObject,
} from "../primitives/registries.js";
import { $EL_EncodeReturn, $EL_Rehydrate } from "../primitives/marshalling.js";
import { $EL_Settle } from "../primitives/promise-settle.js";
import { $EL_InvokeCallback } from "../primitives/callbacks.js";
import { $EL_ConnectionFactories } from "./factories.js";

// ---------------------------------------------------------------------------
// SDK mocks — hoisted by Vitest's transformer so they are in effect before the
// factories module is imported and $EL_ConnectionFactories is constructed.
// ---------------------------------------------------------------------------

vi.mock("@elevenlabs/client", () => {
  const mockWsConn = {
    sendMessage: vi.fn(),
    close: vi.fn().mockResolvedValue(undefined),
    conversationId: "ws-conv-id",
  };
  const mockRtcConn = {
    sendMessage: vi.fn(),
    close: vi.fn().mockResolvedValue(undefined),
    conversationId: "rtc-conv-id",
  };
  return {
    WebSocketConnection: { create: vi.fn().mockResolvedValue(mockWsConn) },
    WebRTCConnection: { create: vi.fn().mockResolvedValue(mockRtcConn) },
    createConnection: vi.fn().mockResolvedValue(mockWsConn),
  };
});

vi.mock("@elevenlabs/client/internal/unity", () => {
  const mockInput = {
    isMuted: vi.fn().mockReturnValue(false),
    close: vi.fn().mockResolvedValue(undefined),
    getVolume: vi.fn().mockReturnValue(0),
  };
  const mockOutput = {
    setVolume: vi.fn(),
    close: vi.fn().mockResolvedValue(undefined),
    getVolume: vi.fn().mockReturnValue(0),
  };
  return {
    MediaDeviceInput: { create: vi.fn().mockResolvedValue(mockInput) },
    MediaDeviceOutput: { create: vi.fn().mockResolvedValue(mockOutput) },
  };
});

// ---------------------------------------------------------------------------
// Import the mocked modules so we can access the vi.fn() references.
// ---------------------------------------------------------------------------

import {
  WebSocketConnection,
  WebRTCConnection,
  createConnection,
} from "@elevenlabs/client";
import {
  MediaDeviceInput,
  MediaDeviceOutput,
} from "@elevenlabs/client/internal/unity";

// ---------------------------------------------------------------------------
// Emscripten-global stub helpers (mirrors the e2e.test.ts setup).
// ---------------------------------------------------------------------------

const heap = new Map<number, string>();
let nextPtr = 1000;

function makePtr(s: string): number {
  const ptr = nextPtr++;
  heap.set(ptr, s);
  return ptr;
}

function readPtr(ptr: number): string {
  return heap.get(ptr) ?? "";
}

beforeEach(() => {
  heap.clear();
  nextPtr = 1000;

  vi.stubGlobal("_EL_Objects", {});
  vi.stubGlobal("_EL_Functions", {});
  vi.stubGlobal("_EL_Factories", {});
  vi.stubGlobal("_EL_NextHandleId", 1);

  vi.stubGlobal("_EL_AllocateObject", $EL_AllocateObject);
  vi.stubGlobal("_EL_AllocateFunction", $EL_AllocateFunction);
  vi.stubGlobal("_EL_LookupFactory", $EL_LookupFactory);
  vi.stubGlobal("_EL_LookupObject", $EL_LookupObject);
  vi.stubGlobal("_EL_ReleaseObject", $EL_ReleaseObject);
  vi.stubGlobal("_EL_ReleaseFunction", $EL_ReleaseFunction);

  vi.stubGlobal("_EL_Rehydrate", $EL_Rehydrate);
  vi.stubGlobal("_EL_EncodeReturn", $EL_EncodeReturn);
  vi.stubGlobal("_EL_InvokeCallback", $EL_InvokeCallback);
  vi.stubGlobal("_EL_Settle", $EL_Settle);
  vi.stubGlobal("_EL_BridgeName", "__ElevenLabsBridge__");
  vi.stubGlobal("SendMessage", vi.fn());

  vi.stubGlobal("UTF8ToString", (ptr: number) => heap.get(ptr) ?? "");
  vi.stubGlobal("lengthBytesUTF8", (s: string) => s.length);
  vi.stubGlobal("_malloc", (n: number) => {
    const ptr = nextPtr;
    nextPtr += Math.max(n, 1);
    return ptr;
  });
  vi.stubGlobal("stringToUTF8", (s: string, ptr: number) => {
    heap.set(ptr, s);
  });

  // Register all connection factories the same way the jslib __postset would —
  // bypasses the timing concern that task 2.4 resolves.
  for (const [name, fn] of Object.entries($EL_ConnectionFactories)) {
    $EL_RegisterFactory(name, fn as (...args: unknown[]) => unknown);
  }
});

// ---------------------------------------------------------------------------
// Helper: last SendMessage payload from the settled promise channel.
// ---------------------------------------------------------------------------

type ElGlobals = typeof globalThis & {
  SendMessage: ReturnType<typeof vi.fn>;
};
const g = globalThis as ElGlobals;

function lastSendMessage(): string {
  const calls = g.SendMessage.mock.calls;
  return calls[calls.length - 1][2] as string;
}

// ---------------------------------------------------------------------------
// Tests.
// ---------------------------------------------------------------------------

const SHAPE_OBJECT = 1;
const SHAPE_VOID = 3;

const wsConfig = { agentId: "agent-123", connectionType: "websocket" };
const rtcConfig = { agentId: "agent-123", connectionType: "webrtc" };
const inputConfig = { format: "pcm", sampleRate: 16000 };
const outputConfig = { format: "pcm", sampleRate: 16000 };

describe("createWebSocketConnection factory", () => {
  it("calls WebSocketConnection.create with the supplied config", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createWebSocketConnection"),
      makePtr(JSON.stringify([wsConfig])),
      SHAPE_OBJECT,
      1,
    );
    await Promise.resolve();

    expect(WebSocketConnection.create).toHaveBeenCalledWith(wsConfig);
  });

  it("settles with a JsObject handle wrapping the connection", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createWebSocketConnection"),
      makePtr(JSON.stringify([wsConfig])),
      SHAPE_OBJECT,
      1,
    );
    await Promise.resolve();

    const msg = lastSendMessage();
    expect(msg).toMatch(/^1:ok:\{"\$ref":\d+\}$/);
  });

  it("dispatches sendMessage via EL_ObjectCallAsync", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createWebSocketConnection"),
      makePtr(JSON.stringify([wsConfig])),
      SHAPE_OBJECT,
      1,
    );
    await Promise.resolve();
    const handle = JSON.parse(lastSendMessage().slice("1:ok:".length))
      .$ref as number;

    g.SendMessage.mockClear();

    EL_ObjectCallAsync(
      handle,
      makePtr("sendMessage"),
      makePtr(JSON.stringify([{ type: "user_message", text: "hello" }])),
      SHAPE_VOID,
      2,
    );
    await Promise.resolve();

    expect(lastSendMessage()).toBe("2:ok:null");

    const mockWsConn = await vi.mocked(WebSocketConnection.create).mock
      .results[0].value;
    expect(mockWsConn.sendMessage).toHaveBeenCalledWith({
      type: "user_message",
      text: "hello",
    });
  });

  it("releases the handle via EL_ObjectRelease", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createWebSocketConnection"),
      makePtr(JSON.stringify([wsConfig])),
      SHAPE_OBJECT,
      1,
    );
    await Promise.resolve();
    const handle = JSON.parse(lastSendMessage().slice("1:ok:".length))
      .$ref as number;

    EL_ObjectRelease(handle);

    const objects = (g as unknown as { _EL_Objects: Record<number, unknown> })
      ._EL_Objects;
    expect(objects[handle]).toBeUndefined();
  });
});

describe("createWebRTCConnection factory", () => {
  it("calls WebRTCConnection.create with the supplied config", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createWebRTCConnection"),
      makePtr(JSON.stringify([rtcConfig])),
      SHAPE_OBJECT,
      10,
    );
    await Promise.resolve();

    expect(WebRTCConnection.create).toHaveBeenCalledWith(rtcConfig);
  });

  it("settles with a JsObject handle", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createWebRTCConnection"),
      makePtr(JSON.stringify([rtcConfig])),
      SHAPE_OBJECT,
      10,
    );
    await Promise.resolve();

    expect(lastSendMessage()).toMatch(/^10:ok:\{"\$ref":\d+\}$/);
  });
});

describe("createConnection factory", () => {
  it("calls createConnection with the supplied config", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createConnection"),
      makePtr(JSON.stringify([wsConfig])),
      SHAPE_OBJECT,
      20,
    );
    await Promise.resolve();

    expect(createConnection).toHaveBeenCalledWith(wsConfig);
  });

  it("settles with a JsObject handle", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createConnection"),
      makePtr(JSON.stringify([wsConfig])),
      SHAPE_OBJECT,
      20,
    );
    await Promise.resolve();

    expect(lastSendMessage()).toMatch(/^20:ok:\{"\$ref":\d+\}$/);
  });
});

describe("createMediaDeviceInput factory", () => {
  it("calls MediaDeviceInput.create with the supplied config", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createMediaDeviceInput"),
      makePtr(JSON.stringify([inputConfig])),
      SHAPE_OBJECT,
      30,
    );
    await Promise.resolve();

    expect(MediaDeviceInput.create).toHaveBeenCalledWith(inputConfig);
  });

  it("settles with a JsObject handle", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createMediaDeviceInput"),
      makePtr(JSON.stringify([inputConfig])),
      SHAPE_OBJECT,
      30,
    );
    await Promise.resolve();

    expect(lastSendMessage()).toMatch(/^30:ok:\{"\$ref":\d+\}$/);
  });

  it("dispatches isMuted via EL_ObjectCallAsync", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createMediaDeviceInput"),
      makePtr(JSON.stringify([inputConfig])),
      SHAPE_OBJECT,
      30,
    );
    await Promise.resolve();
    const handle = JSON.parse(lastSendMessage().slice("30:ok:".length))
      .$ref as number;

    g.SendMessage.mockClear();

    const SHAPE_VALUE = 0;
    EL_ObjectCallAsync(
      handle,
      makePtr("isMuted"),
      makePtr("[]"),
      SHAPE_VALUE,
      31,
    );
    await Promise.resolve();

    expect(lastSendMessage()).toBe("31:ok:false");
  });
});

describe("createMediaDeviceOutput factory", () => {
  it("calls MediaDeviceOutput.create with the supplied config", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createMediaDeviceOutput"),
      makePtr(JSON.stringify([outputConfig])),
      SHAPE_OBJECT,
      40,
    );
    await Promise.resolve();

    expect(MediaDeviceOutput.create).toHaveBeenCalledWith(outputConfig);
  });

  it("settles with a JsObject handle", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createMediaDeviceOutput"),
      makePtr(JSON.stringify([outputConfig])),
      SHAPE_OBJECT,
      40,
    );
    await Promise.resolve();

    expect(lastSendMessage()).toMatch(/^40:ok:\{"\$ref":\d+\}$/);
  });

  it("dispatches setVolume via EL_ObjectCallAsync", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createMediaDeviceOutput"),
      makePtr(JSON.stringify([outputConfig])),
      SHAPE_OBJECT,
      40,
    );
    await Promise.resolve();
    const handle = JSON.parse(lastSendMessage().slice("40:ok:".length))
      .$ref as number;

    g.SendMessage.mockClear();

    EL_ObjectCallAsync(
      handle,
      makePtr("setVolume"),
      makePtr("[0.5]"),
      SHAPE_VOID,
      41,
    );
    await Promise.resolve();

    expect(lastSendMessage()).toBe("41:ok:null");

    const mockOutput = await vi.mocked(MediaDeviceOutput.create).mock.results[0]
      .value;
    expect(mockOutput.setVolume).toHaveBeenCalledWith(0.5);
  });
});
