# Audio output testability — extract `IAudioOutputEngine`, mock Unity's primitives

**Status:** Proposed, 2026-06-25
**Driver:** A debugging session against the native audio path took roughly an hour of round-trips through Unity Play Mode — write code, enter Play Mode, walk into a box, listen, capture log via MCP, repeat. The bugs being chased (~800 ms silence pre-roll, then a 200 ms mid-utterance gap, then bob-leads-audio-by-800 ms) all stemmed from undocumented Unity behaviours around `AudioClip.Create(stream: true, pcmreadercallback)` that the SDK had to discover empirically. Each iteration was a 30–90 second cycle. With proper test infrastructure, every fix could have been driven from an Edit Mode test in ~50 ms.
**Assumes:** Native `UnityAudioSourceOutput` already exists (#9 shipped); the wall-clock `GetVolume` interpolation has landed (this session); existing Edit Mode test patterns in `Tests/Editor/Native/UnityAudioSourceOutputTests.cs` (constructor-driven ring buffer tests, `TimestampProvider` test seam).
**Out of scope:** WebGL audio output (separate path via `WebAudioBackedOutput` + Web Audio sink); input controllers (`UnityMicrophoneInput`); the bob-alignment redesign itself (see "Companion follow-up" below — this plan unblocks it but doesn't ship it).

---

## What we learned the hard way this session

Unity's streaming `AudioClip` has at least three behaviours the SDK depends on, none of which are documented in a way that made them obvious:

1. **`AudioClip.Create(stream: true, pcmreadercallback)` synchronously fires the callback before returning.** Empirically observed: ~12,800 clip-rate samples drained inside the `Create` call on a Unity 6 default audio config (DSP buffer 256 × 4 at 48 kHz, clip at 16 kHz). The drain happens regardless of whether the clip has been attached to an `AudioSource` or `Play()` called. The exact count varies with clip length × Unity's internal callback batch count, but the *total* is roughly constant.

2. **Whatever the callback returns becomes baked into Unity's streaming buffer.** If the ring is empty during the sync pre-fill, the SDK silence-fills the destination array; that silence is then queued ahead of any real audio and plays before the agent's voice. Symptom: a 100–800 ms gap mid-utterance (whose duration matches the silence-filled portion).

3. **PCMReaderCallback cadence after `Play()` is governed by Unity's streaming-buffer depth, not the DSP buffer size.** We observed ~3 callbacks per second (333 ms cadence), which combined with our 5 ms RMS analysis window meant `GetVolume()` returned only ~3 distinct values per second — too coarse for lip-sync. This drove the wall-clock interpolation work that's already shipped.

The chain reaction: gap fix (#2) requires a threshold gate (wait for ring depth ≥ pre-fill demand before calling `AudioClip.Create`); the threshold gate exposes that the wall-clock interpolation needs to compensate for the ~800 ms streaming buffer depth (still open — bob leads audio by ~800 ms after the gap fix). Each finding required a fresh Play Mode round-trip because none of it was testable.

## Goal

Make these Unity behaviours expressible in **Edit Mode tests** so the next layer of audio fixes (bob alignment, interrupt timing, multi-chunk handling) can be developed and regression-locked without ever entering Play Mode. Keep a single Play Mode test as the "is our model of Unity still accurate?" canary.

## Design

### Extract an `IAudioOutputEngine` abstraction

`UnityAudioSourceOutput` currently owns four concerns:

1. PCM decoding + ring buffer (testable today, has good Edit Mode coverage)
2. Volume / interrupt fade logic (mostly testable today)
3. **Coordination with Unity's audio engine** — `AudioClip.Create`, `AudioSource.Play/Stop`, `PCMReaderCallback` wiring (currently untestable in Edit Mode; the source of every iteration cost this session)
4. Wall-clock `GetVolume` interpolation (test seam exists via `TimestampProvider`, but lacks coverage of the real-world Unity behaviour it's compensating for)

Extract #3 behind:

```csharp
internal interface IAudioOutputEngine : IDisposable
{
    /// <summary>
    /// Starts streaming playback. <paramref name="drainCallback"/> is invoked
    /// by the engine whenever it needs more samples; it returns the number of
    /// real samples written (engine is expected to silence-fill the
    /// remainder if &lt; <c>buffer.Length</c>).
    /// </summary>
    /// <remarks>
    /// On real Unity, calling Start synchronously fires drainCallback enough
    /// times to fill the streaming buffer ahead (~12,800 clip-rate samples
    /// on a Unity 6 default config) before returning. Fakes can simulate
    /// any pre-fill count.
    /// </remarks>
    void Start(FormatConfig format, Func<float[], int> drainCallback);

    /// <summary>
    /// Stops playback. Subsequent <see cref="Start"/> calls reset state.
    /// </summary>
    void Stop();

    /// <summary>
    /// Drives any pending engine work — for production, a no-op (Unity's
    /// audio thread runs independently). For fakes, advances simulated time
    /// and fires drain callbacks as the simulated playback head consumes
    /// samples.
    /// </summary>
    void Tick(double elapsedSeconds);
}
```

`UnityAudioSourceOutput` becomes engine-agnostic: it owns the ring + decoding + volume + GetVolume math, and delegates "actually plays audio" to the injected engine. Production wires `new UnityAudioOutputEngine()`; tests wire `new FakeAudioOutputEngine(prefillSamples: 12800)`.

### Production implementation: `UnityAudioOutputEngine`

Wraps today's `AudioClip.Create + AudioSource.Play + PCMReaderCallback` flow. Holds the host `GameObject` (when no `AudioSource` is supplied) and the `AudioClip`. `Start` calls `AudioClip.Create` with `PCMReaderCallback = (data) => drainCallback(data)`. `Stop` calls `AudioSource.Stop` and destroys the clip.

This is a mechanical extraction — no new behaviour, just moves the Unity-API surface into a single class so the rest of `UnityAudioSourceOutput` is engine-agnostic.

### Test implementation: `FakeAudioOutputEngine`

Constructor takes parameters that match observed Unity behaviour, so tests can drive specific scenarios:

```csharp
internal sealed class FakeAudioOutputEngine : IAudioOutputEngine
{
    public int SyncPrefillCallbackCount { get; set; } = 50;
    public int SyncPrefillSampleCountPerCallback { get; set; } = 256;
    public int OngoingCallbackBatchSize { get; set; } = 256;
    public double OngoingCallbackPeriodSeconds { get; set; } = 0.333;
    public int RecordedSilenceFillSamples { get; private set; }
    public int RecordedRealSampleCount { get; private set; }

    public void Start(FormatConfig format, Func<float[], int> drainCallback) {
        // Synchronously fire drainCallback SyncPrefillCallbackCount times
        // with a buffer of SyncPrefillSampleCountPerCallback samples. Record
        // how many real vs silence samples were drained.
    }
    public void Tick(double elapsedSeconds) {
        // Accumulate time. When elapsed >= OngoingCallbackPeriodSeconds,
        // fire one drainCallback with OngoingCallbackBatchSize samples.
    }
    // ...
}
```

Tests can:
- Assert exact silence-fill counts after various ring-write sequences
- Step time deterministically with `Tick(0.05)`
- Compose pathological scenarios (single tiny chunk, chunks-with-gaps, etc.)

### Edit Mode tests to write

In `Tests/Editor/Native/`, against `FakeAudioOutputEngine`:

- `Output_EmptyRingAtStart_PrefillFillsSilence_AndQueuesAheadOfRealAudio` — regression for the original 800 ms gap (pre-threshold-gate)
- `Output_ThresholdGate_DelaysStartUntilRingDepthMet` — current threshold gate behaviour
- `Output_ThresholdGate_TimeoutFiresIfNoFurtherChunks` — single-chunk fallback (currently relies on next-PushAudio firing; needs a real wall-clock timer — see [Companion follow-up](#companion-follow-up))
- `Output_PrefillDrainsEntirelyRealSamples_WhenRingHasEnoughDepth` — happy path
- `GetVolume_TracksPlaybackPosition_DuringSyncPrefill_NotDrainHead` — drives the bob-alignment redesign; fails today (bob reads at `_readPos` which is post-pre-fill ≈ 800 ms ahead of audible playback)
- `GetVolume_TracksPlaybackPosition_BetweenOngoingDrains` — high-refresh interpolation
- `PushAudio_DuringPrefill_ExtendsRingButDoesNotRetriggerStart` — interrupt + push edge case
- `Interrupt_ClearsRing_AndResetsPlaybackStartAnchor` — fade + restart

Each test ~50 ms. Whole suite finishes in seconds.

### Play Mode characterization test

Single test in `Tests/Runtime/Native/UnityAudioOutputEngineCharacterizationTest.cs`:

- Creates a real streaming `AudioClip.Create` with a counting `PCMReaderCallback`
- Records: total synchronous callback fires inside `Create`, total samples drained, callback cadence over the first 5 seconds of playback
- Asserts the observed values are within ranges that match the `FakeAudioOutputEngine`'s defaults (e.g., "sync pre-fill is between 5,000 and 30,000 samples on the current audio config")
- Failure mode: "Unity behaviour drifted; update the fake's defaults to match new reality"

Runs against the active Unity Editor's audio config (whatever the user has set). On CI we can pin the DSP buffer config via `AudioSettings.Reset` in `[SetUp]` so the test is reproducible.

This test is intentionally loose on exact values — we're characterizing "what Unity actually does," not asserting it stays bitwise identical. Tightening over time once we see how stable Unity's behaviour is across versions.

## Companion follow-up: bob-alignment redesign

This plan unblocks but doesn't ship the redesign. Captured here so it doesn't get lost:

The current wall-clock `GetVolume` interpolation is `_readPos`-relative with a single `OutputLatencySamples` offset. That model fails when the engine's "samples queued ahead of speaker" varies between regimes — specifically, the sync pre-fill queues ~800 ms ahead but subsequent ongoing drains queue ~21 ms ahead. A single offset can't capture both.

The replacement: track `_playbackStartStampTicks` (wall clock when `Start` returned) and `_playbackStartRingPos` (= `_readPos - prefillSamples` at that moment). Then:

```csharp
double elapsed = (Now - _playbackStartStampTicks) / StopwatchTicksPerSecond;
int audiblePosition = _playbackStartRingPos + (int)(elapsed * sampleRate);
// Cap at _writePos so we don't read past what's been buffered.
// Read RMS at [audiblePosition - windowSamples, audiblePosition) on the ring.
```

Updates at frame rate (smooth bob), accounts for the full streaming buffer depth (no lead), independent of subsequent drain cadence. The `FakeAudioOutputEngine` lets us drive this against a known-correct simulation of Unity's behaviour before risking another Play Mode round-trip.

Also from this session, the single-chunk timeout fallback in the threshold gate currently only fires when a subsequent `PushAudio` happens to run — if the agent sends one tiny chunk and stops, playback never starts. The redesign should hook a real wall-clock timer (an `Awaitable.WaitForSecondsAsync` spawned from the first `PushAudio`).

## Execution sequencing

Each step is independently committable.

1. **[x] Extract `IAudioOutputEngine`** — interface + `UnityAudioOutputEngine` implementation, no behaviour change. `UnityAudioSourceOutput` constructor takes `IAudioOutputEngine`; the bare `UnityAudioSourceOutput(FormatConfig)` constructor wires a no-op `NullAudioOutputEngine` so existing Edit-Mode tests stay engine-agnostic. Existing tests pass unchanged (all 401). The Unity-API surface (`AudioClip.Create`, `AudioSource.Play/Stop`, host `GameObject` setup, supplied-source pre-session snapshot + restore, mid-session destruction warn-once) is now isolated in `UnityAudioOutputEngine.cs`. Drain callback signature: `Func<float[], int>` returns the count of real samples written; the engine silence-fills any remaining slots before handing the buffer back to Unity (today this is a no-op overlay because `ReadFromRing` still silence-fills itself for analysis-buffer-decay semantics, but the contract is now in place for fakes to assert on silence-fill counts in step 2).
2. **[x] Build `FakeAudioOutputEngine`** — calibrated to current observed Unity behaviour (~12,800 sample sync pre-fill, ~3 Hz callback cadence). Lives at `Tests/Editor/Native/FakeAudioOutputEngine.cs` (test-only — no production caller wires it; the bare `UnityAudioSourceOutput(FormatConfig)` constructor stays on `NullAudioOutputEngine`). Defaults `SyncPrefillCallbackCount = 50` × `SyncPrefillSampleCountPerCallback = 256` = 12,800 sample sync pre-fill; `OngoingCallbackPeriodSeconds = 0.333` ≈ 3 Hz. Cumulative `RecordedSilenceFillSamples` / `RecordedRealSampleCount` counters (plus `ResetRecordedCounts()` for per-phase asserts) tally the real / silence split from the drain callback's return value — the fake silence-fills its own buffers to match `UnityAudioOutputEngine.OnPcmRead`, so the engine-side contract is observable regardless of whether `ReadFromRing` also silence-fills internally. Self-tests at `Tests/Editor/Native/FakeAudioOutputEngineTests.cs` lock the fake's own contract (sync pre-fill firing, multi-period `Tick` advances, Stop / Start budget reset, Dispose flipping `IsAvailable`) so step 4's integration tests fail unambiguously when the regression is in the host vs. the fake.
3. **[x] Port the affected SDK tests** to use `FakeAudioOutputEngine` where they exercise audio-engine interactions (most existing ring-buffer tests don't need the engine and stay unchanged). Tightened the one test that genuinely leaned on `NullAudioOutputEngine` (`SetVolume_ClampedToZeroOne` — `DoesNotThrow` only because it couldn't observe the propagated value; now asserts `fake.Volume` directly), and added four engine-observability tests for paths the Null engine couldn't reach: `Interrupt_ImmediateCut_RestoresUserVolumeOnEngine` (the `_engine.Volume = _userVolume` line in the immediate-cut path), `PushAudio_ResetsEngineVolumeToUserLevel` (the per-call volume reset after `CancelFade`), `Close_DisposesEngine` (lifecycle hand-off), and `GetByteFrequencyData_EngineUnavailable_FillsZeros` (the `!_engine.IsAvailable` short-circuit that was previously uncovered in Edit Mode — the existing `_NoAudioSource_` test goes through the available-engine path with an empty analysis buffer). Existing ring / decode / wall-clock / interrupt-ring-clear tests stay on `NullAudioOutputEngine` — they exercise controller-internal math and don't need engine observability. The four `CreateAsync_*` tests stay on the production `UnityAudioOutputEngine` since they validate `AudioSource` / supplied-source / host-`GameObject` setup that doesn't exist on the fake. Suite is 419 tests (was 414); all green.
4. **[x] Write the new Edit Mode tests** listed above. All 8 tests live in `Tests/Editor/Native/UnityAudioSourceOutputTests.cs` under the "Engine integration via FakeAudioOutputEngine" section. Two are `[Ignore]`d with the regression they document: `Output_ThresholdGate_TimeoutFiresIfNoFurtherChunks` (single-chunk fallback only fires on a subsequent PushAudio today — needs the timer-driven follow-up) and `GetVolume_TracksPlaybackPosition_DuringSyncPrefill_NotDrainHead` (bob reads `_readPos` instead of audible head — the bob-alignment redesign in step 6 will lift the `[Ignore]`). The other 6 pass against today's controller behaviour: empty-ring + manual `Start` proves the original ~800 ms gap regression (`fake.RecordedSilenceFillSamples = 12,700` when only 100 real samples are queued); the threshold-gate happy path drains all 12,800 sync-prefill samples as real; ongoing-drain wall-clock interpolation sweeps the bob from SILENCE into LOUD between fake `Tick`s; subsequent PushAudio calls extend the ring without re-firing `engine.Start`; and Interrupt + ClearRing resets `_lastDrainStampTicks` so a post-interrupt GetVolume short-circuits to 0. Suite is 427 tests (425 reported as passing + 2 ignored); all green.
5. **Add the Play Mode characterization test.** Run once, observe values, set the fake's defaults to match.
6. **Ship the bob-alignment redesign** (separate plan / commit) against the now-passing test suite. No Play Mode iteration needed during development; one Play Mode confirmation at the end.

## What this *doesn't* do

- Doesn't change any production behaviour by itself (steps 1–5).
- Doesn't address WebGL (different audio path, different test surface; if we want similar infrastructure there, it's a separate plan).
- Doesn't replace manual testing of the audio path entirely — for "does the agent's voice sound right in a real scene," nothing substitutes for Play Mode. But "does my SDK plumbing produce the correct sample sequence at the correct cadence" should not require Play Mode, and after this plan it won't.
