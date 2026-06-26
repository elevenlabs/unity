# Audio output filter engine — bypass Unity's streaming-buffer pre-fill via `OnAudioFilterRead`

**Status:** Stub, 2026-06-26 — captured as a deferred follow-up to [`bob-alignment-redesign.md`](bob-alignment-redesign.md). Not scheduled for v0.1.
**Driver:** [`bob-alignment-redesign.md`](bob-alignment-redesign.md) compensates for Unity's structural 800 ms streaming-buffer pre-fill by tracking the audible playback head separately from the drain head. This plan would *eliminate* that pre-fill instead by routing samples through a `MonoBehaviour.OnAudioFilterRead` callback rather than `AudioClip.PCMReaderCallback` — bringing the engine's queued-ahead latency from ~800 ms down to ~5 ms (one DSP buffer at default Unity 6 audio config).
**Assumes:** Bob-alignment redesign has shipped (and is the v0.1 audio output path).

---

## Why this lives separately from the bob redesign

Bob redesign is well-scoped (~couple-hundred lines inside `UnityAudioSourceOutput`), unblocks the existing `[Ignore]`d tests today, and ships against an already-validated engine. The filter engine is a fundamentally different audio plumbing pattern with its own undocumented Unity behaviours to discover empirically (today's session ran into the streaming-buffer pre-fill being structurally locked at 12,800 samples — see [`bob-alignment-redesign.md`](bob-alignment-redesign.md)'s "Empirical findings" section — and the filter path has its own analogous unknowns to surface).

The split: ship the redesign as the v0.1 fix; build the filter engine after we know whether v0.1's structural 800 ms latency actually matters to users in practice (or whether the visual sync from the bob redesign is sufficient).

## Sketch (not a final design)

### The pattern

`OnAudioFilterRead` is a `MonoBehaviour` callback Unity invokes on the audio thread, **once per DSP buffer**, for the `AudioSource` component on the same `GameObject`. Signature:

```csharp
void OnAudioFilterRead(float[] data, int channels)
{
    // data is DSPBufferSize × channels samples, interleaved.
    // Unity fills it with what the AudioSource would output (the
    // playing AudioClip + per-source attenuation). We can read or
    // overwrite in place; the result flows to the spatializer →
    // AudioMixer → output.
}
```

For "generate from scratch": loop a silent `AudioClip` on the AudioSource → `data` arrives full of zeros → overwrite with our agent samples. The signal then takes the rest of Unity's audio path normally.

### What we'd build

A new `IAudioOutputEngine` implementation that:

1. Creates its own `GameObject` (never co-located with user audio — see the multi-source verification notes below).
2. Attaches an `AudioSource` looping a constant silent `AudioClip`.
3. Attaches a `MonoBehaviour` (`UnityFilterAudioEngineProbe` or similar) that implements `OnAudioFilterRead`, calling back into the host's drain callback per DSP buffer.
4. Handles **resampling** from clip rate (typically 16 kHz) → output rate (typically 48 kHz). Linear interpolation is fine for voice quality; this is what most audio SDKs do for non-audiophile rates.
5. Handles **channel fanout** — mono SDK samples → stereo / 5.1 / however many output channels Unity is configured for. Just duplicate mono → all channels.

The existing `UnityAudioSourceOutput` / `IAudioOutputEngine` seam stays intact — the host doesn't care which engine it gets. The `FakeAudioOutputEngine` doesn't need to change (it already exercises the contract, not the implementation). Both `[Ignore]`d regression tests from [`audio-output-testability.md`](audio-output-testability.md) likely just pass against the filter engine without further redesign — pre-fill drops from 12,800 samples to ~256.

### Verified Unity behaviours (from 2026-06-26 deliberation)

Drove a `FilterProbe` MonoBehaviour interactively in the Editor (Getting Started project) across two scenarios:

- **Scenario 1** — two `AudioSource`s on the same `GameObject` + `OnAudioFilterRead` on the same `GameObject`: probe saw peak `0.354` (= `0.500 × √0.5` — Unity applies a per-source -3 dB attenuation before the filter). Could not 100% disambiguate from this data whether the filter binds to one source or fires per source — would need a follow-up probe that records the value of each call individually (today's probe captured min/max/last across calls). For our purposes the binding question is moot — see below.
- **Scenario 2** — two `AudioSource`s on sibling `GameObject`s under a shared parent, filter only on `ChildA`: probe on `ChildA` saw only `ChildA`'s signal (`0.354`). `ChildB`'s audio flowed independently to the AudioListener untouched. **Sibling isolation works.**

**Implication:** the filter engine should always create its own `GameObject` (child of the spatialization parent if user supplies one), never share with user audio. With that constraint, the binding question is moot — we never sit on a `GameObject` with other AudioSources.

### Open questions to settle before / during implementation

- **Pre-fill behaviour of `OnAudioFilterRead`.** Does Unity also fire the filter synchronously inside `AudioSource.Play()`, or only after the audio thread picks up? Quick characterization via the existing `Tests/Runtime/Native/UnityAudioOutputEngineCharacterizationTest.cs` pattern.
- **Resampler quality.** Linear interp is the v0 choice; we may need a half-band or cubic later if voice clarity suffers. Compare A/B against today's streaming-clip path with the same agent audio.
- **Supplied-source ergonomics.** Today `ConversationOptions.OutputAudioSource` lets users supply their own AudioSource so the agent's voice goes through their mixer / spatialization setup. The filter engine breaks that by creating a child `GameObject`. Options: (a) document the supplied source is now treated as a "spatialization parent" hint rather than the literal output source; (b) keep both engines and let users opt in to the streaming-clip path when they need their literal AudioSource used. (a) is simpler; (b) preserves backward compat.
- **CallCount disparity.** The probe saw 172 callbacks in scenario 1 but only 11 in scenario 2 (same 30-frame window). Probably an audio-thread re-priming cost after the scenario-1 teardown — worth confirming so we know how long to wait for the engine to start producing callbacks after `Start`.

### Execution sequencing (sketch — fill in when the plan is scheduled)

1. Build a calibrated characterization test for the filter path (mirror of `UnityAudioOutputEngineCharacterizationTest.cs`).
2. Extract a tiny linear-interp resampler (probably in `Runtime/Native/` alongside the engine, since it's audio-thread hot).
3. Implement `UnityFilterAudioOutputEngine : IAudioOutputEngine`. Production wires it instead of `UnityAudioOutputEngine`.
4. Re-run the `FakeAudioOutputEngine`-driven test suite — it should pass without modification (engine seam is already implementation-agnostic).
5. Adjust `UnityAudioSourceOutput` if the filter path exposes a behaviour the bob redesign couldn't model (e.g., a pre-fill we hadn't accounted for — same surprise pattern as the streaming-clip path).
6. Decide supplied-source ergonomics (see open question above).
7. Manual PlayMode confirmation: A/B against the bob-redesign engine in `Samples/TalkingBox/` or `Samples/ConversationSmokeTest/`.

## What this *doesn't* do

- Doesn't replace WebGL audio (separate path; WebGL already has a low-latency `AnalyserNode`-based bob).
- Doesn't touch the `IAudioOutputEngine` interface — already the right shape.
- Doesn't ship as a v0.1 audio output — that's the bob redesign. This plan is a v0.2+ candidate.
