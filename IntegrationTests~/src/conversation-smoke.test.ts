import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "vitest";
import type { WebSocket } from "playwright";
import { runWebGLSmoke } from "./playwright-harness.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

// The conversation smoke build is a sibling of the primitives build; see
// Editor/HostBuild.cs#BuildConversation and TestProject/build-webgl-conversation.sh.
const BUILD_DIR = path.resolve(
  __dirname,
  "..",
  "..",
  "TestProject",
  "Build",
  "WebGLConversationSmoke",
);

// The smoke MonoBehaviour sends this exact text via SendUserMessage; the wire
// frame assertion below checks that the on-wire `user_message` payload's
// `text` field matches verbatim.
const USER_MESSAGE = "Hello, please reply with the word READY and stop.";

// Liberal timeout: connect + agent thinking + multiple audio chunks +
// EndSession round-trip routinely takes well past the default 15s. CI runners
// can override this if needed.
const TEST_TIMEOUT_MS = 120_000;

type Frame = { json: unknown; raw: string };

test(
  "conversation smoke test passes in Chromium",
  async () => {
    const sentFrames: Frame[] = [];
    const receivedFrames: Frame[] = [];
    let observedWsUrl: string | null = null;

    const outcome = await runWebGLSmoke({
      buildDir: BUILD_DIR,
      matcher: {
        pass: ["[ConvSmoke] All conversation smoke tests passed!"],
        fail: ["ASSERTION FAILED"],
        skip: ["[ConvSmoke] CONFIG MISSING"],
        streamPrefix: "[ConvSmoke]",
      },
      onBeforeNavigate: (page) => {
        // Capture every WebSocket the page opens. The SDK opens one to
        // wss://api.elevenlabs.io/... per session; we record frames in both
        // directions so the post-success assertion can verify on-wire shape.
        page.on("websocket", (ws: WebSocket) => {
          observedWsUrl = ws.url();
          console.log(`  [ws] opened: ${ws.url()}`);
          ws.on("framesent", (event) => {
            const raw = event.payload.toString();
            sentFrames.push({ json: tryParseJson(raw), raw });
          });
          ws.on("framereceived", (event) => {
            const raw = event.payload.toString();
            receivedFrames.push({ json: tryParseJson(raw), raw });
          });
        });
      },
    });

    if (outcome.status === "skip") {
      console.log(`  [skip] ${outcome.message}`);
      // Returning early reports as passing — Vitest has no in-test skip API
      // for already-running tests, but the CONFIG MISSING path is the
      // expected behaviour on a clean clone without an agent secret, so
      // treat it as a green-but-no-op outcome rather than a failure.
      return;
    }

    expect(outcome.status, `Conversation smoke failed: ${outcome.message}`).toBe(
      "pass",
    );

    // ----- Wire-format assertions ---------------------------------------

    expect(observedWsUrl, "no WebSocket opened during session").not.toBeNull();
    expect(observedWsUrl).toMatch(/elevenlabs\.io|elevenlabs\./);

    const sentTypes = sentFrames
      .map((f) => extractType(f.json))
      .filter((t): t is string => t !== null);
    const receivedTypes = receivedFrames
      .map((f) => extractType(f.json))
      .filter((t): t is string => t !== null);
    console.log(`  [ws] sent types: ${JSON.stringify(sentTypes)}`);
    console.log(`  [ws] received types: ${JSON.stringify(receivedTypes)}`);

    // Outbound: the SDK's initiation handshake comes first.
    expect(
      sentTypes[0],
      `first outbound frame should be conversation_initiation_client_data, was '${sentTypes[0]}'`,
    ).toBe("conversation_initiation_client_data");

    // Outbound: the initiation frame carries the dynamic variables hard-coded
    // in ConversationSmokeTest.cs verbatim. The SDK lowercases the camelCase
    // C# field to snake_case (`dynamic_variables`) and forwards the values
    // unchanged — string, number, and bool must survive round-tripping
    // through C# → JObject → JS → wire JSON without coercion.
    const initFrame = sentFrames[0].json;
    expect(
      extractField(initFrame, "dynamic_variables"),
      "first initiation frame missing dynamic_variables",
    ).toEqual({ color: "blue", count: 42, isReady: true });

    // Outbound: at some point we send the user message; payload `.text` must
    // match exactly what the smoke MonoBehaviour passed to SendUserMessage.
    const userMessageFrame = sentFrames.find((f) => extractType(f.json) === "user_message");
    expect(userMessageFrame, "no user_message frame sent").toBeDefined();
    expect(extractField(userMessageFrame!.json, "text")).toBe(USER_MESSAGE);

    // Inbound: initiation metadata arrives before the first agent response.
    const initIdx = receivedTypes.indexOf("conversation_initiation_metadata");
    const agentIdx = receivedTypes.indexOf("agent_response");
    expect(initIdx, "no conversation_initiation_metadata frame received").toBeGreaterThanOrEqual(0);
    expect(agentIdx, "no agent_response frame received").toBeGreaterThanOrEqual(0);
    expect(
      initIdx,
      "conversation_initiation_metadata must arrive before agent_response",
    ).toBeLessThan(agentIdx);

    // Inbound: at least one audio chunk verifies the default-mode pipeline
    // carried real audio over the wire, not just text events.
    expect(
      receivedTypes.filter((t) => t === "audio").length,
      "expected at least one audio frame from the agent",
    ).toBeGreaterThan(0);
  },
  TEST_TIMEOUT_MS,
);

function tryParseJson(raw: string): unknown {
  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

function extractType(json: unknown): string | null {
  if (json && typeof json === "object" && "type" in json) {
    const t = (json as { type: unknown }).type;
    return typeof t === "string" ? t : null;
  }
  return null;
}

function extractField(json: unknown, field: string): unknown {
  if (json && typeof json === "object") {
    // The SDK wraps outgoing user_message as { type, text } at the top level,
    // not nested under a payload — see @elevenlabs/client BaseConnection.
    if (field in json) {
      return (json as Record<string, unknown>)[field];
    }
  }
  return undefined;
}
