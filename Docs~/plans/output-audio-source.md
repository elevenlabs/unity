# `ConversationOptions.OutputAudioSource` — bring-your-own AudioSource

**Status:** Brainstorm, 2026-06-24
**Driver:** The Getting-Started `TalkingBox` demo plays its voice with `spatialBlend = 0` — omnipresent (2D) playback regardless of where the box sits in the scene. A listener walking past a row of talking boxes hears every active one at full volume from nowhere in particular. The same gap will hit every consumer who wants positional voice, AudioMixer routing, or volume-curve control: the SDK owns the `AudioSource` and doesn't let go.
**Assumes:** Native transport (`#9d` in [v0.1-parity.md](./v0.1-parity.md)) is shipped; `UnityAudioSourceOutput` is the canonical native output controller.
**Consumed by:** the `agentAudioSource` Inspector field in [`agent-component.md`](./agent-component.md) §5 — but this primitive stands on its own; low-level `Conversation.StartSessionAsync` users get the same benefit without adopting the component.

---

## Why this exists

Today [`UnityAudioSourceOutput.CreateAsync`](../../Runtime/Native/UnityAudioSourceOutput.cs) unconditionally creates a hidden `GameObject` at the world origin (`HideFlags.HideAndDontSave`) with a fresh `AudioSource`. `AudioSource.spatialBlend` defaults to `0` (2D), so the host GameObject's position is irrelevant. There is no API on `ConversationOptions`, `Conversation`, or `OutputDeviceConfig` that lets a caller say *"play through **this** `AudioSource` instead."*

That blocks:

- **Spatialised voice from talking objects** — NPCs, animatronic props, holographic avatars, XR companions. The headline Unity-as-platform use case.
- **AudioMixer routing** — putting agent voice into a "Dialogue" mixer group with sidechain ducking against music/SFX.
- **Per-source volume curves** — falloff, occlusion-aware curves, custom rolloff modes.
- **Cohabitation with existing audio architecture** — a project that already routes everything through pooled `AudioSource`s, parented to specific transforms, with shared mixer groups, currently can't reuse any of that for the agent.

The component layer ([`agent-component.md`](./agent-component.md) §5) names the gap but sidesteps the Core API question of *how* to plumb the user's `AudioSource` down to `UnityAudioSourceOutput`. This plan answers that question.

## API surface

Preferred: a new nullable field on `ConversationOptions`.

```csharp
public sealed record ConversationOptions
{
    // ... existing fields ...

    /// <summary>
    /// Optional <see cref="AudioSource"/> to play agent audio through.
    /// When set, the native output controller binds to this source instead
    /// of creating its own hidden host GameObject — letting the caller
    /// control spatialisation, mixer routing, rolloff curves, and lifetime.
    /// </summary>
    /// <remarks>
    /// Native only. On WebGL (default mode) the option is silently ignored
    /// because audio flows JS-internal between the connection and the
    /// browser's audio output; Unity-routed WebGL is a v0.3 follow-up
    /// (see <see href="./initial-rfc.md">initial-rfc.md</see>).
    /// </remarks>
    public AudioSource? OutputAudioSource { get; init; }
}
```

`NativeSessionLauncher.StartAsync` forwards the option to a new overload `UnityAudioSourceOutput.CreateAsync(format, device, audioSource)`. `BridgedSessionLauncher` ignores it.

### Alternatives considered

- **Nest under `OutputDeviceConfig.AudioSource`.** Rejected: `OutputDeviceConfig` is for host-OS device selection (which speaker), a different concern from intra-Unity routing (which scene `AudioSource`). Conflating them muddles `ChangeOutputDevice`'s contract.
- **Post-construction `Conversation.SetOutputAudioSource(...)`.** Rejected: audio starts playing immediately after the handshake, so a setter would have to handle a switch-mid-playback case that has no real use, and any caller that knows the source at startup is forced through a less ergonomic two-step.
- **Factory parameter on `Conversation.StartSessionAsync`.** Rejected: would split the entry-point API (`(options)` vs `(options, audioSource)`); putting the new knob on `ConversationOptions` keeps a single options bag and matches the existing pattern for every other input.

## Implementation in `UnityAudioSourceOutput`

