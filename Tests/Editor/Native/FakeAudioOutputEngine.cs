#nullable enable

using System;
using ElevenLabs.Agents;

namespace ElevenLabs.Native.Tests
{
    /// <summary>
    /// Test-only <see cref="IAudioOutputEngine"/> calibrated to the empirically
    /// observed Unity 6 behaviour described in
    /// <c>Docs~/plans/audio-output-testability.md</c>: a synchronous
    /// pre-fill inside <see cref="Start"/> (~12,800 clip-rate samples on a
    /// default Unity 6 audio config) and an ongoing
    /// <see cref="UnityEngine.AudioClip.PCMReaderCallback"/> cadence of
    /// ~60 Hz once playback is rolling (one fire per <c>clipLength</c>
    /// samples, which at 256 clip-rate samples on a 16 kHz clip and a 48 kHz
    /// output rate works out to three DSP buffers per fire). Driven from
    /// Edit-Mode tests so the SDK can exercise pre-fill, threshold-gate,
    /// and bob-alignment scenarios without standing up a real
    /// <see cref="UnityEngine.AudioSource"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Production wires <see cref="UnityAudioOutputEngine"/>; tests wire this
    /// fake via the <c>UnityAudioSourceOutput(FormatConfig, IAudioOutputEngine)</c>
    /// constructor. The fake holds no Unity-API state, runs entirely on the
    /// caller's thread, and silence-fills underrun slots the same way the
    /// production engine does — so <see cref="RecordedSilenceFillSamples"/>
    /// equals (total samples requested) − (real samples returned), giving
    /// tests a single number to assert against.
    /// </para>
    /// <para>
    /// The drain callback is the same <see cref="Func{T1, TResult}"/>
    /// <see cref="UnityAudioSourceOutput.ReadFromRing"/> exposes — return
    /// value is the count of real samples written; the fake silence-fills
    /// any remaining slots in the buffer (mirroring
    /// <see cref="UnityAudioOutputEngine"/>'s <c>OnPcmRead</c> overlay) so
    /// the engine-side contract is observable independent of whether
    /// <c>ReadFromRing</c> also silence-fills internally.
    /// </para>
    /// <para>
    /// Cadence target: defaults model <see cref="UnityAudioOutputEngine"/>
    /// (the legacy streaming-<see cref="UnityEngine.AudioClip"/> path that
    /// production currently wires). The new
    /// <see cref="UnityGeneratorAudioOutputEngine"/> path has structurally
    /// zero sync pre-fill and a ~187.5 Hz ongoing cadence (measured in
    /// <see cref="ElevenLabs.Native.PlayMode.Tests.UnityGeneratorAudioOutputEngineCharacterizationTest"/>);
    /// tests modeling that path set <c>SyncPrefillCallbackCount = 0</c> and
    /// override <see cref="OngoingCallbackPeriodSeconds"/>. With the
    /// step-5 engine-driven threshold gate, the controller's
    /// <see cref="UnityAudioSourceOutput.ComputePrefillThresholdSamples"/>
    /// automatically scales with this fake's
    /// <see cref="SyncPrefillSampleCount"/>, so a test that flips
    /// <see cref="SyncPrefillCallbackCount"/> to 0 also collapses the
    /// gate to just the DSP-buffer margin — no test-side bookkeeping
    /// required. Step 6 of
    /// <c>Docs~/plans/audio-generator-engine.md</c> swaps the production
    /// wiring; the defaults should be revisited at that point.
    /// </para>
    /// </remarks>
    internal sealed class FakeAudioOutputEngine : IAudioOutputEngine
    {
        /// <summary>
        /// Number of synchronous drain-callback fires inside <see cref="Start"/>
        /// (default 50). Combined with <see cref="SyncPrefillSampleCountPerCallback"/>,
        /// this defaults to a total of 12,800 samples — matching the observed
        /// Unity 6 streaming-buffer pre-fill on a 16 kHz clip at the default
        /// DSP buffer config (256 × 4 at 48 kHz).
        /// </summary>
        public int SyncPrefillCallbackCount { get; set; } = 50;

        /// <summary>
        /// Buffer size handed to each synchronous pre-fill drain callback
        /// (default 256). Matches Unity's per-fire buffer size when the
        /// streaming clip length is small.
        /// </summary>
        public int SyncPrefillSampleCountPerCallback { get; set; } = 256;

        /// <summary>
        /// Buffer size handed to each ongoing drain callback fired from
        /// <see cref="Tick"/> (default 256).
        /// </summary>
        public int OngoingCallbackBatchSize { get; set; } = 256;

        /// <summary>
        /// Wall-clock period (seconds) between ongoing drain callbacks
        /// (default 1/60 s ≈ 16.67 ms, matching the ~60 Hz cadence measured
        /// in <c>UnityAudioOutputEngineCharacterizationTest</c> on a Unity 6
        /// default audio config: clip rate 16 kHz, output rate 48 kHz,
        /// DSP buffer 256 — so each fire covers 256 clip samples =
        /// 768 output samples = three DSP buffers). Tick advances a budget;
        /// when the budget crosses this period, a drain callback fires and
        /// the budget subtracts the period (so multi-period advances fire
        /// multiple callbacks in sequence).
        /// </summary>
        public double OngoingCallbackPeriodSeconds { get; set; } = 1.0 / 60.0;

