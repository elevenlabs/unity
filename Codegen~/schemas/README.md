# Vendored protocol schemas

Inputs for the C# codegen under [../src/](../src/). These specs are the
source of truth for the protocol DTOs committed to `Runtime/Core/Protocol/`
and are vendored (rather than read from `node_modules`) so the Unity SDK
isn't gated on a JS SDK release whenever the protocol changes.

## `convai-asyncapi.yml`

Wire protocol for the Conversational AI WebSocket.

| Field         | Value                                                                    |
| ------------- | ------------------------------------------------------------------------ |
| Upstream repo | `elevenlabs/xi` (private)                                                |
| Upstream path | `docs/convai-asyncapi.yml`                                               |
| Pinned commit | `49a9dc283ad663910870b4308897e6d3a7ba73d7`                               |
| Commit date   | 2026-06-11                                                               |
| Source branch | `cursor/fix-convai-asyncapi-ref-c38c` ([PR #37707][pr] - not yet merged) |

[pr]: https://github.com/elevenlabs/xi/pull/37707

> The current pin is the head of an open PR (it contains the fix for the
> `DynamicVariableNestedValueType` ref needed to parse the spec). Re-pin to
> the merged-to-`main` commit once #37707 lands.

Verify the vendored content matches the pin:

```
cd /path/to/xi
git show 49a9dc283ad663910870b4308897e6d3a7ba73d7:docs/convai-asyncapi.yml \
  | diff - /path/to/elevenlabs-unity/Codegen~/schemas/convai-asyncapi.yml
```

### Re-syncing

1. `cp /path/to/xi/docs/convai-asyncapi.yml convai-asyncapi.yml`
2. Update **Pinned commit** and **Commit date** above with the output of
   `git log -1 --format="%H %ai" -- docs/convai-asyncapi.yml` in the xi
   checkout.
3. Run `pnpm --dir Codegen~ run generate` and commit both the spec and
   the regenerated DTOs together.
