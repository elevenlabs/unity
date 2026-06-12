import { beforeEach, describe, expect, it, vi } from "vitest";
import {
  EL_ProbeWasmTable,
  EL_SetInvokeCallbackPtr,
  EL_SetSettleCallback,
} from "./function-pointers";

type ElGlobals = typeof globalThis & {
  _EL_SettlePtr: number;
  _EL_CallbackPtr: number;
  Module: Record<string, unknown>;
};

const g = globalThis as ElGlobals;

beforeEach(() => {
  vi.stubGlobal("_EL_SettlePtr", 0);
  vi.stubGlobal("_EL_CallbackPtr", 0);
  vi.stubGlobal("Module", {});
});

describe("EL_SetSettleCallback", () => {
  it("writes the pointer to _EL_SettlePtr", () => {
    EL_SetSettleCallback(42);
    expect(g._EL_SettlePtr).toBe(42);
  });

  it("overwrites a previous value", () => {
    EL_SetSettleCallback(10);
    EL_SetSettleCallback(99);
    expect(g._EL_SettlePtr).toBe(99);
  });

  it("does not affect _EL_CallbackPtr", () => {
    EL_SetSettleCallback(42);
    expect(g._EL_CallbackPtr).toBe(0);
  });
});

describe("EL_SetInvokeCallbackPtr", () => {
  it("writes the pointer to _EL_CallbackPtr", () => {
    EL_SetInvokeCallbackPtr(7);
    expect(g._EL_CallbackPtr).toBe(7);
  });

  it("overwrites a previous value", () => {
    EL_SetInvokeCallbackPtr(1);
    EL_SetInvokeCallbackPtr(2);
    expect(g._EL_CallbackPtr).toBe(2);
  });

  it("does not affect _EL_SettlePtr", () => {
    EL_SetInvokeCallbackPtr(7);
    expect(g._EL_SettlePtr).toBe(0);
  });
});

describe("EL_ProbeWasmTable", () => {
  it("returns 1 when Module.wasmTable is defined", () => {
    vi.stubGlobal("Module", { wasmTable: {} });
    expect(EL_ProbeWasmTable()).toBe(1);
  });

  it("returns 0 when Module has no wasmTable property", () => {
    vi.stubGlobal("Module", {});
    expect(EL_ProbeWasmTable()).toBe(0);
  });

  it("returns 0 when Module.wasmTable is explicitly undefined", () => {
    vi.stubGlobal("Module", { wasmTable: undefined });
    expect(EL_ProbeWasmTable()).toBe(0);
  });
});
