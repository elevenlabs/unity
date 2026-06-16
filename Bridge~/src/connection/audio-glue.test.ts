// Tests for the audio-glue module (Plan B task 2.3).
//
// Two suites:
//   1. withoutAudioPayload — pure-function tests, no Emscripten setup needed.
//   2. attachDefaultAudio factory — mocks the SDK's attach* helpers, sets up
//      the same Emscripten-global stubs as factories.test.ts, registers the
//      audio-glue factory, drives it through EL_InvokeFactoryAsync, asserts
//      composition + return-handle correctness.

import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  EL_FunctionCallAsync,
  EL_InvokeFactoryAsync,
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
import type { IncomingSocketEvent } from "@elevenlabs/client/internal/unity";

// ---------------------------------------------------------------------------
// SDK mock — hoisted so it's in effect when audio-glue imports.
// ---------------------------------------------------------------------------

vi.mock("@elevenlabs/client/internal/unity", () => ({
  attachInputToConnection: vi.fn(() => vi.fn()),
  attachConnectionToOutput: vi.fn(() => vi.fn()),
}));

import {
  attachInputToConnection,
  attachConnectionToOutput,
} from "@elevenlabs/client/internal/unity";
import { $EL_AudioGlueFactories, withoutAudioPayload } from "./audio-glue.js";

// ---------------------------------------------------------------------------
// Suite 1 — withoutAudioPayload (pure function, no globals).
// ---------------------------------------------------------------------------

describe("withoutAudioPayload", () => {
  it("strips audio_base_64 from `audio` events and preserves event_id/alignment", () => {
    const received: IncomingSocketEvent[] = [];
    const wrapped = withoutAudioPayload((e) => received.push(e));

    const audioEvent = {
      type: "audio",
      audio_event: {
        audio_base_64: "ZmFrZQ==",
        event_id: 42,
        alignment: {
          chars: [],
          char_start_times_ms: [],
          char_durations_ms: [],
        },
      },
    } as unknown as IncomingSocketEvent;

    wrapped(audioEvent);

    expect(received).toHaveLength(1);
    const out = received[0] as { type: string; audio_event: object };
    expect(out.type).toBe("audio");
    expect(out.audio_event).not.toHaveProperty("audio_base_64");
    expect(out.audio_event).toEqual({
      event_id: 42,
      alignment: { chars: [], char_start_times_ms: [], char_durations_ms: [] },
    });
  });

  it("forwards non-audio events unchanged", () => {
    const received: IncomingSocketEvent[] = [];
    const wrapped = withoutAudioPayload((e) => received.push(e));

    const interruption = {
      type: "interruption",
      interruption_event: { reason: "agent" },
    } as unknown as IncomingSocketEvent;
    const transcript = {
      type: "user_transcript",
      user_transcription_event: { user_transcript: "hi" },
    } as unknown as IncomingSocketEvent;

    wrapped(interruption);
    wrapped(transcript);

    expect(received).toEqual([interruption, transcript]);
  });

  it("does not mutate the input event", () => {
    const wrapped = withoutAudioPayload(() => {});
    const source = {
      type: "audio",
      audio_event: { audio_base_64: "ZmFrZQ==", event_id: 7 },
    } as unknown as IncomingSocketEvent;

    wrapped(source);

    expect(
      (source as unknown as { audio_event: { audio_base_64: string } })
        .audio_event.audio_base_64,
    ).toBe("ZmFrZQ==");
  });

  it("returns independent wrappers for distinct callbacks", () => {
    const a = vi.fn();
    const b = vi.fn();
    const wA = withoutAudioPayload(a);
    const wB = withoutAudioPayload(b);

    expect(wA).not.toBe(wB);

    const event = { type: "ping" } as unknown as IncomingSocketEvent;
    wA(event);

    expect(a).toHaveBeenCalledTimes(1);
    expect(b).not.toHaveBeenCalled();
  });
});

// ---------------------------------------------------------------------------
// Suite 2 — attachDefaultAudio factory (via the dispatcher).
// ---------------------------------------------------------------------------

const heap = new Map<number, string>();
let nextPtr = 1000;

function makePtr(s: string): number {
  const ptr = nextPtr++;
  heap.set(ptr, s);
  return ptr;
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

  for (const [name, fn] of Object.entries($EL_AudioGlueFactories)) {
    $EL_RegisterFactory(name, fn);
  }
});

