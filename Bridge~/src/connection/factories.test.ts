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
  $EL_AllocString,
  $EL_DecodeReturnShape,
  $EL_ParseArgs,
  $EL_SettleWith,
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
import { connectionFactories } from "./factories.js";

// ---------------------------------------------------------------------------
// SDK mocks — hoisted by Vitest's transformer so they are in effect before the
// factories module is imported and $EL_ConnectionFactories is constructed.
// ---------------------------------------------------------------------------

vi.mock("@elevenlabs/client/internal/unity", () => {
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
  const mockInput = {
    isMuted: vi.fn().mockReturnValue(false),
    close: vi.fn().mockResolvedValue(undefined),
    getVolume: vi.fn().mockReturnValue(0),
  };
  return {
    WebSocketConnection: { create: vi.fn().mockResolvedValue(mockWsConn) },
    WebRTCConnection: { create: vi.fn().mockResolvedValue(mockRtcConn) },
    createConnection: vi.fn().mockResolvedValue(mockWsConn),
    MediaDeviceInput: { create: vi.fn().mockResolvedValue(mockInput) },
  };
});

// ---------------------------------------------------------------------------
// Import the mocked modules so we can access the vi.fn() references.
// ---------------------------------------------------------------------------

import {
  WebSocketConnection,
  WebRTCConnection,
  createConnection,
  MediaDeviceInput,
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

  vi.stubGlobal("EL_Objects", {});
  vi.stubGlobal("EL_Functions", {});
  vi.stubGlobal("EL_Factories", {});
  vi.stubGlobal("EL_NextHandleId", 1);

  vi.stubGlobal("EL_AllocateObject", $EL_AllocateObject);
  vi.stubGlobal("EL_AllocateFunction", $EL_AllocateFunction);
  vi.stubGlobal("EL_LookupFactory", $EL_LookupFactory);
  vi.stubGlobal("EL_LookupObject", $EL_LookupObject);
  vi.stubGlobal("EL_ReleaseObject", $EL_ReleaseObject);
  vi.stubGlobal("EL_ReleaseFunction", $EL_ReleaseFunction);

  vi.stubGlobal("EL_Rehydrate", $EL_Rehydrate);
  vi.stubGlobal("EL_EncodeReturn", $EL_EncodeReturn);
  vi.stubGlobal("EL_InvokeCallback", $EL_InvokeCallback);
  vi.stubGlobal("EL_AllocString", $EL_AllocString);
  vi.stubGlobal("EL_DecodeReturnShape", $EL_DecodeReturnShape);
  vi.stubGlobal("EL_ParseArgs", $EL_ParseArgs);
  vi.stubGlobal("EL_SettleWith", $EL_SettleWith);
  vi.stubGlobal("EL_Settle", $EL_Settle);
  vi.stubGlobal("EL_SettlePtr", 42);
  vi.stubGlobal("dynCall_viii", vi.fn());
  vi.stubGlobal(
    "stringToNewUTF8",
    vi.fn(() => nextPtr++),
  );
  vi.stubGlobal("_free", vi.fn());
  // SendMessage still used by callbacks.ts (not yet rewritten to DynCall).
  vi.stubGlobal("EL_BridgeName", "__ElevenLabsBridge__");
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

  // Register all connection factories the same way the bundled .jslib does at
  // runtime (see Bridge~/src/connection/index.ts) — bypasses the bundler's
  // postset injection, which is an Emscripten build-time concern.
  for (const [name, fn] of Object.entries(connectionFactories)) {
    $EL_RegisterFactory(name, fn as (...args: unknown[]) => unknown);
  }
});

// ---------------------------------------------------------------------------
// Helper: reconstruct "promiseId:status:payload" from the last DynCall settle.
// ---------------------------------------------------------------------------

type ElGlobals = typeof globalThis & {
  SendMessage: ReturnType<typeof vi.fn>;
  dynCall_viii: ReturnType<typeof vi.fn>;
  stringToNewUTF8: ReturnType<typeof vi.fn>;
};
const g = globalThis as ElGlobals;

function lastSettleMessage(): string {
  const idx = g.dynCall_viii.mock.calls.length - 1;
  const [, promiseId, statusCode] = g.dynCall_viii.mock.calls[idx];
  const status = statusCode === 0 ? "ok" : "err";
  const payload = g.stringToNewUTF8.mock.calls[idx][0] as string;
  return `${promiseId as number}:${status}:${payload}`;
}

// ---------------------------------------------------------------------------
// Tests.
// ---------------------------------------------------------------------------

