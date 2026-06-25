# WebGL has no scriptable audio pipeline — `AudioClip.Create(stream=true, pcmreadercallback=…)`, `OnAudioFilterRead`, `AudioRenderer`, and `AudioListener.GetOutputData` are all unavailable

**Unity version:** 6000.3.6f1 (Unity 6 LTS). Same constraint on every
Unity release that supports WebGL (5.x through Unity 6.x). Not version-
specific — a fundamental platform omission.
**Platform reproduced:** WebGL builds, every browser.
**Severity:** Documented limitation, not a bug. Costs every SDK that
needs to stream PCM through a Unity-owned `AudioSource` on WebGL a
parallel re-implementation against Web Audio.
**Discovered (this codebase):** 2026-06-24 while implementing
`ConversationOptions.OutputAudioSource` for the WebGL target, after
the native implementation shipped using
`AudioClip.Create(stream=true, pcmreadercallback=…)` to drive a
caller-supplied `AudioSource`.

## Summary

Unity's WebGL audio backend exposes only the parts of the `AudioSource`
/ `AudioClip` API that can be implemented on top of the browser's Web
Audio API without script-thread → audio-thread callbacks. Every entry
point that would let a script *generate* or *observe* PCM samples is
explicitly unsupported:

| API | Native | WebGL | Doc reference |
|---|---|---|---|
| `AudioClip.Create(name, lengthSamples, channels, frequency, stream: true, pcmreadercallback: …)` | ✅ | ❌ | [WebGL audio docs](https://docs.unity3d.com/Manual/webgl-audio.html) — "scriptable audio pipeline is not supported" |
| `MonoBehaviour.OnAudioFilterRead(float[], int)` | ✅ | ❌ | [Scripting ref](https://docs.unity3d.com/ScriptReference/MonoBehaviour.OnAudioFilterRead.html) — "not supported on the Web platform" |
| `AudioRenderer.Start()` / `AudioRenderer.Render(NativeArray<float>)` | ✅ | ❌ | Bundled within the "scriptable audio pipeline" ban above |
| `AudioListener.GetOutputData(float[], int)` | ✅ | ❌ (returns silence) | Not explicitly documented; the broader "scriptable audio pipeline" ban appears to cover it |
| `AudioSource.GetOutputData(float[], int)` | ✅ | ❌ (returns silence — source never plays scripted PCM) | Same |
| `AudioMixerGroup` effects beyond volume | ✅ | ❌ | "Volume is the only property you can change on Web. Other properties and sound effects aren't supported." |
| Native audio plugins (DSP) | ✅ | ❌ | Implied by the above — would have to be cross-compiled to WASM with Unity's FMOD substitute, which Unity doesn't expose |

The combined effect: **on WebGL there is no Unity-owned path for an
SDK to play streaming PCM through a user-supplied `AudioSource`**.
Anything that needs scripted audio playback has to bypass Unity's
audio engine entirely and talk to the browser's Web Audio API
directly.

## Minimal reproduction

Author a `MonoBehaviour` that creates a streaming `AudioClip` and
binds it to an `AudioSource`:

```csharp
private void Start()
{
    AudioSource source = GetComponent<AudioSource>();
    AudioClip clip = AudioClip.Create(
        name: "scripted",
        lengthSamples: 16000,
        channels: 1,
        frequency: 16000,
        stream: true,
        pcmreadercallback: data =>
        {
            // Generate a 440 Hz sine. On native this PCMReaderCallback
            // fires on the audio thread; on WebGL the build logs a
            // warning at AudioClip.Create time and the callback NEVER
            // fires.
            for (int i = 0; i < data.Length; i++)
                data[i] = Mathf.Sin(2f * Mathf.PI * 440f * i / 16000f);
        }
    );
    source.clip = clip;
    source.loop = true;
    source.Play();
}
```

**Native (any platform):** sine wave plays.

**WebGL:** silence. Browser console shows
`The scriptable audio pipeline is not supported on Web. Use a non-streaming
AudioClip or play raw audio through the AudioContext directly.` or similar.
`AudioSource.isPlaying` reports `true`, but `AudioSource.GetOutputData`
returns all zeros — there's nothing for Unity's audio thread to drain.

## Expected behaviour

Either:

1. **Real fix** — implement the scriptable audio pipeline on WebGL.
   Web Audio's `AudioWorkletNode` has been broadly available since
   2018 and is the natural backing for `PCMReaderCallback` /
   `OnAudioFilterRead`. Unity could bridge the audio-thread callback
   into a worklet processor and pump samples into the existing
   `AudioSource` → `AudioListener` graph the same way native does. The
   API contract on the C# side is already platform-agnostic.
2. **Stop-gap** — document the unsupported APIs explicitly inside each
   API's own scripting reference. `OnAudioFilterRead` already does
   this; `AudioClip.Create`'s `stream` / `pcmreadercallback` parameters
   and `AudioListener.GetOutputData` do not. A single sentence on each
   API page collapses the discovery cost from "build, ship, observe
   silence, debug" to a 10-second doc read.

## Workaround applied in this SDK

[`Bridge~/src/connection/web-audio-sink.ts`](../../Bridge~/src/connection/web-audio-sink.ts)
implements a parallel Web Audio graph (AudioWorkletNode → GainNode →
AnalyserNode → mono/spatial crossfade → StereoPannerNode +
PannerNode → destination) that the SDK plays agent PCM through
directly, bypassing Unity's audio engine entirely.

[`Runtime/WebGL/Bridged/WebAudioBackedOutput.cs`](../../Runtime/WebGL/Bridged/WebAudioBackedOutput.cs)
holds the user-supplied `AudioSource` (when supplied via
`ConversationOptions.OutputAudioSource`) and once per Unity frame
mirrors a curated subset of its properties onto the JS sink — transform
position (folded into a listener-local frame via
`Transform.InverseTransformPoint`), `spatialBlend`, `minDistance`,
`maxDistance`, `rolloffMode`, `panStereo`, `dopplerLevel`. The
`AudioSource` itself remains *decorative* on WebGL — Unity never
streams audio through it because Unity *can't*.

The downstream consequences for game code are documented in
[`COMPATIBILITY.md`](../../COMPATIBILITY.md) under the "WebGL audio
output limitations" section, including the most surprising one:
`audioSource.GetOutputData(buffer, 0)` returns silence on WebGL even
while the agent is audibly speaking, because the samples never flowed
through Unity's audio engine. SDK consumers wanting per-source volume
or time-domain readings have to use SDK-level APIs
(`Conversation.GetOutputVolume()` /
`Conversation.GetByteFrequencyData(buffer)`) instead.

### Why this isn't a "documented limitation, no action needed"

Two reasons it's worth filing despite being a known platform constraint:

1. **The friction it imposes on SDK authors is real and recurring.**
   Every Unity audio SDK that streams PCM (TTS, voice chat, music
   generation, telephony, …) hits this wall the first time it targets
   WebGL. Each of them has to rebuild a Web Audio graph from scratch,
   maintain its own bridge for property mirroring, and educate their
   users about why `AudioSource.GetOutputData` doesn't work. A first-
   party Unity WebGL scriptable-audio path would absorb that work
   across the ecosystem.
2. **The documentation gap inside specific API pages costs days of
   developer time.** A consumer building cross-platform with the
   ElevenLabs SDK who writes `AudioClip.Create(..., stream: true,
   pcmreadercallback: ...)` will compile cleanly, build cleanly,
   observe silence on WebGL, and have no obvious place to look. The
   single sentence `OnAudioFilterRead` carries should appear on the
   other affected APIs too.

## Asks for Unity

1. **Implement the scriptable audio pipeline on WebGL via
   `AudioWorkletNode`.** Web Audio has had the worklet primitive since
   Chrome 66 (2018); Safari 14.1+ (2021); Firefox 76+ (2020). The
   browser support window now comfortably covers Unity's WebGL
   browser-support matrix. The C# API surface is already there — only
   the backend mapping is missing.
2. **If (1) is out of scope for the foreseeable future, document the
   unsupported APIs at the API level.** Add a "not supported on the
   Web platform" note to:
   - [`AudioClip.Create`](https://docs.unity3d.com/ScriptReference/AudioClip.Create.html)
     (specifically the `stream` and `pcmreadercallback` parameters)
   - [`AudioListener.GetOutputData`](https://docs.unity3d.com/ScriptReference/AudioListener.GetOutputData.html)
   - [`AudioSource.GetOutputData`](https://docs.unity3d.com/ScriptReference/AudioSource.GetOutputData.html)
   - [`AudioRenderer.Start`](https://docs.unity3d.com/ScriptReference/AudioRenderer.Start.html)
     and related entry points
   This matches the existing
   [`OnAudioFilterRead`](https://docs.unity3d.com/ScriptReference/MonoBehaviour.OnAudioFilterRead.html)
   precedent.