type ElGlobals = typeof globalThis & {
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

const SHAPE_FUNCTION = 2;
const SHAPE_VOID = 3;

describe("attachDefaultAudio factory", () => {
  function allocateAndInvoke(bridgeCallbackHandle: number): Promise<number> {
    // Mint object handles for connection / input / output by allocating
    // mock JS objects directly into the registry, then pass {$ref} markers.
    const connection = {
      onMessage: vi.fn(),
      sendMessage: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
    };
    const input = { addListener: vi.fn(), removeListener: vi.fn() };
    const output = { playAudio: vi.fn() };

    const cRef = $EL_AllocateObject(connection);
    const iRef = $EL_AllocateObject(input);
    const oRef = $EL_AllocateObject(output);

    EL_InvokeFactoryAsync(
      makePtr("attachDefaultAudio"),
      makePtr(
        JSON.stringify([
          { $ref: cRef },
          { $ref: iRef },
          { $ref: oRef },
          { $cb: bridgeCallbackHandle },
        ]),
      ),
      SHAPE_FUNCTION,
      99,
    );
    return Promise.resolve().then(() => {
      const msg = lastSettleMessage();
      const match = msg.match(/^99:ok:\{"\$fn":(\d+)\}$/);
      if (!match) throw new Error(`unexpected settle payload: ${msg}`);
      return Number(match[1]);
    });
  }

  it("registers under the expected factory name", () => {
    expect($EL_LookupFactory("attachDefaultAudio")).toBeTypeOf("function");
  });

  it("composes attachInputToConnection, attachConnectionToOutput, and connection.onMessage", async () => {
    await allocateAndInvoke(/* bridge cb handle */ 1);

    expect(attachInputToConnection).toHaveBeenCalledTimes(1);
    expect(attachConnectionToOutput).toHaveBeenCalledTimes(1);
  });

  it("subscribes a callback that strips audio_base_64 from audio events", async () => {
    await allocateAndInvoke(/* bridge cb handle */ 7);

    // The connection mock's onMessage was called with the WRAPPED callback.
    // Pull it out, invoke with an audio event, and verify the C# callback
    // receives a SendMessage with audio_base_64 stripped.
    const connectionHandle = 1; // first $EL_AllocateObject call
    const connection = $EL_LookupObject(connectionHandle) as {
      onMessage: ReturnType<typeof vi.fn>;
    };
    const wrapped = connection.onMessage.mock.calls[0][0] as (
      e: IncomingSocketEvent,
    ) => void;

    wrapped({
      type: "audio",
      audio_event: { audio_base_64: "ZmFrZQ==", event_id: 5 },
    } as unknown as IncomingSocketEvent);

    // The {$cb:7} marker was rehydrated to a closure that fires
    // _EL_InvokeCallback(7, JSON.stringify(arg)) → dynCall_vii with the
    // stripped event as payload.
    expect(g.dynCall_vii).toHaveBeenCalledWith(100, 7, expect.any(Number));
    const payloadStr = g.stringToNewUTF8.mock.calls[
      g.stringToNewUTF8.mock.calls.length - 1
    ][0] as string;
    const parsed = JSON.parse(payloadStr) as {
      type: string;
      audio_event: object;
    };
    expect(parsed.type).toBe("audio");
    expect(parsed.audio_event).not.toHaveProperty("audio_base_64");
    expect(parsed.audio_event).toEqual({ event_id: 5 });
  });

  it("returns a detach JsFunction handle that, when invoked, tears down both attachments", async () => {
    const detachIn = vi.fn();
    const detachOut = vi.fn();
    vi.mocked(attachInputToConnection).mockReturnValueOnce(detachIn);
    vi.mocked(attachConnectionToOutput).mockReturnValueOnce(detachOut);

    const detachHandle = await allocateAndInvoke(/* bridge cb */ 1);

    expect(detachIn).not.toHaveBeenCalled();
    expect(detachOut).not.toHaveBeenCalled();

    EL_FunctionCallAsync(
      detachHandle,
      makePtr(JSON.stringify([])),
      SHAPE_VOID,
      100,
    );
    await Promise.resolve();

    expect(detachIn).toHaveBeenCalledTimes(1);
    expect(detachOut).toHaveBeenCalledTimes(1);
    expect(lastSettleMessage()).toBe("100:ok:null");
  });
});
