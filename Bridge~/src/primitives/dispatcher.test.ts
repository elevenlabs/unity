import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  EL_FunctionCallAsync,
  EL_FunctionCallSync,
  EL_FunctionRelease,
  EL_InvokeFactoryAsync,
  EL_InvokeFactorySync,
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

// Return-shape codes shared with C# (BridgeReturnShape enum in Phase 3).
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
  dynCall_viii: ReturnType<typeof vi.fn>;
  stringToNewUTF8: ReturnType<typeof vi.fn>;
  _free: ReturnType<typeof vi.fn>;
};

const g = globalThis as ElGlobals;

// Simulated Emscripten heap: maps allocated pointer → string content.
const heap = new Map<number, string>();
let nextPtr = 1000;

// Allocate a string in the simulated heap and return its pointer (for input ptrs).
function makePtr(s: string): number {
  const ptr = nextPtr++;
  heap.set(ptr, s);
  return ptr;
}

// Read the string written to a heap pointer (for output ptrs from sync functions).
function readPtr(ptr: number): string {
  return heap.get(ptr) ?? "";
}

beforeEach(() => {
  heap.clear();
  nextPtr = 1000;

  // Registries
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

  // Marshalling
  vi.stubGlobal("_EL_Rehydrate", $EL_Rehydrate);
  vi.stubGlobal("_EL_EncodeReturn", $EL_EncodeReturn);
  vi.stubGlobal("_EL_InvokeCallback", vi.fn()); // needed by $EL_Rehydrate for $cb markers

  // Settlement (uses real implementation; assertions via DynCall mocks)
  vi.stubGlobal("_EL_Settle", $EL_Settle);
  vi.stubGlobal("_EL_SettlePtr", 42);
  vi.stubGlobal("dynCall_viii", vi.fn());
  vi.stubGlobal(
    "stringToNewUTF8",
    vi.fn(() => nextPtr++),
  );
  vi.stubGlobal("_free", vi.fn());

  // Emscripten heap simulation
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
});

// Helper: reconstruct "promiseId:status:payload" from the first DynCall settle.
function settlementMessage(): string {
  const [, promiseId, statusCode] = g.dynCall_viii.mock.calls[0];
  const status = statusCode === 0 ? "ok" : "err";
  const payload = g.stringToNewUTF8.mock.calls[0][0] as string;
  return `${promiseId as number}:${status}:${payload}`;
}

