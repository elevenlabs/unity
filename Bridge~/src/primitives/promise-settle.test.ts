import { beforeEach, describe, expect, it, vi } from "vitest";
import { $EL_Settle } from "./promise-settle";

type ElGlobals = typeof globalThis & {
  EL_SettlePtr: number;
  dynCall_viii: (
    fnPtr: number,
    arg0: number,
    arg1: number,
    arg2: number,
  ) => void;
  stringToNewUTF8: (str: string) => number;
  _free: (ptr: number) => void;
};

const g = globalThis as ElGlobals;

const FAKE_PTR = 0xdeadbeef;
const FAKE_SETTLE_PTR = 42;

beforeEach(() => {
  vi.stubGlobal("EL_SettlePtr", FAKE_SETTLE_PTR);
  vi.stubGlobal(
    "stringToNewUTF8",
    vi.fn(() => FAKE_PTR),
  );
  vi.stubGlobal("_free", vi.fn());
  vi.stubGlobal("dynCall_viii", vi.fn());
});

describe("$EL_Settle", () => {
  it("calls dynCall_viii with statusCode 0 for ok", () => {
    $EL_Settle(10, "ok", '"hello"');
    expect(g.dynCall_viii).toHaveBeenCalledWith(
      FAKE_SETTLE_PTR,
      10,
      0,
      FAKE_PTR,
    );
  });

  it("calls dynCall_viii with statusCode 1 for err", () => {
    $EL_Settle(10, "err", "something went wrong");
    expect(g.dynCall_viii).toHaveBeenCalledWith(
      FAKE_SETTLE_PTR,
      10,
      1,
      FAKE_PTR,
    );
  });

  it("allocates the payload string via stringToNewUTF8", () => {
    $EL_Settle(3, "ok", '"result"');
    expect(g.stringToNewUTF8).toHaveBeenCalledWith('"result"');
  });

  it("frees the payload buffer after dynCall_viii returns", () => {
    const callOrder: string[] = [];
    vi.stubGlobal(
      "dynCall_viii",
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
    $EL_Settle(1, "ok", "null");
    expect(callOrder).toEqual(["dynCall", "free"]);
  });

  it("frees the payload buffer even if dynCall_viii throws", () => {
    vi.stubGlobal(
      "dynCall_viii",
      vi.fn(() => {
        throw new Error("wasm trap");
      }),
    );
    expect(() => $EL_Settle(5, "ok", "null")).toThrow("wasm trap");
    expect(g._free).toHaveBeenCalledWith(FAKE_PTR);
  });

  it("passes the current EL_SettlePtr at call time", () => {
    vi.stubGlobal("EL_SettlePtr", 99);
    $EL_Settle(1, "ok", "null");
    expect(g.dynCall_viii).toHaveBeenCalledWith(99, 1, 0, FAKE_PTR);
  });

  it("error payload with colons survives intact to stringToNewUTF8", () => {
    $EL_Settle(5, "err", "TypeError: Cannot read property 'x' of null");
    expect(g.stringToNewUTF8).toHaveBeenCalledWith(
      "TypeError: Cannot read property 'x' of null",
    );
  });

  it("JSON-escaped error payload survives intact to stringToNewUTF8", () => {
    const jsonPayload = '"Error: line \\"1\\""';
    $EL_Settle(7, "err", jsonPayload);
    expect(g.stringToNewUTF8).toHaveBeenCalledWith(jsonPayload);
  });
});
