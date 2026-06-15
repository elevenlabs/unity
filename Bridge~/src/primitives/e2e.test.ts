// End-to-end coverage for the mathFactory scenario.
//
// Registers a throwaway factory that exercises every primitive surface:
// factory invocation, sync/async method calls, property read, BridgeCallback
// fan-out, function-handle round-trip via removeListener, and dispose.
// Not shipped — purely for cross-cutting integration coverage.
//
// Return shapes are passed per-call as ints (matching the C# BridgeReturnShape
// enum that Phase 3 will introduce), not registered up-front.

import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  EL_FunctionCallAsync,
  EL_FunctionRelease,
  EL_InvokeFactoryAsync,
  EL_ObjectCallAsync,
  EL_ObjectCallSync,
  EL_ObjectGet,
  EL_ObjectRelease,
} from "./dispatcher";
import {
  $EL_AllocateFunction,
  $EL_AllocateObject,
  $EL_LookupFactory,
  $EL_LookupFunction,
  $EL_LookupObject,
  $EL_RegisterFactory,
  $EL_ReleaseFunction,
  $EL_ReleaseObject,
} from "./registries";
import { $EL_EncodeReturn, $EL_Rehydrate } from "./marshalling";
import { $EL_Settle } from "./promise-settle";
import { $EL_InvokeCallback } from "./callbacks";

const SHAPE_VALUE = 0;
const SHAPE_OBJECT = 1;
const SHAPE_FUNCTION = 2;
const SHAPE_VOID = 3;

type ElGlobals = typeof globalThis & {
  _EL_Objects: Record<number, unknown>;
  _EL_Functions: Record<number, (...args: unknown[]) => unknown>;
  _EL_Factories: Record<string, (...args: unknown[]) => unknown>;
  _EL_NextHandleId: number;
  _EL_SettlePtr: number;
  _EL_CallbackPtr: number;
  dynCall_viii: ReturnType<typeof vi.fn>;
  dynCall_vii: ReturnType<typeof vi.fn>;
  stringToNewUTF8: ReturnType<typeof vi.fn>;
  _free: ReturnType<typeof vi.fn>;
};

const g = globalThis as ElGlobals;

// Simulated Emscripten heap: maps pointer → string content.
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

// Listeners registered by mathObj.addTickListener.
const listeners: Array<(tick: number) => void> = [];

// Math object returned by the mathFactory.
const mathObj = {
  add: (a: number, b: number) => a + b,
  addAsync: (a: number, b: number) => Promise.resolve(a + b),
  pi: Math.PI,
  addTickListener: (callback: (tick: number) => void) => {
    listeners.push(callback);
    return () => {
      const idx = listeners.indexOf(callback);
      if (idx >= 0) listeners.splice(idx, 1);
    };
  },
};

beforeEach(() => {
  heap.clear();
  nextPtr = 1000;
  listeners.length = 0;

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

  $EL_RegisterFactory("mathFactory", () => mathObj);
});

// Helper: reconstruct "promiseId:status:payload" from the last DynCall settle.
function lastSettleMessage(): string {
  const idx = g.dynCall_viii.mock.calls.length - 1;
  const [, promiseId, statusCode] = g.dynCall_viii.mock.calls[idx];
  const status = statusCode === 0 ? "ok" : "err";
  const payload = g.stringToNewUTF8.mock.calls[idx][0] as string;
  return `${promiseId as number}:${status}:${payload}`;
}

