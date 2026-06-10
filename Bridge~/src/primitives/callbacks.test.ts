import { beforeEach, describe, expect, it, vi } from "vitest";
import { $EL_InvokeCallback } from "./callbacks";

type ElGlobals = typeof globalThis & {
  _EL_BridgeName: string;
  SendMessage: (gameObject: string, method: string, value: string) => void;
};

const g = globalThis as ElGlobals;

beforeEach(() => {
  vi.stubGlobal("_EL_BridgeName", "__ElevenLabsBridge__");
  vi.stubGlobal("SendMessage", vi.fn());
});

describe("$EL_InvokeCallback", () => {
  it("fires SendMessage to the bridge game object with OnCallbackInvoked", () => {
    $EL_InvokeCallback(42, '"hello"');
    expect(g.SendMessage).toHaveBeenCalledWith(
      "__ElevenLabsBridge__",
      "OnCallbackInvoked",
      '42:"hello"',
    );
  });

  it("uses the current _EL_BridgeName at call time", () => {
    g._EL_BridgeName = "CustomBridge";
    $EL_InvokeCallback(7, "{}");
    expect(g.SendMessage).toHaveBeenCalledWith(
      "CustomBridge",
      "OnCallbackInvoked",
      "7:{}",
    );
  });

  it("formats the value as handle + ':' + payload", () => {
    $EL_InvokeCallback(1, '{"x":1}');
    const value = (g.SendMessage as ReturnType<typeof vi.fn>).mock.calls[0][2];
    expect(value).toBe('1:{"x":1}');
  });

  it("passes the payload through unchanged without re-serialising", () => {
    const payload = '["nested","array"]';
    $EL_InvokeCallback(5, payload);
    const value = (g.SendMessage as ReturnType<typeof vi.fn>).mock.calls[0][2];
    expect(value).toBe("5:" + payload);
  });
});