describe("EL_InvokeFactoryAsync", () => {
  it("settles ok with the return encoded as 'object' when shape is OBJECT", async () => {
    $EL_RegisterFactory("makeBox", () => ({ w: 10, h: 20 }));

    EL_InvokeFactoryAsync(makePtr("makeBox"), makePtr("[]"), SHAPE_OBJECT, 5);
    await Promise.resolve();

    expect(settlementMessage()).toBe('5:ok:{"$ref":1}');
    expect(g._EL_Objects[1]).toEqual({ w: 10, h: 20 });
  });

  it("settles ok with a plain value when shape is VALUE", async () => {
    $EL_RegisterFactory(
      "add",
      (a: unknown, b: unknown) => (a as number) + (b as number),
    );

    EL_InvokeFactoryAsync(makePtr("add"), makePtr("[3,4]"), SHAPE_VALUE, 9);
    await Promise.resolve();

    expect(settlementMessage()).toBe("9:ok:7");
  });

  it("settles ok with null when shape is VOID", async () => {
    $EL_RegisterFactory("noop", () => undefined);

    EL_InvokeFactoryAsync(makePtr("noop"), makePtr("[]"), SHAPE_VOID, 11);
    await Promise.resolve();

    expect(settlementMessage()).toBe("11:ok:null");
  });

  it("settles err when the factory name is not registered", async () => {
    EL_InvokeFactoryAsync(makePtr("ghost"), makePtr("[]"), SHAPE_VALUE, 3);
    await Promise.resolve();

    expect(settlementMessage()).toContain("3:err:");
    expect(settlementMessage()).toContain("ghost");
  });

  it("settles err when the factory throws synchronously", async () => {
    $EL_RegisterFactory("boom", () => {
      throw new Error("factory exploded");
    });

    EL_InvokeFactoryAsync(makePtr("boom"), makePtr("[]"), SHAPE_VALUE, 7);
    await Promise.resolve();

    expect(settlementMessage()).toBe("7:err:factory exploded");
  });

  it("settles ok when the factory returns a Promise", async () => {
    $EL_RegisterFactory("async", () => Promise.resolve(42));

    EL_InvokeFactoryAsync(makePtr("async"), makePtr("[]"), SHAPE_VALUE, 2);
    // Two ticks: one for the outer Promise.resolve, one for the factory's Promise.
    await Promise.resolve();
    await Promise.resolve();

    expect(settlementMessage()).toBe("2:ok:42");
  });

  it("settles err when the factory returns a rejected Promise", async () => {
    $EL_RegisterFactory("failAsync", () =>
      Promise.reject(new Error("async fail")),
    );

    EL_InvokeFactoryAsync(makePtr("failAsync"), makePtr("[]"), SHAPE_VALUE, 4);
    await Promise.resolve();
    await Promise.resolve();

    expect(settlementMessage()).toBe("4:err:async fail");
  });

  it("passes args through rehydration before calling the factory", async () => {
    const innerObj = { tag: "inner" };
    const innerHandle = $EL_AllocateObject(innerObj);
    let received: unknown;
    $EL_RegisterFactory("capture", (arg: unknown) => {
      received = arg;
    });

    const argsJson = JSON.stringify([{ $ref: innerHandle }]);
    EL_InvokeFactoryAsync(makePtr("capture"), makePtr(argsJson), SHAPE_VOID, 1);
    await Promise.resolve();

    expect(received).toBe(innerObj);
  });
});

describe("EL_InvokeFactorySync", () => {
  it("returns the JSON-encoded result for shape VALUE", () => {
    $EL_RegisterFactory("double", (n: unknown) => (n as number) * 2);

    const ptr = EL_InvokeFactorySync(
      makePtr("double"),
      makePtr("[6]"),
      SHAPE_VALUE,
    );
    expect(readPtr(ptr)).toBe("12");
  });

  it("returns { $ref } JSON for shape OBJECT", () => {
    $EL_RegisterFactory("mkObj", () => ({ x: 1 }));

    const ptr = EL_InvokeFactorySync(
      makePtr("mkObj"),
      makePtr("[]"),
      SHAPE_OBJECT,
    );
    expect(readPtr(ptr)).toBe('{"$ref":1}');
    expect(g._EL_Objects[1]).toEqual({ x: 1 });
  });

  it("returns !err: prefix when the factory is not registered", () => {
    const ptr = EL_InvokeFactorySync(
      makePtr("ghost"),
      makePtr("[]"),
      SHAPE_VALUE,
    );
    expect(readPtr(ptr)).toMatch(/^!err:/);
    expect(readPtr(ptr)).toContain("ghost");
  });

  it("returns !err: prefix when the factory throws", () => {
    $EL_RegisterFactory("boom", () => {
      throw new Error("sync boom");
    });

    const ptr = EL_InvokeFactorySync(
      makePtr("boom"),
      makePtr("[]"),
      SHAPE_VALUE,
    );
    expect(readPtr(ptr)).toBe("!err:sync boom");
  });
});