describe("mathFactory end-to-end", () => {
  it("factory invocation returns a JsObject handle for the math object", async () => {
    EL_InvokeFactoryAsync(
      makePtr("mathFactory"),
      makePtr("[]"),
      SHAPE_OBJECT,
      1,
    );
    await Promise.resolve();

    expect(lastSettleMessage()).toBe('1:ok:{"$ref":1}');
    expect(g._EL_Objects[1]).toBe(mathObj);
  });

  it("sync method add(3, 4) → 7", async () => {
    EL_InvokeFactoryAsync(
      makePtr("mathFactory"),
      makePtr("[]"),
      SHAPE_OBJECT,
      1,
    );
    await Promise.resolve();
    const mathHandle = 1;

    const ptr = EL_ObjectCallSync(
      mathHandle,
      makePtr("add"),
      makePtr("[3,4]"),
      SHAPE_VALUE,
    );
    expect(readPtr(ptr)).toBe("7");
  });

  it("async method addAsync(5, 6) settles with 11", async () => {
    EL_InvokeFactoryAsync(
      makePtr("mathFactory"),
      makePtr("[]"),
      SHAPE_OBJECT,
      1,
    );
    await Promise.resolve();
    const mathHandle = 1;

    EL_ObjectCallAsync(
      mathHandle,
      makePtr("addAsync"),
      makePtr("[5,6]"),
      SHAPE_VALUE,
      2,
    );
    await Promise.resolve();
    await Promise.resolve(); // extra tick for the inner Promise.resolve

    expect(lastSettleMessage()).toBe("2:ok:11");
  });

  it("property read: pi returns Math.PI", async () => {
    EL_InvokeFactoryAsync(
      makePtr("mathFactory"),
      makePtr("[]"),
      SHAPE_OBJECT,
      1,
    );
    await Promise.resolve();
    const mathHandle = 1;

    const ptr = EL_ObjectGet(mathHandle, makePtr("pi"));
    expect(JSON.parse(readPtr(ptr))).toBeCloseTo(Math.PI, 10);
  });

  it("BridgeCallback fan-out: addTickListener receives the closure and fires SendMessage on invocation", async () => {
    EL_InvokeFactoryAsync(
      makePtr("mathFactory"),
      makePtr("[]"),
      SHAPE_OBJECT,
      1,
    );
    await Promise.resolve();
    const mathHandle = 1;

    const cbHandle = 2001;
    EL_ObjectCallAsync(
      mathHandle,
      makePtr("addTickListener"),
      makePtr(JSON.stringify([{ $cb: cbHandle }])),
      SHAPE_FUNCTION,
      3,
    );
    await Promise.resolve();

    // Settlement carries the removeListener function handle
    const settleMsg = lastSettleMessage();
    expect(settleMsg).toMatch(/^3:ok:\{"\$fn":\d+\}$/);
    const removeListenerHandle = JSON.parse(settleMsg.slice("3:ok:".length))
      .$fn as number;
    expect(g._EL_Functions[removeListenerHandle]).toBeTypeOf("function");
    expect(listeners.length).toBe(1);

    // Tick — the rehydrated $cb closure fires _EL_InvokeCallback → dynCall_vii
    listeners[0](42);
    expect(g.dynCall_vii).toHaveBeenCalledWith(
      100,
      cbHandle,
      expect.any(Number),
    );
    expect(g.stringToNewUTF8).toHaveBeenLastCalledWith("42");
  });

  it("function-handle round-trip: calling removeListener via EL_FunctionCallAsync removes the listener", async () => {
    EL_InvokeFactoryAsync(
      makePtr("mathFactory"),
      makePtr("[]"),
      SHAPE_OBJECT,
      1,
    );
    await Promise.resolve();
    const mathHandle = 1;

    EL_ObjectCallAsync(
      mathHandle,
      makePtr("addTickListener"),
      makePtr(JSON.stringify([{ $cb: 3001 }])),
      SHAPE_FUNCTION,
      4,
    );
    await Promise.resolve();

    const removeListenerHandle = JSON.parse(
      lastSettleMessage().slice("4:ok:".length),
    ).$fn as number;

    EL_FunctionCallAsync(removeListenerHandle, makePtr("[]"), SHAPE_VOID, 5);
    await Promise.resolve();

    expect(lastSettleMessage()).toBe("5:ok:null");
    expect(listeners.length).toBe(0);
  });

  it("dispose: EL_ObjectRelease clears the object handle", async () => {
    EL_InvokeFactoryAsync(
      makePtr("mathFactory"),
      makePtr("[]"),
      SHAPE_OBJECT,
      1,
    );
    await Promise.resolve();
    const mathHandle = 1;

    EL_ObjectRelease(mathHandle);

    expect(g._EL_Objects[mathHandle]).toBeUndefined();
  });

  it("dispose: EL_FunctionRelease clears the function handle", async () => {
    EL_InvokeFactoryAsync(
      makePtr("mathFactory"),
      makePtr("[]"),
      SHAPE_OBJECT,
      1,
    );
    await Promise.resolve();
    const mathHandle = 1;

    EL_ObjectCallAsync(
      mathHandle,
      makePtr("addTickListener"),
      makePtr(JSON.stringify([{ $cb: 4001 }])),
      SHAPE_FUNCTION,
      6,
    );
    await Promise.resolve();

    const fnHandle = JSON.parse(lastSettleMessage().slice("6:ok:".length))
      .$fn as number;

    EL_FunctionRelease(fnHandle);
    expect(g._EL_Functions[fnHandle]).toBeUndefined();
  });
});
