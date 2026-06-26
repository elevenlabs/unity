# Bob-alignment redesign — track the audible playback head, not the drain head

**Status:** Shipped with known limitation, 2026-06-26 — PlayMode confirmation found turn 1 in sync but turns 2+ jumped to the new chunk's tail because empty silence-only drains between turns re-stamped `_playbackStartStampTicks`. Patched: the re-stamp now also gates on `n > 0` so the anchor only re-captures on a drain that consumed real samples (regression test `GetVolume_AfterClearRingThenNewChunk_OnlyReStampsOnFirstRealDrain`). PlayMode re-test reported "much better" — turn 2+ artifacts remain because Unity's internal streaming buffer is full of silence from the underrun, which the wall-clock model can't compensate for. Structural fix lives in [`audio-output-filter-engine.md`](audio-output-filter-engine.md) (eliminates the 800 ms pre-fill that creates the silence-buffer in the first place).
**Driver:** Companion follow-up from [`audio-output-testability.md`](audio-output-testability.md). The current wall-clock `GetVolume` interpolation is `_readPos`-relative with a single `OutputLatencySamples` offset, but the engine's "samples queued ahead of speaker" varies by regime — the sync pre-fill queues ~12,800 clip-rate samples (≈ 800 ms at 16 kHz) ahead, while subsequent ongoing drains queue ~21 ms ahead (one DSP buffer). A single offset can't capture both, so the bob leads the audio by ~800 ms during the first second of playback.
**Assumes:** Steps 1–5 of [`audio-output-testability.md`](audio-output-testability.md) are landed — `IAudioOutputEngine` seam, `FakeAudioOutputEngine`, ported tests, and the two `[Ignore]`d regression tests (`GetVolume_TracksPlaybackPosition_DuringSyncPrefill_NotDrainHead`, `Output_ThresholdGate_TimeoutFiresIfNoFurtherChunks`) already in place at [`Tests/Editor/Native/UnityAudioSourceOutputTests.cs`](../../Tests/Editor/Native/UnityAudioSourceOutputTests.cs).
**Out of scope:** WebGL audio (separate path); the `IAudioOutputEngine` extraction (already shipped); recalibrating the fake's defaults from an interactive PlayMode characterization run (tracked separately — the redesign works against today's "matches defaults within an order of magnitude" calibration). An alternative low-latency `OnAudioFilterRead`-based engine that would *eliminate* the 800 ms pre-fill rather than compensate for it — tracked separately at [`audio-output-filter-engine.md`](audio-output-filter-engine.md) as a follow-up.

## Empirical findings (2026-06-26 deliberation)

Drove a `lengthSamples` sweep through `AudioClip.Create(stream: true, …)` interactively in the Unity Editor (Getting Started project, Unity 6.0.0.3.6f1, default audio config: DSP buffer 256 × 4 at 48 kHz). Result:

```
lengthSamples=    16 | sync fires=800 | sync samples=12800 (~800.0 ms @ 16000 Hz clip)
lengthSamples=    64 | sync fires=200 | sync samples=12800 (~800.0 ms)
lengthSamples=   256 | sync fires= 50 | sync samples=12800 (~800.0 ms)
lengthSamples=  1024 | sync fires= 13 | sync samples=12800 (~800.0 ms)
lengthSamples=  4096 | sync fires=  4 | sync samples=12800 (~800.0 ms)
lengthSamples= 16000 | sync fires=  4 | sync samples=12800 (~800.0 ms)
```

**Pre-fill is structurally locked at 12,800 clip-rate samples regardless of `lengthSamples`.** Only the per-fire batch granularity changes — Unity slices the same 12,800-sample pre-fill into N fires of `lengthSamples` each. This is ~38× larger than the DSP buffer depth (1024 output-rate samples ≈ 21 ms), so Unity is clearly reserving a fixed streaming-buffer headroom independent of clip configuration. There is no public API to shrink it per-clip. Sweeping the DSP buffer process-wide via `AudioSettings.Reset` would reduce it, but that's invasive (affects all audio in the user's project, can cause crackling on slow machines) and was ruled out as a v0.1 fix.

**Conclusion driving this plan:** the 800 ms pre-fill is a structural property of Unity's streaming `AudioClip`. The bob redesign compensates for it visually; the filter-engine follow-up would eliminate it structurally.

---

## The bug

[`UnityAudioSourceOutput.ComputeWallClockRms`](../../Runtime/Native/UnityAudioSourceOutput.cs) reads the most-recently-played 5 ms window relative to `_readPos`:

```csharp
int elapsedSamples = (int)((elapsedTicks / StopwatchTicksPerSecond) * _format.SampleRate);
int virtualOffset = Math.Min(elapsedSamples, _available);
int audibleHead = Math.Max(0, virtualOffset - OutputLatencySamples);
// Walk back windowSamples from (_readPos + audibleHead) to capture the
// most-recently-played envelope.
```

`_readPos` is the engine's *drain head* — the position the engine has consumed up to. After `engine.Start` returns, `_readPos` is already ~12,800 samples ahead of what the speaker is actually playing (Unity's streaming buffer pre-fill). Wall-clock interpolation from there reads the wrong window:

| When | What `GetVolume` reads | What the speaker plays | Symptom |
|---|---|---|---|
| First ~800 ms after Start | Samples 12,500–12,800 (end of pre-fill) | Samples 0–300 (start of pre-fill) | Bob leads audio by 800 ms |
| Ongoing playback | Samples around `_readPos` | Samples ~256 behind `_readPos` (one DSP buffer) | Bob leads audio by ~5 ms (negligible) |

The single `OutputLatencySamples` offset is sized for the ongoing case (~21 ms at 48 kHz default DSP config) and can't compensate for the pre-fill 800 ms. The regression is captured by [`GetVolume_TracksPlaybackPosition_DuringSyncPrefill_NotDrainHead`](../../Tests/Editor/Native/UnityAudioSourceOutputTests.cs) (currently `[Ignore]`d).

## The fix

Track the audible playback head as a pure wall-clock count from the moment playback started, *independent* of how many samples the engine has drained:

```csharp
// Wall-clock timestamp when engine.Start returned. Zero before Start.
private long _playbackStartStampTicks;

// Ring position (in samples written, NOT modular) the speaker is at when
// _playbackStartStampTicks was captured. Equals _readPos at Start minus
// the engine's sync pre-fill: the audible head trails the drain head by
// the pre-fill amount for the entire session.
private long _playbackStartRingPos;
```

Then `ComputeWallClockRms` becomes:

```csharp
double elapsed = (TimestampProvider() - _playbackStartStampTicks) / StopwatchTicksPerSecond;
long audibleRingPos = _playbackStartRingPos + (long)(elapsed * _format.SampleRate);
// Cap at _writePos (linear, not modular) so we don't sweep into samples
// that haven't been queued yet — that would just read zero-initialised
// ring slots and look identical to silence.
audibleRingPos = Math.Min(audibleRingPos, _writePosLinear);
// Read RMS over [audibleRingPos − windowSamples, audibleRingPos), folded
// onto the modular ring index.
```

Properties of the new model:

- **Updates at frame rate.** No dependence on PCMReaderCallback cadence — the bob refreshes on every poll, just like today's design.
- **Accounts for the full streaming buffer depth.** `_playbackStartRingPos = _readPos_at_start − prefillSamples` bakes the pre-fill offset in once, at start time.
- **Independent of subsequent drain cadence.** Drain fires advance `_readPos` (which now has no role in `GetVolume`); they don't perturb the audible head's wall-clock march.
- **No `OutputLatencySamples` magic.** The DSP-buffer latency is dwarfed by the pre-fill (800 ms ≫ 21 ms) — once we anchor on `engine.Start`'s return, the residual ~21 ms is below the bob's perceptible threshold and can drop out of the math entirely.

### Capturing the pre-fill amount

The host (`UnityAudioSourceOutput`) calls `engine.Start(format, ReadFromRing)`. During that call, the engine fires `ReadFromRing` synchronously — each fire returns the count of real samples drained. Sum the fires inside `Start` to get the exact pre-fill consumed; capture `_readPos`'s linear position immediately afterwards:

```csharp
long preStartReadPosLinear = _readPosLinear;
_engine.Start(_format, ReadFromRing);
long postStartReadPosLinear = _readPosLinear;
long prefillSamples = postStartReadPosLinear - preStartReadPosLinear;
_playbackStartRingPos = preStartReadPosLinear; // audible head = drain head MINUS prefill
_playbackStartStampTicks = TimestampProvider();
```

Notes:

- The pre-fill is measured, not assumed — works identically against the fake (50 × 256 = 12,800) and the real Unity engine (whatever the device actually drains), so the model survives the interactive PlayMode characterization tightening without a code change.
- `_readPosLinear` is a new monotonically-increasing counter alongside `_readPos`. `_readPos` stays modular (it indexes into `_ring`); `_readPosLinear` makes the audible-head math straightforward and lets `audibleRingPos = Math.Min(_, _writePosLinear)` clamp without modular-arithmetic foot-guns. `_writePosLinear` is the symmetric counter on the write side. Both are `long` so multi-hour sessions don't wrap.
- The wall-clock stamp is captured *after* `Start` returns, not before — so the pre-fill drain time itself isn't counted as elapsed playback. On the real engine this is a few ms; on the fake it's effectively zero, but the order matters for determinism either way.