describe("EL_ObjectCallAsync", () => {
  it("calls the method and settles with the return value (shape VALUE)", async () => {
    const obj = { add: (a: number, b: number) => a + b };
    const handle = $EL_AllocateObject(obj);

    EL_ObjectCallAsync(
      handle,
      makePtr("add"),
      makePtr("[10,32]"),
      SHAPE_VALUE,
      1,
    );
    await Promise.resolve();

    expect(settlementMessage()).toBe("1:ok:42");
  });

  it("wraps the return as { $fn } when shape is FUNCTION", async () => {
    const removeListener = () => undefined;
    const obj = { addListener: () => removeListener };
    const handle = $EL_AllocateObject(obj);

    EL_ObjectCallAsync(
      handle,
      makePtr("addListener"),
      makePtr("[]"),
      SHAPE_FUNCTION,
      6,
    );
    await Promise.resolve();

    expect(settlementMessage()).toMatch(/^6:ok:\{"\$fn":\d+\}$/);
    const fnHandle = JSON.parse(settlementMessage().slice("6:ok:".length))
      .$fn as number;
    expect(g._EL_Functions[fnHandle]).toBe(removeListener);
  });

  it("rehydrates { $ref } markers in args before calling the method", async () => {
    const inner = { value: 99 };
    const innerHandle = $EL_AllocateObject(inner);
    let received: unknown;
    const obj = {
      capture(arg: unknown) {
        received = arg;
        return "done";
      },
    };
    const handle = $EL_AllocateObject(obj);

    const argsJson = JSON.stringify([{ $ref: innerHandle }]);
    EL_ObjectCallAsync(
      handle,
      makePtr("capture"),
      makePtr(argsJson),
      SHAPE_VALUE,
      2,
    );
    await Promise.resolve();

    expect(received).toBe(inner);
    expect(settlementMessage()).toBe('2:ok:"done"');
  });

  it("settles err for an unknown object handle", async () => {
    EL_ObjectCallAsync(999, makePtr("foo"), makePtr("[]"), SHAPE_VALUE, 3);
    await Promise.resolve();

    expect(settlementMessage()).toContain("3:err:");
    expect(settlementMessage()).toContain("unknown handle");
  });

  it("settles err when the method does not exist on the object", async () => {
    const handle = $EL_AllocateObject({ x: 1 });

    EL_ObjectCallAsync(
      handle,
      makePtr("missing"),
      makePtr("[]"),
      SHAPE_VALUE,
      4,
    );
    await Promise.resolve();

    expect(settlementMessage()).toContain("4:err:");
    expect(settlementMessage()).toContain("missing");
  });

  it("settles err when the method throws", async () => {
    const obj = {
      fail() {
        throw new Error("method blew up");
      },
    };
    const handle = $EL_AllocateObject(obj);

    EL_ObjectCallAsync(handle, makePtr("fail"), makePtr("[]"), SHAPE_VALUE, 5);
    await Promise.resolve();

    expect(settlementMessage()).toBe("5:err:method blew up");
  });
});

describe("EL_ObjectCallSync", () => {
  it("returns the heap-string-encoded return value (shape VALUE)", () => {
    const obj = { square: (n: number) => n * n };
    const handle = $EL_AllocateObject(obj);

    const ptr = EL_ObjectCallSync(
      handle,
      makePtr("square"),
      makePtr("[7]"),
      SHAPE_VALUE,
    );
    expect(readPtr(ptr)).toBe("49");
  });

  it("returns { $fn } JSON when shape is FUNCTION", () => {
    const cleanup = () => undefined;
    const obj = { register: () => cleanup };
    const handle = $EL_AllocateObject(obj);

    const ptr = EL_ObjectCallSync(
      handle,
      makePtr("register"),
      makePtr("[]"),
      SHAPE_FUNCTION,
    );
    expect(readPtr(ptr)).toMatch(/^\{"\$fn":\d+\}$/);
  });

  it("returns !err: for an unknown handle", () => {
    const ptr = EL_ObjectCallSync(
      999,
      makePtr("foo"),
      makePtr("[]"),
      SHAPE_VALUE,
    );
    expect(readPtr(ptr)).toMatch(/^!err:/);
    expect(readPtr(ptr)).toContain("unknown handle");
  });

  it("returns !err: when the method throws", () => {
    const obj = {
      boom() {
        throw new Error("sync method error");
      },
    };
    const handle = $EL_AllocateObject(obj);

    const ptr = EL_ObjectCallSync(
      handle,
      makePtr("boom"),
      makePtr("[]"),
      SHAPE_VALUE,
    );
    expect(readPtr(ptr)).toBe("!err:sync method error");
  });

  it("returns null-encoded string when method returns undefined", () => {
    const obj = { noop: () => undefined };
    const handle = $EL_AllocateObject(obj);

    const ptr = EL_ObjectCallSync(
      handle,
      makePtr("noop"),
      makePtr("[]"),
      SHAPE_VALUE,
    );
    expect(readPtr(ptr)).toBe("null");
  });
});

