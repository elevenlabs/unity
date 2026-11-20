# Vendored protocol schemas

Inputs for the C# codegen under [../src/](../src/). These specs are the
source of truth for the protocol DTOs committed to `Runtime/Core/Protocol/`
and are vendored (rather than read from `node_modules`) so the Unity SDK
isn't gated on a JS SDK release whenever the protocol changes.

## `convai-asyncapi.yml`

Wire protocol for the Conversational AI WebSocket.

| Field         | Value                                                                                 |
| ------------- | ------------------------------------------------------------------------------------- |
| Upstream repo | ElevenLabs internal monorepo (private)                                                |
| Upstream path | `packages/python/xi_shared_asyncapi/xi_shared_asyncapi/specs/convai-asyncapi.yml`     |
| Pinned commit | `4e218ce8434fd762ab2754342b4b8a609f9f1920`                                            |
| Commit date   | 2026-11-19                                                                            |
| Source branch | `main` (internal PR #65370 — "[Agents] Add unity_sdk conversation initiation source") |

The same document is published at <https://api.elevenlabs.io/convai-asyncapi.yml>;
at the pin above the two are byte-identical.

Verify the vendored content matches the pin:

```
cd /path/to/monorepo
git show 4e218ce8434fd762ab2754342b4b8a609f9f1920:packages/python/xi_shared_asyncapi/xi_shared_asyncapi/specs/convai-asyncapi.yml \
  | diff - /path/to/elevenlabs-unity/Codegen~/schemas/convai-asyncapi.yml
```

### Re-syncing

1. `cp /path/to/monorepo/packages/python/xi_shared_asyncapi/xi_shared_asyncapi/specs/convai-asyncapi.yml convai-asyncapi.yml`
2. Update **Pinned commit** and **Commit date** above with the output of
   `git log -1 --format="%H %ai" -- packages/python/xi_shared_asyncapi/xi_shared_asyncapi/specs/convai-asyncapi.yml`
   in the monorepo checkout.
3. Run `pnpm --dir Codegen~ run generate` and commit both the spec and
   the regenerated DTOs together.
