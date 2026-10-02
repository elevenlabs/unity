<!-- Why is this change needed? Link the issue it fixes, e.g. "Fixes #123". -->

## Changes

<!-- One line per change. -->

-

## Verification

<!-- Tick what you ran; see CONTRIBUTING.md → "Running the full verification suite". Leave unrelated layers unticked. -->

- [ ] `dotnet csharpier check .`
- [ ] `Bridge~` checks (`format:check`, `lint`, `typecheck`, `test`, `verify:primitives`, `verify:connection`)
- [ ] `Codegen~` checks (`verify:protocol-dtos`, `round-trip`)
- [ ] Edit Mode tests (`pnpm --dir TestProject run test`)
- [ ] WebGL builds + browser integration tests (`IntegrationTests~`)
- [ ] Manually tested in a Unity project. Platforms and Unity version:

## Checklist

- [ ] `CHANGELOG.md` updated under `[Unreleased]` for user-facing changes
- [ ] Public API changes are called out above
