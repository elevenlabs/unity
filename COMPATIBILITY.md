# Compatibility

Hard requirements for using the ElevenAgents SDK in a Unity project.

## Unity version

**Unity 6.3 LTS (6000.3.0f1) or later** (tested against `6000.3.6f1`).

### Why Unity 6.3?

The SDK's native audio output engine is built on Unity 6.3's scriptable-audio surface — [`Audio.IAudioGenerator`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.IAudioGenerator.html) + [`Audio.GeneratorInstance`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.GeneratorInstance.html). This eliminates a structural ~800 ms pre-fill latency we used to work around with streaming `AudioClip`s (back-story: [`Docs~/unity-issues/streaming-audioclip-prefill-depth.md`](Docs~/unity-issues/streaming-audioclip-prefill-depth.md)) and gives us first-party audio-thread safety via Burst-compileable realtime structs. Unity 6.3 LTS shipped on 2025-12-04 with support through December 2027.

The async bridge API also uses Unity's [`Awaitable` / `AwaitableCompletionSource<T>`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Awaitable.html), which Unity 6.3 includes by virtue of its 2023.1+ scripting baseline.

## API compatibility level

**Player Settings → Player → Other Settings → Api Compatibility Level** must be **.NET Standard 2.1** or higher.

The SDK reads JS→C# callback payloads on WebGL via [`Marshal.PtrToStringUTF8`](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.ptrtostringutf8), which is not available in .NET Standard 2.0. Unity 2023.1+ projects default to .NET Standard 2.1, so most projects require no change. A project that has been explicitly downgraded to .NET Standard 2.0 will encounter a `MissingMethodException` on the first JS→C# callback at runtime.

## Platform support

The public API (`Runtime/`) is cross-platform. WebGL is the most mature target today; native targets (desktop, mobile, XR) are implemented under `Runtime/Native/` and ship in the same package — the right implementation is selected at compile time per platform.

## Additional WebGL setup

### Enable "Use WebAssembly.Table"

**Player Settings → WebGL → Publishing Settings → Use WebAssembly.Table** must be **on**.

This is a hard requirement. The SDK's JS→C# delivery path uses Emscripten's [`{{{ makeDynCall(...) }}}`](https://docs.unity3d.com/6000.0/Documentation/Manual/web-interacting-browser-deprecated.html) macro, which relies on the wasm table being exported. With the setting off, the macro expands to a call to `getWasmTableEntry`, which references a `wasmTable` that wasn't exported — the result is a runtime `ReferenceError` on the first JS→C# callback, not a link-time error. The build preprocessor included in the SDK will fail the build with instructions when the setting is off.

**Why Unity deprecated the old API:** Unity 6 deprecated the legacy `Module.dynCall_*` family of functions in favour of the `makeDynCall` macro (see Unity's [deprecated browser-interaction APIs page](https://docs.unity3d.com/6000.0/Documentation/Manual/web-interacting-browser-deprecated.html)). Enabling `Use WebAssembly.Table` is Unity's recommended forward-compatible setting that makes the macro path work (see the [WebGL Player Settings docs](https://docs.unity3d.com/6000.3/Documentation/Manual/class-PlayerSettingsWebGL.html)).

**Consumer-impact risk:** enabling `Use WebAssembly.Table` is incompatible with any other `.jslib` in the same project that still calls `Module.dynCall_*`. If a third-party plug-in you are using does this, you will need to either migrate that plug-in or wait for its author to do so before the SDK can be used in the same build. `dynCall_*` is deprecated in Unity 6 regardless of this SDK; the right long-term path is migration.

## WebGL audio output limitations

Unity's WebGL build target has no scriptable audio pipeline — `AudioClip.Create(stream=true, pcmreadercallback=…)`, `OnAudioFilterRead`, `AudioRenderer`, and `AudioListener.GetOutputData` are all unsupported. The SDK works around this on WebGL by playing agent audio through a parallel Web Audio graph ([`Bridge~/src/connection/web-audio-sink.ts`](Bridge~/src/connection/web-audio-sink.ts)) and mirroring a curated subset of `ConversationOptions.OutputAudioSource`'s properties onto that graph once per Unity frame. The `AudioSource` itself is **decorative on WebGL** — Unity never streams samples through it because Unity *can't*. Full platform writeup: [`Docs~/unity-issues/webgl-scriptable-audio-pipeline.md`](Docs~/unity-issues/webgl-scriptable-audio-pipeline.md).

### `AudioSource` property fidelity on WebGL

When you pass an `AudioSource` to `ConversationOptions.OutputAudioSource`, the following properties round-trip onto the Web Audio graph (full design rationale in [`Docs~/plans/output-audio-source.md`](Docs~/plans/output-audio-source.md)):

| Property | Web Audio mapping | Notes |
|---|---|---|
| `volume` | `GainNode.gain` | Direct |
| `transform.position` | `PannerNode.positionX/Y/Z` | Computed relative to the active `AudioListener` |
| `spatialBlend` | Wet/dry mix of `PannerNode` output vs mono | Web Audio analog |
| `minDistance` | `PannerNode.refDistance` | Direct |
| `maxDistance` | `PannerNode.maxDistance` | Direct |
| `rolloffMode` (Linear / Logarithmic) | `PannerNode.distanceModel` | `AudioRolloffMode.Custom` falls back to Logarithmic with a one-time warning |
| `panStereo` | `StereoPannerNode.pan` | Effective when `spatialBlend < 1` |
| `dopplerLevel` | Sampled per-frame velocity → `PannerNode` position pre-emphasis | Approximate |
| `outputAudioMixerGroup` | ❌ Not supported | One-time warning when first observed as non-null |
| Custom rolloff `AnimationCurve` | ❌ Not supported | One-time warning; falls back to `rolloffMode` |
| `bypassEffects`, `bypassListenerEffects`, `bypassReverbZones` | ❌ Not supported | Silently ignored — no Web Audio analog |
| `priority`, `spread`, `reverbZoneMix` | ❌ Not supported | Silently ignored |

Warning text format: `"[ElevenLabs] AudioSource.{Property} is set on a supplied OutputAudioSource but isn't supported on WebGL (FMOD-only concept). Ignoring."` — fired once per session per offending property to avoid log spam.

WebRTC-on-WebGL ignores `OutputAudioSource` entirely (audio goes through LiveKit's own pipeline) until the v0.3 `WebRTCAudioAdapter` lands.

### Don't read PCM directly off the supplied `AudioSource`

Because the `AudioSource` is decorative on WebGL, any Unity audio API that reads scripted samples (`AudioSource.GetOutputData`, `AudioListener.GetOutputData`, `AudioSource.GetSpectrumData`, `OnAudioFilterRead`, `PCMReaderCallback`) returns silence on WebGL even while the agent is audibly speaking. This is a platform constraint, not a Unity bug — see [`Docs~/unity-issues/webgl-scriptable-audio-pipeline.md`](Docs~/unity-issues/webgl-scriptable-audio-pipeline.md).

Use the SDK's cross-platform reading APIs instead — they're tuned to return comparable scalars on native and WebGL:

| What you want | Native-only API (silent on WebGL) | Cross-platform SDK API |
|---|---|---|
| Scalar volume `[0, 1]` for an envelope / meter / bob | `audioSource.GetOutputData(buf); rms(buf);` | `conversation.GetOutputVolume()` |
| Frequency-domain magnitudes | `AudioSource.GetSpectrumData(...)` | `conversation.GetByteFrequencyData(buf)` |

The shipped samples under `Samples~/` follow this rule — copy from them.
