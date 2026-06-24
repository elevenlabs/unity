# Drag-and-drop `ElevenLabsAgent` component

**Status:** Brainstorm, 2026-06-23
**Driver:** First real consumer (the Getting-Started visual demo, [`TalkingBox.cs`](https://github.com/kraenhansen/UnityProjects-Getting-Started)) shows that the current `Conversation.StartSessionAsync(options)` surface is a "build your own MonoBehaviour" kit — boilerplate that every game-dev consumer will re-derive. Once the [v0.1-parity.md](./v0.1-parity.md) native transport lands the SDK works in Editor + standalone, opening the door to a higher-level component.
**Assumes:** Everything in [v0.1-parity.md](./v0.1-parity.md) is done — native transport (#9), full method/event parity (#11), `ConversationConfigOverride` plumbed through (#10), `UploadFileAsync` available (#11b).

---

## Why this exists

A Unity game developer should be able to:

1. Drag an `ElevenLabsAgent` component onto a GameObject.
2. Paste an Agent ID.
3. Hit Play. The character talks.

Today the equivalent is the 178-line `TalkingBox.cs` from the Getting-Started demo, which has to re-derive:

- A ScriptableObject for the agent ID (the SDK doesn't ship one).
- `Resources.Load` shenanigans to find it at runtime.
- `Conversation` lifecycle inside try / catch — start, subscribe, store handle, unsubscribe, dispose.
- A `static TalkingBox? currentlyActive` field to enforce "only one agent at a time."
- `#if UNITY_WEBGL && !UNITY_EDITOR` guards around every SDK call because the SDK doesn't run in Editor today.
- Per-event subscribe-and-cleanup boilerplate.

Every consumer will re-derive 80 % of this. The component is the abstraction.

## Personas

The design has to serve four personas without forcing the harder ones onto the easier ones:

| Persona | Wants | Must not have to |
|---|---|---|
| **Designer** (no C#) | Drag, drop, wire UnityEvents in the Inspector | Write any code, understand `Awaitable`, know what a transport is |
| **Scripter** (game logic in C#) | Component reference → `agent.SendUserMessage(...)`; subscribe to typed events | Re-implement lifecycle, single-session policy, subscribe/unsubscribe boilerplate |
| **Engineer** (full control) | Component as opt-in convenience; low-level `Conversation` still available | Be forced through the component for advanced setups (custom transports, programmatic config, runtime tool composition) |
| **AI assistant** (via [unity-mcp](https://github.com/Unity-Technologies/unity-mcp)) | Add the component, wire dependencies (AudioSource, colliders), configure providers, run a self-test — all without a human pointing and clicking | Reverse-engineer the component by trial-and-error; fight `[SerializeReference]` ergonomics through `SerializedObject`; parse free-form `Debug.Log` to know what happened |

Two of these rows are load-bearing:

- **Engineer:** the component is *additive over* `Conversation`, not a replacement. A user who needs full control still writes `Conversation.StartSessionAsync(options)` directly — the new component is one of several ways to use the SDK.
- **AI assistant:** for users working in Claude Code + unity-mcp this is arguably the *primary* onboarding path, not a fallback. "Add an ElevenLabs agent to my NPC" should scaffold + wire + self-test without the human ever opening the Inspector. See [AI-assisted authoring](#ai-assisted-authoring-via-unity-mcp).

## What the current TalkingBox tells us

Concrete pain points to design out:

- **No SDK-shipped agent-id ScriptableObject** → every demo invents `TalkingBoxAgentConfig` again.
- **No auth strategy abstraction.** Public agent / signed URL / WebRTC token are three first-class flows; the component can't paper over them.
- **Audio out has no obvious Inspector hook.** Once native lands ([`UnityAudioSourceOutput`](../../Runtime/Native/UnityAudioSourceOutput.cs) per [#9d](./v0.1-parity.md#9d--unityaudiosourceoutput)), a designer wants "play through *this* `AudioSource`" — but `ConversationOptions` doesn't take one.
- **Dynamic variables and overrides are dictionary-construction code.** Even a one-liner is something a designer can't write.
- **Events are C# events.** Designers expect `UnityEvent` so they can wire animation triggers in the Inspector without touching `+=`.
- **Editor mode is a dead zone.** Forces every consumer to write `#if UNITY_EDITOR` themselves to avoid runtime exceptions. Native transport fixes the runtime side, but the component should still degrade gracefully in Edit mode.
- **Single-active-session enforcement is a global static.** A common need (`TalkingBox` does it; almost any "NPC you talk to" scene wants it) that today every consumer re-implements.

---

## Proposed component shape

```
ElevenLabsAgent : MonoBehaviour
─── Identity ─────────────────────────────────────────────────────────
  [SerializeReference] IAgentCredentialProvider credentials   ← polymorphic
    InlineAgentIdProvider         : public agent id, no auth
    SignedUrlEndpointProvider     : HTTP fetch from a configured endpoint
    ConversationTokenEndpointProvider : same shape, returns WebRTC token
    CustomCredentialProvider      : MonoBehaviour / ScriptableObject reference

  ConnectionType connectionType = WebSocket

─── Session config ──────────────────────────────────────────────────
  List<DynamicVariableEntry> dynamicVariables   ← typed (string/int/float/bool)
  AgentConfigOverridesInspector overrides       ← per-leaf "use override" toggles
  string userId                                  ← empty = unset
  Dictionary<string, string> customLlmExtraBody  ← advanced, foldout

─── Audio ───────────────────────────────────────────────────────────
  AudioSource? agentAudioSource    ← null → AddComponent at runtime
  string microphoneDeviceName       ← "" → platform default

─── Lifecycle ───────────────────────────────────────────────────────
  bool autoStartOnEnable           = false
  bool autoEndOnDisable            = true
  bool enforceSingleActiveSession  = true

─── UnityEvents ─────────────────────────────────────────────────────
  UnityEvent          OnSessionStarted
  UnityEvent          OnSessionEnded
  UnityEvent<string>  OnAgentResponded         (the text)
  UnityEvent<string>  OnUserTranscribed
  UnityEvent<string>  OnError
  UnityEvent          OnAudioChunkReceived     ("agent is talking now")
  UnityEvent<float>   OnInputVolumeChanged     (mic-level visualisers)
  UnityEvent<float>   OnOutputVolumeChanged    (talking-box pulse)

─── Designer-facing client tools ────────────────────────────────────
  List<SimpleToolBinding> tools                ← name → UnityEvent<string>(json)
```

Public scripting surface (the API a `Scripter` consumes):

```csharp
public sealed class ElevenLabsAgent : MonoBehaviour
{
    /// <summary>Live conversation; null until <see cref="StartSessionAsync"/> resolves.</summary>
    public Conversation? Conversation { get; }

    public Awaitable StartSessionAsync(CancellationToken ct = default);
    public Awaitable EndSessionAsync();

    public void  SendUserMessage(string text);
    public void  SendContextualUpdate(string text);
    public Awaitable SetMicMuted(bool muted);

    public void RegisterTool<TParams, TResult>(string name, Func<TParams, TResult> handler);
    public void RegisterTool<TParams, TResult>(string name, Func<TParams, Awaitable<TResult>> handler);
    public bool UnregisterTool(string name);
}
```

The component is a thin orchestrator: it composes a `ConversationOptions` from its Inspector state, calls `Conversation.StartSessionAsync`, retains the resulting `Conversation`, subscribes to its events, re-raises them as UnityEvents, and tears down on disable.

---

## Design dimensions

### 1. Credential provider — pluggable, not enumerated

There are three valid auth strategies (public agent id, signed WebSocket URL, WebRTC conversation token) and a long tail of "fetch the credential from my backend." The cleanest shape is a small polymorphic provider interface:

```csharp
public interface IAgentCredentialProvider
{
    Awaitable<AgentCredential> ResolveAsync(CancellationToken ct);
}

public sealed record AgentCredential(
    string? AgentId,
    string? SignedUrl,
    string? ConversationToken);
```

`[SerializeReference]` is what makes this work in the Inspector — the field shows the picked subclass's fields and nothing else. Four ship out of the box:

- **`InlineAgentIdProvider`** — public agent id field. The 90 % case. The first-paint default for a freshly added component.
- **`SignedUrlEndpointProvider`** — configurable HTTPS URL (with `{agentId}` token expansion), optional headers list, optional cache duration. Returns the URL from the response body (`{ "signed_url": "..." }` by default; a JSONPath / response field setting handles backends with different shapes).
- **`ConversationTokenEndpointProvider`** — same shape; returns the WebRTC token instead.
- **`CustomCredentialProvider`** — `MonoBehaviour` / `ScriptableObject` reference satisfying `IAgentCredentialProvider`. The escape hatch.

The existing `AgentId XOR SignedUrl XOR ConversationToken` validation in `BridgedSessionLauncher` / `NativeSessionLauncher` (from #10) holds the line if a provider returns a malformed credential.

**Why polymorphic, not an enum.** A `CredentialSource { Inline | HttpEndpoint | Custom }` enum forces every option's fields onto the same component with most hidden behind a switch. The polymorphic form shows only the relevant fields, and stays extensible if we later ship `JwtProvider`, `EnvVarProvider`, or `PlayerPrefsProvider`.

**API key caveat for `SignedUrlEndpointProvider`.** Direct `xi-api-key` fetches from the client are the obvious shortcut and the obvious footgun (key leaks in the build). The provider's docstring and Inspector help-box should steer users toward a backend they own. We can ship an `XiApiKeyProvider` marked clearly as "development only" so the right path is also the discoverable one.

### 2. Session lifecycle triggers — don't bake them in

`TalkingBox` ties session start to `OnTriggerEnter`. Other plausible triggers: scene-load, UI-button-click, gaze-target-acquired (XR), wake-word, voice-activity-detected. No single right answer.

**Decision:** the component exposes `StartSessionAsync()` / `EndSessionAsync()` and one bool (`autoStartOnEnable`). Other triggers belong in companion components or user scripts that call those methods.

Three small companion components ship as samples, demonstrating the integration pattern more than reusable logic:

- **`ElevenLabsAgentProximityTrigger`** — start on collider trigger enter, end on exit. The `TalkingBox` pattern, ~30 lines.
- **`ElevenLabsAgentButtonTrigger`** — start/stop wired to a UI button.
- **`ElevenLabsAgentPushToTalk`** — hold key/button to enable mic. Doesn't open/close the session; toggles mute via `agent.SetMicMuted(...)`.

### 3. Dynamic variables — typed Inspector entries

`ConversationOptions.DynamicVariables` is `IReadOnlyDictionary<string, object>?` and accepts string / int / double / bool per upstream's allow-list. The Inspector representation is:

```csharp
[Serializable]
public class DynamicVariableEntry
{
    public string                 key;
    public DynamicVariableType    type;   // String | Int | Float | Bool
    public string                 stringValue;
    public int                    intValue;
    public float                  floatValue;
    public bool                   boolValue;
}
```

A custom `PropertyDrawer` hides the irrelevant value fields based on the `type` selector. The component composes the dictionary from non-empty entries at session start.

Alternatives considered and rejected:

- **`SerializedDictionary<string, string>`** — lossy (`true` becomes `"true"`), breaks parity. Rejected.
- **Three separate lists, one per type** — clutters the Inspector; users will wonder why they need three.
- **`[SerializeReference]` per-entry union** — clean class hierarchy, but verbose per-entry. Worse UX for a feature designers should use casually.

### 4. Config overrides — per-field "use override" toggles

`ConversationConfigOverride` has sparse nested fields (`agent.firstMessage`, `agent.language`, `tts.voiceId`, `tts.stability`, `conversation.textOnly`, prompt pass-through, etc.). The omission semantics matter — a `null` is "don't override," an empty string is `""`, and the wire is sensitive to the difference. The Inspector should:

- Group fields by subtree (Agent / TTS / Conversation / Prompt) as foldouts, default-collapsed.
- For each leaf field, show a `bool useOverride` toggle and the typed value field. When the toggle is off, the field is omitted from the wire.
- Default all toggles off — a freshly added component sends no overrides.

This is the right place for a custom `PropertyDrawer`. Without it, users would model the same opt-in pattern themselves in inspector fields and likely get the `null`-vs-`""` distinction wrong.

### 5. Audio source wiring

Native (#9d) plays through `UnityAudioSourceOutput`'s internal `AudioSource`. The component should let the user supply an existing `AudioSource` so 3D spatial audio, AudioMixer routing, and AudioSource volume curves work without surgery:

- `agentAudioSource` Inspector field, optional.
- At session start: if set, the native output controller binds to that `AudioSource`; if not, an `AudioSource` is `AddComponent`-ed to the agent's GameObject.

**WebGL caveat.** Default-mode WebGL plays audio through the JS `<audio>` element; the supplied `AudioSource` is ignored. The Inspector should flag this with a HelpBox + XMLdoc note so users don't expect spatialisation in a WebGL build. Routing audio through Unity on WebGL is the *Unity-routed mode* in [`ARCHITECTURE.md`](../ARCHITECTURE.md#audio-routing), tracked for v0.3.

Microphone: a `microphoneDeviceName` field (empty → default). Less commonly customised; expose the field but expect most consumers to ignore it.

The `agentAudioSource` field is the *consumer* of a Core SDK primitive — `ConversationOptions.OutputAudioSource` — that low-level `Conversation.StartSessionAsync` users get the benefit of too without adopting the component. See [`output-audio-source.md`](./output-audio-source.md) for the API surface, the `UnityAudioSourceOutput` implementation, lifecycle, and test plan.

### 6. UnityEvents — the designer's API

The C# `Conversation` exposes typed `event Action<TArgs>` events. The component bridges each useful one to a `UnityEvent<simpler-shape>`:

| `Conversation` event | UnityEvent surface | Shape | Notes |
|---|---|---|---|
| `Connected` | `OnSessionStarted` | no-payload | The session is live and the conversation id is known. |
| `Disconnected` | `OnSessionEnded` | no-payload | `DisconnectionDetails` reason is exposed via a separate `UnityEvent<string>? OnSessionEndedDetailed` for those who want it. |
| `AgentResponded` | `OnAgentResponded` | `string` | Just the text — the full args record is on the underlying `Conversation`. |
| `UserTranscriptReceived` | `OnUserTranscribed` | `string` | |
| `ErrorOccurred` | `OnError` | `string` | |
| `AudioReceived` | `OnAudioChunkReceived` | no-payload | Designers want the trigger, not the bytes (think: lip-sync pulse). |
| polled per-Update | `OnInputVolumeChanged` | `float` | Mic visualiser. |
| polled per-Update | `OnOutputVolumeChanged` | `float` | Talking-box pulse. |

Volume events fire each Update by polling `Conversation.GetInputVolume()` / `GetOutputVolume()`. One virtual call per frame per active agent — acceptable.

The richer typed events (`AgentResponseCorrectionArgs`, `VadScoreArgs`, the upcoming `MCPToolCall*` family from #11c) are not bridged to UnityEvents. They're accessible via `agent.Conversation?.AgentResponseCorrected += ...` from scripter-tier code.

### 7. Client tools — designer + scripter, both shipped

Tool registration has two audiences with very different needs:

- **Designer:** "When the agent calls `light_on`, fire this UnityEvent." Static tool name, no typed params, no reply.
- **Scripter:** "Register a typed handler that gets a parsed params record and returns a typed result."

Ship both. The designer path is a `[Serializable]` list:

```csharp
[Serializable]
public sealed class SimpleToolBinding
{
    public string                  toolName;
    public UnityEvent<string>      onInvoked;   // payload is the raw params JSON
}

[SerializeField] List<SimpleToolBinding> tools;
```

The scripter path is the existing API surfaced through the component:

```csharp
agent.RegisterTool<MyParams, MyResult>("my_tool", p => DoTheThing(p));
```

The component auto-registers each `SimpleToolBinding` at session start. Scripter-registered tools survive end/restart cycles (re-applied each session). Same-name collisions log a warning and prefer the last-registered handler (matches today's `Conversation.RegisterTool` behaviour).

**Reply restriction for `SimpleToolBinding`.** Designer-tier tools are fire-and-forget — the UnityEvent fires and the component sends `client_tool_result` with `result: "ok"` if the agent expects one. Designers who need to reply with a value should graduate to the typed `RegisterTool<>` overload. Stating that limit upfront is cleaner than retrofitting a "reply builder" pattern into the binding.

### 8. Editor-mode behaviour

After #9 the SDK runs in Editor. The component should:

- Run normally in Play mode — sessions open against real agents.
- No-op cleanly in Edit mode. `Awake` / `OnEnable` only act when `Application.isPlaying`.
- Inspector shows a HelpBox if no credential provider is set, or if `InlineAgentIdProvider.agentId` is empty.
- Inspector shows **Start session** / **End session** buttons in Play mode for quick interactive testing without an external trigger.
- A `[ContextMenu("Validate")]` runs `credentials.ResolveAsync` in the Editor and prints the result, so configuration errors surface before hitting Play.

The TalkingBox-style `#if UNITY_WEBGL && !UNITY_EDITOR` guards disappear from user code — that's a v0.1 problem the component inherits the fix for.

### 9. Multi-instance / single-active-session policy

`TalkingBox`'s `static currentlyActive` enforces "only one agent at a time on the scene." Common — a player shouldn't simultaneously talk to two NPCs because the mic feeds both — but not universal (a chorus of ambient text-only agents; a multi-agent narration system).

**Decision:** put the policy on the component, default-on, but keep the implementation behind a small static so consumers can integrate from outside:

```csharp
[SerializeField] bool enforceSingleActiveSession = true;

public async Awaitable StartSessionAsync(CancellationToken ct = default)
{
    if (enforceSingleActiveSession)
        await ElevenLabsAgentScene.EnsureExclusiveAsync(this, ct);
    // … open the session …
}
```

`ElevenLabsAgentScene.EnsureExclusiveAsync` ends any other active component that also opted in. Components with `enforceSingleActiveSession = false` are exempt both ways (they don't end others, and others don't end them) — a deliberate "I know what I'm doing" knob for the chorus / ambient case.

**Why default-on.** Overlapping mics are worse than an unexpected end-of-session log line; first-time users will be confused if two `ElevenLabsAgent`s in their scene both try to take the mic.

---

## AI-assisted authoring via unity-mcp

[unity-mcp](https://github.com/Unity-Technologies/unity-mcp) gives an AI assistant the ability to add components, configure their fields, run editor scripts (`Unity_RunCommand`), generate assets (`Unity_AssetGeneration_GenerateAsset`), read the Console (`Unity_GetConsoleLogs`), and capture scene state (`Unity_SceneView_*`). A user in Claude Code can say *"add an ElevenLabs agent to my NPC character with push-to-talk"* and the assistant runs the scaffolding end-to-end. Designing the component with this workflow in mind costs little and makes the AI path durable instead of fragile.

### Concrete design consequences

**1. Idempotent setup helpers.** Ship a static API that accepts intent and produces a configured component:

```csharp
public static class ElevenLabsAgentSetup
{
    public static ElevenLabsAgent Configure(GameObject target, AgentSetupOptions opts);
    public static void ResetToDefaults(ElevenLabsAgent agent);
}

public sealed record AgentSetupOptions(
    CredentialKind  Credentials,         // PublicAgent | SignedUrl | WebRtcToken | Custom
    string?         AgentId          = null,
    string?         EndpointUrl      = null,
    ConnectionType  Transport        = ConnectionType.WebSocket,
    bool            AddAudioSource   = true,
    bool            EnforceSingleSession = true);
```

`Configure` is the single entry point AI scripts call. It's add-or-find for the component, add-or-find for the `AudioSource`, set-not-replace for the override toggles. Re-running with the same input is a no-op. Re-running with changed input mutates exactly the deltas. Without this, every `Unity_RunCommand` script reinvents the same `GetComponent ?? AddComponent` dance and inevitably leaves orphan state on the second run.

**2. Introspection that round-trips through chat.** A `string DescribeConfiguration()` method (and structured `AgentConfigSnapshot` record behind it) so the assistant can read the component back without parsing serialized YAML:

```csharp
public sealed record AgentConfigSnapshot(
    string                       CredentialKind,
    string?                      AgentIdHash,             // never the raw id, just a hash for parity check
    ConnectionType               Transport,
    IReadOnlyList<string>        DynamicVariableKeys,
    IReadOnlyList<string>        ActiveOverrides,
    bool                         HasAudioSource,
    bool                         EnforceSingleSession,
    IReadOnlyList<string>        ToolNames);

public AgentConfigSnapshot Describe();
public string              DescribeConfiguration();   // human-readable wrapper, also [ContextMenu]
```

The hash-not-value rule for the agent ID is deliberate — secrets shouldn't end up in a chat transcript that gets pasted somewhere. The snapshot is enough for the assistant to say *"this agent is wired to a public agent with three dynamic variables and a TTS voice override; missing an `AudioSource`"* without leaking the id.

**3. Tagged log lines as a public contract.** Standardise the prefix:

```
[ElevenLabsAgent:{name}] connected agent_id_hash=…
[ElevenLabsAgent:{name}] session ended reason=user
[ElevenLabsAgent:{name}] error: <message>
[ElevenLabsAgent:{name}] tool '<name>' invoked params=<json>
```

`Unity_GetConsoleLogs` returns these as grep targets. The convention already exists informally (`[ConvSmoke]`, `[TalkingBox:{name}]`); formalising it in the component is one line of code and a sentence in the docs.

**4. Programmatic ergonomics for `[SerializeReference]`.** Polymorphic credential providers are great for human Inspector use but painful via `SerializedObject` — `ManagedReferenceUtility.SetManagedReference` plus type-string lookup is error-prone, and a misspelled type name silently produces a null field. Two mitigations:

- The `AgentSetupOptions.Credentials` enum on the setup helper hides the polymorphism — the AI passes `CredentialKind.SignedUrl` plus an `EndpointUrl` and never touches `SerializedObject`.
- A `ElevenLabsAgent.SetCredentials(IAgentCredentialProvider)` instance method so scripted setup paths assign the strongly-typed instance directly instead of forcing serialization round-trips during edit-time configuration.

The human Inspector path keeps `[SerializeReference]` because that's what makes the dropdown work; the AI path bypasses it entirely.

**5. Self-test as a built-in.** A method the AI can call after scaffold to confirm the configuration actually works end-to-end:

```csharp
public async Awaitable<HealthReport> RunSelfTestAsync(CancellationToken ct = default);

public sealed record HealthReport(
    bool      Ok,
    string    Summary,
    TimeSpan  CredentialResolveTime,
    TimeSpan? HandshakeTime,
    string?   Error);
```

Resolves the credential, opens a session, waits for `Connected`, sends a one-token contextual update, ends the session. Reports timings on success, the error on failure. The AI runs this after `Configure` and surfaces the result back to the human — *"agent configured; self-test passed in 312 ms"*. Same method is a `[ContextMenu("Run Self-Test")]` for humans. Same method is the basis for the CI smoke check that already lives in `Samples/ConversationSmokeTest/`.

### Sample scenes become scaffold scripts

Hand-built `.unity` files for samples are binary YAML — painful to diff, painful to regenerate, painful for the AI to modify. The same samples expressed as scaffold scripts:

```csharp
// Samples~/QuickStart/Scaffold.cs
public static class QuickStartScaffold
{
    [MenuItem("ElevenLabs/Samples/QuickStart/Build Scene")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
        var go    = new GameObject("ElevenLabs Quickstart Agent");
        ElevenLabsAgentSetup.Configure(go, new AgentSetupOptions(
            Credentials: CredentialKind.PublicAgent,
            AgentId:     "agent_xxx",
            AddAudioSource: true));
        EditorSceneManager.SaveScene(scene, "Assets/Samples/QuickStart.unity");
    }
}
```

…are trivial diffs (one C# file), trivially regenerable (re-run the menu item), AI-mutable via `Unity_RunCommand`, and self-document the recommended setup. Ship the scaffold scripts; let users (or the AI) generate the `.unity` files locally. The `.unity` files themselves stay gitignored under `Samples~/Generated/`.

Asset generation for those samples — character sprites, sound effects, the talking-box's pulse audio — is `Unity_AssetGeneration_GenerateAsset` territory. Document one example end-to-end (*"have Claude generate a wizard sprite and wire it to the agent"*) rather than pretending the SDK ships pre-baked assets.

### How the AI knows about the component

The setup helpers + introspection + self-test surface above are necessary but not sufficient — an AI assistant first has to *know* this SDK exists, what idioms it uses, and which APIs to call. That's what coding-agent skills are for, and the strategy is its own plan: [`unity-coding-agent-skills.md`](./unity-coding-agent-skills.md). The relevant takeaway here is that one of the planned skills — `elevenlabs:unity-agent-component` — is built *around* the `ElevenLabsAgentSetup.Configure` / `Describe` / `RunSelfTestAsync` API surface this section proposes. Without those primitives the skill body would devolve into "tell the user where to click in the Inspector"; with them, the skill emits ready-to-run `Unity_RunCommand` recipes. The component plan and the skills plan are co-evolving for this reason.

### The Editor wizard, reframed

The "Editor wizard / setup window" originally deferred in this plan reframes: **the AI assistant is the wizard** for users who work in Claude Code + unity-mcp. The docs guide them with *"tell Claude: 'set up a push-to-talk agent on the player character'"* instead of *"open the wizard, click Next, fill these fields."*

A native Editor wizard still has a place for users who don't have unity-mcp configured — but it moves from "primary path" to "fallback for non-AI users." That softens the timing: ship the AI-friendly setup APIs in v0.2 alongside the component itself, defer the GUI wizard to v0.3 when there's real user data on which subset of fields actually trip people up.

---

## Companion components — the demo set

The component is the spine; small companions show how to wire it. These ship as `Samples~/` entries (per [#12](./v0.1-parity.md#12--docs--samples-polish) — replacing the smoke tests, which move under `Tests~/`):

1. **QuickStart** — single `ElevenLabsAgent` on an empty GameObject, public agent id, `autoStartOnEnable = true`. Hello-world. ~5 lines of inspector config, zero scripts.
2. **Talking Box** — the Getting-Started demo, but using `ElevenLabsAgent` + `ElevenLabsAgentProximityTrigger`. Replaces the bespoke `TalkingBox.cs` with one component plus two UnityEvent wirings. **The "look how much code disappeared" sample.**
3. **Push-to-Talk** — button-driven session, mic muted unless held. Demonstrates `ElevenLabsAgentPushToTalk`.
4. **Client tools** — agent controls a scene light via a `SimpleToolBinding`; the same scene shows a typed `RegisterTool<>` for a more complex tool with a typed reply.
5. **Microphone visualiser** — `OnInputVolumeChanged` drives a UI bar.
6. **Dynamic variables** — three `ElevenLabsAgent`s in one scene, same agent id, different dynamic variables (the original `TalkingBox` motivation from [`dynamic-variables.md`](./dynamic-variables.md)).

Each sample ships as a scaffold script per [Sample scenes become scaffold scripts](#sample-scenes-become-scaffold-scripts), not as a committed `.unity` file. The Getting-Started repo can then upgrade `TalkingBox.cs` to use the component and shrink to ~30 lines (most of which is the pulse animation, which doesn't belong in the SDK).

---

## What this plan deliberately defers

- **Visual scripting integration** (Unity Visual Scripting / Bolt). UnityEvents already give designers a hook; visual-scripting nodes are polish for whichever version we hear consistent demand at.
- **Native Editor wizard / setup window.** A right-click `GameObject ▸ ElevenLabs ▸ Agent` menu item ships with the component. A full GUI wizard defers to v0.3 — the [AI-assisted authoring](#ai-assisted-authoring-via-unity-mcp) path covers the same need for unity-mcp users, and a GUI wizard for non-AI users is better designed after real feedback on which fields actually trip people up.
- **Persistent / cross-scene sessions.** `DontDestroyOnLoad` on an `ElevenLabsAgent` should Just Work, but stateful persistence (conversation token reuse, transcript replay across scenes) is a separate design.
- **Component pooling.** 50 NPCs each with an `ElevenLabsAgent` ≠ 50 concurrent sessions — the single-session policy handles that — but "one shared session that swaps between NPCs as the player walks past" is a separate pattern.
- **ScriptableObject preset templates.** Useful for "all our merchant agents share these overrides"; ship later if users report duplication pain.
- **File upload UI** (`UploadFileAsync` from #11b → `SendMultimodalMessage`). The scripter API will exist, but a designer-friendly "drag-and-drop a file in the Inspector to send it" surface is its own design.
- **MCP tool approval UI.** Once `SendMCPToolApprovalResult` lands (#11b, contingent on spec re-vendor), there's a natural "prompt the user with an approval modal" UX. Out of scope here.

---

## Open questions

1. **Default for `enforceSingleActiveSession`.** Recommend `true` per the reasoning above. Confirm with one early adopter before shipping.
2. **`agentAudioSource` field visibility on WebGL.** Hide it via a custom drawer when `EditorUserBuildSettings.activeBuildTarget == WebGL`, or always show with a help-box explaining it's ignored? Lean toward the second — Inspector consistency is worth one help-box, and the field will start working once Unity-routed-mode WebGL lands.
3. **`OnAudioChunkReceived` vs. `OnOutputVolumeChanged`.** Both signal "the agent is talking right now." Ship both — semantically distinct ("chunk arrived from wire" vs. "current playback level"), and the pulse-on-chunk pattern in `TalkingBox` doesn't have an obvious volume-threshold equivalent.
4. **Component naming.** `ElevenLabsAgent` is most discoverable in the Add Component dropdown but risks namespace pollution if other ElevenLabs products ship Unity components later. Alternatives: `ConversationalAgent`, `AgentBehaviour`, `ElevenLabsConversationAgent`. Lean `ElevenLabsAgent` for v0.2 and rename pre-v1 if needed.
5. **Where does the SDK ship the component?** Same package (`io.elevenlabs.agents`) or a sibling (`io.elevenlabs.agents.unity`)? Same package is simpler for users; sibling lets the core SDK stay free of `UnityEngine.UI` / `UnityEvent` baggage. Lean same-package — the dependencies are all already part of `UnityEngine`, no new platform-engine surface.
6. **Inspector drawer for `ConversationConfigOverride`.** Hand-rolled per-leaf drawer (verbose but precise omission semantics) vs. reflective auto-drawer that walks the record (less code but harder to express the "off = omit" toggle). Lean hand-rolled; the override surface is small enough.
7. **`CancellationToken` propagation.** `StartSessionAsync(CancellationToken)` is easy to add; `EndSessionAsync()` is harder because the underlying `Conversation.EndSession()` doesn't take a token. Consistency with `Conversation`'s surface vs. component-level cancellation — lean toward matching `Conversation` (no token on End) until there's a concrete need.
8. **`AgentConfigSnapshot.AgentIdHash` — hash function and stability guarantee.** SHA-256 truncated to 8 hex chars is enough for "did this change?" parity but isn't a stable contract across SDK versions if we change the hash. Decide whether to commit to a stable hash (so AI tooling can compare snapshots across builds) or treat it as opaque per-snapshot. Lean opaque — fewer commitments to maintain — and let the AI compare full snapshots if it needs cross-build parity.
9. **`RunSelfTestAsync` cost-of-call.** The self-test opens a real session against the configured agent — which costs the user money. Default it to "opt-in" (the AI explicitly invokes it; not auto-run on `Configure`) and document the cost in the XMLdoc. Open question: should the self-test have a "dry-run" mode that only resolves the credential and validates the URL without opening a session? Useful for unauthenticated configuration checks; loses the end-to-end signal.
