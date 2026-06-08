import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const jslibSource = readFileSync(
  join(__dirname, "../../Plugins/WebGL/ElevenLabsBridge.jslib"),
  "utf-8",
);

// The jslib uses Unity-injected globals (mergeInto, LibraryManager, UTF8ToString,
// _EL_BridgeName). new Function executes in sloppy (non-strict) mode, so implicit
// global assignments like `_EL_BridgeName = ...` land on globalThis — matching
// how Unity injects shared state from $-prefixed entries at build time.

type UnityGlobals = {
  LibraryManager: { library: Record<string, unknown> };
  mergeInto: (
    target: Record<string, unknown>,
    source: Record<string, unknown>,
  ) => void;
  UTF8ToString: (ptr: number) => string;
  SendMessage: (gameObject: string, method: string, value: string) => void;
  _EL_BridgeName: string;
  _EL_Log: (level: string, scope: string, msg: string) => void;
};

type JslibLibrary = {
  $EL_BridgeName: string;
  EL_SetBridgeName__deps: string[];
  EL_SetBridgeName: (namePtr: number) => void;
  $EL_Log: (level: string, scope: string, msg: string) => void;
  $EL_CallPromise__deps: string[];
  $EL_CallPromise: (promiseId: number, promise: Promise<unknown>) => void;
};

function loadJslib({
  utf8ToString = (ptr: number) => `string_at_${ptr}`,
  sendMessage = vi.fn(),
}: {
  utf8ToString?: (ptr: number) => string;
  sendMessage?: (gameObject: string, method: string, value: string) => void;
} = {}): JslibLibrary {
  const library: Record<string, unknown> = {};
  const g = globalThis as unknown as UnityGlobals;
  g.LibraryManager = { library };
  g.mergeInto = (
    target: Record<string, unknown>,
    source: Record<string, unknown>,
  ) => {
    Object.assign(target, source);
  };
  g.UTF8ToString = utf8ToString;
  g.SendMessage = sendMessage;
  g._EL_BridgeName = "";

  // Execute the jslib outside any module scope so implicit global assignments
  // (e.g. `_EL_BridgeName = UTF8ToString(ptr)`) reach globalThis.
  new Function(jslibSource)(); // eslint-disable-line no-new-func

  // Simulate Unity's dependency hoisting: $-prefixed entries that other
  // functions declare as deps become _-prefixed globals at build time.
  g._EL_Log = (library as unknown as JslibLibrary).$EL_Log;

  return library as unknown as JslibLibrary;
}

