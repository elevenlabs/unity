# Contributing

## Repository layout

The repository root is the UPM package itself — `package.json` is at the top level so consumers can install directly via a git URL with no subfolder path.

```
Runtime/          C# source compiled into the shipped package
  Core/           Cross-platform asmdef `ElevenLabs.Agents.Core`. Owns the public API
                  (Conversation, ConversationOptions, ClientToolException, …), the
                  internal IConnection / IInputController / IOutputController abstractions
                  the platform impls satisfy, and the generated protocol DTOs under
                  Protocol/*.g.cs. No platform restrictions.
  WebGL/          Bridge primitives (JsObject, JsFunction, BridgeCallback handle types + their
                  IJsObject / IJsFunction interface seams) and the Bridged/ folder of
                  IConnection / IInputController / IOutputController implementations that
                  wrap @elevenlabs/client. Compiled by the Editor + WebGL asmdef
                  `ElevenLabs.Agents.WebGL`, which sits at `Runtime/` itself — so a new file
                  dropped directly into `Runtime/` lands in that assembly.
  Native/         excludePlatforms=WebGL asmdef `ElevenLabs.Agents.Native`: ClientWebSocket
                  connection, UnityEngine.Microphone input, IAudioGenerator-based output
                  (UnityAudioSourceOutput + the IAudioOutputEngine implementations), and
                  NativeSessionLauncher.

Editor/           Editor-only C#: HostBuild entry points for the WebGL and standalone smoke
                  builds, a build preprocessor that validates WebGL Player Settings, and the
                  link.xml injector.

Tests/
  Editor/         Edit Mode tests (run headlessly in CI). Native/ is its own asmdef.
  Runtime/Native/ PlayMode characterization tests for Unity's audio APIs (run manually).

Plugins/
  WebGL/          .jslib bundles — Unity's required location for WebGL native plugins.
                  ElevenLabsBridge.jslib is the bridge primitives layer (bundled from
                  Bridge~/src/primitives/); ElevenLabsConnection.jslib is the
                  @elevenlabs/client factory + audio-glue registrations (bundled from
                  Bridge~/src/connection/).
                  ⚠️  Both files are build artefacts. Do not edit by hand — edit the
                  TypeScript source in Bridge~/src/ and run `pnpm --dir Bridge~ run
                  build:primitives` / `build:connection` to regenerate. The
                  `verify:primitives` / `verify:connection` scripts catch drift in CI.

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

Samples~/         User-facing UPM samples that ship with the package, hidden from the
                  AssetDatabase (Unity convention — the tilde prevents the consuming project
                  from seeing them twice once Package Manager copies them into Assets/Samples/).
  GettingStarted/          Walk-up-and-talk demo — the README's headline sample.
  QuickStart/              Minimal one-MonoBehaviour smoke sample.

Samples/          In-repo smoke harnesses. NOT in package.json#files — Unity compiles them
                  so HostBuild can load the MonoBehaviour types via Type.GetType, but they
                  never reach UPM consumers.
  BridgeSmokeTest/         JS↔C# primitives smoke MonoBehaviour (exercised by the WebGL
                           primitives integration test).
  ConversationSmokeTest/   End-to-end Conversation smoke MonoBehaviour (exercised by the
                           WebGL conversation integration test).
  StandaloneSmokeTest/     Native end-to-end smoke MonoBehaviour for the standalone player.

TestProject/      Embedded dev Unity host project. Used to (a) run the Edit Mode test suite
                  headlessly and (b) produce WebGL builds of the samples for the browser
                  integration tests. Not shipped with the package. Most of Assets/ is
                  gitignored so a fresh clone always opens to a known-good state.

IntegrationTests~/  Vitest + Playwright tests that drive the WebGL builds produced by
                    TestProject/ from a headless Chromium. Runs entirely outside Unity.

Docs~/            Design documents and RFCs. Read these before making architectural changes;
                  Docs~/ARCHITECTURE.md is the current architectural reference.
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
| [`Codegen~/`](Codegen~/) | Protocol DTO codegen from AsyncAPI spec | `format:check`, `lint`, `typecheck`, `test`, `generate`, `round-trip`, `verify:protocol-dtos` |
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
Newtonsoft.Json — each outgoing event type serialises to the expected wire shape and each
incoming event type deserialises through the polymorphic converter.
**How:** the generator re-emits the DTOs **and** a sibling `Codegen~/round-trip/Program.cs`
that exercises every type; `dotnet run` compiles and runs it.
**Run:** `pnpm --dir Codegen~ run round-trip`. The companion `pnpm --dir Codegen~ run
verify:protocol-dtos` regenerates and asserts `git diff --exit-code` to catch drift between
the AsyncAPI spec and the committed DTOs; `pnpm --dir Codegen~ run test` unit-tests the
generator's C# preset.

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

### 4 — Player builds (`HostBuild` via `-executeMethod`) — `TestProject/build-*.sh`

**What:** that the C# + jslib code actually compiles into a working IL2CPP player.
**How:** [`Editor/HostBuild.cs`](Editor/HostBuild.cs) constructs an empty scene with one of
the smoke MonoBehaviours, runs `BuildPipeline.BuildPlayer`, and exits with the build result.
Each entry point writes to its own directory so each browser test can run against its own
build:

| Script | Sample | Output |
|---|---|---|
| `bash TestProject/build-webgl.sh` | `Samples/BridgeSmokeTest/` (primitives) | `TestProject/Build/WebGL/` |
| `bash TestProject/build-webgl-conversation.sh` | `Samples/ConversationSmokeTest/` | `TestProject/Build/WebGLConversationSmoke/` |
| `bash TestProject/build-webgl-conversation-spatial.sh` | `Samples/ConversationSmokeTest/` (spatial) | `TestProject/Build/WebGLConversationSpatialSmoke/` |
| `bash TestProject/build-standalone.sh` | `Samples/StandaloneSmokeTest/` | `TestProject/Build/Standalone/` |

IL2CPP compilation is the bottleneck — expect each build to take several minutes. The three
WebGL builds must exist for the browser tests below to run. The standalone smoke is run by
hand; see [`Samples/StandaloneSmokeTest/README.md`](Samples/StandaloneSmokeTest/README.md).

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
- The **spatial smoke** drives the spatial conversation build and asserts the Web Audio sink
  mirrors the supplied `AudioSource`.
- The **conversation shape** test statically inspects the conversation build's emitted
  framework JS.

**How:** [`IntegrationTests~/src/playwright-harness.ts`](IntegrationTests~/src/playwright-harness.ts)
exposes a shared `runWebGLSmoke(opts)` helper — both test files declare pass / fail / skip
matchers and let the helper resolve a typed outcome from the console + page-error streams.
**Run:** `pnpm --dir IntegrationTests~ run test`. Requires the WebGL builds to exist on
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
pnpm --dir Codegen~ run test
pnpm --dir Codegen~ run round-trip
pnpm --dir Codegen~ run verify:protocol-dtos

pnpm --dir TestProject run typecheck
pnpm --dir TestProject run test
bash TestProject/build-webgl.sh                # only when WebGL surface changed
bash TestProject/build-webgl-conversation.sh   # only when WebGL or Conversation changed
bash TestProject/build-webgl-conversation-spatial.sh   # only when WebGL audio output changed

pnpm --dir IntegrationTests~ run typecheck
pnpm --dir IntegrationTests~ run test
```

