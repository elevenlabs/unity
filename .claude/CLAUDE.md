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
cd Bridge~ && pnpm run format:check   # Prettier
cd Bridge~ && pnpm run lint           # ESLint
cd Bridge~ && pnpm run typecheck      # TypeScript (tsc --noEmit per project)
cd Bridge~ && pnpm run test           # Vitest
```

## What cannot be verified without Unity

- **C# compilation** — no `.csproj`/`.sln` at the root; Unity generates its own project files when it opens the package. Compilation errors only surface inside Unity (locally or in CI).
- **Unity Test Runner** — Edit Mode tests require a full Unity installation (`-batchmode -nographics -runTests`). Setting up a host project to enable local builds and test runs (and eventually CI) is a Phase 1 task still pending.

## Project plans

Implementation plans live in `.claude/plans/`. Look there for context on agreed approaches, and place any new plans there too.