const SHAPE_OBJECT = 1;
const SHAPE_VOID = 3;

const wsConfig = { agentId: "agent-123", connectionType: "websocket" };
const rtcConfig = { agentId: "agent-123", connectionType: "webrtc" };
const inputConfig = { format: "pcm", sampleRate: 16000 };

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

  it("forwards dynamicVariables through to WebSocketConnection.create with string/number/bool values preserved", async () => {
    // The C# BuildSessionConfig serializes IReadOnlyDictionary<string, object>
    // values through Newtonsoft.Json's JObject.FromObject; the bridge layer is
    // a pure passthrough, so the SDK should see the same primitive runtime
    // types it would if a JS caller had constructed the config directly.
    const configWithDynVars = {
      ...wsConfig,
      dynamicVariables: { color: "red", count: 3, isReady: true },
    };
    EL_InvokeFactoryAsync(
      makePtr("createWebSocketConnection"),
      makePtr(JSON.stringify([configWithDynVars])),
      SHAPE_OBJECT,
      2,
    );
    await Promise.resolve();

    expect(WebSocketConnection.create).toHaveBeenCalledWith(configWithDynVars);
  });

  it("forwards overrides / userId / customLlmExtraBody through to WebSocketConnection.create unchanged", async () => {
    // C# ConversationOptions.Overrides / UserId / CustomLlmExtraBody flow
    // straight through the bridge into the SDK's SessionConfig contract: the
    // JS SDK's constructOverrides() is what re-keys them onto the wire. This
    // test pins the bridge boundary — if the SDK adds a sanitiser or rename
    // step, it'll show up as a diff here before users hit it at runtime.
    const configWithOverrides = {
      ...wsConfig,
      userId: "user-42",
      customLlmExtraBody: { temperature: 0.7, max_tokens: 256 },
      overrides: {
        agent: {
          firstMessage: "Hello!",
          language: "en",
          // Inner prompt object stays in snake_case wire-shape — JS SDK
          // forwards this nested object directly to the server.
          prompt: { prompt: "Be terse.", llm: "gpt-4o-mini" },
        },
        tts: {
          voiceId: "voice-42",
          stability: 0.0,
          speed: 1.0,
          similarityBoost: 0.8,
        },
        conversation: { textOnly: true },
      },
    };
    EL_InvokeFactoryAsync(
      makePtr("createWebSocketConnection"),
      makePtr(JSON.stringify([configWithOverrides])),
      SHAPE_OBJECT,
      3,
    );
    await Promise.resolve();

    expect(WebSocketConnection.create).toHaveBeenCalledWith(
      configWithOverrides,
    );
  });

  it("settles with a JsObject handle wrapping the connection", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createWebSocketConnection"),
      makePtr(JSON.stringify([wsConfig])),
      SHAPE_OBJECT,
      1,
    );
    await Promise.resolve();

    const msg = lastSettleMessage();
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
    const handle = JSON.parse(lastSettleMessage().slice("1:ok:".length))
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

    expect(lastSettleMessage()).toBe("2:ok:null");

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
    const handle = JSON.parse(lastSettleMessage().slice("1:ok:".length))
      .$ref as number;

    EL_ObjectRelease(handle);

    const objects = (g as unknown as { EL_Objects: Record<number, unknown> })
      .EL_Objects;
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

    expect(lastSettleMessage()).toMatch(/^10:ok:\{"\$ref":\d+\}$/);
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

    expect(lastSettleMessage()).toMatch(/^20:ok:\{"\$ref":\d+\}$/);
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

    expect(lastSettleMessage()).toMatch(/^30:ok:\{"\$ref":\d+\}$/);
  });

  it("dispatches isMuted via EL_ObjectCallAsync", async () => {
    EL_InvokeFactoryAsync(
      makePtr("createMediaDeviceInput"),
      makePtr(JSON.stringify([inputConfig])),
      SHAPE_OBJECT,
      30,
    );
    await Promise.resolve();
    const handle = JSON.parse(lastSettleMessage().slice("30:ok:".length))
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

    expect(lastSettleMessage()).toBe("31:ok:false");
  });
});

// createMediaDeviceOutput was removed when the WebSocket arm migrated to
// createWebAudioSink (see Bridge~/src/connection/web-audio-sink.ts +
// Runtime/WebGL/Bridged/WebAudioBackedOutput.cs). The WebGL output's
// factory + dispatch surface is covered by web-audio-sink.test.ts now.
