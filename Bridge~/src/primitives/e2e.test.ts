// End-to-end coverage for the mathFactory scenario.
//
// Registers a throwaway factory that exercises every primitive surface:
// factory invocation, sync/async method calls, property read, BridgeCallback
// fan-out, function-handle round-trip via removeListener, and dispose.
// Not shipped — purely for cross-cutting integration coverage.

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
  $EL_GetMethodShape,
  $EL_LookupFactory,
  $EL_LookupFunction,
  $EL_LookupObject,
  $EL_RegisterFactory,
  $EL_RegisterMethods,
  $EL_ReleaseFunction,
  $EL_ReleaseObject,
} from "./registries";
import { $EL_EncodeReturn, $EL_Rehydrate } from "./marshalling";
import { $EL_Settle } from "./promise-settle";
import { $EL_InvokeCallback } from "./callbacks";

type ElGlobals = typeof globalThis & {
  _EL_Objects: Record<number, unknown>;
  _EL_Functions: Record<number, (...args: unknown[]) => unknown>;
  _EL_Factories: Record<
    string,
    { fn: (...args: unknown[]) => unknown; returnShape: string }
  >;
  _EL_MethodShapes: Record<number, unknown>;
  _EL_NextHandleId: number;
  _EL_BridgeName: string;
  SendMessage: (go: string, method: string, value: string) => void;
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
  vi.stubGlobal("_EL_MethodShapes", {});
  vi.stubGlobal("_EL_NextHandleId", 1);

  vi.stubGlobal("_EL_AllocateObject", $EL_AllocateObject);
  vi.stubGlobal("_EL_AllocateFunction", $EL_AllocateFunction);
  vi.stubGlobal("_EL_LookupFactory", $EL_LookupFactory);
  vi.stubGlobal("_EL_LookupObject", $EL_LookupObject);
  vi.stubGlobal("_EL_LookupFunction", $EL_LookupFunction);
  vi.stubGlobal("_EL_ReleaseObject", $EL_ReleaseObject);
  vi.stubGlobal("_EL_ReleaseFunction", $EL_ReleaseFunction);
  vi.stubGlobal("_EL_RegisterMethods", $EL_RegisterMethods);
  vi.stubGlobal("_EL_GetMethodShape", $EL_GetMethodShape);

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

  $EL_RegisterFactory("mathFactory", () => mathObj, "object");
});

function lastSendMessage(): string {
  const calls = (g.SendMessage as ReturnType<typeof vi.fn>).mock.calls;
  return calls[calls.length - 1][2] as string;
}

