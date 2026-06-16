// Headless Unity 6 batchmode run of the Edit Mode test suite. Replaces the
// previous run-tests.sh so the runner can also format failures for VS Code's
// problem matcher and stay portable to CI workflows (no bash required).
//
// Usage:
//   node TestProject/run-tests.ts [--xml <path>] [--unity <path>] [--verbose]
//
// Default: Unity runs silently; if it exits non-zero, its buffered stdout/stderr
// is dumped so the failure is visible. Per-failure summary lines (parsed from
// the NUnit XML) are always printed last in the format
//   <file>:<line>: error <testname>: <message>
// which the .vscode/tasks.json problem matcher consumes to surface failures
// in the VS Code Problems panel.
//
// Pass --verbose (or -v) to stream Unity's batchmode log through stdout/stderr
// live — useful when debugging slow or hung runs.
//
// CI: pass --xml <stable/path/to/results.xml> so the workflow can upload the
// artefact for downstream test-reporter actions.
//
// Gotcha: do NOT add -quit to the Unity args. Unity honors -quit before the
// test runner fires and exits silently with no results (see HP.4 in
// Docs~/plans/generic-bridge-primitives.md).

import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseArgs } from "node:util";

const here = dirname(fileURLToPath(import.meta.url));

const { values } = parseArgs({
  options: {
    xml: { type: "string" },
    unity: { type: "string" },
    verbose: { type: "boolean", short: "v", default: false },
    help: { type: "boolean", short: "h", default: false },
  },
  strict: true,
});

if (values.help) {
  process.stdout.write(`Usage: node TestProject/run-tests.ts [options]

Options:
  --xml <path>     Path to write the NUnit XML results
                   (default: TestProject/test-results.xml)
  --unity <path>   Path to the Unity binary
                   (default: $UNITY or the macOS Hub install of 6000.3.6f1)
  -v, --verbose    Stream Unity's batchmode log to stdout/stderr live.
                   Without this, the log is buffered and only printed
                   if Unity exits non-zero. The per-failure summary is
                   always printed at the end.
  -h, --help       Show this message
`);
  process.exit(0);
}

const xmlPath = resolve(values.xml ?? resolve(here, "test-results.xml"));
const unity =
  values.unity ??
  process.env.UNITY ??
  "/Applications/Unity/Hub/Editor/6000.3.6f1/Unity.app/Contents/MacOS/Unity";

if (!existsSync(unity)) {
  console.error(`Unity binary not found: ${unity}`);
  process.exit(2);
}

const unityArgs = [
  "-batchmode",
  "-nographics",
  "-projectPath",
  here,
  "-runTests",
  "-testPlatform",
  "editmode",
  "-testResults",
  xmlPath,
  "-logFile",
  "-",
];

const verbose = values.verbose;
console.log(`> ${unity} ${unityArgs.join(" ")}${verbose ? "" : "  (silent — pass --verbose to stream)"}`);

const child = spawn(unity, unityArgs, {
  stdio: verbose ? "inherit" : ["ignore", "pipe", "pipe"],
});

const buffer: string[] = [];
if (!verbose) {
  child.stdout?.on("data", (chunk: Buffer) => buffer.push(chunk.toString()));
  child.stderr?.on("data", (chunk: Buffer) => buffer.push(chunk.toString()));
}

child.on("error", (err) => {
  console.error(`Failed to spawn Unity: ${err.message}`);
  process.exit(2);
});

child.on("exit", (code) => {
  const exitCode = code ?? 1;
  if (!verbose && exitCode !== 0) {
    process.stdout.write(buffer.join(""));
  }
  reportFailures(xmlPath);
  process.exit(exitCode);
});

function reportFailures(xmlPath: string): void {
  if (!existsSync(xmlPath)) {
    console.error(
      `\nNo test-results.xml at ${xmlPath} — Unity may have crashed before writing it.`,
    );
    return;
  }
  const xml = readFileSync(xmlPath, "utf8");

  const caseRe =
    /<test-case\b([^>]*?)\sresult="Failed"([^>]*?)>([\s\S]*?)<\/test-case>/g;
  const attrRe = /(\w+)="([^"]*)"/g;
  const messageRe = /<message><!\[CDATA\[([\s\S]*?)\]\]><\/message>/;
  const stackRe = /<stack-trace><!\[CDATA\[([\s\S]*?)\]\]><\/stack-trace>/;
  const frameRe = /\bat\s+.+?\s+in\s+(.+?\.cs):(\d+)/;
  const runRe =
    /<test-run\b[^>]*\btotal="(\d+)"[^>]*\bpassed="(\d+)"[^>]*\bfailed="(\d+)"/;

  const failures = Array.from(xml.matchAll(caseRe))
    .map((match) => {
      const attrs = Object.fromEntries(
        Array.from((match[1] + match[2]).matchAll(attrRe)).map((a) => [
          a[1],
          a[2],
        ]),
      );
      const body = match[3];
      const fullname = attrs.fullname ?? attrs.name ?? "<unknown>";
      const message =
        (body.match(messageRe)?.[1] ?? "").trim().split("\n")[0] ?? "";
      const frame = (body.match(stackRe)?.[1] ?? "").match(frameRe);
      return frame
        ? { file: frame[1], line: frame[2], fullname, message }
        : null;
    })
    .filter((f): f is NonNullable<typeof f> => f !== null);

  const runMatch = xml.match(runRe);
  console.log("");
  if (runMatch) {
    const [, total, passed, failed] = runMatch;
    console.log(`Tests: ${passed}/${total} passed, ${failed} failed`);
  }
  for (const { file, line, fullname, message } of failures) {
    console.log(
      `${file}:${line}: error ${fullname}: ${message || "(no message)"}`,
    );
  }
}
