# Streaming `AudioClip` pre-fill depth is structurally locked at ~800 ms regardless of `lengthSamples`

**Unity version:** 6000.3.6f1 (Unity 6 LTS). Reproduced on the default
audio configuration only (DSP buffer 256 samples × 4 buffers at 48 kHz
output). Pre-fill depth scales with the DSP buffer width, so a
process-wide `AudioSettings.Reset` to a smaller DSP buffer reduces it
proportionally — but `lengthSamples` on the clip itself has no effect.
**Platform reproduced:** macOS arm64 standalone + Editor. Same backend
codepath on every native target (Windows / Linux / iOS / Android), so
the constraint very likely generalises.
**Severity:** Undocumented behaviour. Forces every SDK that streams PCM
through `AudioClip.Create(stream: true, pcmreadercallback: …)` to either
ship a visual-sync workaround (compensate for the ~800 ms lead) or
bypass streaming `AudioClip` entirely (e.g. via `OnAudioFilterRead`).
**Discovered (this codebase):** 2026-06-26 while diagnosing why the
volume bob on
[`Samples/QuickStart/`](../../Samples/QuickStart/) /
`Samples/TalkingBox/` led the agent's voice by ~800 ms during the
first second of playback. The wall-clock interpolation we initially
shipped assumed pre-fill ≈ DSP buffer depth (~21 ms); the real depth
turned out to be ~38× larger.

## Summary

Calling
`AudioClip.Create(name, lengthSamples, channels, frequency, stream: true, pcmreadercallback: cb)`
followed by `AudioSource.Play()` fires the `PCMReaderCallback`
synchronously inside `Play` (or shortly after, on the audio thread)
until Unity has drained a fixed quantity of clip-rate samples into its
internal streaming buffer. That quantity is **invariant in
`lengthSamples`** and pinned at roughly 12,800 samples (~800 ms at a
16 kHz clip on the default Unity 6 audio config).

Concretely, after `AudioSource.Play()` returns, the speaker hasn't yet
played any of those 12,800 samples — they're queued in Unity's
internal buffer, waiting their turn at the audio device's drain rate.
The drain rate matches the speaker's playback rate, so:

- **First ~800 ms of playback**: the speaker is playing samples 0 to
  ~12,800, but the script-visible "drain head" (every sample
  `PCMReaderCallback` has been asked for) is already at ~12,800 — i.e.
  ~800 ms ahead of the speaker.
- **Ongoing playback**: the speaker continues to lag the drain head
  by ~12,800 samples for the entire session. Each subsequent
  `PCMReaderCallback` fire only refills what the speaker has already
  drained (one DSP buffer's worth at a time), so the gap is stable.

This becomes visible whenever an SDK needs to *visualise* what's
currently audible (volume meters, lip-sync, beat indicators). Reading
the buffer at the position `PCMReaderCallback` last drained — the only
position the script can observe — corresponds to audio that won't
play for another ~800 ms. The mismatch is most jarring at the start of
each new sound, when ~800 ms of audio appears to "queue" before the
visualiser starts moving.

There is no public Unity API to shrink the pre-fill depth per clip.
`AudioSettings.Reset` to a smaller DSP buffer reduces it
proportionally, but that's a process-wide setting that affects every
audio source in the user's project (and can introduce crackling on
slow machines).

## Empirical pre-fill measurement (2026-06-26)

Drove a `lengthSamples` sweep through `AudioClip.Create(stream: true, …)`
interactively in the Unity Editor (default audio config: DSP buffer
256 × 4 at 48 kHz, clip at 16 kHz mono). For each row, queued enough
ring data to satisfy every `PCMReaderCallback` fire, called
`AudioSource.Play()`, and counted how many samples the callback drained
synchronously before control returned:

