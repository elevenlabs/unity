import path from "node:path";
import { fileURLToPath } from "node:url";
import { existsSync } from "node:fs";
import { afterAll, beforeAll, test, expect } from "vitest";
import { chromium, type Browser, type Page } from "playwright";
import { startWebGLServer, type WebGLServer } from "./serve-webgl.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// The committed WebGL build lives two directories above this file:
//   IntegrationTests~/src/  →  ../../  →  repo root  →  TestProject/Build/WebGL/
const BUILD_DIR = path.resolve(
  __dirname,
  "..",
  "..",
  "TestProject",
  "Build",
  "WebGL",
);

let server: WebGLServer;
let browser: Browser;

beforeAll(async () => {
  if (!existsSync(BUILD_DIR)) {
    throw new Error(
      `WebGL build not found at ${BUILD_DIR}.\n` +
        `Run 'bash TestProject/build-webgl.sh' to produce it first.`,
    );
  }

  server = await startWebGLServer(BUILD_DIR);
  browser = await chromium.launch({
    args: ["--disable-dev-shm-usage", "--no-sandbox"],
  });
});

afterAll(async () => {
  await browser?.close();
  server?.close();
});

test("bridge primitive smoke test passes in Chromium", async () => {
  const page: Page = await browser.newPage();

  try {
    // Resolve outcome from the page's [SmokeTest] log stream, page errors, or crashes.
    // Listeners must be registered before navigation so no messages are missed.
    let resolveOutcome!: (r: { passed: boolean; message: string }) => void;
    const outcome = new Promise<{ passed: boolean; message: string }>(
      (resolve) => {
        resolveOutcome = resolve;
      },
    );

    // Stream every [SmokeTest] message in real-time so a hang or partial run is
    // immediately visible without having to re-run with verbose mode.
    page.on("console", (msg) => {
      const text = msg.text();
      if (text.includes("[SmokeTest]")) {
        console.log("  ", text);
      }
      if (text.includes("[SmokeTest] All smoke tests passed!")) {
        resolveOutcome({ passed: true, message: text });
      } else if (text.includes("ASSERTION FAILED")) {
        resolveOutcome({ passed: false, message: text });
      }
    });

    page.on("pageerror", (err) => {
      console.log("  [pageerror]", err.message.slice(0, 250));
      resolveOutcome({
        passed: false,
        message: `Uncaught page error: ${err.message}`,
      });
    });

    page.on("crash", () => {
      console.log("  [crash] page crashed");
      resolveOutcome({ passed: false, message: "Page crashed" });
    });

    await page.goto(server.url);
    const result = await outcome;
    expect(result.passed, `Smoke test failed: ${result.message}`).toBe(true);
  } finally {
    await page.close();
  }
});