describe("EL_ObjectGet", () => {
  it("returns the JSON-encoded property value", () => {
    const obj = { pi: 3.14, name: "circle" };
    const handle = $EL_AllocateObject(obj);

    expect(readPtr(EL_ObjectGet(handle, makePtr("pi")))).toBe("3.14");
    expect(readPtr(EL_ObjectGet(handle, makePtr("name")))).toBe('"circle"');
  });

  it("returns null for a missing property", () => {
    const handle = $EL_AllocateObject({ a: 1 });

    const ptr = EL_ObjectGet(handle, makePtr("missing"));
    expect(readPtr(ptr)).toBe("null");
  });

  it("returns !err: for an unknown handle", () => {
    const ptr = EL_ObjectGet(999, makePtr("prop"));
    expect(readPtr(ptr)).toMatch(/^!err:/);
    expect(readPtr(ptr)).toContain("unknown handle");
  });

  it("returns a nested object as JSON", () => {
    const obj = { data: { x: 1, y: 2 } };
    const handle = $EL_AllocateObject(obj);

    const ptr = EL_ObjectGet(handle, makePtr("data"));
    expect(readPtr(ptr)).toBe('{"x":1,"y":2}');
  });
});

describe("EL_ObjectRelease", () => {
  it("drops the registry entry so subsequent async calls settle with unknown handle", async () => {
    const obj = { foo: () => 1 };
    const handle = $EL_AllocateObject(obj);

    EL_ObjectRelease(handle);

    EL_ObjectCallAsync(handle, makePtr("foo"), makePtr("[]"), SHAPE_VALUE, 99);
    await Promise.resolve();

    expect(settlementMessage()).toContain("99:err:");
    expect(settlementMessage()).toContain("unknown handle");
  });

  it("drops the registry entry so subsequent sync calls return !err:", () => {
    const obj = { foo: () => 1 };
    const handle = $EL_AllocateObject(obj);

    EL_ObjectRelease(handle);

    const ptr = EL_ObjectCallSync(
      handle,
      makePtr("foo"),
      makePtr("[]"),
      SHAPE_VALUE,
    );
    expect(readPtr(ptr)).toMatch(/^!err:/);
    expect(readPtr(ptr)).toContain("unknown handle");
  });

  it("drops the registry entry so EL_ObjectGet returns !err:", () => {
    const obj = { x: 42 };
    const handle = $EL_AllocateObject(obj);

    EL_ObjectRelease(handle);

    const ptr = EL_ObjectGet(handle, makePtr("x"));
    expect(readPtr(ptr)).toMatch(/^!err:/);
    expect(readPtr(ptr)).toContain("unknown handle");
  });
});

