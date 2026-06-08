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

type PendingInvocation = {
  resolve: (value: string) => void;
  reject: (reason: Error) => void;
};

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
  _EL_Observers: Record<number, () => void>;
  _EL_PendingInvocations: Record<number, PendingInvocation>;
  _EL_InvocationCounter: number;
};

type JslibLibrary = {
  $EL_BridgeName: string;
  EL_SetBridgeName__deps: string[];
  EL_SetBridgeName: (namePtr: number) => void;
  $EL_Log: (level: string, scope: string, msg: string) => void;
  $EL_CallPromise__deps: string[];
  $EL_CallPromise: (promiseId: number, promise: Promise<unknown>) => void;
  $EL_Observers: Record<number, () => void>;
  $EL_RegisterObserver__deps: string[];
  $EL_RegisterObserver: (observerId: number, unsubscribe: () => void) => void;
  $EL_FireObserverEvent__deps: string[];
  $EL_FireObserverEvent: (observerId: number, payload: string) => void;
  EL_DisposeObserver__deps: string[];
  EL_DisposeObserver: (observerId: number) => void;
  $EL_PendingInvocations: Record<number, PendingInvocation>;
  $EL_InvocationCounter: number;
  $EL_CreateInvocation__deps: string[];
  $EL_CreateInvocation: (
    handlerName: string,
    payload: string,
  ) => Promise<string>;
  EL_ResolveInvocation__deps: string[];
  EL_ResolveInvocation: (invocationId: number, resultPtr: number) => void;
  EL_RejectInvocation__deps: string[];
  EL_RejectInvocation: (invocationId: number, errorPtr: number) => void;
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
  const lib = library as unknown as JslibLibrary;
  g._EL_Log = lib.$EL_Log;
  g._EL_Observers = lib.$EL_Observers;
  g._EL_PendingInvocations = lib.$EL_PendingInvocations;
  g._EL_InvocationCounter = lib.$EL_InvocationCounter;

  return lib;
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

describe("Observer lifecycle", () => {
  it("$EL_RegisterObserver stores the unsubscribe function keyed by observer ID", () => {
    const lib = loadJslib();
    const g = globalThis as unknown as UnityGlobals;
    const unsubscribe = vi.fn();

    lib.$EL_RegisterObserver(42, unsubscribe);

    expect(g._EL_Observers[42]).toBe(unsubscribe);
  });

  it("$EL_FireObserverEvent calls SendMessage with id:payload format", () => {
    const sendMessage = vi.fn();
    const lib = loadJslib({
      utf8ToString: () => "__ElevenLabsBridge__",
      sendMessage,
    });
    lib.EL_SetBridgeName(0);

    lib.$EL_FireObserverEvent(7, '{"type":"agent_response"}');

    expect(sendMessage).toHaveBeenCalledWith(
      "__ElevenLabsBridge__",
      "OnObserverEvent",
      '7:{"type":"agent_response"}',
    );
  });

  it("EL_DisposeObserver calls the stored unsubscribe and removes the entry", () => {
    const lib = loadJslib();
    const g = globalThis as unknown as UnityGlobals;
    const unsubscribe = vi.fn();

    lib.$EL_RegisterObserver(42, unsubscribe);
    lib.EL_DisposeObserver(42);

    expect(unsubscribe).toHaveBeenCalledOnce();
    expect(g._EL_Observers[42]).toBeUndefined();
  });

  it("EL_DisposeObserver is a no-op when the observer ID is unknown", () => {
    const lib = loadJslib();
    expect(() => lib.EL_DisposeObserver(999)).not.toThrow();
  });

  it("double dispose is safe — unsubscribe called only once", () => {
    const lib = loadJslib();
    const unsubscribe = vi.fn();

    lib.$EL_RegisterObserver(1, unsubscribe);
    lib.EL_DisposeObserver(1);
    lib.EL_DisposeObserver(1);

    expect(unsubscribe).toHaveBeenCalledOnce();
  });

  it("$EL_RegisterObserver and $EL_FireObserverEvent are declared with correct deps", () => {
    const lib = loadJslib();
    expect(lib["$EL_RegisterObserver__deps"]).toContain("$EL_Observers");
    expect(lib["$EL_FireObserverEvent__deps"]).toContain("$EL_BridgeName");
    expect(lib["EL_DisposeObserver__deps"]).toContain("$EL_Observers");
    expect(lib["EL_DisposeObserver__deps"]).toContain("$EL_Log");
  });
});

describe("Handler Invocation lifecycle", () => {
  it("$EL_CreateInvocation fires SendMessage with id:handlerName:payload format", () => {
    const sendMessage = vi.fn();
    const lib = loadJslib({
      utf8ToString: () => "__ElevenLabsBridge__",
      sendMessage,
    });
    lib.EL_SetBridgeName(0);

    lib.$EL_CreateInvocation("tool:get_weather", '{"location":"London"}');

    expect(sendMessage).toHaveBeenCalledWith(
      "__ElevenLabsBridge__",
      "OnHandlerInvoked",
      expect.stringMatching(/^\d+:tool:get_weather:\{"location":"London"\}$/),
    );
  });

  it("$EL_CreateInvocation stores resolve and reject in $EL_PendingInvocations", () => {
    const lib = loadJslib({
      utf8ToString: () => "__ElevenLabsBridge__",
      sendMessage: vi.fn(),
    });
    lib.EL_SetBridgeName(0);
    const g = globalThis as unknown as UnityGlobals;

    lib.$EL_CreateInvocation("handler:a", "{}");

    const ids = Object.keys(g._EL_PendingInvocations).map(Number);
    expect(ids).toHaveLength(1);
    expect(g._EL_PendingInvocations[ids[0]]).toMatchObject({
      resolve: expect.any(Function),
      reject: expect.any(Function),
    });
  });

  it("returned Promise resolves with the decoded string when EL_ResolveInvocation is called", async () => {
    const messages: string[] = [];
    const lib = loadJslib({
      utf8ToString: (ptr: number) =>
        ptr === 0 ? "__ElevenLabsBridge__" : `decoded:${ptr}`,
      sendMessage: vi.fn((_go, _method, msg: string) => messages.push(msg)),
    });
    lib.EL_SetBridgeName(0);

    const promise = lib.$EL_CreateInvocation("tool:get_weather", "{}");
    const id = parseInt(messages[0].split(":")[0]);
    lib.EL_ResolveInvocation(id, 42);

    await expect(promise).resolves.toBe("decoded:42");
  });

  it("returned Promise rejects with an Error when EL_RejectInvocation is called", async () => {
    const messages: string[] = [];
    const lib = loadJslib({
      utf8ToString: (ptr: number) =>
        ptr === 0 ? "__ElevenLabsBridge__" : `error msg ${ptr}`,
      sendMessage: vi.fn((_go, _method, msg: string) => messages.push(msg)),
    });
    lib.EL_SetBridgeName(0);

    const promise = lib.$EL_CreateInvocation("tool:get_weather", "{}");
    const id = parseInt(messages[0].split(":")[0]);
    lib.EL_RejectInvocation(id, 99);

    await expect(promise).rejects.toThrow("error msg 99");
  });

  it("EL_ResolveInvocation removes the entry from $EL_PendingInvocations", async () => {
    const messages: string[] = [];
    const lib = loadJslib({
      utf8ToString: (ptr: number) =>
        ptr === 0 ? "__ElevenLabsBridge__" : "result",
      sendMessage: vi.fn((_go, _method, msg: string) => messages.push(msg)),
    });
    lib.EL_SetBridgeName(0);
    const g = globalThis as unknown as UnityGlobals;

    lib.$EL_CreateInvocation("handler:a", "{}");
    const id = parseInt(messages[0].split(":")[0]);
    lib.EL_ResolveInvocation(id, 0);

    expect(g._EL_PendingInvocations[id]).toBeUndefined();
  });

  it("EL_ResolveInvocation is a no-op for unknown IDs", () => {
    const lib = loadJslib({
      utf8ToString: () => "result",
      sendMessage: vi.fn(),
    });
    expect(() => lib.EL_ResolveInvocation(999, 0)).not.toThrow();
  });

  it("EL_RejectInvocation is a no-op for unknown IDs", () => {
    const lib = loadJslib({
      utf8ToString: () => "error",
      sendMessage: vi.fn(),
    });
    expect(() => lib.EL_RejectInvocation(999, 0)).not.toThrow();
  });

  it("double resolve is safe — entry removed after first call, second is no-op", async () => {
    const messages: string[] = [];
    const lib = loadJslib({
      utf8ToString: (ptr: number) =>
        ptr === 0 ? "__ElevenLabsBridge__" : "result",
      sendMessage: vi.fn((_go, _method, msg: string) => messages.push(msg)),
    });
    lib.EL_SetBridgeName(0);

    const promise = lib.$EL_CreateInvocation("handler:a", "{}");
    const id = parseInt(messages[0].split(":")[0]);
    lib.EL_ResolveInvocation(id, 1); // ptr=1 → "result"
    lib.EL_ResolveInvocation(id, 1); // second call: entry already removed, no-op

    await expect(promise).resolves.toBe("result");
  });

  it("concurrent invocations receive unique monotonically increasing IDs", () => {
    const lib = loadJslib({
      utf8ToString: () => "__ElevenLabsBridge__",
      sendMessage: vi.fn(),
    });
    lib.EL_SetBridgeName(0);
    const g = globalThis as unknown as UnityGlobals;

    lib.$EL_CreateInvocation("handler:a", "{}");
    lib.$EL_CreateInvocation("handler:b", "{}");

    const ids = Object.keys(g._EL_PendingInvocations).map(Number);
    expect(ids).toHaveLength(2);
    expect(ids[1]).toBeGreaterThan(ids[0]);
  });

  it("declares correct deps for all handler invocation entries", () => {
    const lib = loadJslib();
    expect(lib["$EL_CreateInvocation__deps"]).toContain("$EL_BridgeName");
    expect(lib["$EL_CreateInvocation__deps"]).toContain(
      "$EL_PendingInvocations",
    );
    expect(lib["$EL_CreateInvocation__deps"]).toContain(
      "$EL_InvocationCounter",
    );
    expect(lib["EL_ResolveInvocation__deps"]).toContain(
      "$EL_PendingInvocations",
    );
    expect(lib["EL_ResolveInvocation__deps"]).toContain("$EL_Log");
    expect(lib["EL_RejectInvocation__deps"]).toContain(
      "$EL_PendingInvocations",
    );
    expect(lib["EL_RejectInvocation__deps"]).toContain("$EL_Log");
  });
});
