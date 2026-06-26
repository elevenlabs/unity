#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using ElevenLabs.Agents;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ElevenLabs.Native
{
    /// <summary>
    /// <see cref="IOutputController"/> backed by an <see cref="IAudioOutputEngine"/>
    /// (production: Unity's <see cref="AudioSource"/> + streaming
    /// <see cref="AudioClip"/>). Each <see cref="PushAudio(byte[])"/> chunk
    /// is decoded from 16-bit little-endian PCM into floats and written into
    /// a ring buffer; the engine drains the ring on Unity's audio thread via
    /// the callback registered with <see cref="IAudioOutputEngine.Start"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors <c>MediaDeviceOutput</c> from <c>@elevenlabs/client</c>: queued
    /// chunks play in order, <see cref="Interrupt(int?)"/> fades the source
    /// volume down over <c>resetDurationMs</c> and then flushes the ring, and
    /// the next <see cref="PushAudio(byte[])"/> snaps volume back to the
    /// user-set level.
    /// </para>
    /// <para>
    /// Threading: <see cref="PushAudio(byte[])"/>, <see cref="Interrupt(int?)"/>,
    /// <see cref="SetVolume(float)"/>, and lifecycle methods run on the Unity
    /// main thread (the native read loop marshals there before raising
    /// events). <see cref="IAudioOutputEngine"/> invokes the drain callback
    /// on Unity's audio thread — the ring buffer and analysis buffer are
    /// guarded by a single lock so the two threads never tear each other's
    /// writes.
    /// </para>
    /// <para>
    /// All Unity-API interaction (AudioClip lifecycle, AudioSource.Play/Stop,
    /// host GameObject setup, supplied-source restoration) lives inside the
    /// injected <see cref="IAudioOutputEngine"/>. The plumbing in this class
    /// is engine-agnostic, which lets Edit-Mode tests drive ring / decode /
    /// volume-math paths against a no-op
    /// <see cref="NullAudioOutputEngine"/> — and (step 2 of
    /// <c>Docs~/plans/audio-output-testability.md</c>) lets future tests
    /// drive a calibrated <c>FakeAudioOutputEngine</c> for pre-fill /
    /// drain-cadence scenarios.
    /// </para>
    /// </remarks>
    internal sealed class UnityAudioSourceOutput : IOutputController
    {
        // Five seconds of slack is generous enough for typical network jitter
        // without growing the per-session footprint past ~480 KB at 24 kHz.
        // The JS SDK's audioConcatProcessor doesn't cap its queue at all;
        // we cap explicitly so a slow main thread can't pin memory.
        internal const float RingBufferLengthSec = 5f;

        // Default fade window when Interrupt is invoked without an override —
        // matches MediaDeviceOutput.interrupt(resetDuration = 2000) in
        // @elevenlabs/client.
        internal const int DefaultInterruptDurationMs = 2000;

        // Most-recent samples kept for RMS / FFT visualisers. 5 ms (~240 samples
        // at 48 kHz) tracks syllable-level envelope tightly enough that a
        // GetVolume-driven scale pulse matches the audible cadence of
        // `audioSource.GetOutputData(buffer:256, channel:0)` — the pre-step-6
        // pattern devs were using directly. The previous 25 ms window dragged
        // the bob behind the agent's voice by enough to look sluggish on the
        // shipped TalkingBox sample.
        internal const int AnalysisWindowMs = 5;

        private readonly FormatConfig _format;
        private readonly int _ringCapacity;
        private readonly float[] _ring;
        private readonly float[] _analysisBuffer;
        private readonly IAudioOutputEngine _engine;

        // PCMReaderCallback (audio thread, invoked via the engine's drain
        // callback) and PushAudio / Interrupt (main thread) both touch _ring
        // + _analysisBuffer. Hold this lock around any read or write so the
        // audio thread never sees a half-written sample. Critical sections
        // are O(samplesPerCallback) and lock-free alternatives aren't worth
        // the complexity at v0.1.
        private readonly object _bufferLock = new();

        private int _readPos;
        private int _writePos;
        private int _available;
        private int _analysisWritePos;

        // Monotonically-increasing counters parallel to the modular _readPos /
        // _writePos. Used by the wall-clock GetVolume math (audible playback
        // head is anchored in linear ring coordinates so the modular wrap
        // doesn't introduce edge cases). `long` is sized for multi-hour
        // sessions without wrapping.
        private long _readPosLinear;
        private long _writePosLinear;

        // Wall-clock timestamp (Stopwatch ticks) at the moment playback became
        // audible — set by ReadFromRing on its first call (which happens
        // inside engine.Start's synchronous pre-fill, or on the first drain
        // after Interrupt/ClearRing resets the anchors). Combined with
        // _playbackStartRingPos, lets GetVolume compute the audible playback
        // head as a pure wall-clock count, independent of engine drain
        // cadence and independent of how much Unity pre-buffered ahead of the
        // speaker. Zero before the first drain — GetVolume treats that as
        // "no playback yet" and returns 0.
        private long _playbackStartStampTicks;

        // Linear ring position (in samples written across the session) the
        // speaker started playing from. Equals _readPosLinear at the moment
        // _playbackStartStampTicks was captured — i.e., where the drain head
        // was BEFORE Unity's first drain consumed any samples. The audible
        // head sweeps forward from here at the configured sample rate
        // regardless of how many further drains have fired.
        private long _playbackStartRingPos;

        // Test seam: lets the wall-clock advance be driven manually in unit
        // tests without spinning a real audio thread. Defaults to
        // Stopwatch.GetTimestamp (monotonic, lock-free, allocation-free) in
        // production.
        internal Func<long> TimestampProvider { get; set; } = static () => Stopwatch.GetTimestamp();

        private static readonly double StopwatchTicksPerSecond =
            Stopwatch.Frequency > 0 ? Stopwatch.Frequency : 10_000_000.0;

        // Test seam: the awaitable used by the single-chunk pre-fill timeout
        // fallback. Production wires Awaitable.WaitForSecondsAsync; tests
        // inject an AwaitableCompletionSource they control so the timeout
        // fires deterministically without the Unity frame loop. Signature
        // mirrors the production overload (seconds + cancellation).
        internal Func<
            float,
            CancellationToken,
            Awaitable
        > WaitForSecondsAsyncProvider { get; set; } =
            static (sec, ct) => Awaitable.WaitForSecondsAsync(sec, ct);

        private float _userVolume = 1f;
        private CancellationTokenSource? _fadeCts;
        private CancellationTokenSource? _prefillTimeoutCts;
        private int _disposed;

        /// <summary>
        /// Bare-engine constructor for Edit-Mode tests that exercise the
        /// ring / decode / volume-math paths directly against synthetic
        /// samples — wires a no-op <see cref="NullAudioOutputEngine"/> so
        /// the controller logic stays uniform without needing a real
        /// <see cref="AudioSource"/>. Production callers go through
        /// <see cref="CreateAsync"/>, which wires
        /// <see cref="UnityAudioOutputEngine"/>.
        /// </summary>
        internal UnityAudioSourceOutput(FormatConfig format)
            : this(format, new NullAudioOutputEngine()) { }

        /// <summary>
        /// Construct with an explicit <see cref="IAudioOutputEngine"/>.
        /// Production passes <see cref="UnityAudioOutputEngine"/>; future
        /// Edit-Mode tests can inject a calibrated fake to drive the
        /// engine's pre-fill / drain-cadence behaviour deterministically
        /// (see <c>Docs~/plans/audio-output-testability.md</c>).
        /// </summary>
        internal UnityAudioSourceOutput(FormatConfig format, IAudioOutputEngine engine)
        {
            _format = format ?? throw new ArgumentNullException(nameof(format));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            if (format.SampleRate <= 0)
                throw new ArgumentException(
                    "FormatConfig.SampleRate must be positive.",
                    nameof(format)
                );
            // ulaw is documented in the AsyncAPI surface but the JS SDK's
            // worklet does the codec decoding in JS. v0.1 native is PCM-only;
            // ulaw lands alongside a hand-rolled decoder in a follow-up.
            if (!string.Equals(format.Format, "pcm", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"UnityAudioSourceOutput supports PCM only at v0.1; got '{format.Format}'."
                );
            _ringCapacity = Math.Max(1, (int)Math.Round(format.SampleRate * RingBufferLengthSec));
            _ring = new float[_ringCapacity];
            int windowSamples = Math.Max(1, format.SampleRate * AnalysisWindowMs / 1000);
            _analysisBuffer = new float[windowSamples];
        }

        /// <summary>
        /// Production factory. Marshals onto the main thread, constructs a
        /// <see cref="UnityAudioOutputEngine"/> (which sets up the host
        /// <see cref="GameObject"/> + <see cref="AudioSource"/>), and
        /// returns a controller wired to it. Playback itself — the
        /// streaming <see cref="AudioClip"/> + <see cref="AudioSource.Play"/>
        /// — is deferred to the first <see cref="PushAudio(byte[])"/>
        /// crossing the pre-fill threshold; see
        /// <c>Docs~/plans/audio-output-testability.md</c> for the design
        /// rationale.
        /// </summary>
        /// <param name="format">Negotiated agent-output format.</param>
        /// <param name="device">Optional output device override (logged + ignored).</param>
        /// <param name="audioSource">
        /// Optional user-supplied <see cref="AudioSource"/> to play through.
        /// When non-null, the engine binds to it instead of creating a hidden
        /// host — preserving spatialisation, mixer routing, and transform
        /// parenting. Pre-session <c>volume</c>, <c>loop</c>, and <c>clip</c>
        /// are captured and restored on <see cref="Close"/>.
        /// </param>
        internal static async Awaitable<UnityAudioSourceOutput> CreateAsync(
            FormatConfig format,
            OutputDeviceConfig? device = null,
            AudioSource? audioSource = null
        )
        {
            await Awaitable.MainThreadAsync();
            // Engine construction runs Unity-API setup (GameObject.Create +
            // AddComponent on the owned-host path; pre-session snapshot
            // capture on the supplied-source path), so the main-thread
            // await above is load-bearing.
            IAudioOutputEngine engine = new UnityAudioOutputEngine(audioSource, device);
            return new UnityAudioSourceOutput(format, engine);
        }

        // True once the first real chunk has triggered IAudioOutputEngine.Start
        // (AudioClip.Create + AudioSource.Play under the hood). Deferring
        // engine.Start to the moment we have enough audio queued avoids
        // Unity pre-buffering hundreds of milliseconds of silence ahead of
        // the real samples — that silence has to drain through the speaker
        // before the agent's voice is heard, and shows up as a perceived
        // end-to-end latency of ~700 ms on a typical Unity 6 audio config
        // (DSP buffer 256 × 4 at 48 kHz, clip at 16 kHz). See
        // Docs~/plans/audio-output-testability.md for the full story.
        private bool _playbackStarted;

        public void PushAudio(byte[] pcm)
        {
            if (pcm == null || pcm.Length < 2)
                return;
            // Surface mid-session destruction of a supplied source through a
            // one-time warning (the engine fires it lazily from
            // IsAvailable); subsequent pushes silently no-op.
            if (!_engine.IsAvailable)
                return;
            int samples = pcm.Length / 2;
            // New agent audio arrives → cancel any in-flight interrupt fade
            // and restore the user volume so playback resumes at the right
            // level. Mirrors MediaDeviceOutput.playAudio's
            // cancelScheduledValues + gain reset before queueing the chunk.
            CancelFade();
            _engine.Volume = _userVolume;
            // Write the chunk to the ring BEFORE flipping playback on. Unity
            // pre-fills the streaming clip's internal buffer the moment
            // Play() is called (8+ PCMReaderCallback fires at 100 ms each
            // = ~800 ms of buffer depth on the Unity 6 default audio
            // config); if the ring is empty when those fire, they all
            // silence-fill and queue ahead of the real audio. Writing
            // first means the pre-fills land on real samples.
            lock (_bufferLock)
            {
                for (int i = 0; i < samples; i++)
                {
                    short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                    float f = DecodeInt16Sample(s);
                    _ring[_writePos] = f;
                    _writePos = (_writePos + 1) % _ringCapacity;
                    _writePosLinear++;
                    if (_available < _ringCapacity)
                    {
                        _available++;
                    }
                    else
                    {
                        // Overrun — drop the oldest sample by advancing the
                        // read pointer. A buffer overflow at the SDK's
                        // chunk cadence usually means the audio thread isn't
                        // keeping up, not that the agent is too fast.
                        _readPos = (_readPos + 1) % _ringCapacity;
                        _readPosLinear++;
                    }
                }
            }
            // First real chunk: trigger engine.Start() (AudioClip.Create +
            // AudioSource.Play under the hood). Deferred from CreateAsync
            // because AudioClip.Create SYNCHRONOUSLY fires the drain
            // callback enough times to fill Unity's streaming buffer
            // (~12,800 clip-rate samples ≈ 800 ms on the Unity 6 default
            // audio config), and whatever the callback returns gets baked
            // into the internal clip buffer ahead of the speaker — if the
            // ring is empty or under-supplied when those sync fires
            // happen, that silence plays before the real audio. Wait for
            // the ring to hold enough samples to cover the pre-fill demand
            // (with margin), or fall back to a wall-clock timeout for
            // short single-chunk responses where the threshold will never
            // be reached. See Docs~/plans/audio-output-testability.md and
            // Docs~/plans/bob-alignment-redesign.md.
            if (_playbackStarted)
                return;
            int prefillThresholdSamples = _format.SampleRate * PrefillThresholdMs / 1000;
            if (_available >= prefillThresholdSamples)
            {
                CancelPrefillTimeout();
                StartPlayback();
            }
            else if (_prefillTimeoutCts == null)
            {
                _prefillTimeoutCts = new CancellationTokenSource();
                _ = RunPrefillTimeoutAsync(_prefillTimeoutCts.Token);
            }
        }

        // Ring-depth gate (clip-rate ms): wait until the ring holds this
        // much audio before triggering AudioClip.Create. Sized to cover the
        // ~800 ms pre-fill Unity does synchronously inside Create, plus a
        // small safety margin so the pre-fill never silence-fills.
        internal const int PrefillThresholdMs = 900;

        // Wall-clock fallback (ms after the first PushAudio that didn't
        // trip the threshold): trigger engine.Start anyway after this
        // elapses, so short single-chunk responses don't stall forever
        // waiting for more audio that won't arrive. Driven by an
        // Awaitable.WaitForSecondsAsync (via the WaitForSecondsAsyncProvider
        // test seam), so the fallback fires even with no follow-up
        // PushAudio.
        internal const int PrefillTimeoutMs = 500;

        // Common entry into engine.Start — kept as a separate helper so
        // both the threshold-met path (called from PushAudio) and the
        // wall-clock-timeout path (called from RunPrefillTimeoutAsync) flow
        // through the same idempotent gate. ReadFromRing stamps the
        // audible-head anchors on its first call inside engine.Start's
        // synchronous pre-fill.
        private void StartPlayback()
        {
            if (_playbackStarted)
                return;
            _playbackStarted = true;
            _engine.Start(_format, ReadFromRing);
        }

        private async Awaitable RunPrefillTimeoutAsync(CancellationToken token)
        {
            try
            {
                await WaitForSecondsAsyncProvider(PrefillTimeoutMs / 1000f, token);
                if (token.IsCancellationRequested || _playbackStarted || !_engine.IsAvailable)
                    return;
                StartPlayback();
            }
            catch (OperationCanceledException)
            {
                // Expected when the threshold trips before the timeout or
                // when Close/Dispose tears the controller down.
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private void CancelPrefillTimeout()
        {
            if (_prefillTimeoutCts == null)
                return;
            try
            {
                _prefillTimeoutCts.Cancel();
            }
            catch (ObjectDisposedException) { }
            try
            {
                _prefillTimeoutCts.Dispose();
            }
            catch (ObjectDisposedException) { }
            _prefillTimeoutCts = null;
        }

        // Engine drain callback — Unity guarantees this runs on the dedicated
        // audio thread (separate from Update / coroutines), so the ring
        // access goes through the same lock as the main-thread writes.
        // Returns the number of REAL samples written; the engine silence-
        // fills any remaining slots before handing the buffer back to
        // Unity. The local silence-fill below stays put so the analysis
        // buffer decays naturally after the agent stops talking — and so
        // existing Edit-Mode tests that drive ReadFromRing directly still
        // see a silence-filled dest array on underrun.
        internal int ReadFromRing(float[] dest)
        {
            if (dest == null || dest.Length == 0)
                return 0;
            lock (_bufferLock)
            {
                long preDrainReadPosLinear = _readPosLinear;
                int n = Math.Min(dest.Length, _available);
                for (int i = 0; i < n; i++)
                {
                    float f = _ring[_readPos];
                    dest[i] = f;
                    _readPos = (_readPos + 1) % _ringCapacity;
                    _readPosLinear++;
                    // Feed the analysis buffer here (audio-thread drain) rather
                    // than at PushAudio time so GetVolume / GetByteFrequencyData
                    // reflect what's *playing right now*, not what was last
                    // received over the network. Without this, visualisers
                    // freeze on the last received chunk while the ring drains
                    // for the next few seconds of playback.
                    _analysisBuffer[_analysisWritePos] = f;
                    _analysisWritePos = (_analysisWritePos + 1) % _analysisBuffer.Length;
                }
                _available -= n;
                // Underrun — fill the remainder with silence. Drain callbacks
                // must fill the entire buffer; leaving the tail unwritten
                // plays whatever the buffer previously held. Feed the
                // silence into the analysis buffer too so RMS decays
                // naturally after the agent stops talking, instead of
                // holding the last non-silent value forever.
                if (n < dest.Length)
                {
                    Array.Clear(dest, n, dest.Length - n);
                    for (int i = n; i < dest.Length; i++)
                    {
                        _analysisBuffer[_analysisWritePos] = 0f;
                        _analysisWritePos = (_analysisWritePos + 1) % _analysisBuffer.Length;
                    }
                }
                // First drain after engine.Start (or after Interrupt/ClearRing
                // reset the anchors) that actually consumed real samples —
                // capture the audible-head anchors. The pre-drain linear
                // position is "where the speaker started playing from"
                // (everything before this is pre-history, not audible). The
                // current wall-clock stamp is "when the speaker started
                // playing" — give or take Unity's DSP-buffer latency
                // (~21 ms), which we tolerate to keep the model simple. From
                // here, ComputeWallClockRms sweeps the audible head forward
                // at the sample rate, independent of further drains.
                //
                // Gating on n > 0 is load-bearing for the second-turn case:
                // between turns the ring is empty and the audio thread keeps
                // firing silence-only drains. Stamping on those would anchor
                // the wall-clock model at the silence-gap start, so by the
                // time real samples for the next turn arrive seconds later
                // the audible head has already swept forward into them and
                // GetVolume reads the chunk's tail instead of 0. Holding off
                // until a drain actually consumes real samples keeps the
                // anchor aligned with audible playback across interrupts.
                // (Doesn't compensate for Unity's internal streaming-buffer
                // silence depth — see the audio-output-filter-engine.md
                // follow-up.)
                if (_playbackStartStampTicks == 0 && n > 0)
                {
                    _playbackStartRingPos = preDrainReadPosLinear;
                    _playbackStartStampTicks = TimestampProvider();
                }
                return n;
            }
        }

        public void Interrupt(int? resetDurationMs = null)
        {
            // Same warn-once gate as PushAudio so a destroyed supplied source
            // surfaces on whichever entry point fires first.
            if (!_engine.IsAvailable)
                return;
            int duration = resetDurationMs ?? DefaultInterruptDurationMs;
            CancelFade();
            if (duration <= 0 || !Application.isPlaying)
            {
                // Immediate-cut request, or Edit Mode
                // (Awaitable.NextFrameAsync only ticks during Play Mode) —
                // skip the fade and clear the ring synchronously.
                ClearRing();
                _engine.Volume = _userVolume;
                return;
            }
            _fadeCts = new CancellationTokenSource();
            _ = RunFadeAsync(duration, _fadeCts.Token);
        }

        private async Awaitable RunFadeAsync(int durationMs, CancellationToken token)
        {
            try
            {
                float startVolume = _engine.Volume;
                float startTime = Time.unscaledTime;
                float duration = durationMs / 1000f;
                while (true)
                {
                    await Awaitable.NextFrameAsync(token);
                    if (token.IsCancellationRequested || !_engine.IsAvailable)
                        return;
                    float elapsed = Time.unscaledTime - startTime;
                    if (elapsed >= duration)
                    {
                        _engine.Volume = 0f;
                        break;
                    }
                    _engine.Volume = Mathf.Lerp(startVolume, 0f, elapsed / duration);
                }
                if (token.IsCancellationRequested)
                    return;
                ClearRing();
                if (_engine.IsAvailable)
                    _engine.Volume = _userVolume;
            }
            catch (OperationCanceledException)
            {
                // Expected when PushAudio or Close cancels the fade.
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private void CancelFade()
        {
            if (_fadeCts == null)
                return;
            try
            {
                _fadeCts.Cancel();
            }
            catch (ObjectDisposedException) { }
            try
            {
                _fadeCts.Dispose();
            }
            catch (ObjectDisposedException) { }
            _fadeCts = null;
        }

        // Drop every queued sample. Called when the agent is interrupted so
        // the next chunk plays from silence instead of catching up.
        internal void ClearRing()
        {
            lock (_bufferLock)
            {
                _readPos = 0;
                _writePos = 0;
                _available = 0;
                _readPosLinear = 0;
                _writePosLinear = 0;
                Array.Clear(_ring, 0, _ring.Length);
                Array.Clear(_analysisBuffer, 0, _analysisBuffer.Length);
                _analysisWritePos = 0;
                // Reset the audible-head anchors so a GetVolume right after
                // an interrupt short-circuits to 0 instead of sweeping into
                // the now-zeroed ring at a stale wall-clock offset. The next
                // drain after a follow-up PushAudio will re-stamp.
                _playbackStartStampTicks = 0;
                _playbackStartRingPos = 0;
            }
        }

        public void SetVolume(float volume)
        {
            _userVolume = Mathf.Clamp01(volume);
            // While a fade is in flight, RunFadeAsync owns the volume value
            // and will restore _userVolume itself when the fade completes —
            // don't fight it from here.
            if (_fadeCts == null)
                _engine.Volume = _userVolume;
        }

        public float GetVolume()
        {
            if (!_engine.IsAvailable)
                return 0f;
            return ComputeWallClockRms();
        }

        // Wall-clock-interpolated RMS over the most-recently-played 5 ms
        // window, factored out from GetVolume so unit tests can exercise
        // the compute without standing up a real AudioSource.
        //
        // Sweeps forward through the ring at the caller's poll rate, not at
        // PCMReaderCallback's audio-thread cadence (~3 Hz on a streaming
        // AudioClip empirically, regardless of DSP buffer size). Matches the
        // WebGL backend's AnalyserNode behaviour where each
        // getByteTimeDomainData call returns a fresh snapshot from the live
        // audio graph. The 5 ms window matches UnityMicrophoneInput's
        // GetVolume so meters render input + output identically.
        private float ComputeWallClockRms()
        {
            int windowSamples = _analysisBuffer.Length;
            if (windowSamples == 0)
                return 0f;
            double sumSquares = 0.0;
            lock (_bufferLock)
            {
                if (_playbackStartStampTicks == 0)
                {
                    // No drain has happened yet (pre-first-callback or post-
                    // ClearRing). The ring is either empty or carries data
                    // that hasn't yet entered playback — return 0 rather
                    // than report stale or yet-to-play audio as "current
                    // volume".
                    return 0f;
                }
                long elapsedTicks = TimestampProvider() - _playbackStartStampTicks;
                if (elapsedTicks < 0)
                    elapsedTicks = 0;
                // Audible playback head as a linear ring position: anchored at
                // _playbackStartRingPos (where the drain head was BEFORE the
                // first drain consumed any samples) and swept forward at the
                // configured sample rate. Independent of how many drains have
                // fired since — Unity's streaming-buffer pre-fill (~12,800
                // samples on a typical Unity 6 config) drained ahead of the
                // speaker, and reading those samples here would put the bob
                // ~800 ms ahead of the audio.
                long elapsedSamples = (long)(
                    (elapsedTicks / StopwatchTicksPerSecond) * _format.SampleRate
                );
                long audibleRingPos = _playbackStartRingPos + elapsedSamples;
                // Clamp at the write head — beyond that, no audio has been
                // queued, so reading would sweep into zero-initialised ring
                // slots (silence). Pinning at _writePosLinear keeps the RMS
                // window at the tail of what's been buffered.
                if (audibleRingPos > _writePosLinear)
                    audibleRingPos = _writePosLinear;
                long windowStart = audibleRingPos - windowSamples;
                long minReadable = _writePosLinear - _ringCapacity;
                if (minReadable < 0)
                    minReadable = 0;
                for (int i = 0; i < windowSamples; i++)
                {
                    long linearPos = windowStart + i;
                    if (linearPos < _playbackStartRingPos || linearPos < minReadable)
                    {
                        // Pre-history (before the speaker started playing) or
                        // overwritten by ring wrap — pad with silence so the
                        // RMS naturally ramps in/out instead of reading stale
                        // or unmapped slots.
                        continue;
                    }
                    if (linearPos >= audibleRingPos)
                    {
                        // After the audible head — hasn't been played yet.
                        continue;
                    }
                    int idx = (int)(linearPos % _ringCapacity);
                    if (idx < 0)
                        idx += _ringCapacity;
                    float s = _ring[idx];
                    sumSquares += s * s;
                }
            }
            return Mathf.Clamp01((float)Math.Sqrt(sumSquares / windowSamples));
        }

        // Test seam: bypasses the engine.IsAvailable short-circuit so unit
        // tests can drive the wall-clock-interpolated RMS path directly,
        // even with a bare-constructor (NullEngine) controller.
        internal float Test_ComputeWallClockRms() => ComputeWallClockRms();

        public Awaitable SetDevice(OutputDeviceConfig? config = null, FormatConfig? format = null)
        {
            // Sample-rate change isn't supported once the AudioClip exists —
            // PCMReaderCallback's frequency is fixed at AudioClip.Create time
            // (recreating the clip while playing would skip frames). Mirrors
            // MediaDeviceOutput.setDevice's documented limitation in the JS SDK.
            if (format != null && format.SampleRate != _format.SampleRate)
                throw new NotSupportedException(
                    "Changing the output sample rate after session start is not supported; "
                        + "restart the session with the new format instead."
                );
            if (config != null && !string.IsNullOrEmpty(config.OutputDeviceId))
            {
                Debug.LogWarning(
                    "UnityAudioSourceOutput: per-source output device selection isn't "
                        + "supported; the requested device id was ignored."
                );
            }
            return CompletedAwaitable();
        }

        public void GetByteFrequencyData(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
                return;
            if (!_engine.IsAvailable)
            {
                Array.Clear(buffer, 0, buffer.Length);
                return;
            }
            float[] snapshot;
            int count;
            lock (_bufferLock)
            {
                count = _analysisBuffer.Length;
                snapshot = new float[count];
                Array.Copy(_analysisBuffer, snapshot, count);
            }
            // FFT helper lives on UnityMicrophoneInput so the visualiser
            // semantics (voice-band mapping, dB normalisation) stay identical
            // across input and output. Both classes are internal to this
            // assembly; promote the helper to a shared module if a third
            // native audio class needs it.
            UnityMicrophoneInput.ComputeByteFrequencyData(
                snapshot,
                count,
                _format.SampleRate,
                buffer
            );
        }

        public async Awaitable Close()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;
            CancelFade();
            CancelPrefillTimeout();
            await Awaitable.MainThreadAsync();
            // Engine handles AudioSource.Stop + supplied-source restoration +
            // host GameObject / AudioClip destruction — everything Unity-API
            // shaped lives behind the seam so this class stays
            // engine-agnostic.
            _engine.Dispose();
        }

        // Static helpers — pulled out so unit tests can exercise the decoder
        // and capacity math without an active AudioSource / audio thread.

        /// <summary>
        /// Decode <paramref name="byteCount"/> bytes of 16-bit little-endian
        /// PCM from <paramref name="source"/> into floats in <c>[-1, 1]</c>
        /// in <paramref name="dest"/>. Returns the number of samples written.
        /// </summary>
        /// <remarks>
        /// Mirrors the asymmetry in <see cref="UnityMicrophoneInput.EncodePcm16"/>:
        /// negative samples scale by 32768 so <see cref="short.MinValue"/>
        /// maps to exactly <c>-1.0</c>; positives scale by 32767 so
        /// <see cref="short.MaxValue"/> maps to <c>~1.0</c> without overflow.
        /// </remarks>
        internal static int DecodePcm16(byte[] source, int byteCount, float[] dest)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (dest == null)
                throw new ArgumentNullException(nameof(dest));
            if (byteCount < 0 || byteCount > source.Length)
                throw new ArgumentOutOfRangeException(nameof(byteCount));
            int samples = byteCount / 2;
            if (dest.Length < samples)
                throw new ArgumentException(
                    "Destination buffer is too small for the requested sample count.",
                    nameof(dest)
                );
            for (int i = 0; i < samples; i++)
            {
                short s = (short)(source[i * 2] | (source[i * 2 + 1] << 8));
                dest[i] = DecodeInt16Sample(s);
            }
            return samples;
        }

        private static float DecodeInt16Sample(short s) => s < 0 ? s / 32768f : s / 32767f;

        /// <summary>
        /// Capacity (in samples) the ring buffer holds for a given
        /// <paramref name="sampleRate"/>. Exposed for tests to pin format
        /// negotiation propagation.
        /// </summary>
        internal static int CalculateRingCapacity(int sampleRate)
        {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            return Math.Max(1, (int)Math.Round(sampleRate * RingBufferLengthSec));
        }

        // Test-only inspectors — let tests assert on the ring state without
        // promoting the fields themselves to internal mutables.
        internal int Test_RingCapacity => _ringCapacity;
        internal int Test_AvailableSamples
        {
            get
            {
                lock (_bufferLock)
                    return _available;
            }
        }
        internal float Test_AnalysisBufferRms
        {
            get
            {
                lock (_bufferLock)
                {
                    double sumSquares = 0.0;
                    int count = _analysisBuffer.Length;
                    for (int i = 0; i < count; i++)
                        sumSquares += _analysisBuffer[i] * _analysisBuffer[i];
                    return count == 0 ? 0f : (float)Math.Sqrt(sumSquares / count);
                }
            }
        }

        private static Awaitable CompletedAwaitable()
        {
            var source = new AwaitableCompletionSource();
            source.SetResult();
            return source.Awaitable;
        }
    }
}
