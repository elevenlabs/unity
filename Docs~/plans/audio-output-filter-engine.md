# Audio output filter engine — bypass Unity's streaming-buffer pre-fill via `OnAudioFilterRead`

**Status:** Paused, 2026-06-29 — superseded by [`audio-generator-engine.md`](audio-generator-engine.md). Decision: rather than work around Unity 6.0 LTS's streaming-`AudioClip` pre-fill via `OnAudioFilterRead`, elevate the SDK's minimum Unity version to 6.3 LTS and adopt the new [`UnityEngine.Audio.IAudioGenerator` / `GeneratorInstance`](../unity-issues/streaming-audioclip-prefill-depth.md#newer-apis-worth-evaluating-unity-63-lts) APIs as the primitive instead. The audio-generator path eliminates the pre-fill structurally without the supplied-source-as-parent-hint ergonomic break and likely without the resampler-quality tradeoff. Branch `feat/audio-output-filter-engine` left open in case we need to resume (e.g. if 6.3 adoption turns out to be a non-starter for downstream users), but no further work scheduled.

**Resume conditions:** would be revisited only if (a) the Unity 6.3 floor bump is rejected after user feedback, or (b) the `IAudioGenerator` migration turns up a structural problem that makes `OnAudioFilterRead` the more pragmatic v0.2 fix after all.

**Stranded artifact (removed):** `Tests/Runtime/Native/UnityFilterAudioOutputEngineCharacterizationTest.cs` was written but never run before the pivot; deleted as part of the pause to avoid the appearance of partial coverage for a plan we aren't currently building toward. Reachable in git history if the resume conditions ever hit (search for the filename on the `feat/audio-output-filter-engine` branch).

**Original status (for posterity):** Active, 2026-06-29 — sketch expanded into a concrete plan after sign-off on the three open questions (supplied source → spatialization parent hint; full plan in a single branch; empirical unknowns probed via unity-mcp during step 1). Branch: `feat/audio-output-filter-engine`.
**Driver:** [`bob-alignment-redesign.md`](bob-alignment-redesign.md) compensates for Unity's structural 800 ms streaming-buffer pre-fill (written up at [`Docs~/unity-issues/streaming-audioclip-prefill-depth.md`](../unity-issues/streaming-audioclip-prefill-depth.md)) by tracking the audible playback head separately from the drain head. This plan *eliminates* that pre-fill instead by routing samples through a `MonoBehaviour.OnAudioFilterRead` callback rather than `AudioClip.PCMReaderCallback` — bringing the engine's queued-ahead latency from ~800 ms down to ~5 ms (one DSP buffer at default Unity 6 audio config) and eliminating the residual turn-2+ bob drift the redesign can't compensate for.
**Assumes:** Bob-alignment redesign has shipped (v0.1 audio output path).
**Out of scope:** WebGL audio (separate path; WebGL already has a low-latency `AnalyserNode`-based bob); changes to the `IAudioOutputEngine` interface (already the right shape — engine seam stays intact); Unity 6.3's [`IAudioGenerator` / `GeneratorInstance`](../unity-issues/streaming-audioclip-prefill-depth.md#newer-apis-worth-evaluating-unity-63-lts) APIs (potential future replacement once the SDK floor moves above Unity 6.0 LTS).

---

## Why this lives separately from the bob redesign

Bob redesign was well-scoped (~couple-hundred lines inside `UnityAudioSourceOutput`), unblocked the existing `[Ignore]`d tests, and shipped against an already-validated engine. The filter engine is a fundamentally different audio plumbing pattern with its own undocumented Unity behaviours to discover empirically (the 2026-06-26 session ran into the streaming-buffer pre-fill being structurally locked at 12,800 samples — see [`bob-alignment-redesign.md`](bob-alignment-redesign.md)'s "Empirical findings" — and the filter path has analogous unknowns to surface in step 1 below).

The split: ship the redesign as the v0.1 fix; build the filter engine after we know whether v0.1's structural 800 ms latency actually matters to users in practice. Turn-2+ PlayMode confirmation showed it does, so this plan is the next move.

## The pattern

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

## Verified Unity behaviours (from 2026-06-26 deliberation)

Drove a `FilterProbe` MonoBehaviour interactively in the Editor (Getting Started project) across two scenarios:

- **Scenario 1** — two `AudioSource`s on the same `GameObject` + `OnAudioFilterRead` on the same `GameObject`: probe saw peak `0.354` (= `0.500 × √0.5` — Unity applies a per-source -3 dB attenuation before the filter). Could not 100% disambiguate from this data whether the filter binds to one source or fires per source — would need a follow-up probe that records the value of each call individually. For our purposes the binding question is moot — see below.
- **Scenario 2** — two `AudioSource`s on sibling `GameObject`s under a shared parent, filter only on `ChildA`: probe on `ChildA` saw only `ChildA`'s signal (`0.354`). `ChildB`'s audio flowed independently to the AudioListener untouched. **Sibling isolation works.**

**Implication:** the filter engine always creates its own `GameObject` (child of the spatialization parent if user supplies one), never shares with user audio. With that constraint, the binding question is moot — we never sit on a `GameObject` with other AudioSources.

## Supplied-source ergonomics (decided)

Today `ConversationOptions.OutputAudioSource` lets users supply their own AudioSource so the agent's voice goes through their mixer / spatialization setup. The filter engine can't reuse the supplied source directly (it needs its own GameObject for filter isolation). Resolution:

**Treat the supplied source as a spatialization parent hint.** The filter engine creates a new child `GameObject` *under the supplied source's GameObject*; the agent's audio inherits the supplied source's transform position (so 3D spatialization works), but the supplied source itself isn't the literal output source. The filter engine's own AudioSource is what actually plays — with mixer routing, volume, and rolloff curves carried over from the supplied source where reasonable.

State to capture and carry over from supplied source → filter engine's own source:

- `outputAudioMixerGroup` (mixer routing — users care about this most)
- `spatialBlend` / `rolloffMode` / `minDistance` / `maxDistance` / `dopplerLevel` (3D positioning)
- `bypassEffects` / `bypassListenerEffects` / `bypassReverbZones` (mixer effect chain config)
- *Not* `clip`, `loop`, `volume` (SDK-owned during session, same as today's snapshot/restore in [`UnityAudioOutputEngine`](../../Runtime/Native/UnityAudioOutputEngine.cs))
- *Not* `playOnAwake`, `priority` (orthogonal to filter operation)

The supplied source itself is left untouched throughout the session (no snapshot/restore needed — we don't modify it). Documented breaking-change note: callers relying on the literal supplied source playing the audio (e.g., subscribing to `AudioSource.isPlaying` on it) will need to point to the new child source instead. Filter-engine releases an event or exposes a property so callers can find that child source if they need to introspect it.

## Open questions to settle during step 1

- **Pre-fill behaviour of `OnAudioFilterRead`.** Does Unity fire the filter synchronously inside `AudioSource.Play()`, or only once the audio thread picks up? Step 1's characterization test answers this directly.
- **Resampler quality.** Linear interp is the v0 choice; A/B against today's streaming-clip path during step 7 manual confirmation. If voice clarity suffers, swap in a half-band or polyphase filter as a follow-up (not blocking this plan).
- **First-frame underrun behaviour.** Today's engine has a 900 ms threshold + 500 ms timeout gate before `Start` fires (so the sync pre-fill lands on real samples). With the filter engine's ~5 ms pre-fill, that gate is over-conservative — does the threshold gate even need to fire before `Start` returns, or can we drop it to a single DSP buffer? Step 5 decision.
- **CallCount disparity from 2026-06-26 probe.** Probe saw 172 callbacks in scenario 1 but only 11 in scenario 2 (same 30-frame window). Likely an audio-thread re-priming cost after scenario-1 teardown. Step 1 measures whether there's a startup gap we need to wait through before counting steady-state cadence.

---

## Execution sequencing

### Step 1 — Characterization test for the filter path

Mirror [`Tests/Runtime/Native/UnityAudioOutputEngineCharacterizationTest.cs`](../../Tests/Runtime/Native/UnityAudioOutputEngineCharacterizationTest.cs) at `Tests/Runtime/Native/UnityFilterAudioOutputEngineCharacterizationTest.cs`:

- Sets up a `GameObject` with an `AudioSource` looping a silent `AudioClip` + a probe `MonoBehaviour` implementing `OnAudioFilterRead`.
- Counts sync filter fires inside `Play()` vs ongoing fires over 5 s.
- Records per-fire `data.Length` and `channels` to confirm DSP buffer size matches `AudioSettings.GetConfiguration().dspBufferSize`.
- Assertions: loose bands ("filter fires at >0 Hz and <500 Hz over the window"), logs concrete values so the FakeAudioOutputEngine defaults can be tuned to match.
- Batchmode path: `Assert.Inconclusive` if zero fires (no audio device), same pattern as today's characterization.

Run interactively via unity-mcp (`Unity_RunCommand` to set up scene, enter Play Mode, observe console). Findings folded into the plan's "Verified Unity behaviours" section.

- [x] Test file written (later deleted on pivot — see "Stranded artifact" above)
- [ ] PlayMode run captured: sync pre-fill count, ongoing cadence, DSP buffer / channel counts — *attempted via unity-mcp but blocked by transient discovery hiccup; never re-attempted before pivot*
- [ ] Plan updated with empirical findings; open questions resolved or deferred

### Step 2 — Linear-interp resampler

Standalone helper at `Runtime/Native/LinearInterpResampler.cs`. Audio-thread-hot, so:

- Zero per-call allocations once initialized (preallocate scratch buffers, never resize after `Initialize`).
- Stateless across calls except for the in-flight fractional sample position (so resampling across DSP buffer boundaries doesn't click).
- API: `Initialize(int inputRate, int outputRate, int outputChannels)`, `Process(ReadOnlySpan<float> inputMono, Span<float> outputInterleaved)`, `Reset()`.
- Mono input → N-channel interleaved output (channel fanout = duplicate mono → all channels, simplest correct thing).
- Edit-Mode unit tests under `Tests/Editor/Native/LinearInterpResamplerTests.cs`: zero-input → zero-output, DC input → DC output, sine input → output amplitude preserved, fractional position carries across calls.

- [ ] Resampler implementation
- [ ] Edit-Mode unit tests cover zero/DC/sine/cross-call cases

### Step 3 — `UnityFilterAudioOutputEngine : IAudioOutputEngine`

New file `Runtime/Native/UnityFilterAudioOutputEngine.cs`. Mirrors [`UnityAudioOutputEngine`](../../Runtime/Native/UnityAudioOutputEngine.cs)'s shape (constructor with optional supplied source / OutputDeviceConfig, `IsAvailable`, `Volume`, `Start`, `Stop`, `Tick`, `Dispose`) but with internals swapped:

- Constructor: when `suppliedSource != null`, create a child `GameObject` under `suppliedSource.gameObject` and copy spatial/mixer state per the [supplied-source ergonomics](#supplied-source-ergonomics-decided) section. When `null`, owned hidden-host pattern (matching today's engine).
- Companion `MonoBehaviour` on the child GameObject: `UnityFilterAudioEngineProbe`. Implements `OnAudioFilterRead(float[] data, int channels)`. Pulls mono samples from a ring (drained via the engine's `Func<float[], int> drainCallback`), runs them through `LinearInterpResampler`, writes the interleaved result into `data`.
- `Start`: assigns a tiny silent looping `AudioClip` (e.g. 1 sample of 0f), calls `AudioSource.Play()`. The silent clip exists so Unity has something to "play" and triggers the filter callback chain.
- `Stop`: `AudioSource.Stop()`. Same as today.
- `Dispose`: tear down the child `GameObject`, the silent clip, the resampler. Owned-host path destroys its host. Supplied-source path leaves the supplied source untouched (no snapshot to restore — we never modified it).
- `Tick`: no-op (Unity's audio thread drives the filter callback independently).
- Audio-thread safety: same `object _bufferLock` pattern as today's `OnPcmRead`. The drain callback is called from the audio thread; ring access is already locked.

- [ ] Engine class with all `IAudioOutputEngine` methods implemented
- [ ] `UnityFilterAudioEngineProbe` MonoBehaviour wired correctly
- [ ] Owned-host and supplied-source-as-parent paths both compile and behave (manually validated)

### Step 4 — Re-run Fake-driven test suite against the new engine

The `IAudioOutputEngine` seam is implementation-agnostic; the existing Fake-driven tests under `Tests/Editor/Native/UnityAudioSourceOutputTests.cs` should pass without modification. The two `[Ignore]`d regression tests from [`audio-output-testability.md`](audio-output-testability.md) likely also pass — pre-fill drops from 12,800 samples to ~256, well within their existing wall-clock window.

If anything fails: either the Fake's defaults need a parallel `FilterFakeAudioOutputEngine` (separate file, same pattern, calibrated to step 1's findings) OR the engine's behaviour diverges from the contract in a way that exposes a real bug. Diagnose and decide.

- [ ] All Fake-driven tests under `Tests/Editor/Native/UnityAudioSourceOutputTests.cs` pass against `UnityFilterAudioOutputEngine` (substituted via the engine constructor's optional injection)
- [ ] Two previously-`[Ignore]`d regression tests un-ignored if they now pass cleanly
- [ ] New FilterFake (if needed) calibrated to step 1's empirical numbers

### Step 5 — Adjust `UnityAudioSourceOutput` for filter-path surprises

Likely smaller than the bob-redesign delta — the host's contract with the engine is the same, and the audible-head wall-clock model in [`UnityAudioSourceOutput.ComputeWallClockRms`](../../Runtime/Native/UnityAudioSourceOutput.cs) was already conservatively sized for the larger pre-fill. With ~5 ms pre-fill, the bob's `OutputLatencySamples` offset alone is sufficient; the `_playbackStartStampTicks` / `_playbackStartRingPos` anchor path can be a no-op (drain head ≈ audible head when pre-fill is small).

Open: does the 900 ms threshold gate before `Start` fire still make sense? With the filter engine, `Start` returning doesn't synchronously consume hundreds of ms of audio; the threshold could shrink to a single DSP buffer (~5 ms) without underrun risk. Likely a "ship the gate as-is for parity, file a follow-up to tune" call rather than blocking this branch.

- [ ] Audible-head model verified to still work (or simplified to a no-op for the small pre-fill case)
- [ ] Threshold gate sizing decision documented (kept as-is OR shrunk)

### Step 6 — Production wiring swap

Single line change at [`UnityAudioSourceOutput.CreateAsync`](../../Runtime/Native/UnityAudioSourceOutput.cs#L204) (line 215 of `UnityAudioSourceOutput.cs` per the recon): swap `new UnityAudioOutputEngine(audioSource, device)` for `new UnityFilterAudioOutputEngine(audioSource, device)`.

Keep the old `UnityAudioOutputEngine` class in the tree (don't delete it) — it's the proven-correct fallback if the filter engine surfaces a regression in the field. Removable in a follow-up once v0.2 has soaked.

- [ ] Production wiring switched to the filter engine
- [ ] Old engine class retained, marked as deprecated-but-available in its XML doc

### Step 7 — Manual PlayMode A/B confirmation

Drive `Samples/TalkingBox/` and `Samples/ConversationSmokeTest/` interactively:

- **A/B 1 — Turn 1 latency.** Confirm the agent's voice and the bob start within ~50 ms of each other (was ~800 ms on the streaming-clip path before the bob redesign, ~0 ms after; should be ~0 ms here without the wall-clock compensation needing to do work).
- **A/B 2 — Turn 2+ drift.** Confirm the residual turn-2+ artifact from the bob redesign is gone (bob no longer leads speaker by ~800 ms on turns 2+).
- **A/B 3 — Voice clarity.** Subjective listen comparing streaming-clip path vs filter path. If linear interp is audible, file a follow-up for a higher-quality resampler (not blocking).
- **A/B 4 — Supplied-source ergonomics.** Set `ConversationOptions.OutputAudioSource` to an AudioSource positioned 3 m to the left of the AudioListener. Confirm the agent's voice is audibly panned left (spatialization carried over via the parent hint).

- [ ] Turn 1 latency A/B captured (subjective + ideally a recorded clip in `Docs~/plans/`)
- [ ] Turn 2+ drift A/B captured
- [ ] Voice clarity A/B captured (subjective fine)
- [ ] Supplied-source spatialization A/B captured

---

## Tests

Already covered above; consolidated for reference:

- `Tests/Runtime/Native/UnityFilterAudioOutputEngineCharacterizationTest.cs` — step 1, PlayMode characterization
- `Tests/Editor/Native/LinearInterpResamplerTests.cs` — step 2, Edit-Mode unit tests
- `Tests/Editor/Native/UnityAudioSourceOutputTests.cs` — step 4, existing Fake-driven suite re-run against `UnityFilterAudioOutputEngine`
- (Possible) `Tests/Editor/Native/FilterFakeAudioOutputEngine.cs` — step 4, only if the existing `FakeAudioOutputEngine` can't cover both engine cadences

## What this *doesn't* do

- Doesn't replace WebGL audio (separate path).
- Doesn't touch the `IAudioOutputEngine` interface (already the right shape).
- Doesn't delete the old `UnityAudioOutputEngine` (kept as fallback through v0.2 soak).
- Doesn't adopt Unity 6.3's `IAudioGenerator` / `GeneratorInstance` ([noted](../unity-issues/streaming-audioclip-prefill-depth.md#newer-apis-worth-evaluating-unity-63-lts)) — those APIs may supersede this engine in a future release once the SDK floor moves above Unity 6.0 LTS, but that's a separate plan.