describe("EL_FunctionCallAsync", () => {
  it("calls the function and settles with the return value (shape VALUE)", async () => {
    const fn = (...args: unknown[]) =>
      (args[0] as number) * (args[1] as number);
    const handle = $EL_AllocateFunction(fn);

    EL_FunctionCallAsync(handle, makePtr("[6,7]"), SHAPE_VALUE, 10);
    await Promise.resolve();

    expect(settlementMessage()).toBe("10:ok:42");
  });

  it("wraps the return as { $fn } when shape is FUNCTION", async () => {
    const inner = () => undefined;
    const fn = () => inner;
    const handle = $EL_AllocateFunction(fn);

    EL_FunctionCallAsync(handle, makePtr("[]"), SHAPE_FUNCTION, 14);
    await Promise.resolve();

    expect(settlementMessage()).toMatch(/^14:ok:\{"\$fn":\d+\}$/);
  });

  it("settles err for an unknown function handle", async () => {
    EL_FunctionCallAsync(999, makePtr("[]"), SHAPE_VALUE, 11);
    await Promise.resolve();

    expect(settlementMessage()).toContain("11:err:");
    expect(settlementMessage()).toContain("unknown handle");
  });

  it("settles err when the function throws", async () => {
    const fn = () => {
      throw new Error("fn error");
    };
    const handle = $EL_AllocateFunction(fn);

    EL_FunctionCallAsync(handle, makePtr("[]"), SHAPE_VALUE, 12);
    await Promise.resolve();

    expect(settlementMessage()).toBe("12:err:fn error");
  });

  it("passes args through rehydration before calling the function", async () => {
    const inner = { key: "val" };
    const innerHandle = $EL_AllocateObject(inner);
    let received: unknown;
    const fn = (arg: unknown) => {
      received = arg;
    };
    const handle = $EL_AllocateFunction(fn);

    EL_FunctionCallAsync(
      handle,
      makePtr(JSON.stringify([{ $ref: innerHandle }])),
      SHAPE_VOID,
      13,
    );
    await Promise.resolve();

    expect(received).toBe(inner);
  });
});

describe("EL_FunctionCallSync", () => {
  it("calls the function and returns the heap-string-encoded result (shape VALUE)", () => {
    const fn = (...args: unknown[]) => (args[0] as string).toUpperCase();
    const handle = $EL_AllocateFunction(fn);

    const ptr = EL_FunctionCallSync(handle, makePtr('["hello"]'), SHAPE_VALUE);
    expect(readPtr(ptr)).toBe('"HELLO"');
  });

  it("returns !err: for an unknown function handle", () => {
    const ptr = EL_FunctionCallSync(999, makePtr("[]"), SHAPE_VALUE);
    expect(readPtr(ptr)).toMatch(/^!err:/);
    expect(readPtr(ptr)).toContain("unknown handle");
  });

  it("returns !err: when the function throws", () => {
    const fn = () => {
      throw new Error("sync fn error");
    };
    const handle = $EL_AllocateFunction(fn);

    const ptr = EL_FunctionCallSync(handle, makePtr("[]"), SHAPE_VALUE);
    expect(readPtr(ptr)).toBe("!err:sync fn error");
  });
});

describe("EL_FunctionRelease", () => {
  it("drops the registry entry so subsequent async calls settle with unknown handle", async () => {
    const fn = () => 1;
    const handle = $EL_AllocateFunction(fn);

    EL_FunctionRelease(handle);

    EL_FunctionCallAsync(handle, makePtr("[]"), SHAPE_VALUE, 20);
    await Promise.resolve();

    expect(settlementMessage()).toContain("20:err:");
    expect(settlementMessage()).toContain("unknown handle");
  });

  it("drops the registry entry so subsequent sync calls return !err:", () => {
    const fn = () => 1;
    const handle = $EL_AllocateFunction(fn);

    EL_FunctionRelease(handle);

    const ptr = EL_FunctionCallSync(handle, makePtr("[]"), SHAPE_VALUE);
    expect(readPtr(ptr)).toMatch(/^!err:/);
    expect(readPtr(ptr)).toContain("unknown handle");
  });
});

describe("returnShape decoding", () => {
  it("defaults to 'value' for an unknown shape code", async () => {
    $EL_RegisterFactory("makeNum", () => 42);

    EL_InvokeFactoryAsync(makePtr("makeNum"), makePtr("[]"), 99, 1);
    await Promise.resolve();

    expect(settlementMessage()).toBe("1:ok:42");
  });
});
