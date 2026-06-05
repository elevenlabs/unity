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

Bridge~/          JS dev tooling: Prettier, ESLint, Vitest, and future codegen scripts.
                  The ~ suffix causes Unity to ignore this directory entirely, so nothing
                  here is included in builds or shipped to consumers. Codegen here targets
                  both sides of the bridge: .jslib additions in Plugins/WebGL/ and C# façade
                  classes in Runtime/WebGL/.

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
pnpm install
pnpm run lint
pnpm run test
```

## Platform conventions

- WebGL-specific C# lives under `Runtime/WebGL/` in an assembly definition scoped to `WebGL` + `Editor` platforms.
- Native C# lives under `Runtime/Native/` in an assembly definition with no platform restriction (excluded from WebGL via `excludePlatforms`).
- The public API (`Runtime/`) has no platform restriction and no platform-specific code — it selects the right implementation at compile time via `#if UNITY_WEBGL`.
- `.jslib` files must be placed under `Plugins/WebGL/` for Unity's build pipeline to pick them up.
