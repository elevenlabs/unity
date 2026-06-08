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

Bridge~/          JS dev tooling: Prettier, ESLint, Vitest, TypeScript, and codegen scripts.
                  The ~ suffix causes Unity to ignore this directory entirely, so nothing
                  here is included in builds or shipped to consumers.
  src/            TypeScript source for the JS side of the bridge. Compiled and transformed
                  by `pnpm run build` into the .jslib files under Plugins/WebGL/. The build
                  step is required because Unity cannot consume TypeScript directly.
                  (build script not yet wired — see bridge-primitives plan, codegen phase)
  tests/          Vitest tests exercising the .jslib files in isolation — JS only, no Unity,
                  no C#. Unity globals (mergeInto, LibraryManager, UTF8ToString, SendMessage)
                  are replaced with lightweight fakes so the JS functions can be called and
                  observed directly in Node. These tests catch regressions in the JS half of
                  the bridge contract; they cannot reach any C# code.

plans/            Design documents and RFCs. Read these before making architectural changes.
```

## C# tooling

Install the pinned tools with:

```sh
dotnet tool restore
```

This brings in **CSharpier** (formatter) and **dotnet format** (analyzer fixes). Run before committing C# changes:

```sh
dotnet csharpier .
dotnet format
```

## JS tooling

All JS tooling lives in `Bridge~/` and is managed with **pnpm**:

```sh
cd Bridge~
pnpm install          # install deps
pnpm run format:check # Prettier — check formatting
pnpm run format       # Prettier — auto-fix
pnpm run lint         # ESLint
pnpm run typecheck    # TypeScript — type-check without emitting
pnpm run test         # Vitest
```

## Testing

The bridge has three test layers. Each layer covers a different part of the stack
and has different infrastructure requirements.

### 1 — JS unit tests (Vitest, Node.js) — `Bridge~/tests/`

**What:** the `.jslib` JavaScript functions in isolation.
**How:** Unity's runtime globals (`mergeInto`, `UTF8ToString`, `SendMessage`, etc.) are
replaced with fakes; the jslib source is evaluated with `new Function()` so its functions
can be called directly and their side-effects (console output, global state) asserted on.
**What it cannot test:** anything in C#.
**Run:** `cd Bridge~ && pnpm run test`

### 2 — C# unit tests (Unity Test Runner, Edit Mode) — `Tests/Editor/`

**What:** C# classes in isolation: message parsers, ID generators, registries.
**How:** Unity's Edit Mode test runner compiles the package into a host project and runs
NUnit tests headlessly (`-batchmode -nographics -runTests`). No WebGL build, no browser.
**What it cannot test:** actual JS↔C# communication — `DllImport` calls are stubbed out
or replaced with throwing stubs outside a WebGL build.
**Run:** requires a host Unity project (pending — see bridge-primitives plan, Phase 1).

### 3 — Integration tests (Vitest browser mode + Playwright) — planned Phase 6

**What:** the full JS↔C# bridge round-trip in a real browser.
**How:** a minimal Unity scene is compiled to WebGL; Playwright loads it in Chrome/Firefox/
Safari; the Vitest test drives the scene from the JS side and asserts that the right
messages travel in both directions.
**Who drives:** Vitest/Playwright, not Unity. The test is authored in TypeScript and runs
against the bridge protocol boundary — what the JS side sends and what it receives back.
This is a better fit than Unity Play Mode WebGL tests, which are harder to run headlessly
and harder to target across multiple browsers from CI.
**Requires:** host Unity project + WebGL build pipeline (pending Phase 5/6).

## Platform conventions

- WebGL-specific C# lives under `Runtime/WebGL/` in an assembly definition scoped to `WebGL` + `Editor` platforms.
- Native C# lives under `Runtime/Native/` in an assembly definition with no platform restriction (excluded from WebGL via `excludePlatforms`).
- The public API (`Runtime/`) has no platform restriction and no platform-specific code — it selects the right implementation at compile time via `#if UNITY_WEBGL`.
- `.jslib` files must be placed under `Plugins/WebGL/` for Unity's build pipeline to pick them up.
