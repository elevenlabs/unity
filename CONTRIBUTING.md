# Contributing

## Repository layout

The repository root is the UPM package itself — `package.json` is at the top level so consumers can install directly via a git URL with no subfolder path.

```
Runtime/          C# source compiled into the shipped package
  WebGL/          WebGL bridge primitives (BridgePromise, BridgeObserver, BridgeRequestHandler)
  Native/         Native implementation (desktop, mobile, XR) — WebSocket transport, audio

Editor/           Editor-only C# (inspectors, build post-processors, validation)

Tests/
  Editor/         Unity Test Runner tests (Edit Mode — run headlessly in CI)

Plugins/
  WebGL/          .jslib files — Unity's required location for WebGL native plugins.
                  ElevenLabsBridge.jslib is the JS side of the bridge primitives.
                  ⚠️  These files are build artefacts. Do not edit them by hand once the
                  codegen pipeline exists — edit the TypeScript source in Bridge~/src/
                  and run `pnpm run build` to regenerate them.

Bridge~/          JS dev tooling: Prettier, ESLint, Vitest, TypeScript, and the Rolldown-based
                  bundler that produces the .jslib files. The ~ suffix causes Unity to ignore
                  this directory entirely, so nothing here is included in builds or shipped to
                  consumers.
  src/            TypeScript source for the JS side of the bridge. Vitest tests live alongside
                  the sources they cover (e.g. `src/connection/factories.test.ts`). `pnpm run
                  build:primitives` / `build:connection` bundle the sources into the .jslib
                  files under Plugins/WebGL/ — Unity cannot consume TypeScript directly.

Codegen~/         Protocol DTO codegen: reads the vendored AsyncAPI spec at
                  Codegen~/schemas/convai-asyncapi.yml and emits the C# DTO + dispatcher +
                  args records under Runtime/Core/Protocol/*.g.cs. Standalone pnpm project.

Samples/          Self-contained UPM samples that ship with the package.
  BridgeSmokeTest/         JS↔C# primitives smoke MonoBehaviour (exercised by the WebGL
                           primitives integration test).
  ConversationSmokeTest/   End-to-end Conversation smoke MonoBehaviour (exercised by the
                           WebGL conversation integration test).

TestProject/      Embedded dev Unity host project. Used to (a) run the Edit Mode test suite
                  headlessly and (b) produce WebGL builds of the samples for the browser
                  integration tests. Not shipped with the package. Most of Assets/ is
                  gitignored so a fresh clone always opens to a known-good state.

IntegrationTests~/  Vitest + Playwright tests that drive the two WebGL builds produced by
                    TestProject/ from a headless Chromium. Runs entirely outside Unity.

Docs~/            Design documents and RFCs. Read these before making architectural changes;
                  Docs~/plans/plan-b.md is the current architectural reference.
```

## C# tooling

Install the pinned tools with:

```sh
dotnet tool restore
```

This brings in **CSharpier** (formatter). Run from the repo root before committing C# changes:

```sh
dotnet csharpier check .   # fail if any file isn't formatted
dotnet csharpier format .  # auto-format in place
```

The check variant is what CI runs; use it locally before pushing to avoid a CI round-trip.

## JS / TypeScript tooling

Each `~`-suffixed workspace and `TestProject/` carries its own `package.json` and is managed
with **pnpm**. Run commands with `pnpm --dir <workspace> run <script>` from the repo root.

| Workspace | Purpose | Commands |
|---|---|---|
| [`Bridge~/`](Bridge~/) | JS↔C# primitives + connection bundles | `format:check`, `lint`, `typecheck`, `test`, `verify:primitives`, `verify:connection` |
| [`Codegen~/`](Codegen~/) | Protocol DTO codegen from AsyncAPI spec | `format:check`, `lint`, `typecheck`, `generate`, `round-trip`, `verify:protocol-dtos` |
| [`TestProject/`](TestProject/) | Edit Mode test runner driver (`run-tests.ts`) | `typecheck`, `test` |
| [`IntegrationTests~/`](IntegrationTests~/) | Vitest + Playwright browser-mode harness | `typecheck`, `test`, `setup` (one-time Chromium install) |

