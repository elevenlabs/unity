// End-to-end happy-path session test for the connection + audio-glue layer
// (Plan B task 2.5).
//
// Drives the full WebSocket session lifecycle through the primitive dispatcher,
// combining factory invocations from factories.test.ts and audio-glue coverage
// from audio-glue.test.ts into a single integrated flow. Individual primitive
// and factory behaviours are tested in their respective unit-test files;
// this file covers cross-factory sequencing and the end-to-end message path.
//
// Flow:
//   1. createWebSocketConnection  → JsObject handle for the connection
//   2. createMediaDeviceInput     → JsObject handle for the input controller
//   3. createMediaDeviceOutput    → JsObject handle for the output controller
//   4. attachDefaultAudio         → JsFunction handle for the detach closure
//   5. fire incoming messages     → assert bridge callback receives stripped events
//   6. send a user message        → assert connection.sendMessage is called
//   7. invoke detach handle       → assert both SDK detach functions are called

import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  EL_FunctionCallAsync,
  EL_InvokeFactoryAsync,
  EL_ObjectCallAsync,
  EL_ObjectRelease,
  EL_FunctionRelease,
} from "../primitives/dispatcher.js";
import {
  $EL_AllocateFunction,
  $EL_AllocateObject,
  $EL_LookupFactory,
  $EL_LookupFunction,
  $EL_LookupObject,
  $EL_RegisterFactory,
  $EL_ReleaseFunction,
  $EL_ReleaseObject,
} from "../primitives/registries.js";
import { $EL_EncodeReturn, $EL_Rehydrate } from "../primitives/marshalling.js";
import { $EL_Settle } from "../primitives/promise-settle.js";
import { $EL_InvokeCallback } from "../primitives/callbacks.js";
import { $EL_ConnectionFactories } from "./factories.js";
import { $EL_AudioGlueFactories } from "./audio-glue.js";
import type { IncomingSocketEvent } from "@elevenlabs/client/internal/unity";

// ---------------------------------------------------------------------------
// SDK mocks — hoisted so they're in effect before factories/audio-glue import.
// ---------------------------------------------------------------------------

vi.mock("@elevenlabs/client/internal/unity", () => {
  const mockWsConn = {
    sendMessage: vi.fn(),
    onMessage: vi.fn(),
    close: vi.fn().mockResolvedValue(undefined),
    conversationId: "test-conv-id",
  };
  return {
    WebSocketConnection: { create: vi.fn().mockResolvedValue(mockWsConn) },
    WebRTCConnection: {
      create: vi.fn().mockResolvedValue({
        close: vi.fn().mockResolvedValue(undefined),
      }),
    },
    createConnection: vi.fn().mockResolvedValue(mockWsConn),
    MediaDeviceInput: {
      create: vi
        .fn()
        .mockResolvedValue({ close: vi.fn().mockResolvedValue(undefined) }),
    },
    MediaDeviceOutput: {
      create: vi
        .fn()
        .mockResolvedValue({ close: vi.fn().mockResolvedValue(undefined) }),
    },
    attachInputToConnection: vi.fn(() => vi.fn()),
    attachConnectionToOutput: vi.fn(() => vi.fn()),
  };
});

import {
  WebSocketConnection,
  attachInputToConnection,
  attachConnectionToOutput,
} from "@elevenlabs/client/internal/unity";

// ---------------------------------------------------------------------------
// Emscripten-global stub helpers (mirrors the e2e.test.ts setup in primitives).
// ---------------------------------------------------------------------------

const heap = new Map<number, string>();
let nextPtr = 1000;

function makePtr(s: string): number {
  const ptr = nextPtr++;
  heap.set(ptr, s);
  return ptr;
}

type ElGlobals = typeof globalThis & {
  _EL_Objects: Record<number, unknown>;
  _EL_Functions: Record<number, (...args: unknown[]) => unknown>;
  _EL_CallbackPtr: number;
  dynCall_vii: ReturnType<typeof vi.fn>;
  dynCall_viii: ReturnType<typeof vi.fn>;
  stringToNewUTF8: ReturnType<typeof vi.fn>;
};
const g = globalThis as ElGlobals;

// Helper: reconstruct "promiseId:status:payload" from the last DynCall settle.
function lastSettleMessage(): string {
  const idx = g.dynCall_viii.mock.calls.length - 1;
  const [, promiseId, statusCode] = g.dynCall_viii.mock.calls[idx];
  const status = statusCode === 0 ? "ok" : "err";
  const payload = g.stringToNewUTF8.mock.calls[idx][0] as string;
  return `${promiseId as number}:${status}:${payload}`;
}

// Extracts the numeric value after the second colon in a "pid:ok:{…}" message.
function parsePayload(msg: string): unknown {
  const secondColon = msg.indexOf(":", msg.indexOf(":") + 1);
  return JSON.parse(msg.slice(secondColon + 1));
}

