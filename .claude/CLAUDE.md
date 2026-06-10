# ElevenLabs Unity SDK — Claude Context

## Verification commands

Run these to check the codebase is in good shape before committing.

### C# formatting (CSharpier)

```bash
dotnet csharpier check .
```

To auto-format:

```bash
dotnet csharpier format .
```

> If `csharpier` is not found, run `dotnet tool restore` once to install it from `.config/dotnet-tools.json`.

### JS tooling (Bridge~/)

```bash
pnpm --dir Bridge~ run format:check       # Prettier
pnpm --dir Bridge~ run lint               # ESLint
pnpm --dir Bridge~ run typecheck          # TypeScript (tsc --noEmit per project)
pnpm --dir Bridge~ run test               # Vitest
pnpm --dir Bridge~ run verify:primitives  # Rebundle jslib and assert no drift vs committed copy
```

The primitives `.jslib` under `Plugins/WebGL/` is generated from `Bridge~/src/primitives/`
by Rolldown. Regenerate via `pnpm --dir Bridge~ run build:primitives` after editing any
source under `Bridge~/src/primitives/`. The `verify:primitives` script rebuilds and uses
`git diff --exit-code` to fail if the committed file is stale — wire this into CI once
the workflow is in place.

## What cannot be verified without Unity

- **C# compilation** — no `.csproj`/`.sln` at the root; Unity generates its own project files when it opens the package. Compilation errors only surface inside Unity (locally or in CI).
- **Unity Test Runner** — Edit Mode tests require a full Unity installation (`-batchmode -nographics -runTests`). Setting up a host project to enable local builds and test runs (and eventually CI) is a Phase 1 task still pending.

## Language conventions

Write all JS-side code in TypeScript — sources, tests, and build/tooling scripts alike.
Node 23+ runs `.ts` files directly via type-stripping, so build scripts under
`Bridge~/build/` are authored as `.ts` and invoked with bare `node script.ts` (no
`tsx`, no transpile step). Only fall back to `.js` / `.mjs` for files a tool requires
in a non-TS form (e.g. `eslint.config.js`).

## Project plans

Implementation plans live in `Docs~/plans/` (alongside `Docs~/ARCHITECTURE.md`). Look there for context on agreed approaches, and place any new plans there too.
