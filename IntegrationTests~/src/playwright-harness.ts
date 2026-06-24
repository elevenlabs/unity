import { existsSync } from "node:fs";
import { chromium, type Page } from "playwright";
import { startWebGLServer } from "./serve-webgl.js";

export type SmokeStatus = "pass" | "fail" | "skip";

export interface SmokeOutcome {
  status: SmokeStatus;
  message: string;
}

export interface SmokeMatcher {
  /** Substrings that mark a passing run when seen in console output. */
  pass: string[];
  /** Substrings that mark a failing run when seen in console output. */
  fail: string[];
  /** Substrings that mark a skipped run when seen in console output. */
  skip?: string[];
  /**
   * Console-prefix substring to stream-log (mirrored to the test console)
   * for live diagnostics. Match against `msg.text()` exactly as it arrives
   * from the browser; matches anywhere in the message.
   */
  streamPrefix?: string;
}

export interface RunWebGLSmokeOptions {
  /** Absolute path to the Unity WebGL build directory (containing index.html). */
  buildDir: string;
  matcher: SmokeMatcher;
  /**
   * Hook invoked after the page is created but BEFORE navigation. Use this
   * to register `page.on('websocket')` listeners (registered after
   * `page.goto(...)` they miss the SDK's handshake frames entirely) or to
   * install `page.addInitScript(...)` instrumentation. May return a promise
   * — the harness awaits it before navigating.
   */
  onBeforeNavigate?: (page: Page) => void | Promise<void>;
  /**
   * Hook invoked after the smoke outcome resolves but BEFORE the page +
   * browser are torn down. Use this to `page.evaluate(...)` and assert on
   * page-context state captured during the run — once the harness's
   * `finally` runs, the page is closed and `page.evaluate` rejects. The
   * hook's promise is awaited; throwing here surfaces as a test failure
   * (no separate fail-state from the smoke's pass/fail/skip).
   */
  onResolved?: (page: Page, outcome: SmokeOutcome) => void | Promise<void>;
}

/**
 * Spin up a one-shot Playwright/Chromium session against a Unity WebGL build,
 * resolve a pass/fail/skip outcome from its console + page-error + crash
 * streams, then tear everything down.
 *
 * The harness deliberately registers console listeners BEFORE navigation so
 * no early messages from `Start()` are missed. The same goes for the
 * `onBeforeNavigate` hook — anything that needs to observe network or
 * runtime events from the very first frame must wire up there.
 *
 * Throws if `buildDir` does not exist on disk: that's almost certainly a
 * missing `bash TestProject/build-webgl*.sh` invocation, not a smoke-test
 * failure, and an explicit error beats a confusing 404 inside the browser.
 */
export async function runWebGLSmoke(
  opts: RunWebGLSmokeOptions,
): Promise<SmokeOutcome> {
  if (!existsSync(opts.buildDir)) {
    throw new Error(
      `WebGL build not found at ${opts.buildDir}.\n` +
        `Run the corresponding TestProject/build-*.sh script to produce it first.`,
    );
  }

  const server = await startWebGLServer(opts.buildDir);
  const browser = await chromium.launch({
    args: ["--disable-dev-shm-usage", "--no-sandbox"],
  });
  const page = await browser.newPage();

  try {
    let resolveOutcome!: (o: SmokeOutcome) => void;
    const outcome = new Promise<SmokeOutcome>((resolve) => {
      resolveOutcome = resolve;
    });

    page.on("console", (msg) => {
      const text = msg.text();
      if (opts.matcher.streamPrefix && text.includes(opts.matcher.streamPrefix)) {
        console.log("  ", text);
      }
      if (opts.matcher.pass.some((s) => text.includes(s))) {
        resolveOutcome({ status: "pass", message: text });
      } else if (opts.matcher.fail.some((s) => text.includes(s))) {
        resolveOutcome({ status: "fail", message: text });
      } else if (opts.matcher.skip?.some((s) => text.includes(s))) {
        resolveOutcome({ status: "skip", message: text });
      }
    });

    page.on("pageerror", (err) => {
      console.log("  [pageerror]", err.message.slice(0, 250));
      resolveOutcome({
        status: "fail",
        message: `Uncaught page error: ${err.message}`,
      });
    });

    page.on("crash", () => {
      console.log("  [crash] page crashed");
      resolveOutcome({ status: "fail", message: "Page crashed" });
    });

    await opts.onBeforeNavigate?.(page);

    await page.goto(server.url);
    const resolved = await outcome;
    if (opts.onResolved) {
      await opts.onResolved(page, resolved);
    }
    return resolved;
  } finally {
    await page.close();
    await browser.close();
    server.close();
  }
}
