# Codegen~

TypeScript-based generator that reads vendored protocol schemas in
[schemas/](schemas/) and emits C# DTOs to `Runtime/Core/Protocol/`.

Runs in Node 23+ via direct TypeScript execution — no build step.

## Commands

After `pnpm install` in this directory:

- `pnpm --dir Codegen~ run generate` — regenerate C# DTOs
- `pnpm --dir Codegen~ run typecheck`
- `pnpm --dir Codegen~ run lint`
- `pnpm --dir Codegen~ run format` / `format:check`

The `~` suffix excludes this directory from Unity's asset import.