describe("mathFactory end-to-end", () => {
  it("factory invocation returns a JsObject handle for the math object", async () => {
    EL_InvokeFactoryAsync(makePtr("mathFactory"), makePtr("[]"), 1);
    await Promise.resolve();

    expect(lastSendMessage()).toBe('1:ok:{"$ref":1}');
    expect(g._EL_Objects[1]).toBe(mathObj);
  });

  it("sync method add(3, 4) → 7", async () => {
    EL_InvokeFactoryAsync(makePtr("mathFactory"), makePtr("[]"), 1);
    await Promise.resolve();
    const mathHandle = 1;

    const ptr = EL_ObjectCallSync(mathHandle, makePtr("add"), makePtr("[3,4]"));
    expect(readPtr(ptr)).toBe("7");
  });

  it("async method addAsync(5, 6) settles with 11", async () => {
    EL_InvokeFactoryAsync(makePtr("mathFactory"), makePtr("[]"), 1);
    await Promise.resolve();
    const mathHandle = 1;

    (g.SendMessage as ReturnType<typeof vi.fn>).mockClear();

    EL_ObjectCallAsync(mathHandle, makePtr("addAsync"), makePtr("[5,6]"), 2);
    await Promise.resolve();
    await Promise.resolve(); // extra tick for the inner Promise.resolve

    expect(lastSendMessage()).toBe("2:ok:11");
  });

  it("property read: pi returns Math.PI", async () => {
    EL_InvokeFactoryAsync(makePtr("mathFactory"), makePtr("[]"), 1);
    await Promise.resolve();
    const mathHandle = 1;

    const ptr = EL_ObjectGet(mathHandle, makePtr("pi"));
    expect(JSON.parse(readPtr(ptr))).toBeCloseTo(Math.PI, 10);
  });

  it("BridgeCallback fan-out: addTickListener receives the closure and fires SendMessage on invocation", async () => {
    EL_InvokeFactoryAsync(makePtr("mathFactory"), makePtr("[]"), 1);
    await Promise.resolve();
    const mathHandle = 1;

    $EL_RegisterMethods(mathHandle, {
      addTickListener: { returnShape: "function" },
    });

    (g.SendMessage as ReturnType<typeof vi.fn>).mockClear();

    const cbHandle = 2001;
    EL_ObjectCallAsync(
      mathHandle,
      makePtr("addTickListener"),
      makePtr(JSON.stringify([{ $cb: cbHandle }])),
      3,
    );
    await Promise.resolve();

    // Settlement carries the removeListener function handle
    const settleMsg = lastSendMessage();
    expect(settleMsg).toMatch(/^3:ok:\{"\$fn":\d+\}$/);
    const removeListenerHandle = JSON.parse(settleMsg.slice("3:ok:".length))
      .$fn as number;
    expect(g._EL_Functions[removeListenerHandle]).toBeTypeOf("function");
    expect(listeners.length).toBe(1);

    (g.SendMessage as ReturnType<typeof vi.fn>).mockClear();

    // Tick — the rehydrated $cb closure fires SendMessage via _EL_InvokeCallback
    listeners[0](42);
    expect(g.SendMessage).toHaveBeenCalledWith(
      "__ElevenLabsBridge__",
      "OnCallbackInvoked",
      `${cbHandle}:42`,
    );
  });

  it("function-handle round-trip: calling removeListener via EL_FunctionCallAsync removes the listener", async () => {
    EL_InvokeFactoryAsync(makePtr("mathFactory"), makePtr("[]"), 1);
    await Promise.resolve();
    const mathHandle = 1;

    $EL_RegisterMethods(mathHandle, {
      addTickListener: { returnShape: "function" },
    });

    (g.SendMessage as ReturnType<typeof vi.fn>).mockClear();

    EL_ObjectCallAsync(
      mathHandle,
      makePtr("addTickListener"),
      makePtr(JSON.stringify([{ $cb: 3001 }])),
      4,
    );
    await Promise.resolve();

    const removeListenerHandle = JSON.parse(
      lastSendMessage().slice("4:ok:".length),
    ).$fn as number;

    (g.SendMessage as ReturnType<typeof vi.fn>).mockClear();

    EL_FunctionCallAsync(removeListenerHandle, makePtr("[]"), 5);
    await Promise.resolve();

    expect(lastSendMessage()).toBe("5:ok:null");
    expect(listeners.length).toBe(0);
  });

  it("dispose: EL_ObjectRelease clears the object handle and its method shapes", async () => {
    EL_InvokeFactoryAsync(makePtr("mathFactory"), makePtr("[]"), 1);
    await Promise.resolve();
    const mathHandle = 1;

    $EL_RegisterMethods(mathHandle, { add: { returnShape: "value" } });

    EL_ObjectRelease(mathHandle);

    expect(g._EL_Objects[mathHandle]).toBeUndefined();
    expect(g._EL_MethodShapes[mathHandle]).toBeUndefined();
  });

  it("dispose: EL_FunctionRelease clears the function handle", async () => {
    EL_InvokeFactoryAsync(makePtr("mathFactory"), makePtr("[]"), 1);
    await Promise.resolve();
    const mathHandle = 1;

    $EL_RegisterMethods(mathHandle, {
      addTickListener: { returnShape: "function" },
    });

    (g.SendMessage as ReturnType<typeof vi.fn>).mockClear();

    EL_ObjectCallAsync(
      mathHandle,
      makePtr("addTickListener"),
      makePtr(JSON.stringify([{ $cb: 4001 }])),
      6,
    );
    await Promise.resolve();

    const fnHandle = JSON.parse(lastSendMessage().slice("6:ok:".length))
      .$fn as number;

    EL_FunctionRelease(fnHandle);
    expect(g._EL_Functions[fnHandle]).toBeUndefined();
  });
});