beforeEach(() => {
  heap.clear();
  nextPtr = 1000;
  vi.clearAllMocks();

  vi.stubGlobal("_EL_Objects", {});
  vi.stubGlobal("_EL_Functions", {});
  vi.stubGlobal("_EL_Factories", {});
  vi.stubGlobal("_EL_NextHandleId", 1);

  vi.stubGlobal("_EL_AllocateObject", $EL_AllocateObject);
  vi.stubGlobal("_EL_AllocateFunction", $EL_AllocateFunction);
  vi.stubGlobal("_EL_LookupFactory", $EL_LookupFactory);
  vi.stubGlobal("_EL_LookupObject", $EL_LookupObject);
  vi.stubGlobal("_EL_LookupFunction", $EL_LookupFunction);
  vi.stubGlobal("_EL_ReleaseObject", $EL_ReleaseObject);
  vi.stubGlobal("_EL_ReleaseFunction", $EL_ReleaseFunction);

  vi.stubGlobal("_EL_Rehydrate", $EL_Rehydrate);
  vi.stubGlobal("_EL_EncodeReturn", $EL_EncodeReturn);
  vi.stubGlobal("_EL_InvokeCallback", $EL_InvokeCallback);
  vi.stubGlobal("_EL_Settle", $EL_Settle);
  vi.stubGlobal("_EL_SettlePtr", 42);
  vi.stubGlobal("dynCall_viii", vi.fn());
  vi.stubGlobal(
    "stringToNewUTF8",
    vi.fn(() => nextPtr++),
  );
  vi.stubGlobal("_free", vi.fn());
  vi.stubGlobal("_EL_CallbackPtr", 100);
  vi.stubGlobal("dynCall_vii", vi.fn());

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

  for (const [name, fn] of Object.entries($EL_ConnectionFactories)) {
    $EL_RegisterFactory(name, fn as (...args: unknown[]) => unknown);
  }
  for (const [name, fn] of Object.entries($EL_AudioGlueFactories)) {
    $EL_RegisterFactory(name, fn as (...args: unknown[]) => unknown);
  }
});

// ---------------------------------------------------------------------------
// Return-shape constants (match C# BridgeReturnShape enum).
// ---------------------------------------------------------------------------

const SHAPE_OBJECT = 1;
const SHAPE_FUNCTION = 2;
const SHAPE_VOID = 3;

// Stable bridge-callback handle, as C# would allocate.
const CB_HANDLE = 9001;

const wsConfig = { agentId: "agent-test", connectionType: "websocket" };
const inputConfig = { format: "pcm", sampleRate: 16000 };
const outputConfig = { format: "pcm", sampleRate: 16000 };

// ---------------------------------------------------------------------------
// Session setup helper.
// ---------------------------------------------------------------------------

// Drives all four factory invocations and returns the resulting handles.
async function startSession(): Promise<{
  connHandle: number;
  inputHandle: number;
  outputHandle: number;
  detachHandle: number;
}> {
  EL_InvokeFactoryAsync(
    makePtr("createWebSocketConnection"),
    makePtr(JSON.stringify([wsConfig])),
    SHAPE_OBJECT,
    1,
  );
  await Promise.resolve();
  const connHandle = (parsePayload(lastSettleMessage()) as { $ref: number })
    .$ref;

  EL_InvokeFactoryAsync(
    makePtr("createMediaDeviceInput"),
    makePtr(JSON.stringify([inputConfig])),
    SHAPE_OBJECT,
    2,
  );
  await Promise.resolve();
  const inputHandle = (parsePayload(lastSettleMessage()) as { $ref: number })
    .$ref;

  EL_InvokeFactoryAsync(
    makePtr("createMediaDeviceOutput"),
    makePtr(JSON.stringify([outputConfig])),
    SHAPE_OBJECT,
    3,
  );
  await Promise.resolve();
  const outputHandle = (parsePayload(lastSettleMessage()) as { $ref: number })
    .$ref;

  EL_InvokeFactoryAsync(
    makePtr("attachDefaultAudio"),
    makePtr(
      JSON.stringify([
        { $ref: connHandle },
        { $ref: inputHandle },
        { $ref: outputHandle },
        { $cb: CB_HANDLE },
      ]),
    ),
    SHAPE_FUNCTION,
    4,
  );
  await Promise.resolve();
  const detachHandle = (parsePayload(lastSettleMessage()) as { $fn: number })
    .$fn;

  return { connHandle, inputHandle, outputHandle, detachHandle };
}

// ---------------------------------------------------------------------------
// Tests.
// ---------------------------------------------------------------------------

