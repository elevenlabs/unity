# `ConversationOptions.OutputAudioSource` — bring-your-own AudioSource

**Status:** Approved, 2026-06-24
**Driver:** The Getting-Started `TalkingBox` demo plays its voice with `spatialBlend = 0` — omnipresent (2D) playback regardless of where the box sits in the scene. A listener walking past a row of talking boxes hears every active one at full volume from nowhere in particular. The same gap will hit every consumer who wants positional voice, AudioMixer routing, or volume-curve control: the SDK owns the `AudioSource` and doesn't let go.
**Assumes:** Native transport (`#9d` in [v0.1-parity.md](./v0.1-parity.md)) is shipped; `UnityAudioSourceOutput` is the canonical native output controller; the bridge primitives in `Bridge~/src/connection/` are stable.
**Consumed by:** the `agentAudioSource` Inspector field in [`agent-component.md`](./agent-component.md) §5 — but this primitive stands on its own; low-level `Conversation.StartSessionAsync` users get the same benefit without adopting the component.

---

## Why this exists

Today [`UnityAudioSourceOutput.CreateAsync`](../../Runtime/Native/UnityAudioSourceOutput.cs) unconditionally creates a hidden `GameObject` at the world origin (`HideFlags.HideAndDontSave`) with a fresh `AudioSource`. `AudioSource.spatialBlend` defaults to `0` (2D), so the host GameObject's position is irrelevant. There is no API on `ConversationOptions`, `Conversation`, or `OutputDeviceConfig` that lets a caller say *"play through **this** `AudioSource` instead."*

That blocks:

- **Spatialised voice from talking objects** — NPCs, animatronic props, holographic avatars, XR companions. The headline Unity-as-platform use case.
- **AudioMixer routing** — putting agent voice into a "Dialogue" mixer group with sidechain ducking against music/SFX. *(Native only — Web Audio has no equivalent.)*
- **Per-source volume curves** — falloff, occlusion-aware curves, custom rolloff modes.
- **Cohabitation with existing audio architecture** — a project that already routes everything through pooled `AudioSource`s, parented to specific transforms, with shared mixer groups, currently can't reuse any of that for the agent.

The component layer ([`agent-component.md`](./agent-component.md) §5) names the gap but sidesteps the Core API question of *how* to plumb the user's `AudioSource` down to the platform-specific output controller. This plan answers that question — and answers it *consistently across native + WebGL*, not just native.

## Cross-platform consistency principle

The earlier draft of this plan treated WebGL as "field silently ignored, deferred to v0.3" on the basis that the JS SDK kept audio entirely JS-side. That was a footgun: a Unity dev wires a 3D `AudioSource` on an NPC, ships a WebGL build, and discovers in QA that spatialisation silently doesn't work.

Two facts shape the rewrite:

