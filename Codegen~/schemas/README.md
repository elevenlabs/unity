# Vendored protocol schemas

Inputs for the C# codegen under [../src/](../src/). These specs are the
source of truth for the protocol DTOs committed to `Runtime/Core/Protocol/`
and are vendored (rather than read from `node_modules`) so the Unity SDK
isn't gated on a JS SDK release whenever the protocol changes.

## `convai-asyncapi.yml`

Wire protocol for the Conversational AI WebSocket.

| Field         | Value                                                                            |
| ------------- | -------------------------------------------------------------------------------- |
| Upstream repo | ElevenLabs internal monorepo (private)                                           |
| Upstream path | `docs/convai-asyncapi.yml`                                                       |
| Pinned commit | `87a762f38086ea67286dc549701c075702742444`                                       |
| Commit date   | 2026-06-22                                                                       |
| Source branch | `main` (internal PR #38788 — "register public AsyncAPI events and reduce unregistered allowlist") |

Verify the vendored content matches the pin:

```
cd /path/to/monorepo
git show 3b73154ddaf2176e297c037b9af8e69231b8d42d:docs/convai-asyncapi.yml \
  | diff - /path/to/elevenlabs-unity/Codegen~/schemas/convai-asyncapi.yml
```

### Re-syncing

1. `cp /path/to/monorepo/docs/convai-asyncapi.yml convai-asyncapi.yml`
2. Update **Pinned commit** and **Commit date** above with the output of
   `git log -1 --format="%H %ai" -- docs/convai-asyncapi.yml` in the
   monorepo checkout.
3. Run `pnpm --dir Codegen~ run generate` and commit both the spec and
   the regenerated DTOs together.