describe("WebSocket session happy path", () => {
  it("factory invocations (steps 1-3) produce three distinct monotonically-increasing JsObject handles", async () => {
    const { connHandle, inputHandle, outputHandle } = await startSession();

    expect(connHandle).toBeGreaterThan(0);
    expect(inputHandle).toBeGreaterThan(connHandle);
    expect(outputHandle).toBeGreaterThan(inputHandle);
    expect(WebSocketConnection.create).toHaveBeenCalledWith(wsConfig);
  });

  it("attachDefaultAudio (step 4) returns a JsFunction handle and calls both SDK wiring helpers", async () => {
    const { detachHandle } = await startSession();

    expect(detachHandle).toBeGreaterThan(0);
    expect(g._EL_Functions[detachHandle]).toBeTypeOf("function");
    expect(attachInputToConnection).toHaveBeenCalledTimes(1);
    expect(attachConnectionToOutput).toHaveBeenCalledTimes(1);
  });

  it("audio events (step 5) have audio_base_64 stripped before reaching the bridge callback", async () => {
    const { connHandle } = await startSession();

    const connection = $EL_LookupObject(connHandle) as {
      onMessage: ReturnType<typeof vi.fn>;
    };
    const wrappedCb = connection.onMessage.mock.calls[0][0] as (
      e: IncomingSocketEvent,
    ) => void;

    wrappedCb({
      type: "audio",
      audio_event: {
        audio_base_64: "ZmFrZQ==",
        event_id: 99,
        alignment: {
          chars: [],
          char_start_times_ms: [],
          char_durations_ms: [],
        },
      },
    } as unknown as IncomingSocketEvent);

    expect(g.dynCall_vii).toHaveBeenCalledWith(
      100,
      CB_HANDLE,
      expect.any(Number),
    );
    const payloadStr = g.stringToNewUTF8.mock.calls[
      g.stringToNewUTF8.mock.calls.length - 1
    ][0] as string;
    const event = JSON.parse(payloadStr) as {
      type: string;
      audio_event: { event_id: number };
    };
    expect(event.type).toBe("audio");
    expect(event.audio_event).not.toHaveProperty("audio_base_64");
    expect(event.audio_event).toHaveProperty("event_id", 99);
  });

  it("non-audio events (step 5) pass through to the bridge callback without modification", async () => {
    const { connHandle } = await startSession();

    const connection = $EL_LookupObject(connHandle) as {
      onMessage: ReturnType<typeof vi.fn>;
    };
    const wrappedCb = connection.onMessage.mock.calls[0][0] as (
      e: IncomingSocketEvent,
    ) => void;

    const interruption = {
      type: "interruption",
      interruption_event: { reason: "agent" },
    } as unknown as IncomingSocketEvent;
    wrappedCb(interruption);

    expect(g.dynCall_vii).toHaveBeenCalledWith(
      100,
      CB_HANDLE,
      expect.any(Number),
    );
    const payloadStr = g.stringToNewUTF8.mock.calls[
      g.stringToNewUTF8.mock.calls.length - 1
    ][0] as string;
    const event = JSON.parse(payloadStr) as { type: string };
    expect(event.type).toBe("interruption");
  });

  it("EL_ObjectCallAsync (step 6) dispatches sendMessage to the JS connection", async () => {
    const { connHandle } = await startSession();

    EL_ObjectCallAsync(
      connHandle,
      makePtr("sendMessage"),
      makePtr(
        JSON.stringify([{ type: "user_message", text: "hello from Unity" }]),
      ),
      SHAPE_VOID,
      5,
    );
    await Promise.resolve();

    expect(lastSettleMessage()).toBe("5:ok:null");
    const mockWsConn = await vi.mocked(WebSocketConnection.create).mock
      .results[0].value;
    expect(mockWsConn.sendMessage).toHaveBeenCalledWith({
      type: "user_message",
      text: "hello from Unity",
    });
  });

  it("EL_FunctionCallAsync on the detach handle (step 7) calls both SDK detach functions", async () => {
    const detachIn = vi.fn();
    const detachOut = vi.fn();
    vi.mocked(attachInputToConnection).mockReturnValueOnce(detachIn);
    vi.mocked(attachConnectionToOutput).mockReturnValueOnce(detachOut);

    const { detachHandle } = await startSession();

    expect(detachIn).not.toHaveBeenCalled();
    expect(detachOut).not.toHaveBeenCalled();

    EL_FunctionCallAsync(
      detachHandle,
      makePtr(JSON.stringify([])),
      SHAPE_VOID,
      6,
    );
    await Promise.resolve();

    expect(lastSettleMessage()).toBe("6:ok:null");
    expect(detachIn).toHaveBeenCalledTimes(1);
    expect(detachOut).toHaveBeenCalledTimes(1);
  });

  it("EL_ObjectRelease and EL_FunctionRelease clear all handles after teardown", async () => {
    const { connHandle, inputHandle, outputHandle, detachHandle } =
      await startSession();

    EL_FunctionCallAsync(
      detachHandle,
      makePtr(JSON.stringify([])),
      SHAPE_VOID,
      7,
    );
    await Promise.resolve();

    EL_ObjectRelease(connHandle);
    EL_ObjectRelease(inputHandle);
    EL_ObjectRelease(outputHandle);
    EL_FunctionRelease(detachHandle);

    expect(g._EL_Objects[connHandle]).toBeUndefined();
    expect(g._EL_Objects[inputHandle]).toBeUndefined();
    expect(g._EL_Objects[outputHandle]).toBeUndefined();
    expect(g._EL_Functions[detachHandle]).toBeUndefined();
  });
});
