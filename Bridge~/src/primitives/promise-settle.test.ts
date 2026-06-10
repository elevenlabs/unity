import { beforeEach, describe, expect, it, vi } from "vitest";
import { $EL_Settle } from "./promise-settle";

type ElGlobals = typeof globalThis & {
  _EL_BridgeName: string;
  SendMessage: (gameObject: string, method: string, value: string) => void;
};

const g = globalThis as ElGlobals;

beforeEach(() => {
  vi.stubGlobal("_EL_BridgeName", "__ElevenLabsBridge__");
  vi.stubGlobal("SendMessage", vi.fn());
});

describe("$EL_Settle", () => {
  it("fires SendMessage with OnPromiseSettled for an ok result", () => {
    $EL_Settle(10, "ok", '"hello"');
    expect(g.SendMessage).toHaveBeenCalledWith(
      "__ElevenLabsBridge__",
      "OnPromiseSettled",
      '10:ok:"hello"',
    );
  });

  it("fires SendMessage with OnPromiseSettled for an err result", () => {
    $EL_Settle(10, "err", "something went wrong");
    expect(g.SendMessage).toHaveBeenCalledWith(
      "__ElevenLabsBridge__",
      "OnPromiseSettled",
      "10:err:something went wrong",
    );
  });

  it("uses the current _EL_BridgeName at call time", () => {
    g._EL_BridgeName = "CustomBridge";
    $EL_Settle(1, "ok", "null");
    expect(g.SendMessage).toHaveBeenCalledWith(
      "CustomBridge",
      "OnPromiseSettled",
      "1:ok:null",
    );
  });

  it("formats the value as promiseId + ':' + status + ':' + payload", () => {
    $EL_Settle(99, "ok", '{"result":42}');
    const value = (g.SendMessage as ReturnType<typeof vi.fn>).mock.calls[0][2];
    expect(value).toBe('99:ok:{"result":42}');
  });

  it("error message with colons in payload survives intact", () => {
    $EL_Settle(5, "err", "TypeError: Cannot read property 'x' of null");
    const value = (g.SendMessage as ReturnType<typeof vi.fn>).mock.calls[0][2];
    expect(value).toBe("5:err:TypeError: Cannot read property 'x' of null");
  });

  it("JSON-escaped error message survives intact", () => {
    const jsonPayload = '"Error: line \\"1\\""';
    $EL_Settle(7, "err", jsonPayload);
    const value = (g.SendMessage as ReturnType<typeof vi.fn>).mock.calls[0][2];
    expect(value).toBe("7:err:" + jsonPayload);
  });
});