Each command is independent — run them one-by-one rather than chained with `&&`, both to
keep the local permissions allowlist working cleanly and to make the first failure
immediately visible.

## Platform conventions

- The public API lives in the cross-platform `ElevenLabs.Agents.Core` asmdef under
  `Runtime/Core/` and has no platform-specific code. `Conversation` orchestrates the three
  internal abstractions (`IConnection` / `IInputController` / `IOutputController`); platform
  asmdefs ship the implementations.
- WebGL-specific C# lives under `Runtime/WebGL/` in `ElevenLabs.Agents.WebGL`, scoped to
  `WebGL` + `Editor` platforms. Native C# lives under `Runtime/Native/` in
  `ElevenLabs.Agents.Native` (`excludePlatforms=WebGL`).
- **Core never references a platform asmdef.** `Conversation.StartSessionAsync` calls an
  `internal static Func<ConversationOptions, Awaitable<Conversation>>? SessionFactory` that
  each platform's launcher assigns on load:
  [`BridgedSessionLauncher.cs`](Runtime/WebGL/Bridged/BridgedSessionLauncher.cs) and
  [`NativeSessionLauncher.cs`](Runtime/Native/NativeSessionLauncher.cs). Both asmdefs compile
  in the Editor, so each launcher's registration is wrapped in `#if UNITY_WEBGL` /
  `#if !UNITY_WEBGL` to make exactly one of them assign the factory for the active build
  target.
- `.jslib` files must be placed under `Plugins/WebGL/` for Unity's build pipeline to pick
  them up.

## Submitting changes

- For anything beyond a small fix, open an [issue](https://github.com/elevenlabs/unity/issues/new/choose) first so the approach can be agreed before you invest in it.
- Branch from `main`, run the verification layers your change touches (see [Running the full verification suite](#running-the-full-verification-suite)), and fill in the pull request template.
- Add an entry under `[Unreleased]` in [`CHANGELOG.md`](CHANGELOG.md) for user-facing changes, and call out any public API change in the PR description.
- CI runs the lint lane (formatters, linters, typechecks, JS unit tests, generated-artefact drift) on every PR. The Unity lanes (Edit Mode tests, WebGL builds, browser integration tests) need Unity license secrets that GitHub doesn't expose to PRs from forks, so they fail there; a maintainer runs them before merging.
- Report security vulnerabilities privately per [`SECURITY.md`](.github/SECURITY.md), not in issues or PRs.

By contributing you agree that your contributions are licensed under the repository's [MIT license](LICENSE).