1. **The `@elevenlabs/client` SDK already accepts custom output sinks.** [`attachConnectionToOutput`](../../Bridge~/node_modules/@elevenlabs/client/dist/utils/attachConnectionToOutput.d.ts) only requires `{ playAudio(chunk: ArrayBuffer): void }` — no upstream changes are needed to intercept PCM. The "needs upstream PCM intercept hook" claim in [`ARCHITECTURE.md`](../ARCHITECTURE.md#audio-routing) is stale and should be patched as part of executing this plan. (`initial-rfc.md` is a historical RFC and is not edited.)
2. **`AudioClip.PCMReaderCallback` is not supported on WebGL.** [Unity's WebGL audio docs](https://docs.unity3d.com/Manual/webgl-audio.html) state that `AudioClip.Create` only works with `stream: false`; the "scriptable audio pipeline is not supported." This rules out the obvious-looking simplification of reusing `UnityAudioSourceOutput` on WebGL.

The path therefore: keep `UnityAudioSourceOutput` for native, build a Web Audio-based emulation layer on WebGL that exposes the *same* `ConversationOptions.OutputAudioSource` API. Users get a coherent cross-platform contract; FMOD-specific concepts (mixer groups, reverb zones, custom rolloff curves) degrade gracefully on WebGL with a one-time warning.

WebRTC-on-WebGL remains v0.3 work (LiveKit owns the audio pipeline via remote `AudioTrack` rather than `audio` events — a `WebRTCAudioAdapter` is genuinely needed there; see [`initial-rfc.md`](./initial-rfc.md) §132).

## API surface

A new nullable field on `ConversationOptions`.

```csharp
public sealed record ConversationOptions
{
    // ... existing fields ...

    /// <summary>
    /// Optional <see cref="AudioSource"/> to play agent audio through.
    /// When set, the platform output controller binds to this source instead
    /// of creating its own hidden host GameObject — letting the caller
    /// control spatialisation, mixer routing, rolloff curves, and lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Honored on native (WebSocket transport) and WebGL (WebSocket transport
    /// only — WebRTC's audio path goes through LiveKit and ignores the field
    /// until the v0.3 <c>WebRTCAudioAdapter</c> lands).
    /// </para>
    /// <para>
    /// On WebGL the binding goes through a Web Audio emulation layer
    /// (<c>WebAudioBackedOutput</c>) that mirrors a curated subset of
    /// <see cref="AudioSource"/> properties — volume, transform position,
    /// spatial blend, min/max distance, rolloff mode, panStereo. FMOD-only
    /// concepts (<see cref="AudioMixerGroup"/>, reverb zones, custom rolloff
    /// <see cref="AnimationCurve"/>s, effect bypass) emit a one-time warning
    /// the first time they're observed as non-default and degrade to the
    /// nearest Web Audio approximation.
    /// </para>
    /// </remarks>
    public AudioSource? OutputAudioSource { get; init; }
}
```

`NativeSessionLauncher.StartAsync` forwards the option to a new overload `UnityAudioSourceOutput.CreateAsync(format, device, audioSource)`. `BridgedSessionLauncher` (WebSocket arm) forwards it to `BridgedSession.StartWebSocketAsync` which constructs a `WebAudioBackedOutput`. WebRTC arm ignores it (with the one-time warning above) until the LiveKit adapter ships.

### Alternatives considered

- **Nest under `OutputDeviceConfig.AudioSource`.** Rejected: `OutputDeviceConfig` is for host-OS device selection (which speaker), a different concern from intra-Unity routing (which scene `AudioSource`). Conflating them muddles `ChangeOutputDevice`'s contract.
- **Post-construction `Conversation.SetOutputAudioSource(...)`.** Rejected: audio starts playing immediately after the handshake, so a setter would have to handle a switch-mid-playback case that has no real use, and any caller that knows the source at startup is forced through a less ergonomic two-step.
- **Factory parameter on `Conversation.StartSessionAsync`.** Rejected: would split the entry-point API (`(options)` vs `(options, audioSource)`); putting the new knob on `ConversationOptions` keeps a single options bag and matches the existing pattern for every other input.
- **Native-only field, defer WebGL entirely to v0.3.** Rejected — that's the footgun this rewrite exists to avoid. See "Cross-platform consistency principle" above.

## Native implementation (`UnityAudioSourceOutput`)

`StartAudioSource` becomes branch-aware. When `audioSource == null` (today's path), it creates the hidden host GameObject as before. When supplied:

- **Overwrites** (SDK-owned; captured at bind time for restoration on `Close`):
  - `clip` — must point at the streaming `AudioClip` the ring buffer feeds.
  - `loop` — set to `true`; required by `PCMReaderCallback` semantics. The ring is what stops, not the clip.
  - `volume` — managed by `SetVolume` and interrupt fades.
- **Preserves** (user-owned, untouched at bind and at teardown):
  - `spatialBlend`, `minDistance`, `maxDistance`, `rolloffMode`, custom rolloff `AnimationCurve`s.
  - `outputAudioMixerGroup`, `panStereo`, `dopplerLevel`, `spread`, `reverbZoneMix`.
  - `priority`, `bypassEffects`, `bypassListenerEffects`, `bypassReverbZones`.
  - Transform position/parent — the SDK does not reparent the `AudioSource`.

### Lifecycle

`Close()` distinguishes owned vs supplied:

- **Owned** (hidden host GameObject): destroyed as today.
- **Supplied**: SDK calls `Stop()`, sets `clip = null`, restores the pre-session `volume` *and* `loop` (both captured at `StartAudioSource` time — symmetric treatment of every SDK-owned overwrite). The `AudioSource` and its GameObject are left intact for the user to reuse on the next session.

### Defensive handling

The user's `AudioSource` can be destroyed mid-session (scene unload, prefab swap, deliberate teardown). `PushAudio` and `Interrupt` check `_audioSource != null` (Unity's overloaded null check, which covers destroyed objects) before touching it; on the first miss, log a single warning (`"[ElevenLabs] OutputAudioSource was destroyed mid-session; audio output disabled."`) and no-op the rest of the session. The connection stays open — the caller can still send messages and receive events; only playback is suppressed.

## WebGL implementation (`WebAudioBackedOutput` + JS sink)

Three layers introduced together; each replaces or extends an existing piece.

### Layer 1 — JS-side Web Audio sink (`Bridge~/src/connection/web-audio-sink.ts`)

A new factory `createWebAudioSink(config)` registered alongside the existing connection factories. Builds a Web Audio graph:

```
AudioWorkletNode (PCM intake, ring-buffered)
  → GainNode (volume)
  → splitter
    → PannerNode (3D position, only when spatialBlend > 0)
    → StereoPannerNode (panStereo, only when spatialBlend < 1)
  → AudioContext.destination
```

Exposes:

- `playAudio(chunk: ArrayBuffer): void` — satisfies the [`attachConnectionToOutput`](../../Bridge~/node_modules/@elevenlabs/client/dist/utils/attachConnectionToOutput.d.ts) contract so the existing JS-side wiring in [`audio-glue.ts`](../../Bridge~/src/connection/audio-glue.ts) still works. Audio bytes flow connection → sink entirely JS-side; **the bridge does not carry PCM payloads.**
- Property setters called from C#: `setVolume(v)`, `setPosition(x,y,z)`, `setListenerPosition(x,y,z)` + orientation, `setSpatialBlend(b)`, `setMinDistance(d)`, `setMaxDistance(d)`, `setRolloffMode(0|1)` (Linear / Logarithmic — Inverse not supported; Web Audio's `PannerNode.distanceModel` covers both), `setPanStereo(p)`, `setDopplerLevel(d)`, `interrupt(durationMs)`, `close()`.
- Read-only getters: `getVolume()` (RMS over recent samples for visualisers), `getByteFrequencyData(buffer)`.

The sink is constructed via the existing primitives surface (`JsBridge.InvokeFactoryAsync<JsObject>("createWebAudioSink", config)`); its handle is held by the C# wrapper. No new bridge primitives needed — property setters are invoked via `JsObject.Get<JsFunction>("setVolume").Invoke(0.5f)` and friends, all string/number args.

### Layer 2 — C# WebGL output controller (`Runtime/WebGL/Bridged/WebAudioBackedOutput.cs`)

A new `IOutputController` implementation. Constructor takes the JS sink handle and an optional `AudioSource`. Runtime behaviour:

- **`PushAudio(byte[] pcm)`**: forwards to the JS sink. Audio bytes never crossed the bridge — `attachConnectionToOutput` in `audio-glue.ts` already wired the connection directly to `sink.playAudio` JS-side. So `WebAudioBackedOutput.PushAudio` should never actually fire; if it does (e.g. tests stubbing the connection), it logs and no-ops.
- **`UpdateProperties()`** (called from `Conversation`'s polling loop, once per Unity frame): reads relevant properties off `_audioSource` and the active `AudioListener`, computes listener-relative position, calls the JS sink's setters via the `JsObject` primitive. Changed-only — caches the last-pushed values and skips no-op updates. When `_audioSource == null`, pushes defaults (volume=1, spatialBlend=0, mono).
- **`SetVolume`, `Interrupt`, `Close`**: forwarded to the JS sink.

`Close` follows the same supplied-vs-owned distinction as native, but on WebGL there's no "owned" path — if `OutputAudioSource` is null we just don't poll properties; the sink stays mono-omnidirectional. No teardown of user GameObjects, ever.

### Layer 3 — Updated `BridgedSession.StartWebSocketAsync` wiring

Replace `createMediaDeviceOutput` with `createWebAudioSink`. Drop the `MediaDeviceOutput` factory registration from [`factories.ts`](../../Bridge~/src/connection/factories.ts). [`audio-glue.ts`](../../Bridge~/src/connection/audio-glue.ts) and `withoutAudioPayload` stay — they're correct for the new sink too (audio bytes still don't cross the bridge; only property updates do, in the other direction).

[`BridgedOutputController`](../../Runtime/WebGL/Bridged/BridgedOutputController.cs) becomes a thin compatibility shim for the WebRTC arm only, or gets folded into `BridgedWebRTCConnection.GetCoupledOutput()` directly. Decision deferred to execution.

### `AudioSource` properties honored on WebGL

| Property | Web Audio mapping | Notes |
|---|---|---|
| `volume` | `GainNode.gain` | Direct |
| `transform.position` | `PannerNode.positionX/Y/Z` | Computed relative to active `AudioListener` |
| `spatialBlend` | Wet/dry mix of PannerNode output vs mono | Web Audio analog |
| `minDistance` | `PannerNode.refDistance` | Direct |
| `maxDistance` | `PannerNode.maxDistance` | Direct |
| `rolloffMode` (Linear/Logarithmic) | `PannerNode.distanceModel` | Inverse rolloff → warning + falls back to Logarithmic |
| `panStereo` | `StereoPannerNode.pan` | Effective when `spatialBlend < 1` |
| `dopplerLevel` | Sampled per-frame velocity → PannerNode position pre-emphasis | Approximate |
| `outputAudioMixerGroup` | ❌ Not supported | One-time warning when first observed as non-null |
| Custom rolloff `AnimationCurve` | ❌ Not supported on WebGL | One-time warning; falls back to `rolloffMode` |
| `bypassEffects`, `bypassListenerEffects`, `bypassReverbZones` | ❌ Not supported | Silently ignored — no Web Audio analog |
| `priority`, `spread`, `reverbZoneMix` | ❌ Not supported | Silently ignored |

Warning text format: `"[ElevenLabs] AudioSource.{Property} is set on a supplied OutputAudioSource but isn't supported on WebGL (FMOD-only concept). Ignoring."` — fired once per session per offending property to avoid log spam.

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

The box prefab carries an `AudioSource` with `spatialBlend = 1`, `Linear Rolloff`, `Min Distance = 2`, `Max Distance = 20`. **On native, voice plays from the box's transform with full FMOD fidelity** (including any `AudioMixerGroup` routing). **On WebGL, voice plays from the box's transform via Web Audio's `PannerNode`** with the same min/max/rolloff falloff curve — mixer routing is dropped with a one-time log warning. The listener hears stereo panning and falloff as they walk past the box, on both platforms. The existing `GetOutputVolume()`-driven scale pulse continues to work unchanged — it reads from the analysis buffer regardless of which backend is downstream.

## Test plan

### Native — Edit Mode (existing harness)

- `UnityAudioSourceOutput_CreatesOwnHost_WhenNoAudioSourceSupplied` — current behaviour, regression-locked.
- `UnityAudioSourceOutput_BindsToSuppliedAudioSource_WhenProvided` — assert `clip`, `loop`, `volume` are set; assert `spatialBlend`, `outputAudioMixerGroup`, transform parent are untouched.
- `UnityAudioSourceOutput_RestoresVolumeAndLoop_OnClose_WhenSourceSupplied` — capture pre-session `volume` and `loop`, run `Close()`, assert both restored.
- `UnityAudioSourceOutput_LogsAndNoOps_WhenSuppliedSourceDestroyedMidSession` — destroy the source between two `PushAudio` calls; assert one warning + no exception.

### WebGL — Bridge~ unit tests (Vitest under `Bridge~/src/connection/web-audio-sink.test.ts`)

- `createWebAudioSink_BuildsExpectedGraph` — assert AudioContext + worklet + GainNode + PannerNode wiring (use jsdom + a stub AudioContext).
- `setVolume_UpdatesGainNode` — call setter, assert `GainNode.gain.value` updated.
- `setPosition_UpdatesPannerNode` — assert `positionX/Y/Z` updated.
- `setSpatialBlend_MixesMonoAndSpatialOutputs` — assert routing change at `spatialBlend = 0` / `0.5` / `1`.
- `setRolloffMode_MapsCorrectly` — Linear → `"linear"`, Logarithmic → `"exponential"` (Web Audio's nearest analog).
- `playAudio_FeedsWorkletRingBuffer` — push a chunk, assert worklet receives it.
- `interrupt_FadesGainOverDuration` — assert gain ramps.

### WebGL — C# Edit Mode tests (`Tests/Editor/WebGL/Bridged/WebAudioBackedOutputTests.cs`)

- `WebAudioBackedOutput_ForwardsPushAudioToSink` — mock `JsObject`, assert `playAudio` invoked.
- `WebAudioBackedOutput_PollsAudioSourceProperties_OncePerFrame` — drive `LateUpdate`, assert setters invoked with current values.
- `WebAudioBackedOutput_SkipsRedundantPropertyUpdates` — set property to same value twice, assert second invocation skipped.
- `WebAudioBackedOutput_DefaultsWhenNoAudioSource` — instantiate without `OutputAudioSource`, assert volume=1, spatialBlend=0 pushed once at init.
- `WebAudioBackedOutput_WarnsOnceForUnsupportedMixerGroup` — set `outputAudioMixerGroup`, assert single warning.

### WebGL — browser integration (existing harness under `IntegrationTests~/`)

Extend the conversation smoke to additionally:

- Build a variant of `Samples/ConversationSmokeTest/` that supplies an `AudioSource` with `spatialBlend = 1`, `transform.position = (3, 0, 0)`.
- Drive the existing Playwright harness; assert via `page.evaluate` that the running Web Audio graph contains a `PannerNode` with `positionX.value == 3`.
- Assert via the WebSocket-frame observer that audio frames still arrive with `audio_base_64` stripped (no perf regression).

### Standalone smoke ([`Samples/StandaloneSmokeTest`](../../Samples/StandaloneSmokeTest/))

Extend with an optional second variant that supplies a pre-built `AudioSource` and asserts the same agent-response round trip succeeds. Confirms IL2CPP doesn't strip the new code path on standalone targets.

## Scope split

### In scope

- `ConversationOptions.OutputAudioSource` field.
- Native: `UnityAudioSourceOutput` branch-aware construction + `Close` behaviour (capture/restore `volume` and `loop`).
- Native: `NativeSessionLauncher` forwarding.
- WebGL: new `Bridge~/src/connection/web-audio-sink.ts` factory + AudioWorklet + Web Audio graph.
- WebGL: new `Runtime/WebGL/Bridged/WebAudioBackedOutput.cs` `IOutputController` implementation.
- WebGL: `BridgedSession.StartWebSocketAsync` wiring change (`createWebAudioSink` replaces `createMediaDeviceOutput`).
- WebGL: removal of the `MediaDeviceOutput` factory registration for the WebSocket arm.
- WebGL: per-frame `AudioSource` property polling + listener-relative position math.
- A note in `BridgedWebRTCConnection.GetCoupledOutput()` that `OutputAudioSource` is intentionally ignored on the WebRTC arm until v0.3.
- One-time warnings for FMOD-only properties on the WebGL path.
- Test coverage across native Edit Mode, Bridge~ Vitest, C# Edit Mode, and `IntegrationTests~` browser harness.
- Patch [`ARCHITECTURE.md`](../ARCHITECTURE.md#audio-routing) to remove the stale "needs upstream PCM intercept hook" framing — the upstream surface always supported a custom `playAudio` sink. (`initial-rfc.md` left as-is — historical RFC.)
- Document the WebGL `AudioSource` property fidelity gaps in [`COMPATIBILITY.md`](../../COMPATIBILITY.md) under a new "WebGL audio output limitations" subsection: which FMOD-only properties are silently ignored vs. warn-once, and the Inverse-rolloff → Logarithmic fallback. Cross-link from the WebGL output controller's XML docs and from `ConversationOptions.OutputAudioSource`'s `<remarks>`.

### Out of scope (deferred)

- WebRTC-on-WebGL — feeding the supplied `AudioSource` from a LiveKit `RemoteAudioTrack`. Requires the `WebRTCAudioAdapter` work described in [`initial-rfc.md`](./initial-rfc.md) §132. v0.3.
- Microphone-side equivalent (`InputAudioSource` / route mic capture *from* a Unity `AudioSource`). Different design (Unity microphone capture already happens on the C# side), parked until there's demand.
- Full AudioMixer parity on WebGL. Would require a Web Audio mixer-group emulation layer with limited demand evidence. v0.3+ if at all.
- Custom rolloff `AnimationCurve` support on WebGL. Doable by sampling the curve C#-side and pushing a LUT to a custom worklet processor; deferred until a user asks.

## Execution sequencing

Order matters; each step is independently committable.

1. **Native first** — `UnityAudioSourceOutput` branch + `ConversationOptions` field + `NativeSessionLauncher` forwarding + native Edit Mode tests. Lands cleanly without touching WebGL; gives us a working API to validate the field shape against.
2. **JS sink** — `web-audio-sink.ts` + Vitest coverage. Standalone, can be built and tested in isolation before C# wiring lands.
3. **C# `WebAudioBackedOutput`** — wraps the sink, holds the `AudioSource`, owns the property-polling loop. C# Edit Mode tests using a mock `JsObject`.
4. **`BridgedSession` integration** — swap `createMediaDeviceOutput` for `createWebAudioSink`; delete the now-unused factory registration; update any tests that referenced the old factory.
5. **Integration smoke** — extend `Samples/ConversationSmokeTest/` and `IntegrationTests~/` to assert spatial properties round-trip.
6. **Docs fix-up** — patch `ARCHITECTURE.md` audio-routing section + add a "WebGL audio output limitations" subsection to `COMPATIBILITY.md` mirroring the fidelity matrix above.
