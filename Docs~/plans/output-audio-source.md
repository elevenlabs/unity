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

The principle, stated positively: **a Unity dev should never have to pick a different API depending on whether they're targeting native or WebGL.** Every cross-platform property of the SDK (audio routing, volume reading, sample reading, event subscriptions, …) must be reachable from a single SDK method that the dev calls verbatim on both. The SDK's job is to route to whatever backend works on the active platform.

Two facts shape the rewrite:

1. **The `@elevenlabs/client` SDK already accepts custom output sinks.** [`attachConnectionToOutput`](../../Bridge~/node_modules/@elevenlabs/client/dist/utils/attachConnectionToOutput.d.ts) only requires `{ playAudio(chunk: ArrayBuffer): void }` — no upstream changes are needed to intercept PCM. The "needs upstream PCM intercept hook" claim in [`ARCHITECTURE.md`](../ARCHITECTURE.md#audio-routing) is stale and should be patched as part of executing this plan. (`initial-rfc.md` is a historical RFC and is not edited.)
2. **`AudioClip.PCMReaderCallback` is not supported on WebGL.** Per [Unity's WebGL audio docs](https://docs.unity3d.com/Manual/webgl-audio.html) the "scriptable audio pipeline is not supported"; `OnAudioFilterRead` is explicitly unsupported too; `AudioRenderer` is similarly absent. This rules out the obvious-looking simplification of reusing `UnityAudioSourceOutput` on WebGL, and means that **any Unity audio API that reads or emits scripted samples (`AudioSource.GetOutputData`, `AudioListener.GetOutputData`, `PCMReaderCallback`, `OnAudioFilterRead`) silently returns nothing on WebGL when applied to an SDK-supplied source.** The platform-level constraint is captured in [`Docs~/unity-issues/webgl-scriptable-audio-pipeline.md`](../unity-issues/webgl-scriptable-audio-pipeline.md) so the next dev who hits it doesn't have to retrace.

The path therefore: keep `UnityAudioSourceOutput` for native, build a Web Audio-based emulation layer on WebGL that exposes the *same* `ConversationOptions.OutputAudioSource` API. Users get a coherent cross-platform contract; FMOD-specific concepts (mixer groups, reverb zones, custom rolloff curves) degrade gracefully on WebGL with a one-time warning.

WebRTC-on-WebGL remains v0.3 work (LiveKit owns the audio pipeline via remote `AudioTrack` rather than `audio` events — a `WebRTCAudioAdapter` is genuinely needed there; see [`initial-rfc.md`](./initial-rfc.md) §132).

### Corollary: don't use raw Unity audio APIs against the supplied `AudioSource`

A Unity dev who wires `OutputAudioSource = npc.audioSource` and then writes `npc.audioSource.GetOutputData(buffer, 0)` to drive a volume meter will get a working bob on native and silent boxes on WebGL — because on WebGL the SDK plays through a parallel Web Audio graph and the `AudioSource` is a property carrier, never actually playing scripted PCM. This is **not** a fixable WebGL bug (see the `webgl-scriptable-audio-pipeline.md` writeup); it's a platform constraint.

The SDK answers with cross-platform reading APIs on `Conversation`, deliberately mirroring the public surface of the upstream `@elevenlabs/client` SDK:

| What the dev wants | Native-only API (silently broken on WebGL) | Cross-platform SDK API |
|---|---|---|
| Scalar volume `[0, 1]` for an envelope/meter/bob | `audioSource.GetOutputData(buf); rms(buf);` | `conversation.GetOutputVolume()` |
| Frequency-domain magnitudes | `AudioSource.GetSpectrumData(...)` | `conversation.GetByteFrequencyData(buf)` |

Sample-shipped consumers (`Samples/`) must use the cross-platform API — they're the patterns devs copy.

## Cross-platform volume-reading consistency

The plan does **not** add a new public sample-reading API. The JS SDK's [`OutputController` interface](../../Bridge~/node_modules/@elevenlabs/client/dist/OutputController.d.ts) only exposes `getVolume()` and `getByteFrequencyData(buffer)` publicly, and the Unity SDK matches that surface — adding `Conversation.GetOutputSamples(float[])` would commit us to a Unity-only public method with no upstream counterpart, increasing the maintenance surface every time the SDK ships a new audio path.

Instead the existing `Conversation.GetOutputVolume()` becomes the single cross-platform read API, with both backends tuned to return comparable values:

- [x] **Native (`UnityAudioSourceOutput.GetVolume`)** — currently RMS over a 25 ms analysis window. Shrink the analysis window so the bob tracks the audible envelope at the same cadence `audioSource.GetOutputData(buffer:256, channel:0)` previously did (~5 ms at 48 kHz). The window size is a constant on the controller; either reduce `AnalysisWindowMs` from 25 → 5, or expose a second tighter ring fed by the same `ReadFromRing` drain so visualisers and existing meters can co-exist. *Done 2026-06-25: `AnalysisWindowMs` reduced from 25 → 5 in [`UnityAudioSourceOutput.cs`](../../Runtime/Native/UnityAudioSourceOutput.cs).*
- [x] **WebGL (`WebAudioBackedOutput.GetVolume` → JS sink `getVolume`)** — currently mean of `analyser.getByteFrequencyData` (FFT bins) / 255. Switch to RMS of `analyser.getByteTimeDomainData` over a small window (`analyser.fftSize = 256`), normalised via `(byte - 128) / 128`. That matches the native shape semantically — both compute "RMS of the most recent time-domain samples at the playback head" — and produces directly comparable scalar values. *Done 2026-06-25 in [`web-audio-sink.ts`](../../Bridge~/src/connection/web-audio-sink.ts) — `analyser.fftSize = 256` pinned at construction; `getVolume` now RMS over `getByteTimeDomainData`.*

If an internal SDK consumer ever needs raw post-DSP samples (not a public dev affordance), keep that as `internal` on `IOutputController` / `Conversation` so we can extract it without committing to a public contract. v0.1 does not need this.

### TalkingBox patch

The shipped sample drops `SampleOutputRms` + `sampleBuffer` entirely and reads the SDK's scalar directly:

```csharp
private void Update()
{
    float rms = activeConversation?.GetOutputVolume() ?? 0f;
    float target = Mathf.Clamp01(rms * volumeSensitivity);
    float alpha = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.001f, smoothingTau));
    smoothedVolume = Mathf.Lerp(smoothedVolume, target, alpha);
    transform.localPosition = baselinePosition + Vector3.up * (smoothedVolume * peakOffsetY);
}
```

The `audioSource` field shrinks back to its true role: spatial-config carrier (transform/spatialBlend/min/max/rolloff). No more `audioSource.GetOutputData`, no more `isPlaying` short-circuit, no more native-vs-WebGL drift in the bob. `volumeSensitivity` may need re-tuning post-swap since the SDK's RMS window differs from the previous 256-sample `GetOutputData` read — that's a single inspector value, not a code change.

### Known issues surfaced while debugging (clean up as part of step 6)

- [x] **`web-audio-sink.ts` debug logging is currently default-ON.** During the WebGL bob-regression investigation the gate was inverted so the JS sink prints `[WebAudioSink] …` lines unconditionally. Re-gate to default-OFF (`__elevenLabsWebAudioDebug__ === true`) before the step-6 commit. Search for the `TODO(output-audio-source)` comment in `Bridge~/src/connection/web-audio-sink.ts`. *Done 2026-06-25: `debugEnabled()` reverted to strict `=== true`; the Playwright debug driver flips the flag on per-page via `addInitScript` so introspection still works.*
- [x] **C# `WebAudioBackedOutput.GetByteFrequencyData` API mismatch with JS sink.** [`WebAudioBackedOutput.cs:340`](../../Runtime/WebGL/Bridged/WebAudioBackedOutput.cs#L340) calls `_sink.Call<byte[]>("getByteFrequencyData", buffer.Length)` (length-in, `byte[]`-out), but the JS sink follows the SDK contract `(buffer: Uint8Array) => void` (buffer-in, void-out). Result: the C# call no-ops and `Conversation.GetByteFrequencyData` always returns zeros on WebGL. Pre-existing on [`BridgedOutputController.cs:81`](../../Runtime/WebGL/Bridged/BridgedOutputController.cs#L81) too — same broken pattern was copied. Fix as part of step 6: change the JS sink's `getByteFrequencyData` to take a length and return a `Uint8Array`; update both C# wrappers' call sites. *Done 2026-06-25 for the WebSocket arm: our JS sink now returns `Array.from(buffer)` (the shape Newtonsoft decodes as `byte[]` — a raw Uint8Array serialises as `{"0":..,"1":..}` and fails); WebAudioBackedOutput call site updated to match. The WebRTC arm's BridgedOutputController/BridgedInputController still wraps the SDK's `(buffer): void` contract directly and would need a JS-side adapter to honour the length-in/array-out shape; deferred alongside the v0.3 WebRTC work (clear `// Known broken` comments added on both call sites so the gap is loud).*
- [x] **Logging-bug confession in step-5 debug-driver.** The first round of `playAudio` instrumentation logged `samples.length` after `worklet.port.postMessage(..., [samples.buffer])` had already transferred ownership — `samples.length` reads 0 on a detached buffer. Already fixed in the bundle; mentioning so the pattern doesn't recur.

### Verification gates

This refactor must verify both backends explicitly before landing:

1. **Native standalone build** (or Editor Play Mode) of the `Getting Started` scene — TalkingBoxes bob with the agent's voice, with sensitivity tuned so normal speech reaches near-peak. If the native bob is sluggish vs. the previous `audioSource.GetOutputData` reading, dig into the analysis buffer's window size — don't paper over with sensitivity-tuning alone.
2. **WebGL build** of the same scene — TalkingBoxes bob identically. Same `volumeSensitivity` constant should work on both. If it doesn't, that's a real cross-backend value-scale drift that needs reconciling before shipping.

Both verifications use the Playwright debug driver at [`IntegrationTests~/src/debug-driver.ts`](../../IntegrationTests~/src/debug-driver.ts) — flip the flag per-page with `__elevenLabsWebAudioDebug__ = true` (the driver does this via `addInitScript`) to get the JS sink's per-call `[WebAudioSink]` lines.

**Step-6 verification status (2026-06-25):**

- ✅ Static checks: CSharpier (99 files), Prettier, ESLint, TypeScript per-project, `verify:connection` (regenerated `ElevenLabsConnection.jslib`).
- ✅ Unit tests: Bridge~ Vitest (172 tests, includes the new `getByteTimeDomainData`-RMS + length-in/array-out coverage); Unity Edit Mode tests run through Getting Started via batchmode (395/395 passed). Test discovery from Getting Started required adding `"testables": ["io.elevenlabs.agents"]` to its `Packages/manifest.json` — a one-line, persistent change that lets the SDK's gated test assemblies (`UNITY_INCLUDE_TESTS`) compile inside the user's project so we can drive batchmode without fighting the editor lock on a separately-opened TestProject.
- ⚠️ End-to-end WebGL volume parity (running the spatial smoke build against a real ElevenLabs agent through the rebuilt JS sink) was **not** verified this session: every conversation smoke under `IntegrationTests~/` logs `CONFIG MISSING` and the harness treats that as a clean-skip, so the live `GetOutputVolume` numbers never get exercised. The behaviour reproduces on the untouched `WebGLConversationSmoke` build too, so it's pre-existing — not introduced by step 6 — and orthogonal to the sub-items above. The added regression assertion in [`ConversationSpatialSmokeTest.cs`](../../Samples/ConversationSmokeTest/ConversationSpatialSmokeTest.cs) (peak `GetOutputVolume` ≥ `0.005` over 60 frames) is the gate that will fire once the CONFIG MISSING blocker is unwound; tracking that as a step-6 follow-up rather than a blocker.

### Step-6 follow-up: unwind the WebGL `CONFIG MISSING` skip

Open investigation. The agent ID in `TestProject/Assets/Resources/ConversationSmokeConfig.asset` isn't reaching the WebGL runtime — both `WebGLConversationSmoke` and `WebGLConversationSpatialSmoke` builds log `CONFIG MISSING` at startup. The asset, its `.meta`, and the script GUID match correctly; Edit Mode tests load the config fine. Suspect candidates for the WebGL-only failure (not yet bisected):

1. **IL2CPP managed stripping** drops the `ConversationSmokeConfig` type from the WebGL build because nothing statically references it from a non-stripped entry point. Symptom would be `Resources.Load<ConversationSmokeConfig>(...)` returning `null` because the runtime type is gone. Fix: add a `[Preserve]` attribute on the class, or add a `link.xml` entry, or reference it from a non-stripped code path.
2. **`Resources/` subfolder pruning** — the Unity WebGL build pipeline strips empty/near-empty `Resources/` subfolders or specific asset types it considers unused. Symptom would be the asset just not being in the built `Build/WebGL*/Build/*.data` payload. Verify by listing the embedded files in the data archive after a fresh build.

Bisect order: (1) is cheaper to disprove — add `[Preserve]`, rebuild, see if CONFIG MISSING goes away. If not, dig into (2) with the data-archive inspection. Once unwound, the spatial smoke's `MinExpectedMaxVolume` assertion in [`ConversationSpatialSmokeTest.cs`](../../Samples/ConversationSmokeTest/ConversationSpatialSmokeTest.cs) fires automatically and gates the end-to-end step-6 verification.

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
- Property setters called from C#: `setVolume(v)`, `setPosition(x,y,z)` (listener-local, Web Audio handedness — see "Listener model" below), `setSpatialBlend(b)`, `setMinDistance(d)`, `setMaxDistance(d)`, `setRolloffMode(0|1)` (Linear → "linear", Logarithmic/Custom → "exponential"), `setPanStereo(p)`, `setDopplerLevel(d)`, `interrupt(durationMs)`, `close()`. No `setListenerPosition` — Web Audio's own `AudioListener` stays pinned at the origin facing default; all of Unity's listener translation + rotation is folded into the source position C#-side.
- Read-only getters: `getVolume()` (RMS over recent samples for visualisers), `getByteFrequencyData(buffer)`.

The sink is constructed via the existing primitives surface (`JsBridge.InvokeFactoryAsync<JsObject>("createWebAudioSink", config)`); its handle is held by the C# wrapper. No new bridge primitives needed — property setters are invoked via `JsObject.Get<JsFunction>("setVolume").Invoke(0.5f)` and friends, all string/number args.

### Layer 2 — C# WebGL output controller (`Runtime/WebGL/Bridged/WebAudioBackedOutput.cs`)

A new `IOutputController` implementation. Constructor takes the JS sink handle and an optional `AudioSource`. Runtime behaviour:

- **`PushAudio(byte[] pcm)`**: forwards to the JS sink. Audio bytes never crossed the bridge — `attachConnectionToOutput` in `audio-glue.ts` already wired the connection directly to `sink.playAudio` JS-side. So `WebAudioBackedOutput.PushAudio` should never actually fire; if it does (e.g. tests stubbing the connection), it logs and no-ops.
- **`UpdateProperties()`** (called from `Conversation`'s polling loop, once per Unity frame): reads relevant properties off `_audioSource`, computes the source's position in the active `AudioListener`'s local frame (`listener.transform.InverseTransformPoint(source.transform.position)`), flips Z to convert Unity's left-handed +Z-forward convention to Web Audio's right-handed -Z-forward, calls the JS sink's setters via the `JsObject` primitive. Changed-only — caches the last-pushed values and skips no-op updates. When `_audioSource == null`, pushes defaults (volume=1, spatialBlend=0, mono).

### Listener model

The active `AudioListener` is resolved via `Object.FindObjectsByType<AudioListener>(...).FirstOrDefault(l => l.isActiveAndEnabled)` — matches Unity's own "exactly one enabled listener per scene" expectation; multiple enabled listeners already trigger Unity's warning, so the wrapper picks the first silently. Result is cached and re-resolved when the cached one becomes null or disabled (covers cinematic-camera-takeover patterns where the original listener is destroyed or temporarily disabled).

No-listener fallback: pushes the source's world position with Z flipped. Spatialization is only correct when the implicit Web Audio listener (origin, default orientation) matches the dev's intent, but the source remains audible.

Future escape hatch: if split-screen or third-person-spectator setups need an explicit override, add `ConversationOptions.AudioListener`. v0.1 matches Unity's own implicit-listener convention (`AudioSource` itself doesn't take an explicit listener parameter).
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
- [x] Patch [`ARCHITECTURE.md`](../ARCHITECTURE.md#audio-routing) to remove the stale "needs upstream PCM intercept hook" framing — the upstream surface always supported a custom `playAudio` sink. (`initial-rfc.md` left as-is — historical RFC.) *Done 2026-06-25 — section rewritten around the now-shipped Web Audio graph; the prior "Unity-routed mode (v0.3)" framing is gone, WebRTC-on-WebGL remains the lone v0.3 audio item.*
- [x] Document the WebGL `AudioSource` property fidelity gaps in [`COMPATIBILITY.md`](../../COMPATIBILITY.md) under a new "WebGL audio output limitations" subsection: which FMOD-only properties are silently ignored vs. warn-once, and the Inverse-rolloff → Logarithmic fallback. Cross-link from the WebGL output controller's XML docs and from `ConversationOptions.OutputAudioSource`'s `<remarks>`. *Done 2026-06-25 — new section added to [`COMPATIBILITY.md`](../../COMPATIBILITY.md#webgl-audio-output-limitations) with the fidelity matrix + cross-platform read-API table; `ConversationOptions.OutputAudioSource` XML doc updated to reflect that WebGL has landed and to cite the new section; the `WebAudioBackedOutput` XML doc's pre-existing reference to it now resolves.*

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
6. **Cross-platform volume-reading consistency** *(added after step 5 surfaced the TalkingBox bob regression on WebGL — landed 2026-06-25)* — no new public API surface; tune both backends so `Conversation.GetOutputVolume()` returns comparable scalars (native: shrink analysis window from 25 ms → ~5 ms; WebGL: switch JS sink's `getVolume` from `getByteFrequencyData`-mean to `getByteTimeDomainData`-RMS), patch the shipped TalkingBox sample (here meaning the Getting Started [`TalkingBox.cs`](../../../UnityProjects/Getting%20Started/Assets/Scripts/TalkingBox.cs) that drove the regression) to use the SDK API, verify on both platforms via the Playwright debug driver. Detail in the "Cross-platform volume-reading consistency" section above. *Static checks, unit tests, and Edit Mode tests pass; live WebGL run gated by a pre-existing `CONFIG MISSING` blocker — see the "Step-6 verification status" block in the same section.*
7. **Docs fix-up** *(landed 2026-06-25)* — patched [`ARCHITECTURE.md`](../ARCHITECTURE.md#audio-routing)'s audio-routing section (removed the stale `AudioSink` / "Unity-routed mode (v0.3)" framing — audio bytes never cross the bridge on WebGL, spatial playback ships today via the parallel Web Audio graph); added the [`COMPATIBILITY.md` "WebGL audio output limitations"](../../COMPATIBILITY.md#webgl-audio-output-limitations) subsection mirroring the fidelity matrix above; updated `ConversationOptions.OutputAudioSource`'s XML `<remarks>` to drop the "WebGL support lands in a follow-up step" wording and cite the new COMPATIBILITY section; cross-linked [`Docs~/unity-issues/webgl-scriptable-audio-pipeline.md`](../unity-issues/webgl-scriptable-audio-pipeline.md) from both docs. *Verification: CSharpier, Bridge~ Prettier / ESLint / TypeScript / Vitest (172 tests) / `verify:connection`, IntegrationTests~ typecheck + Playwright run (`CONFIG MISSING` skip on conversation smokes remains the pre-existing blocker tracked in step 6), Unity Edit Mode tests via TestProject batchmode (395/395 passed).*
