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

// Stripping-regression for the Emscripten JSDCE destructuring bug
// (Docs~/plans/jslib-bundler-iife-scope.md, "Post-fix symptom" section).
// `WebSocketConnection.create` in the bundled SDK does
//   const { name: source, version } = sourceInfo;
// then uses `${source}` and `${version}` in a template literal. Emscripten's
// own `tools/acorn-optimizer.js` JSDCE pass strips that declaration while
// leaving the template references intact, producing a runtime
// `ReferenceError: source is not defined` from the first WebSocket session
// open. The `lowerDestructuring` Rolldown plugin rewrites the destructuring
// to plain `source = sourceInfo.name, version = sourceInfo.version` form
// before Emscripten sees it, which JSDCE leaves alone.
//
// The shape we check for: every `${source}` template reference inside the
// `WebSocketConnection.create` body must be matched by a `source =`
// declaration in the same function scope. Same for `${version}`. The pre-fix
// build fails this assertion at runtime; the post-fix build must keep both
// halves in sync — if Rolldown ever stops emitting the lowered form, or the
// SDK refactors the URL construction, this test fires before a user does.
test("framework.js retains `source` and `version` declarations inside WebSocketConnection.create", () => {
  const idx = frameworkJs.indexOf("WebSocketConnection");
  expect(idx, "WebSocketConnection symbol missing from framework.js").toBeGreaterThanOrEqual(0);

  // Conservatively isolate the create() body. We don't try to parse — just
  // take a fixed window forward from the WebSocketConnection occurrence. The
  // bundled create() body is ~2.5KB; 8KB gives plenty of margin without
  // crossing into unrelated classes.
  const region = frameworkJs.slice(idx, idx + 8000);

  // The URL construction uses `source=${source}` AND `version=${version}` —
  // both halves are signed-URL parameters the SDK has shipped for releases.
  // If the SDK ever drops them, the test below would over-fire; revisit then.
  expect(
    region,
    "source=${source} template reference missing — the SDK might have changed; check WebSocketConnection.create",
  ).toContain("source=${source}");
  expect(
    region,
    "version=${version} template reference missing — the SDK might have changed; check WebSocketConnection.create",
  ).toContain("version=${version}");

  // The declaration. Accept either of:
  //   * the destructuring form `{name:source,version}` (if Rolldown ever
  //     learns to leave it alone AND Emscripten JSDCE gets fixed upstream)
  //   * the lowered form `source = sourceInfo.name` / `source=sourceInfo.name`
  // Either keeps `source` and `version` bound in scope.
  const hasDestructuring = /\{\s*name\s*:\s*source\s*,\s*version\s*\}/.test(region);
  const hasLowered = /\bsource\s*=\s*sourceInfo\.name\b/.test(region);
  expect(
    hasDestructuring || hasLowered,
    "WebSocketConnection.create has `${source}` template references but no `source` binding — " +
      "Emscripten's acorn-optimizer JSDCE pass likely stripped the declaration. " +
      "See Docs~/plans/jslib-bundler-iife-scope.md.",
  ).toBe(true);
});
