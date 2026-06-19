import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "vitest";
import { runWebGLSmoke } from "./playwright-harness.js";

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

test("bridge primitive smoke test passes in Chromium", async () => {
  const outcome = await runWebGLSmoke({
    buildDir: BUILD_DIR,
    matcher: {
      pass: ["[SmokeTest] All smoke tests passed!"],
      fail: ["ASSERTION FAILED"],
      streamPrefix: "[SmokeTest]",
    },
  });
  expect(outcome.status, `Smoke test failed: ${outcome.message}`).toBe("pass");
});