```
lengthSamples=    16 | sync fires=800 | sync samples=12800 (~800.0 ms @ 16000 Hz clip)
lengthSamples=    64 | sync fires=200 | sync samples=12800 (~800.0 ms)
lengthSamples=   256 | sync fires= 50 | sync samples=12800 (~800.0 ms)
lengthSamples=  1024 | sync fires= 13 | sync samples=12800 (~800.0 ms)
lengthSamples=  4096 | sync fires=  4 | sync samples=12800 (~800.0 ms)
lengthSamples= 16000 | sync fires=  4 | sync samples=12800 (~800.0 ms)
```

Only the per-fire batch granularity changes — Unity slices the same
12,800-sample pre-fill into N fires of `lengthSamples` each. The total
is constant. ~38× larger than the DSP buffer depth (1024 output-rate
samples ≈ 21 ms), so Unity is clearly reserving a fixed
streaming-buffer headroom independent of clip configuration.

## Minimal reproduction

```csharp
public class PrefillProbe : MonoBehaviour
{
    private int _syncFires;
    private int _syncSamples;

    private void Start()
    {
        const int sampleRate = 16000;
        const int lengthSamples = 256; // any of the values above
        AudioClip clip = AudioClip.Create(
            name: "probe",
            lengthSamples: lengthSamples,
            channels: 1,
            frequency: sampleRate,
            stream: true,
            pcmreadercallback: data =>
            {
                _syncFires++;
                _syncSamples += data.Length;
                // Fill with silence so we don't crackle; the count
                // above is what matters.
                for (int i = 0; i < data.Length; i++) data[i] = 0f;
            }
        );

        AudioSource src = gameObject.AddComponent<AudioSource>();
        src.clip = clip;
        src.loop = true;

        _syncFires = 0;
        _syncSamples = 0;
        src.Play(); // pre-fill drains synchronously here
        Debug.Log($"sync fires={_syncFires} sync samples={_syncSamples}");
    }
}
```

Drop on any GameObject in an empty scene, press Play, observe the
console: roughly `sync fires=50 sync samples=12800` for
`lengthSamples=256` on the default Unity 6 audio config.

## Expected behaviour

Either:

1. **Public API to control the streaming-clip pre-fill depth.** A new
   parameter on `AudioClip.Create` (e.g. `prefillSamples: int`) — or a
   property on the returned `AudioClip` — lets SDK authors choose a
   pre-fill matched to their latency budget. The current behaviour is
   reasonable as a default for asset-streaming workloads (it absorbs
   I/O stalls), but punishing for low-latency PCM streaming where the
   ring buffer is in memory and the cost of an underrun is bounded.