One-time setup after cloning:

```sh
pnpm --dir Bridge~ install
pnpm --dir Codegen~ install
pnpm --dir TestProject install
pnpm --dir IntegrationTests~ install
pnpm --dir IntegrationTests~ run setup   # download Playwright Chromium
```

The `verify:*` scripts re-run the relevant code generator and assert the committed artefact
matches via `git diff --exit-code` — they catch drift between source and generated output and
are what CI gates on.

## Testing

The codebase has five test layers. Each covers a different slice of the stack and has
different infrastructure requirements. The full verification suite (all five layers + the
formatters / linters / typecheckers above) is what `.claude/CLAUDE.md` documents as the
pre-commit gate.

### 1 — JS unit tests (Vitest, Node.js) — `Bridge~/src/**/*.test.ts`

**What:** the TypeScript sources for the `.jslib` bundles in isolation — bridge primitives
(dispatcher, registries, marshalling, BridgeCallback fan-out, function-pointer pool) plus the
connection layer (factories, audio-glue, end-to-end). Tests are co-located with sources.
**How:** Unity's runtime globals (`mergeInto`, `UTF8ToString`, `SendMessage`, etc.) are
replaced with fakes; functions are called directly in Node and their side-effects asserted.
**What it cannot test:** anything in C#; the real Emscripten runtime; actual `.jslib` bundling.
**Run:** `pnpm --dir Bridge~ run test`

### 2 — Codegen round-trip (Node.js + `dotnet run`) — `Codegen~/round-trip/`

**What:** the generated `Runtime/Core/Protocol/*.g.cs` DTOs round-trip cleanly through
`System.Text.Json` — each outgoing event type serialises to the expected wire shape and each
incoming event type deserialises through the polymorphic converter.
**How:** the generator re-emits the DTOs **and** a sibling `Codegen~/round-trip/Program.cs`
that exercises every type; `dotnet run` compiles and runs it.
**Run:** `pnpm --dir Codegen~ run round-trip`. The companion `pnpm --dir Codegen~ run
verify:protocol-dtos` regenerates and asserts `git diff --exit-code` to catch drift between
the AsyncAPI spec and the committed DTOs.

### 3 — C# Edit Mode tests (Unity Test Runner) — `Tests/Editor/`

