import { beforeEach, describe, expect, it, vi } from "vitest";
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

// The primitive helpers reference `_EL_Objects`, `_EL_Functions`,
// `_EL_Factories`, `_EL_MethodShapes`, `_EL_NextHandleId` — the Unity-hoisted
// module-level globals. In Node we stub them on globalThis via vi.stubGlobal so
// vitest restores them between tests (unstubGlobals is enabled in vitest.config.ts).
type ElGlobals = typeof globalThis & {
  _EL_Objects: Record<number, unknown>;
  _EL_Functions: Record<number, (...args: unknown[]) => unknown>;
  _EL_Factories: Record<
    string,
    {
      fn: (...args: unknown[]) => unknown;
      returnShape: "object" | "function" | "value" | "void";
    }
  >;
  _EL_MethodShapes: Record<number, unknown>;
  _EL_NextHandleId: number;
};
const g = globalThis as ElGlobals;

beforeEach(() => {
  vi.stubGlobal("_EL_Objects", {});
  vi.stubGlobal("_EL_Functions", {});
  vi.stubGlobal("_EL_Factories", {});
  vi.stubGlobal("_EL_MethodShapes", {});
  vi.stubGlobal("_EL_NextHandleId", 1);
});

describe("object registry", () => {
  it("allocates monotonic handles and returns the same instance on lookup", () => {
    const a = { tag: "a" };
    const b = { tag: "b" };
    const ha = $EL_AllocateObject(a);
    const hb = $EL_AllocateObject(b);

    expect(hb).toBeGreaterThan(ha);
    expect($EL_LookupObject(ha)).toBe(a);
    expect($EL_LookupObject(hb)).toBe(b);
  });

  it("returns undefined for unknown handles", () => {
    expect($EL_LookupObject(999)).toBeUndefined();
  });

  it("release-then-lookup misses cleanly", () => {
    const h = $EL_AllocateObject({});
    $EL_ReleaseObject(h);
    expect($EL_LookupObject(h)).toBeUndefined();
  });

  it("double-release is a no-op", () => {
    const h = $EL_AllocateObject({});
    $EL_ReleaseObject(h);
    expect(() => $EL_ReleaseObject(h)).not.toThrow();
    expect($EL_LookupObject(h)).toBeUndefined();
  });
});

describe("function registry", () => {
  it("allocates monotonic handles and returns the same function on lookup", () => {
    const fa = () => "a";
    const fb = () => "b";
    const ha = $EL_AllocateFunction(fa);
    const hb = $EL_AllocateFunction(fb);

    expect(hb).toBeGreaterThan(ha);
    expect($EL_LookupFunction(ha)).toBe(fa);
    expect($EL_LookupFunction(hb)).toBe(fb);
  });

  it("returns undefined for unknown handles", () => {
    expect($EL_LookupFunction(999)).toBeUndefined();
  });

  it("release-then-lookup misses cleanly", () => {
    const h = $EL_AllocateFunction(() => undefined);
    $EL_ReleaseFunction(h);
    expect($EL_LookupFunction(h)).toBeUndefined();
  });

  it("double-release is a no-op", () => {
    const h = $EL_AllocateFunction(() => undefined);
    $EL_ReleaseFunction(h);
    expect(() => $EL_ReleaseFunction(h)).not.toThrow();
    expect($EL_LookupFunction(h)).toBeUndefined();
  });
});

describe("shared handle space", () => {
  it("uses a single counter across object and function allocations", () => {
    const ho = $EL_AllocateObject({});
    const hf = $EL_AllocateFunction(() => undefined);
    const ho2 = $EL_AllocateObject({});

    expect(hf).toBeGreaterThan(ho);
    expect(ho2).toBeGreaterThan(hf);
  });
});

describe("factory registry", () => {
  it("registers and looks up factories with their return shape", () => {
    const fn = vi.fn();
    $EL_RegisterFactory("makeWidget", fn, "object");

    const entry = $EL_LookupFactory("makeWidget");
    expect(entry).toBeDefined();
    expect(entry?.fn).toBe(fn);
    expect(entry?.returnShape).toBe("object");
  });

  it("returns undefined for unknown factory names", () => {
    expect($EL_LookupFactory("nope")).toBeUndefined();
  });

  it("overwrites an existing factory of the same name", () => {
    const first = vi.fn();
    const second = vi.fn();
    $EL_RegisterFactory("makeWidget", first, "object");
    $EL_RegisterFactory("makeWidget", second, "value");

    const entry = $EL_LookupFactory("makeWidget");
    expect(entry?.fn).toBe(second);
    expect(entry?.returnShape).toBe("value");
  });
});

describe("method shape registry", () => {
  it("returns 'value' for a handle with no registered method shapes", () => {
    const h = $EL_AllocateObject({});
    expect($EL_GetMethodShape(h, "someMethod")).toBe("value");
  });

  it("returns 'value' for an unknown method on a handle that has shapes", () => {
    const h = $EL_AllocateObject({});
    $EL_RegisterMethods(h, { knownMethod: { returnShape: "object" } });
    expect($EL_GetMethodShape(h, "unknownMethod")).toBe("value");
  });

  it("returns the registered return shape for a known method", () => {
    const h = $EL_AllocateObject({});
    $EL_RegisterMethods(h, {
      addListener: { returnShape: "function" },
      create: { returnShape: "object" },
      compute: { returnShape: "value" },
      noop: { returnShape: "void" },
    });

    expect($EL_GetMethodShape(h, "addListener")).toBe("function");
    expect($EL_GetMethodShape(h, "create")).toBe("object");
    expect($EL_GetMethodShape(h, "compute")).toBe("value");
    expect($EL_GetMethodShape(h, "noop")).toBe("void");
  });

  it("overwrites previously registered shapes for the same handle", () => {
    const h = $EL_AllocateObject({});
    $EL_RegisterMethods(h, { foo: { returnShape: "object" } });
    $EL_RegisterMethods(h, { foo: { returnShape: "function" } });
    expect($EL_GetMethodShape(h, "foo")).toBe("function");
  });

  it("EL_ReleaseObject also clears method shapes for the handle", () => {
    const h = $EL_AllocateObject({});
    $EL_RegisterMethods(h, { foo: { returnShape: "object" } });

    $EL_ReleaseObject(h);

    expect(g._EL_MethodShapes[h]).toBeUndefined();
    expect($EL_GetMethodShape(h, "foo")).toBe("value");
  });
});