        /// <summary>
        /// Cumulative count of underrun samples the fake has silence-filled
        /// across every drain callback this session (sync pre-fill + Tick).
        /// Reset by <see cref="ResetRecordedCounts"/>; otherwise accumulates
        /// across multiple <see cref="Start"/> / <see cref="Stop"/> cycles
        /// so tests can snapshot before / after a phase and diff.
        /// </summary>
        public int RecordedSilenceFillSamples { get; private set; }

        /// <summary>
        /// Cumulative count of real (non-silence) samples the drain callback
        /// returned across every drain callback this session. Same reset
        /// semantics as <see cref="RecordedSilenceFillSamples"/>.
        /// </summary>
        public int RecordedRealSampleCount { get; private set; }

        /// <summary>
        /// Count of <see cref="Start"/> invocations on this fake. Tests
        /// asserting on the threshold gate read this to confirm
        /// <see cref="UnityAudioSourceOutput"/> deferred / fired
        /// <see cref="IAudioOutputEngine.Start"/> as expected.
        /// </summary>
        public int StartCallCount { get; private set; }

        /// <summary>
        /// Count of <see cref="Stop"/> invocations on this fake.
        /// </summary>
        public int StopCallCount { get; private set; }

        /// <summary>
        /// The most recent <see cref="FormatConfig"/> passed to
        /// <see cref="Start"/>. Null before the first call.
        /// </summary>
        public FormatConfig? LastStartFormat { get; private set; }

        public bool IsAvailable => !_disposed;

        public float Volume { get; set; } = 1f;

        // Derived from the configured sync-pre-fill behaviour so the
        // controller's engine-driven threshold gate stays in lockstep with
        // whatever cadence the test calibrates. Tests modeling the
        // generator path set SyncPrefillCallbackCount = 0, which collapses
        // this to 0 — matching UnityGeneratorAudioOutputEngine's
        // zero-pre-fill contract.
        public int SyncPrefillSampleCount =>
            SyncPrefillCallbackCount * SyncPrefillSampleCountPerCallback;

        private Func<float[], int>? _drainCallback;
        private double _tickBudgetSeconds;
        private bool _disposed;

        public void Start(FormatConfig format, Func<float[], int> drainCallback)
        {
            if (format == null)
                throw new ArgumentNullException(nameof(format));
            if (drainCallback == null)
                throw new ArgumentNullException(nameof(drainCallback));
            if (_disposed)
                return;
            StartCallCount++;
            LastStartFormat = format;
            _drainCallback = drainCallback;
            // Reset the Tick budget on (re)start so an old budget from a
            // previous Start doesn't fire an immediate post-Start drain.
            _tickBudgetSeconds = 0;
            for (int i = 0; i < SyncPrefillCallbackCount; i++)
            {
                FireDrain(SyncPrefillSampleCountPerCallback);
            }
        }

        public void Stop()
        {
            StopCallCount++;
            _drainCallback = null;
            _tickBudgetSeconds = 0;
        }

        public void Tick(double elapsedSeconds)
        {
            if (_drainCallback == null || _disposed)
                return;
            if (elapsedSeconds <= 0)
                return;
            if (OngoingCallbackPeriodSeconds <= 0)
                throw new InvalidOperationException(
                    "OngoingCallbackPeriodSeconds must be positive for Tick to fire drain callbacks."
                );
            _tickBudgetSeconds += elapsedSeconds;
            // Fire as many drain callbacks as elapsed time covers — keeps
            // long Tick advances behaviourally identical to many short
            // advances, so tests can step time in any granularity.
            while (_tickBudgetSeconds >= OngoingCallbackPeriodSeconds)
            {
                _tickBudgetSeconds -= OngoingCallbackPeriodSeconds;
                FireDrain(OngoingCallbackBatchSize);
                if (_drainCallback == null)
                    break; // Stop / Dispose happened inside the callback.
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _drainCallback = null;
            _tickBudgetSeconds = 0;
        }

        /// <summary>
        /// Zero the cumulative <see cref="RecordedSilenceFillSamples"/> /
        /// <see cref="RecordedRealSampleCount"/> counters. Useful when a
        /// test wants to assert on per-phase totals without tracking
        /// snapshots manually.
        /// </summary>
        public void ResetRecordedCounts()
        {
            RecordedSilenceFillSamples = 0;
            RecordedRealSampleCount = 0;
        }

        // Fire one drain callback at the requested buffer size, account for
        // the real / silence split, and silence-fill the underrun tail to
        // mirror UnityAudioOutputEngine.OnPcmRead. The buffer is allocated
        // per-call so the fake stays trivial — tests don't run hot enough to
        // care about GC pressure here.
        private void FireDrain(int bufferSize)
        {
            Func<float[], int>? cb = _drainCallback;
            if (cb == null || bufferSize <= 0)
                return;
            var buffer = new float[bufferSize];
            int real = cb(buffer);
            if (real < 0)
                real = 0;
            if (real > bufferSize)
                real = bufferSize;
            int silence = bufferSize - real;
            if (silence > 0)
                Array.Clear(buffer, real, silence);
            RecordedRealSampleCount += real;
            RecordedSilenceFillSamples += silence;
        }
    }
}
