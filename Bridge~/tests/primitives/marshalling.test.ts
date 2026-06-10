import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  $EL_EncodeReturn,
  $EL_Rehydrate,
} from "../../src/primitives/marshalling";
import {
  $EL_AllocateFunction,
  $EL_AllocateObject,
} from "../../src/primitives/registries";

// Stand up the Unity-hoisted globals on globalThis before each test so the
// helper functions can mutate and read them exactly as they would in a live
// WebGL build.
type ElGlobals = typeof globalThis & {
  _EL_Objects: Record<number, unknown>;
  _EL_Functions: Record<number, (...args: unknown[]) => unknown>;
  _EL_NextHandleId: number;
  _EL_AllocateObject: typeof $EL_AllocateObject;
  _EL_AllocateFunction: typeof $EL_AllocateFunction;
  _EL_InvokeCallback: (handle: number, payload: string) => void;
};

const g = globalThis as ElGlobals;

beforeEach(() => {
  g._EL_Objects = {};
  g._EL_Functions = {};
  g._EL_NextHandleId = 1;
  g._EL_AllocateObject = $EL_AllocateObject;
  g._EL_AllocateFunction = $EL_AllocateFunction;
  g._EL_InvokeCallback = vi.fn();
});

describe("$EL_Rehydrate", () => {
  it("passes through primitives unchanged", () => {
    expect($EL_Rehydrate(42)).toBe(42);
    expect($EL_Rehydrate("hello")).toBe("hello");
    expect($EL_Rehydrate(null)).toBeNull();
    expect($EL_Rehydrate(true)).toBe(true);
  });

  it("resolves { $ref } to the registered object", () => {
    const obj = { tag: "test-object" };
    const handle = $EL_AllocateObject(obj);
    expect($EL_Rehydrate({ $ref: handle })).toBe(obj);
  });

  it("resolves { $fn } to the registered function", () => {
    const fn = () => "result";
    const handle = $EL_AllocateFunction(fn);
    expect($EL_Rehydrate({ $fn: handle })).toBe(fn);
  });

  it("resolves { $cb } to a closure that fires _EL_InvokeCallback", () => {
    const handle = 99;
    const closure = $EL_Rehydrate({ $cb: handle }) as (arg: unknown) => void;
    expect(typeof closure).toBe("function");
    closure("my-payload");
    expect(g._EL_InvokeCallback).toHaveBeenCalledWith(handle, '"my-payload"');
  });

  it("{ $cb } closure serialises object args to JSON", () => {
    const handle = 7;
    const closure = $EL_Rehydrate({ $cb: handle }) as (arg: unknown) => void;
    closure({ x: 1, y: 2 });
    expect(g._EL_InvokeCallback).toHaveBeenCalledWith(handle, '{"x":1,"y":2}');
  });

  it("passes through plain objects without markers", () => {
    expect($EL_Rehydrate({ x: 1, y: "two" })).toEqual({ x: 1, y: "two" });
  });

  it("recurses into nested objects to rehydrate markers", () => {
    const inner = { value: "deep" };
    const handle = $EL_AllocateObject(inner);
    const result = $EL_Rehydrate({
      nested: { $ref: handle },
      plain: "str",
    }) as Record<string, unknown>;
    expect(result.nested).toBe(inner);
    expect(result.plain).toBe("str");
  });

  it("recurses into arrays to rehydrate markers", () => {
    const obj1 = { a: 1 };
    const obj2 = { b: 2 };
    const h1 = $EL_AllocateObject(obj1);
    const h2 = $EL_AllocateObject(obj2);
    const result = $EL_Rehydrate([{ $ref: h1 }, 42, { $ref: h2 }]) as unknown[];
    expect(result[0]).toBe(obj1);
    expect(result[1]).toBe(42);
    expect(result[2]).toBe(obj2);
  });

  it("handles mixed marker types in a nested structure", () => {
    const obj = { tag: "obj" };
    const fn = () => undefined;
    const objHandle = $EL_AllocateObject(obj);
    const fnHandle = $EL_AllocateFunction(fn);
    const result = $EL_Rehydrate({
      ref: { $ref: objHandle },
      fn: { $fn: fnHandle },
      cb: { $cb: 77 },
      plain: "raw",
    }) as Record<string, unknown>;
    expect(result.ref).toBe(obj);
    expect(result.fn).toBe(fn);
    expect(typeof result.cb).toBe("function");
    expect(result.plain).toBe("raw");
  });

  it("passes through unknown object shapes (no markers) recursively", () => {
    const fn = () => "nested";
    const handle = $EL_AllocateFunction(fn);
    const result = $EL_Rehydrate({
      outer: { inner: { $fn: handle } },
    }) as Record<string, unknown>;
    expect((result.outer as Record<string, unknown>).inner).toBe(fn);
  });
});

describe("$EL_EncodeReturn", () => {
  it("shape=object allocates a registry entry and returns { $ref }", () => {
    const obj = { data: 42 };
    const encoded = $EL_EncodeReturn(obj, "object") as { $ref: number };
    expect(encoded).toHaveProperty("$ref");
    expect(typeof encoded.$ref).toBe("number");
    expect(g._EL_Objects[encoded.$ref]).toBe(obj);
  });

  it("shape=function allocates a registry entry and returns { $fn }", () => {
    const fn = () => "hello";
    const encoded = $EL_EncodeReturn(fn, "function") as { $fn: number };
    expect(encoded).toHaveProperty("$fn");
    expect(typeof encoded.$fn).toBe("number");
    expect(g._EL_Functions[encoded.$fn]).toBe(fn);
  });

  it("shape=void returns null regardless of value", () => {
    expect($EL_EncodeReturn({ anything: true }, "void")).toBeNull();
    expect($EL_EncodeReturn(42, "void")).toBeNull();
  });

  it("shape=value returns the value unchanged", () => {
    expect($EL_EncodeReturn(123, "value")).toBe(123);
    expect($EL_EncodeReturn("hello", "value")).toBe("hello");
    const obj = { a: 1 };
    expect($EL_EncodeReturn(obj, "value")).toBe(obj);
  });

  it("round-trip object: encode then rehydrate returns the original", () => {
    const original = { key: "round-trip" };
    const encoded = $EL_EncodeReturn(original, "object") as { $ref: number };
    expect($EL_Rehydrate(encoded)).toBe(original);
  });

  it("round-trip function: encode then rehydrate returns the original", () => {
    const original = () => "fn-round-trip";
    const encoded = $EL_EncodeReturn(original, "function") as { $fn: number };
    expect($EL_Rehydrate(encoded)).toBe(original);
  });

  it("consecutive object encodes produce distinct handles", () => {
    const a = $EL_EncodeReturn({}, "object") as { $ref: number };
    const b = $EL_EncodeReturn({}, "object") as { $ref: number };
    expect(a.$ref).not.toBe(b.$ref);
  });
});