beforeEach(() => {
  (globalThis as unknown as UnityGlobals)._EL_BridgeName = "";
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("EL_SetBridgeName", () => {
  it("decodes the pointer via UTF8ToString", () => {
    const utf8ToString = vi.fn(() => "__ElevenLabsBridge__");
    const lib = loadJslib({ utf8ToString });

    lib.EL_SetBridgeName(42);

    expect(utf8ToString).toHaveBeenCalledWith(42);
  });

  it("stores the decoded name in _EL_BridgeName", () => {
    const lib = loadJslib({ utf8ToString: () => "__ElevenLabsBridge__" });
    const g = globalThis as unknown as UnityGlobals;
    g._EL_BridgeName = "";

    lib.EL_SetBridgeName(42);

    expect(g._EL_BridgeName).toBe("__ElevenLabsBridge__");
  });

  it("declares $EL_BridgeName as a dependency", () => {
    const lib = loadJslib();
    expect(lib["EL_SetBridgeName__deps"]).toContain("$EL_BridgeName");
  });
});

describe("$EL_Log", () => {
  it('routes info-level messages to console.log with "[ElevenLabs Bridge]" prefix', () => {
    const log = vi.spyOn(console, "log").mockImplementation(() => {});
    const lib = loadJslib();

    lib.$EL_Log("info", "MyScope", "hello world");

    expect(log).toHaveBeenCalledWith(
      "[ElevenLabs Bridge] MyScope: hello world",
    );
  });

  it("routes warn-level messages to console.warn", () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    const lib = loadJslib();

    lib.$EL_Log("warn", "MyScope", "a warning");

    expect(warn).toHaveBeenCalledWith("[ElevenLabs Bridge] MyScope: a warning");
  });

  it("routes error-level messages to console.error", () => {
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    const lib = loadJslib();

    lib.$EL_Log("error", "MyScope", "something failed");

    expect(error).toHaveBeenCalledWith(
      "[ElevenLabs Bridge] MyScope: something failed",
    );
  });

  it("falls back to console.log for unrecognised levels", () => {
    const log = vi.spyOn(console, "log").mockImplementation(() => {});
    const lib = loadJslib();

    lib.$EL_Log("debug", "MyScope", "debug info");

    expect(log).toHaveBeenCalledWith("[ElevenLabs Bridge] MyScope: debug info");
  });

  it("includes scope and message verbatim in the formatted line", () => {
    const log = vi.spyOn(console, "log").mockImplementation(() => {});
    const lib = loadJslib();

    lib.$EL_Log("info", "EL_SetBridgeName", "payload with: colons");

    expect(log).toHaveBeenCalledWith(
      "[ElevenLabs Bridge] EL_SetBridgeName: payload with: colons",
    );
  });
});

describe("$EL_CallPromise", () => {
  it("declares $EL_BridgeName and $EL_Log as dependencies", () => {
    const lib = loadJslib();
    expect(lib["$EL_CallPromise__deps"]).toContain("$EL_BridgeName");
    expect(lib["$EL_CallPromise__deps"]).toContain("$EL_Log");
  });

  it("calls SendMessage with id:ok:payload when the promise resolves", async () => {
    const sendMessage = vi.fn();
    const lib = loadJslib({
      utf8ToString: () => "__ElevenLabsBridge__",
      sendMessage,
    });
    lib.EL_SetBridgeName(0);

    lib.$EL_CallPromise(42, Promise.resolve('{"answer":1}'));
    await Promise.resolve();

    expect(sendMessage).toHaveBeenCalledWith(
      "__ElevenLabsBridge__",
      "OnPromiseSettled",
      '42:ok:{"answer":1}',
    );
  });

  it("calls SendMessage with id:err:message when the promise rejects with an Error", async () => {
    const sendMessage = vi.fn();
    const lib = loadJslib({
      utf8ToString: () => "__ElevenLabsBridge__",
      sendMessage,
    });
    lib.EL_SetBridgeName(0);

    lib.$EL_CallPromise(7, Promise.reject(new Error("network timeout")));
    await Promise.resolve();

    expect(sendMessage).toHaveBeenCalledWith(
      "__ElevenLabsBridge__",
      "OnPromiseSettled",
      "7:err:network timeout",
    );
  });

  it("calls SendMessage with id:err:string when the promise rejects with a plain string", async () => {
    const sendMessage = vi.fn();
    const lib = loadJslib({
      utf8ToString: () => "__ElevenLabsBridge__",
      sendMessage,
    });
    lib.EL_SetBridgeName(0);

    lib.$EL_CallPromise(3, Promise.reject("bad input"));
    await Promise.resolve();

    expect(sendMessage).toHaveBeenCalledWith(
      "__ElevenLabsBridge__",
      "OnPromiseSettled",
      "3:err:bad input",
    );
  });

  it("treats null resolve value as empty payload", async () => {
    const sendMessage = vi.fn();
    const lib = loadJslib({
      utf8ToString: () => "__ElevenLabsBridge__",
      sendMessage,
    });
    lib.EL_SetBridgeName(0);

    lib.$EL_CallPromise(1, Promise.resolve(null));
    await Promise.resolve();

    expect(sendMessage).toHaveBeenCalledWith(
      "__ElevenLabsBridge__",
      "OnPromiseSettled",
      "1:ok:",
    );
  });

  it("logs an error via _EL_Log when the promise rejects", async () => {
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    const lib = loadJslib({
      utf8ToString: () => "__ElevenLabsBridge__",
    });
    lib.EL_SetBridgeName(0);

    lib.$EL_CallPromise(5, Promise.reject(new Error("oops")));
    await Promise.resolve();

    expect(error).toHaveBeenCalledWith(
      expect.stringContaining("[ElevenLabs Bridge]"),
    );
    expect(error).toHaveBeenCalledWith(expect.stringContaining("oops"));
  });
});