2. **Documentation.** Add a "Streaming clips reserve approximately
   `dspBufferSize × N` samples of pre-fill ahead of the speaker
   regardless of `lengthSamples`; if low latency matters, see
   [`OnAudioFilterRead`](https://docs.unity3d.com/ScriptReference/MonoBehaviour.OnAudioFilterRead.html)"
   note to
   [`AudioClip.Create`'s `pcmreadercallback` parameter](https://docs.unity3d.com/ScriptReference/AudioClip.Create.html)
   and the
   [`AudioClip.Create` manual page](https://docs.unity3d.com/Manual/StreamingAudioClips.html).
   The current pages don't mention the pre-fill at all; SDK authors
   discover it the way we did — empirically, after shipping a
   visualiser that lags reality.

## Workaround applied in this SDK

This SDK is mid-migration on the workaround:

**Shipped today (v0.1):** track an "audible playback head" separately
from Unity's drain head inside the `PCMReaderCallback` host
[`Runtime/Native/UnityAudioSourceOutput.cs`](../../Runtime/Native/UnityAudioSourceOutput.cs).
The audible head sweeps forward from `AudioSource.Play()`'s return at
the clip sample rate (wall-clock interpolation); the volume bob reads
RMS at the audible head's position rather than the drain head's.
Compensates for the ~800 ms pre-fill *visually* without changing the
audio path. Full design rationale + Edit-Mode test coverage at
[`Docs~/plans/bob-alignment-redesign.md`](../../Docs~/plans/bob-alignment-redesign.md).

**Known residual artifact:** turns 2+ in a multi-turn conversation
inherit ~800 ms of silence in Unity's internal buffer from the
underrun drains that fire during the silence gap between turns. The
wall-clock model can't compensate for that silence (we have no
visibility into how full Unity's internal buffer is); the bob ends up
~800 ms ahead of the speaker on every turn after the first. Tolerable
but not ideal for v0.1.

**In flight (v0.2):** replace `AudioClip.Create(stream: true, …)` with
a `MonoBehaviour.OnAudioFilterRead`-based engine that bypasses Unity's
streaming-clip pre-fill entirely — `OnAudioFilterRead` fires once per
DSP buffer (~5 ms at the default config) with no comparable headroom
reserve. Eliminates the structural cause. Sketch at
[`Docs~/plans/audio-output-filter-engine.md`](../../Docs~/plans/audio-output-filter-engine.md).

Either workaround is more code than a `prefillSamples` parameter
would be, and the residual-artifact problem above demonstrates why
the structural fix matters even for SDKs that have already absorbed
the visual-compensation cost.

## Newer APIs worth evaluating (Unity 6.3 LTS)

Unity 6.3 introduces a new audio-generation surface that looks like a
better structural fit for real-time PCM streaming than either
`AudioClip.Create(stream: true, …)` or `OnAudioFilterRead`:

- [`UnityEngine.Audio.IAudioGenerator`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.IAudioGenerator.html)
  — factory interface for instantiating a `GeneratorInstance`,
  attachable to an `AudioSource` via the new `AudioSource.generator` /
  `AudioSource.generatorInstance` properties (or allocatable
  programmatically via `ControlContext.AllocateGenerator()`).
- [`UnityEngine.Audio.GeneratorInstance`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Audio.GeneratorInstance.html)
  — a `ProcessorInstance` that generates audio. Implementors provide
  `IControl` (control-thread state) and `IRealtime` (a `Process()`
  callback that fills a `ChannelBuffer` at a given sample rate /
  speaker mode), giving SDK authors a first-class hook into the audio
  pipeline without the streaming-`AudioClip` indirection.

We haven't evaluated whether the pre-fill behaviour described above
applies to `GeneratorInstance` as well, but the surface area suggests
it shouldn't: there's no `lengthSamples` / streaming-buffer
intermediary, and `Process()` is documented as the realtime callback
itself. If true, this would supersede both the v0.1 wall-clock
workaround and the v0.2 `OnAudioFilterRead` engine for projects on
Unity 6.3+. The SDK currently targets Unity 6 LTS (6000.0+), so any
adoption would need to coexist with the existing path until the
floor moves to 6.3.

## Asks for Unity

1. **Add a `prefillSamples` (or similar) parameter to
   [`AudioClip.Create`](https://docs.unity3d.com/ScriptReference/AudioClip.Create.html)
   for the `stream: true` overload.** Default to today's behaviour so
   existing projects are unaffected. The audio engine knows the value
   it picked; exposing it through the C# surface is a one-line
   addition for callers who care about latency.
2. **Document the current pre-fill behaviour at minimum.** Either on
   [`AudioClip.Create`](https://docs.unity3d.com/ScriptReference/AudioClip.Create.html)
   directly or on the
   [Streaming Audio Clips](https://docs.unity3d.com/Manual/StreamingAudioClips.html)
   manual page. A two-sentence note describing the
   ~`dspBufferSize × N`-sample reserve and pointing latency-sensitive
   callers at `OnAudioFilterRead` would collapse the discovery cost
   from "ship a visualiser, observe drift, sweep `lengthSamples`,
   conclude there's no API control" to a 30-second doc read.
3. **(Stretch)** Expose the picked value as a read-only property on
   the returned `AudioClip` (or via `AudioSettings`), so SDKs that
   can't get (1) but can live with the default depth can at least
   *compensate exactly* rather than estimating. Today's wall-clock
   compensation works because the 12,800-sample value is empirically
   stable, but it could change between Unity versions or per-device
   audio backend without warning.
