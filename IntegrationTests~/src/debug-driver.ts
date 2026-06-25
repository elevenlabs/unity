// Standalone Playwright driver for ad-hoc debugging of a Unity WebGL build.
// Loads the build, sets the web-audio-sink debug flag, captures EVERY
// console line (not just smoke-tagged ones), and writes them to stdout
// timestamped so cross-checking C# Debug.Log against JS console.log is
// trivial. Exits when the smoke logs a pass/fail/skip marker — or after a
// hard duration cap if nothing matches.
//
// Usage:
//   node IntegrationTests~/src/debug-driver.ts \
//        [build-dir] [duration-seconds]
//
// Defaults: TestProject/Build/WebGLConversationSpatialSmoke, 90 seconds.
//
// Exits 0 on pass, 1 on fail / timeout. Useful for running locally and
// piping output to a file:
//
//   node IntegrationTests~/src/debug-driver.ts > /tmp/spatial-debug.log 2>&1

import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";
// .ts extension (not the .js alias used by sibling test files) so plain
// `node IntegrationTests~/src/debug-driver.ts` resolves the helper without
// needing tsx or a transpilation step.
import { startWebGLServer } from "./serve-webgl.ts";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

const args = process.argv.slice(2);
const buildDir =
  args[0] ??
  path.resolve(
    __dirname,
    "..",
    "..",
    "TestProject",
    "Build",
    "WebGLConversationSpatialSmoke",
  );
const durationSeconds = args[1] ? Number.parseInt(args[1], 10) : 90;

// Pass / fail / skip markers — match either the conversation smoke or the
// spatial variant so the driver works for both builds out of the box.
const PASS_MARKERS = [
  "[ConvSmoke] All conversation smoke tests passed!",
  "[SpatialSmoke] All spatial smoke tests passed!",
];
const FAIL_MARKERS = ["ASSERTION FAILED"];
const SKIP_MARKERS = ["[ConvSmoke] CONFIG MISSING", "[SpatialSmoke] CONFIG MISSING"];

function stamp(): string {
  return new Date().toISOString().slice(11, 23); // HH:MM:SS.mmm
}

function log(...args: unknown[]): void {
  // eslint-disable-next-line no-console
  console.log(`[${stamp()}]`, ...args);
}

async function main(): Promise<void> {
  log(`debug-driver starting; buildDir = ${buildDir}, duration = ${durationSeconds}s`);
  const server = await startWebGLServer(buildDir);
  log(`server listening at ${server.url}`);
  const browser = await chromium.launch({
    args: ["--disable-dev-shm-usage", "--no-sandbox"],
  });
  const page = await browser.newPage();

  let exitCode = 1;
  let resolveOutcome!: (code: number) => void;
  const outcome = new Promise<number>((resolve) => {
    resolveOutcome = resolve;
  });

  try {
    page.on("console", (msg) => {
      const text = msg.text();
      log(`[${msg.type()}]`, text);
      if (PASS_MARKERS.some((m) => text.includes(m))) {
        log("PASS marker detected");
        resolveOutcome(0);
      } else if (FAIL_MARKERS.some((m) => text.includes(m))) {
        log("FAIL marker detected");
        resolveOutcome(1);
      } else if (SKIP_MARKERS.some((m) => text.includes(m))) {
        log("SKIP marker detected");
        resolveOutcome(0);
      }
    });
    page.on("pageerror", (err) => {
      log("[pageerror]", err.message);
    });
    page.on("crash", () => {
      log("[crash] page crashed");
      resolveOutcome(1);
    });
    page.on("websocket", (ws) => {
      log(`[ws] opened: ${ws.url()}`);
      ws.on("framereceived", (event) => {
        try {
          const obj = JSON.parse(event.payload.toString());
          if (obj.type && typeof obj.type === "string") {
            log(`[ws recv] type=${obj.type}`);
          }
        } catch {
          // Non-JSON frame (binary) — uninteresting for protocol-shape debugging.
        }
      });
    });

    // Enable the JS-side debug logging in the page context BEFORE Unity loads
    // and constructs the sink. Without addInitScript the flag wouldn't be set
    // when createWebAudioSink first runs.
    await page.addInitScript(() => {
      (window as unknown as { __elevenLabsWebAudioDebug__: boolean }).__elevenLabsWebAudioDebug__ =
        true;
    });

    await page.goto(server.url);

    const timer = setTimeout(() => {
      log(`duration cap of ${durationSeconds}s hit without a pass/fail/skip marker`);
      resolveOutcome(1);
    }, durationSeconds * 1000);

    exitCode = await outcome;
    clearTimeout(timer);
    log(`exiting with code ${exitCode}`);
  } finally {
    await page.close();
    await browser.close();
    server.close();
  }
  process.exit(exitCode);
}

main().catch((err) => {
  log("driver failed:", err);
  process.exit(1);
});
