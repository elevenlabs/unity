import { brotliDecompressSync, gunzipSync } from "node:zlib";
import { readFileSync, existsSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect, beforeAll } from "vitest";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// Same artifact the live conversation-smoke harness consumes; see
// TestProject/build-webgl-conversation.sh.
const BUILD_DIR = path.resolve(
  __dirname,
  "..",
  "..",
  "TestProject",
  "Build",
  "WebGLConversationSmoke",
  "Build",
);

// Unity 6 emits the framework JS as <name>.framework.js, optionally
// post-compressed to <name>.framework.js.gz or .br depending on the build
// settings. The conversation smoke build currently emits .gz; we accept either
// (and the uncompressed form) so the test stays useful if compression flips.
const CANDIDATES = [
  "WebGLConversationSmoke.framework.js.gz",
  "WebGLConversationSmoke.framework.js.br",
  "WebGLConversationSmoke.framework.js",
];

let frameworkJs = "";

beforeAll(() => {
  if (!existsSync(BUILD_DIR)) {
    throw new Error(
      `WebGL build not found at ${BUILD_DIR}.\n` +
        `Run bash TestProject/build-webgl-conversation.sh first.`,
    );
  }
  const found = CANDIDATES.map((name) => path.join(BUILD_DIR, name)).find(
    existsSync,
  );
  if (!found) {
    throw new Error(
      `No framework.js artifact found under ${BUILD_DIR}.\n` +
        `Looked for: ${CANDIDATES.join(", ")}.`,
    );
  }
  const bytes = readFileSync(found);
  if (found.endsWith(".gz")) {
    frameworkJs = gunzipSync(bytes).toString("utf8");
  } else if (found.endsWith(".br")) {
    frameworkJs = brotliDecompressSync(bytes).toString("utf8");
  } else {
    frameworkJs = bytes.toString("utf8");
  }
});

// Stripping-regression for the bundler IIFE-scope bug
// (Docs~/plans/jslib-bundler-iife-scope.md). The connection .jslib used to
// wrap the bundled @elevenlabs/client/internal/unity SDK in a Rolldown IIFE;
// only the library object returned from the IIFE survived Emscripten's
// library-evaluation step, so the SDK classes the factory closures captured
// ended up undefined at runtime. The three assertions cover the three pieces
// of evidence the plan called out — a class definition, a runtime
// side-effect call, and a factory-registration call — so a regression on any
// one of them fails loud.
test("conversation-smoke framework.js retains SDK symbols the connection .jslib closes over", () => {
  expect(
    frameworkJs,
    "WebSocketConnection class missing — bundled SDK was DCE'd out of the runtime",
  ).toMatch(/class WebSocketConnection\b/);

  expect(
    frameworkJs,
    "setWebRTCAudioAdapterFactory call missing — connection .jslib side-effects never reached runtime",
  ).toContain("setWebRTCAudioAdapterFactory");

  // EL_ConnectionFactories was the pre-fix library variable that the IIFE
  // exposed; the new postset-based bundler doesn't surface it as a library
  // entry, but the registration path still has to land in framework.js. The
  // concrete name we check for is the connectionFactories map that
  // Bridge~/src/connection/index.ts iterates to call EL_RegisterFactory.
  expect(
    frameworkJs,
    "connectionFactories map missing — bundle didn't reach framework.js",
  ).toContain("connectionFactories");
});