`StartAudioSource` becomes branch-aware. When `audioSource == null` (today's path), it creates the hidden host GameObject as before. When supplied:

- **Overwrites** (SDK-owned):
  - `clip` — must point at the streaming `AudioClip` the ring buffer feeds.
  - `loop = true` — required by `PCMReaderCallback` semantics; the ring is what stops, not the clip.
  - `volume` — managed by `SetVolume` and interrupt fades.
- **Preserves** (user-owned):
  - `spatialBlend`, `minDistance`, `maxDistance`, `rolloffMode`, custom rolloff `AnimationCurve`s.
  - `outputAudioMixerGroup`, `panStereo`, `dopplerLevel`, `spread`, `reverbZoneMix`.
  - `priority`, `bypassEffects`, `bypassListenerEffects`, `bypassReverbZones`.
  - Transform position/parent — the SDK does not reparent the `AudioSource`.

### Lifecycle

`Close()` distinguishes owned vs supplied:

- **Owned** (hidden host GameObject): destroyed as today.
- **Supplied**: SDK calls `Stop()`, sets `clip = null`, and restores the pre-session `volume` (captured at `StartAudioSource` time). The `AudioSource` and its GameObject are left intact for the user to reuse on the next session.

### Defensive handling

The user's `AudioSource` can be destroyed mid-session (scene unload, prefab swap, deliberate teardown). `PushAudio` and `Interrupt` check `_audioSource != null` (Unity's overloaded null check, which covers destroyed objects) before touching it; on the first miss, log a single warning (`"[ElevenLabs] OutputAudioSource was destroyed mid-session; audio output disabled."`) and no-op the rest of the session. The connection stays open — the caller can still send messages and receive events; only playback is suppressed.

## TalkingBox validation

With this primitive in place, `TalkingBox` becomes:

```csharp
[SerializeField] private AudioSource audioSource;
// ...
var options = new ConversationOptions
{
    AgentId = config.AgentId,
    OutputAudioSource = audioSource,
    DynamicVariables = new Dictionary<string, object>
    {
        ["color"] = color,
        ["mood"] = MoodString,
    },
};
activeConversation = await Conversation.StartSessionAsync(options);
```

The box prefab carries an `AudioSource` with `spatialBlend = 1`, `Linear Rolloff`, `Min Distance = 2`, `Max Distance = 20`, optionally routed to a `Dialogue` mixer group. Voice plays from the box's transform; the listener hears stereo panning and falloff as they walk past it. The existing `GetOutputVolume()`-driven scale pulse continues to work unchanged — it reads from the same analysis buffer regardless of which `AudioSource` is downstream.

## Test plan

Edit Mode (existing harness):

- `UnityAudioSourceOutput_CreatesOwnHost_WhenNoAudioSourceSupplied` — current behaviour, regression-locked.
- `UnityAudioSourceOutput_BindsToSuppliedAudioSource_WhenProvided` — assert `clip`, `loop`, `volume` are set; assert `spatialBlend`, `outputAudioMixerGroup`, transform parent are untouched.
- `UnityAudioSourceOutput_RestoresVolume_OnClose_WhenSourceSupplied` — capture pre-session volume, run `Close()`, assert restored.
- `UnityAudioSourceOutput_LogsAndNoOps_WhenSuppliedSourceDestroyedMidSession` — destroy the source between two `PushAudio` calls; assert one warning + no exception.

Standalone smoke ([`Samples/StandaloneSmokeTest`](../../Samples/StandaloneSmokeTest/)): extend with an optional second variant that supplies a pre-built `AudioSource` and asserts the same agent-response round trip succeeds. Confirms IL2CPP doesn't strip the new code path.

## Scope split

In scope here:

- `ConversationOptions.OutputAudioSource` field.
- `UnityAudioSourceOutput` branch-aware construction + `Close` behaviour.
- `NativeSessionLauncher` forwarding.
- Test coverage above.
- A note in `BridgedSessionLauncher` (and/or `BridgedOutputController`) that the field is intentionally ignored in default-mode WebGL.

Out of scope (deferred to v0.3, per [`initial-rfc.md`](./initial-rfc.md)):

- Unity-routed WebGL — feeding the supplied `AudioSource` from the JS audio path. Requires the `@elevenlabs/client` PCM intercept hook and the `AudioSink` interface work the RFC describes.
- Microphone-side equivalent (`InputAudioSource` / route mic capture *from* a Unity `AudioSource`). Different design (Unity microphone capture already happens on the C# side), parked until there's demand.
