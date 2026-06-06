import { readFileSync } from "fs";
import { dirname, join } from "path";
import { fileURLToPath } from "url";
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
  _EL_BridgeName: string;
};

type JslibLibrary = {
  $EL_BridgeName: string;
  EL_SetBridgeName__deps: string[];
  EL_SetBridgeName: (namePtr: number) => void;
  $EL_Log: (level: string, scope: string, msg: string) => void;
};

function loadJslib({
  utf8ToString = (ptr: number) => `string_at_${ptr}`,
}: { utf8ToString?: (ptr: number) => string } = {}): JslibLibrary {
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
  g._EL_BridgeName = "";

  // Execute the jslib outside any module scope so implicit global assignments
  // (e.g. `_EL_BridgeName = UTF8ToString(ptr)`) reach globalThis.
  new Function(jslibSource)(); // eslint-disable-line no-new-func

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