**What:** C# classes in isolation — message parsers, ID generators, registries, the
`Conversation` message router, client-tool dispatch, and the bridged-wrapper interfaces
(stubbed via `IJsObject` / `IJsFunction` so the assertions don't need a live JS bridge).
**How:** Unity 6 batchmode compiles the package into the embedded `TestProject/` host project
and runs the NUnit suite headlessly (`-batchmode -nographics -runTests`). No WebGL build, no
browser; `DllImport` calls into the bridge throw in this mode and are exercised via the
interface seams instead.
**Run:** `pnpm --dir TestProject run test` (silent unless Unity fails; pass `-- --verbose` to
stream Unity's log live). Failures are formatted for the VS Code problem matcher in
[`.vscode/tasks.json`](.vscode/tasks.json). Requires Unity **6000.3.6f1** at the path
`/Applications/Unity/Hub/Editor/6000.3.6f1/Unity.app/Contents/MacOS/Unity` (or override via
`$UNITY` / `--unity`).

### 4 — WebGL builds (Unity Test Runner via `HostBuild`) — `TestProject/build-*.sh`

**What:** that the C# + jslib code actually compiles into a working WebGL artifact via IL2CPP.
**How:** [`Editor/HostBuild.cs`](Editor/HostBuild.cs) constructs an empty scene with one of
the smoke MonoBehaviours, runs `BuildPipeline.BuildPlayer`, and exits with the build result.
Two entry points produce two artifacts under separate dirs so each browser test can run
against its own build:

| Script | Sample | Output |
|---|---|---|
| `bash TestProject/build-webgl.sh` | `Samples/BridgeSmokeTest/` (primitives) | `TestProject/Build/WebGL/` |
| `bash TestProject/build-webgl-conversation.sh` | `Samples/ConversationSmokeTest/` | `TestProject/Build/WebGLConversationSmoke/` |

IL2CPP compilation is the bottleneck — expect each build to take several minutes. Both must
exist for the browser tests below to run.

### 5 — Browser integration tests (Vitest + Playwright) — `IntegrationTests~/`

**What:** the full bridge round-trip in real Chromium.

- The **primitives smoke** drives `Samples/BridgeSmokeTest/` and asserts on its `[SmokeTest]`
  console log trail (sync/async method dispatch, callbacks, function-handle round-trip,
  payload edge cases).
- The **conversation smoke** drives `Samples/ConversationSmokeTest/`, asserts on its
  `[ConvSmoke]` log trail, AND additionally observes the actual WebSocket frames via
  Playwright's `page.on('websocket')` to lock the on-wire protocol shape (handshake,
  `user_message` payload, `conversation_initiation_metadata` / `agent_response` / `audio`
  frame sequence).

**How:** [`IntegrationTests~/src/playwright-harness.ts`](IntegrationTests~/src/playwright-harness.ts)
exposes a shared `runWebGLSmoke(opts)` helper — both test files declare pass / fail / skip
matchers and let the helper resolve a typed outcome from the console + page-error streams.
**Run:** `pnpm --dir IntegrationTests~ run test`. Requires both WebGL builds to exist on
disk (a missing build surfaces an explicit error from the harness, not a confusing browser
404).

**Conversation smoke + real agent.** The conversation smoke needs an ElevenLabs agent ID to
hit the live API. Configuration ships as a `ScriptableObject` (`ConversationSmokeConfig`)
loaded from any `Resources/` folder under `Assets/`; the asset is gitignored so credentials
stay per-developer. When the asset is absent the smoke logs `[ConvSmoke] CONFIG MISSING` and
the harness treats it as a green-but-no-op outcome — fresh clones stay green in CI without an
agent secret. Full developer + CI setup notes:
[`Samples/ConversationSmokeTest/README.md`](Samples/ConversationSmokeTest/README.md).

## Running the full verification suite

Every check the pre-commit gate exercises, in the order `.claude/CLAUDE.md` documents:

```sh
dotnet csharpier check .

pnpm --dir Bridge~ run format:check
pnpm --dir Bridge~ run lint
pnpm --dir Bridge~ run typecheck
pnpm --dir Bridge~ run test
pnpm --dir Bridge~ run verify:primitives
pnpm --dir Bridge~ run verify:connection

pnpm --dir Codegen~ run format:check
pnpm --dir Codegen~ run lint
pnpm --dir Codegen~ run typecheck
pnpm --dir Codegen~ run generate
pnpm --dir Codegen~ run round-trip
pnpm --dir Codegen~ run verify:protocol-dtos

pnpm --dir TestProject run typecheck
pnpm --dir TestProject run test
bash TestProject/build-webgl.sh                # only when WebGL surface changed
bash TestProject/build-webgl-conversation.sh   # only when WebGL or Conversation changed

pnpm --dir IntegrationTests~ run typecheck
pnpm --dir IntegrationTests~ run test
```

Each command is independent — run them one-by-one rather than chained with `&&`, both to
keep the local permissions allowlist working cleanly and to make the first failure
immediately visible.

## Platform conventions

- WebGL-specific C# lives under `Runtime/WebGL/` in an assembly definition scoped to `WebGL` + `Editor` platforms.
- Native C# lives under `Runtime/Native/` in an assembly definition with no platform restriction (excluded from WebGL via `excludePlatforms`).
- The public API (`Runtime/`) has no platform restriction and no platform-specific code — it selects the right implementation at compile time via `#if UNITY_WEBGL`.
- `.jslib` files must be placed under `Plugins/WebGL/` for Unity's build pipeline to pick them up.
