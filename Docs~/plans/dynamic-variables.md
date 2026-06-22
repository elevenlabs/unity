# Dynamic variables in `ConversationOptions`

**Status:** Proposed, 2026-06-22
**Driver:** Phase 4 follow-up explicitly deferred in [`ConversationOptions.cs`](../../Runtime/Core/ConversationOptions.cs)
**Motivating use case:** Getting-Started demo where three boxes in a scene share one agent and inject their colour + mood as templated variables.

---

## Summary

Surface upstream `@elevenlabs/client`'s session-time `dynamicVariables` field on the C# `ConversationOptions` record and forward it through `BridgedSession.BuildSessionConfig` so a single agent's system prompt can be templated per-session from Unity. No bridge-primitive, jslib, or protocol-DTO changes — the wire shape is already generated, and the JS side already accepts `dynamicVariables` via the upstream `SessionConfig` re-export.

## Why now

Three signals converged:

1. **First real consumer hit it.** The Getting-Started visual demo (one agent, three boxes with `happy`/`sad`/`angry` moods) needs templated prompts; without dynamic variables the only options are three agents or `SendContextualUpdate` workarounds, both of which work against the canonical product surface.
2. **Plumbing is trivial.** `BaseSessionConfig.dynamicVariables` is already on upstream's `SessionConfig` (verified in `@elevenlabs/client@1.11.2` at `dist/utils/BaseConnection.d.ts`); we re-export `SessionConfig` directly from [`Bridge~/src/connection/types.ts`](../../Bridge~/src/connection/types.ts), so the JS bridge accepts the field today. Only the C# `BuildSessionConfig` omits it.
3. **The protocol DTO already has it.** [`Runtime/Core/Protocol/OutgoingSocketEvent.g.cs:136-137`](../../Runtime/Core/Protocol/OutgoingSocketEvent.g.cs#L136-L137) emits `DynamicVariables` on `ConversationInitiationClientData` via codegen — but no C# code path produces that frame (the upstream JS `WebSocketConnection.create()` sends it during the handshake from the `SessionConfig` we hand in). Adding the option closes the half-finished surface.

## Scope

### In scope

- New property: `ConversationOptions.DynamicVariables` of type `IReadOnlyDictionary<string, object>?`
  - Value type is `object` to match upstream's `string | number | boolean` union; the three runtime types serialize cleanly through Newtonsoft.Json.
  - Nullable / defaults to `null` so existing callers are unaffected.
  - Validation deferred to the server — upstream does no client-side type check either.
- `BridgedSession.BuildSessionConfig` emits a `dynamicVariables` JObject when the dictionary is non-null **and** non-empty (omission semantics match the existing `agentId` / `signedUrl` pattern — the union types reject empty objects in some arms).
- One Edit-mode test asserting the JSON shape of `BuildSessionConfig` with and without dynamic variables.
- One bridge test in [`Bridge~/src/connection/factories.test.ts`](../../Bridge~/src/connection/factories.test.ts) asserting a config carrying `dynamicVariables` flows into the SDK call.
- An assertion in the conversation smoke integration test that, when `ConversationSmokeConfig` declares `dynamicVariables`, the first `conversation_initiation_client_data` WebSocket frame includes them under `dynamic_variables`.

### Explicitly out of scope (next plans)

- **`overrides`** (agent prompt / first-message / language / TTS knobs / `conversation.textOnly`). Same shape, same wire frame, but each knob carries product semantics that deserve its own design note — at minimum the textOnly + audio-pipeline interaction.
- **`customLlmExtraBody`** (untyped passthrough). Trivial to add but needs a stance on whether we accept `JObject` directly or wrap it; defer to keep this PR's surface small.
- **Mid-session updates.** Upstream only accepts dynamic variables at session start; we match that. Any future "agent-state push" surface is a separate proposal.

## Design

### Type for the value

| Option | Pros | Cons |
| --- | --- | --- |
| `IReadOnlyDictionary<string, object>` ✓ | Matches upstream union (`string \| number \| boolean`) with zero new types; mirrors the codegen DTO (`Dictionary<string, dynamic>`). | Accepts unsupported types at compile time (e.g. `DateTime`); only caught at JSON serialization or on the server. |
| Custom union type (`DynamicVarValue`) | Compile-time-safe to upstream's three types. | Adds a public type for a feature the server still ultimately validates; awkward initializer syntax in user code. |
| `IReadOnlyDictionary<string, string>` | Simplest. | Lossy — booleans and numbers stringify and the agent sees `"true"` not `true`. Breaks parity with JS. |

Pick the dictionary-of-object form: it is the smallest delta, matches the existing generated DTOs, and the cost of looser typing is acceptable for a passthrough field.

### Serialization

The JObject construction in `BuildSessionConfig` uses Newtonsoft's `JObject.FromObject(...)` against the dictionary so that string / number / bool flow through naturally. Wrapping that in a small helper keeps the omission rule (skip when null or empty) in one place. JObject's null-property omission already covers the union-type sensitivity called out in the existing comment at `BridgedSession.cs:179-183`.

### Public API delta

```csharp
public sealed record ConversationOptions
{
    // existing properties unchanged …

    /// <summary>
    /// Per-session variables substituted into the agent's templated system
    /// prompt and first message. Values must be string, number, or bool —
    /// other runtime types serialize but the server rejects them.
    /// </summary>
    public IReadOnlyDictionary<string, object>? DynamicVariables { get; init; }
}
```

Conversation-side callers gain one extra named arg on `StartSessionAsync(options)`:

```csharp
var convo = await Conversation.StartSessionAsync(new ConversationOptions
{
    AgentId = "agent_xxx",
    DynamicVariables = new Dictionary<string, object>
    {
        ["color"] = "red",
        ["mood"] = "angry",
    },
});
```

## Test plan

| Layer | Test | Asserts |
| --- | --- | --- |
| C# Edit mode | `BridgedSession_BuildSessionConfig_Tests` (new) | (a) omits `dynamicVariables` when null / empty; (b) emits a JObject with mixed string / int / bool values when populated. |
| JS unit | `Bridge~/src/connection/factories.test.ts` | The factory passes `dynamicVariables` through to the SDK's `WebSocketConnection.create` call. |
| JS / browser e2e | `IntegrationTests~/src/conversation-smoke.test.ts` | When the smoke config declares `dynamicVariables`, the captured first WS frame contains them at `dynamic_variables.<key>` with the original types preserved. |

The Edit-mode test is the load-bearing one: it pins down the JSON shape regardless of upstream changes, so a future bump of `@elevenlabs/client` that renames the field surfaces here first.

## Migration / compatibility

- Pure additive change. Existing callers (only the in-tree smoke test and the Getting-Started demo) compile and behave unchanged because the new property is nullable.
- No CHANGELOG entry needed beyond "Add `ConversationOptions.DynamicVariables`" — no deprecations, no behavior shifts.
- No bridge primitive or jslib rebuild required; the JSON the C# side hands over to `JsBridge.InvokeFactoryAsync` is treated as opaque by the primitives layer.

## Open questions

- Should we publish a tiny C# helper for the upstream allow-list (e.g. `DynamicVariables.Of(("color", "red"), ("count", 3))`) to make call sites read better? Decision: ship without; revisit if the demo's call site is ugly enough to justify the API surface.
- Do we want a one-off Roslyn analyzer warning on non-`string|int|long|double|bool` values? Out of scope here; revisit alongside the broader `overrides` plan where typed knobs are more useful.
