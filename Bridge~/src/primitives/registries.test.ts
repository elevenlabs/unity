import { beforeEach, describe, expect, it, vi } from "vitest";
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

// The primitive helpers reference `EL_Objects`, `EL_Functions`,
// `EL_Factories`, `EL_NextHandleId` — the Unity-hoisted module-level globals.
// In Node we stub them on globalThis via vi.stubGlobal so vitest restores them
// between tests (unstubGlobals is enabled in vitest.config.ts).

beforeEach(() => {
  vi.stubGlobal("EL_Objects", {});
  vi.stubGlobal("EL_Functions", {});
  vi.stubGlobal("EL_Factories", {});
  vi.stubGlobal("EL_NextHandleId", 1);
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
  it("registers and looks up factories by name", () => {
    const fn = vi.fn();
    $EL_RegisterFactory("makeWidget", fn);

    expect($EL_LookupFactory("makeWidget")).toBe(fn);
  });

  it("returns undefined for unknown factory names", () => {
    expect($EL_LookupFactory("nope")).toBeUndefined();
  });

  it("overwrites an existing factory of the same name", () => {
    const first = vi.fn();
    const second = vi.fn();
    $EL_RegisterFactory("makeWidget", first);
    $EL_RegisterFactory("makeWidget", second);

    expect($EL_LookupFactory("makeWidget")).toBe(second);
  });
});