### Interrupt / restart semantics

[`ClearRing`](../../Runtime/Native/UnityAudioSourceOutput.cs) already resets `_lastDrainStampTicks = 0` so post-interrupt `GetVolume` short-circuits to zero. The new model needs the same reset on the new anchors:

```csharp
internal void ClearRing()
{
    lock (_bufferLock)
    {
        // ...existing zeroing of _ring / _readPos / _writePos / _available / _analysisBuffer...
        _readPosLinear = 0;
        _writePosLinear = 0;
        _playbackStartStampTicks = 0;
        _playbackStartRingPos = 0;
    }
}
```

`_playbackStarted` stays at `true` — the engine itself is still alive after `ClearRing` (`Interrupt` doesn't call `engine.Stop`); a follow-up `PushAudio` will write into the cleared ring and the drain callback will fire as usual. The "first PushAudio after Interrupt re-anchors the audible head" case is handled by re-stamping `_playbackStartStampTicks` and `_playbackStartRingPos` on the first drain after a `ClearRing`-driven reset — gated on `_playbackStartStampTicks == 0` so it's a self-healing edge case rather than a separate code path.

`GetVolume` short-circuits to zero when `_playbackStartStampTicks == 0`, replacing today's `_lastDrainStampTicks == 0` guard. Same observable behaviour, new anchor field.

### Single-chunk timeout fallback (companion fix)

The threshold gate currently has a wall-clock fallback (`PrefillTimeoutMs = 500`) that fires `engine.Start` after 500 ms even if the ring never reaches the threshold — but the check only runs from inside `PushAudio`, so a single chunk that's followed by silence never trips it. [`Output_ThresholdGate_TimeoutFiresIfNoFurtherChunks`](../../Tests/Editor/Native/UnityAudioSourceOutputTests.cs) is `[Ignore]`d for this reason.

Fix: when the first `PushAudio` arrives, schedule a one-shot `Awaitable.WaitForSecondsAsync(PrefillTimeoutMs / 1000f)` that re-runs the gate-evaluation. Cancel the awaitable if the threshold trips first (so the timer doesn't fire `engine.Start` redundantly).

```csharp
private CancellationTokenSource? _prefillTimeoutCts;

// Inside PushAudio, after writing to the ring:
if (!_playbackStarted)
{
    if (ringThresholdMet)
    {
        StartPlayback(); // common helper: captures linear ring pos, calls engine.Start, captures stamp
    }
    else if (_prefillTimeoutCts == null)
    {
        _prefillTimeoutCts = new CancellationTokenSource();
        _ = RunPrefillTimeoutAsync(_prefillTimeoutCts.Token);
    }
}

private async Awaitable RunPrefillTimeoutAsync(CancellationToken token)
{
    try
    {
        await Awaitable.WaitForSecondsAsync(PrefillTimeoutMs / 1000f, token);
        if (token.IsCancellationRequested || _playbackStarted || !_engine.IsAvailable)
            return;
        StartPlayback();
    }
    catch (OperationCanceledException) { /* expected */ }
}
```

Edit-Mode caveat: `Awaitable.WaitForSecondsAsync` only ticks during Play Mode. The Edit-Mode test [`Output_ThresholdGate_TimeoutFiresIfNoFurtherChunks`](../../Tests/Editor/Native/UnityAudioSourceOutputTests.cs) was written to advance a fake clock and re-check `StartCallCount` — which won't work with a real awaitable. Two options:

1. **Test seam.** Add `internal Func<float, CancellationToken, Awaitable> WaitForSecondsAsyncProvider { get; set; }` defaulting to `Awaitable.WaitForSecondsAsync`. The test injects a synchronous-completing awaitable that fires immediately once the test advances the fake clock past the timeout. **Picked.** Mirrors the existing `TimestampProvider` test seam and keeps the test as-is (no PlayMode dependency for what is fundamentally a timer-gate behaviour assertion).
2. **PlayMode test.** Move the test to `Tests/Runtime/Native/` and use `[UnityTest]` with `yield return new WaitForSeconds(...)`. Heavier (PlayMode runs are ~5× slower in CI than Edit Mode) and conflates the timer mechanism with the gate semantics.

The test seam wins. Production keeps its `Awaitable`-based wait; tests substitute a deterministic stub.

### What this *doesn't* fix

- **DSP-buffer latency (~10–30 ms).** The new model drops `OutputLatencySamples` entirely because the residual lead it would correct is invisible to the eye. If someone reports a sluggish-feeling bob after this lands and the residual matters, add a small constant subtract back into `audibleRingPos`. Not in the initial cut — KISS.
- **Recalibrating the fake's defaults.** Still owed from step 5 of [`audio-output-testability.md`](audio-output-testability.md). The redesign works against today's defaults; the recalibration is a separate (small) commit once the user does the interactive PlayMode characterization run.
- **Spatial bob.** When a user supplies an `AudioSource`, Unity's spatializer attenuates the audible signal based on listener distance — but `GetVolume` is reading the pre-spatializer ring buffer, so the bob doesn't dim with distance. Different problem, different plan.

## Execution sequencing

Each step is independently committable. The whole sequence should land in a single PR (steps share an integration test).

1. **[x] Introduce `_readPosLinear` / `_writePosLinear` counters alongside `_readPos` / `_writePos`.** Landed atomically with steps 2–3 (the counters are referenced from `ComputeWallClockRms` and the audible-head capture, so they couldn't usefully ship independently).

2. **[x] Rewrite `ComputeWallClockRms` against `_playbackStartStampTicks` + `_playbackStartRingPos`.** Replaced the `_lastDrainStampTicks` guard, dropped `OutputLatencySamples` + `ComputeOutputLatencySamples` entirely. Lifted `[Ignore]` on `GetVolume_TracksPlaybackPosition_DuringSyncPrefill_NotDrainHead`. Rewrote `GetVolume_SweepsForwardBetweenDrains_AsWallClockAdvances` → `..._SweepsForwardWithWallClock_AfterFirstDrainStampsAnchor` and `GetVolume_CapsVirtualOffsetAtAvailable_HoldsRmsAtTailDuringUnderrun` → `GetVolume_AudibleHead_ClampsAtWritePos_HoldsRmsAtTailDuringUnderrun` against new-model semantics. Deleted `GetVolume_OutputLatencyOffset_ShiftsTheRmsWindowBackward` (the field it tested no longer exists).

3. **[x] Add the single-chunk timeout fallback via `WaitForSecondsAsyncProvider` test seam.** Extracted `StartPlayback()` helper + `RunPrefillTimeoutAsync` + `CancelPrefillTimeout`. Lifted `[Ignore]` on `Output_ThresholdGate_TimeoutFiresIfNoFurtherChunks` (stubs the awaitable via `AwaitableCompletionSource`). Added `Output_ThresholdGate_TimerCanceled_IfThresholdTripsFirst` to cover the idempotent-start guard.

4. **[x] Verify against the existing suite.** All 428 tests green (was 427 with 2 ignored — both lifted, +1 new). `dotnet csharpier check .`, `pnpm --dir Bridge~ run format:check lint typecheck test verify:primitives verify:connection`, `pnpm --dir IntegrationTests~ run typecheck` all pass.

5. **[x] Check off step 6 in [`audio-output-testability.md`](audio-output-testability.md).** Done.

6. **[x] Manual PlayMode confirmation.** Interactive run against the Getting Started `TalkingBox` scene on 2026-06-26. Turn 1 was confirmed in sync; turns 2+ initially jumped to the new chunk's tail because empty silence-only drains between turns were re-stamping the audible-head anchor. Patched with the `n > 0` gate on the re-stamp branch in `ReadFromRing` (regression-tested by `GetVolume_AfterClearRingThenNewChunk_OnlyReStampsOnFirstRealDrain`). PlayMode re-test reported "much better" — residual artifacts remain (the new chunk's bob still leads the speaker by Unity's internal streaming-buffer silence depth, ~800 ms), but acceptable for v0.1 ship. Full structural fix is the filter-engine follow-up at [`audio-output-filter-engine.md`](audio-output-filter-engine.md).

## What this *doesn't* do

- Doesn't touch WebGL (`WebAudioBackedOutput`'s bob comes from a real `AnalyserNode` reading the post-spatializer signal — no equivalent drift).
- Doesn't change the threshold-gate's pre-fill amount (`PrefillThresholdMs = 900`) or the fade-out / interrupt semantics.
- Doesn't ship a new `IAudioOutputEngine` method or break the fake's API — the engine seam is already sufficient. The new anchor lives entirely inside `UnityAudioSourceOutput`.
