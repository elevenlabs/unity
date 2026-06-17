import { beforeEach, describe, expect, it, vi } from "vitest";
import { $EL_InvokeCallback } from "./callbacks";

type ElGlobals = typeof globalThis & {
  EL_CallbackPtr: number;
  dynCall_vii: (fnPtr: number, arg0: number, arg1: number) => void;
  stringToNewUTF8: (str: string) => number;
  _free: (ptr: number) => void;
};

const g = globalThis as ElGlobals;

const FAKE_PTR = 0xdeadbeef;
const FAKE_CALLBACK_PTR = 42;

beforeEach(() => {
  vi.stubGlobal("EL_CallbackPtr", FAKE_CALLBACK_PTR);
  vi.stubGlobal(
    "stringToNewUTF8",
    vi.fn(() => FAKE_PTR),
  );
  vi.stubGlobal("_free", vi.fn());
  vi.stubGlobal("dynCall_vii", vi.fn());
});

describe("$EL_InvokeCallback", () => {
  it("calls dynCall_vii with the handle and payload pointer", () => {
    $EL_InvokeCallback(42, '"hello"');
    expect(g.dynCall_vii).toHaveBeenCalledWith(FAKE_CALLBACK_PTR, 42, FAKE_PTR);
  });

  it("uses the current EL_CallbackPtr at call time", () => {
    vi.stubGlobal("EL_CallbackPtr", 99);
    $EL_InvokeCallback(7, "{}");
    expect(g.dynCall_vii).toHaveBeenCalledWith(99, 7, FAKE_PTR);
  });

  it("allocates the payload string via stringToNewUTF8", () => {
    $EL_InvokeCallback(1, '{"x":1}');
    expect(g.stringToNewUTF8).toHaveBeenCalledWith('{"x":1}');
  });

  it("frees the payload buffer after dynCall_vii returns", () => {
    const callOrder: string[] = [];
    vi.stubGlobal(
      "dynCall_vii",
      vi.fn(() => {
        callOrder.push("dynCall");
      }),
    );
    vi.stubGlobal(
      "_free",
      vi.fn(() => {
        callOrder.push("free");
      }),
    );
    $EL_InvokeCallback(1, "null");
    expect(callOrder).toEqual(["dynCall", "free"]);
  });

  it("frees the payload buffer even if dynCall_vii throws", () => {
    vi.stubGlobal(
      "dynCall_vii",
      vi.fn(() => {
        throw new Error("wasm trap");
      }),
    );
    expect(() => $EL_InvokeCallback(5, "null")).toThrow("wasm trap");
    expect(g._free).toHaveBeenCalledWith(FAKE_PTR);
  });

  it("passes the payload through unchanged to stringToNewUTF8", () => {
    const payload = '["nested","array"]';
    $EL_InvokeCallback(5, payload);
    expect(g.stringToNewUTF8).toHaveBeenCalledWith(payload);
  });
});
