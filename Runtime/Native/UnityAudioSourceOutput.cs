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
    /// <see cref="IOutputController"/> backed by Unity's <see cref="AudioSource"/>.
    /// Each <see cref="PushAudio(byte[])"/> chunk is decoded from 16-bit
    /// little-endian PCM into floats and written into a ring buffer; a
    /// streaming <see cref="AudioClip"/> drains the ring on the audio thread
    /// via its <see cref="AudioClip.PCMReaderCallback"/>.
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
    /// events). <see cref="AudioClip.PCMReaderCallback"/> runs on Unity's
    /// audio thread — the ring buffer and analysis buffer are guarded by a
    /// single lock so the two threads never tear each other's writes.
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

        // PCMReaderCallback (audio thread) and PushAudio / Interrupt (main
        // thread) both touch _ring + _analysisBuffer. Hold this lock around
        // any read or write so the audio thread never sees a half-written
        // sample. Critical sections are O(samplesPerCallback) and lock-free
        // alternatives aren't worth the complexity at v0.1.
        private readonly object _bufferLock = new();

        private int _readPos;
        private int _writePos;
        private int _available;
        private int _analysisWritePos;

        // Wall-clock timestamp (Stopwatch ticks) of the last audio-thread drain.
        // Combined with SampleRate, lets GetVolume on the main thread interpolate
        // the playback position between PCMReaderCallback fires. Empirically the
        // callback lands around 3 Hz on a streaming AudioClip — without this
        // interpolation, GetVolume's refresh rate is bounded by that cadence
        // even when callers poll at frame rate. Updated inside _bufferLock so
        // GetVolume sees a consistent (_readPos, _available, stamp) tuple.
        // Zero before the first drain — GetVolume treats that as "no playback
        // yet" and returns 0.
        private long _lastDrainStampTicks;

        // Test seam: lets the wall-clock advance be driven manually in unit
        // tests without spinning a real audio thread. Defaults to
        // Stopwatch.GetTimestamp (monotonic, lock-free, allocation-free) in
        // production.
        internal Func<long> TimestampProvider { get; set; } = static () => Stopwatch.GetTimestamp();

        private static readonly double StopwatchTicksPerSecond =
            Stopwatch.Frequency > 0 ? Stopwatch.Frequency : 10_000_000.0;

        // Output latency in clip-rate samples — the time between Unity's audio
        // engine consuming samples (PCMReaderCallback fire) and those samples
        // reaching the speakers. Subtracted from the wall-clock-interpolated
        // virtual playback head so GetVolume reads what's *audible now*, not
        // what's *consumed now*. Without this offset the RMS leads the audio
        // by one DSP buffer (~10-30 ms on typical setups), enough to throw
        // off lip-sync visually.
        //
        // Captured at construction from AudioSettings.GetDSPBufferSize + the
        // engine output rate; readable + writable for tests that need to pin
        // it independent of the host audio configuration.
        internal int OutputLatencySamples { get; set; }

        private float _userVolume = 1f;
        private CancellationTokenSource? _fadeCts;

        private GameObject? _hostObject;
        private AudioSource? _audioSource;
        private AudioClip? _outputClip;
        private int _disposed;

        // True when _audioSource was supplied via ConversationOptions.OutputAudioSource
        // rather than created on a hidden host. Drives Close() behaviour
        // (restore vs destroy) and the mid-session destruction warning.
        private bool _suppliedSource;

        // Pre-session snapshot of SDK-owned overwrites on a supplied source,
        // restored on Close. Captured at StartAudioSource time so a session
        // that's torn down before any audio plays still rolls cleanly back.
        private float _savedVolume;
        private bool _savedLoop;
        private AudioClip? _savedClip;

        // Latches once the supplied source is observed as null (destroyed) by
        // PushAudio / Interrupt so the warning fires exactly once per session.
        // Doesn't apply to the owned-host path: HideAndDontSave + DontDestroyOnLoad
        // means external code can't destroy it.
        private bool _destructionWarned;

        // Exposed for tests — the launcher takes the factory route which
        // creates the AudioSource + AudioClip; unit tests bypass both by
        // constructing the controller directly to exercise the decode +
        // ring + analysis paths against synthetic samples.
        internal UnityAudioSourceOutput(FormatConfig format)
        {
            _format = format ?? throw new ArgumentNullException(nameof(format));
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
            OutputLatencySamples = ComputeOutputLatencySamples(format.SampleRate);
        }

        // Estimate the number of clip-rate samples between
        // PCMReaderCallback fire (audio engine consumes samples) and
        // audible speaker output. Built from AudioSettings.GetDSPBufferSize
        // (samples per DSP buffer, in output device rate) and the typical
        // double-buffering count Unity uses to keep playback smooth.
        // Falls back to a conservative 20 ms when AudioSettings hasn't been
        // initialized (e.g. batch-mode Edit Mode tests) so the compensation
        // never lands at zero and silently re-introduces the lead.
        private static int ComputeOutputLatencySamples(int clipSampleRate)
        {
            const int FallbackLatencyMs = 20;
            try
            {
                AudioSettings.GetDSPBufferSize(out int bufLen, out int bufCount);
                int outputRate = AudioSettings.outputSampleRate;
                if (bufLen <= 0 || bufCount <= 0 || outputRate <= 0)
                {
                    return clipSampleRate * FallbackLatencyMs / 1000;
                }
                // Effective latency = one full set of DSP buffers in flight
                // between consumption and audible output. Convert to clip-
                // rate samples since virtualOffset lives in that space.
                double latencySeconds = (double)bufLen * bufCount / outputRate;
                return (int)(latencySeconds * clipSampleRate);
            }
            catch
            {
                // AudioSettings.GetDSPBufferSize can throw inside isolated
                // test runners without a real audio context; swallow and
                // fall back to the conservative default rather than break
                // construction.
                return clipSampleRate * FallbackLatencyMs / 1000;
            }
        }

        /// <summary>
        /// Production factory. Marshals onto the main thread, creates the
        /// host <see cref="GameObject"/> + <see cref="AudioSource"/> +
        /// streaming <see cref="AudioClip"/>, and starts playback. The
        /// returned instance is ready to accept <see cref="PushAudio(byte[])"/>
        /// calls immediately.
        /// </summary>
        /// <param name="format">Negotiated agent-output format.</param>
        /// <param name="device">Optional output device override (logged + ignored).</param>
        /// <param name="audioSource">
        /// Optional user-supplied <see cref="AudioSource"/> to play through.
        /// When non-null, the controller binds to it instead of creating a
        /// hidden host — preserving spatialisation, mixer routing, and
        /// transform parenting. Pre-session <c>volume</c>, <c>loop</c>, and
        /// <c>clip</c> are captured and restored on <see cref="Close"/>.
        /// </param>
        internal static async Awaitable<UnityAudioSourceOutput> CreateAsync(
            FormatConfig format,
            OutputDeviceConfig? device = null,
            AudioSource? audioSource = null
        )
        {
            await Awaitable.MainThreadAsync();
            var output = new UnityAudioSourceOutput(format);
            output.StartAudioSource(device, audioSource);
            return output;
        }

        private void StartAudioSource(OutputDeviceConfig? device, AudioSource? suppliedSource)
        {
            // CRITICAL: AudioClip.Create with a PCMReaderCallback synchronously
            // fires the callback enough times to fill Unity's DSP buffer-ahead
            // queue (~8 fires × 100 ms = 800 ms on the Unity 6 default config).
            // If we create the clip here, the ring is still empty, every
            // pre-fill drains silence, and the speaker plays through that
            // silence before reaching any real audio — perceived as ~700 ms
            // of latency between session start and audible voice. Defer the
            // AudioClip.Create to the first PushAudio so the synchronous
            // pre-fills land on real samples in the ring instead.
            if (suppliedSource != null)
            {
                _suppliedSource = true;
                _audioSource = suppliedSource;
                // Capture every SDK-owned overwrite so Close can put the
                // source back exactly the way the caller handed it over.
                _savedVolume = suppliedSource.volume;
                _savedLoop = suppliedSource.loop;
                _savedClip = suppliedSource.clip;
            }
            else
            {
                _hostObject = new GameObject("ElevenLabs.UnityAudioSourceOutput")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                if (Application.isPlaying)
                    UnityEngine.Object.DontDestroyOnLoad(_hostObject);
                _audioSource = _hostObject.AddComponent<AudioSource>();
            }

            // Stop any prior playback (a supplied source might have been
            // playing something else). Critically, we also defer ASSIGNING
            // the streaming AudioClip until the first PushAudio — Unity
            // pre-fills the streaming clip's internal buffer the moment
            // it's attached to a source, and that pre-fill runs through
            // PCMReaderCallback with our (still-empty) ring, queuing
            // ~800 ms of silence ahead of any real audio. By holding the
            // clip off until we have real samples, the pre-fill picks up
            // the real audio on its first fire instead.
            _audioSource.Stop();
            _audioSource.loop = true;
            _audioSource.volume = _userVolume;
            // Output device switching is process-wide via AudioSettings; not
            // per-AudioSource. Mirror the SetDevice path's warning so callers
            // know the request didn't take effect.
            if (device != null && !string.IsNullOrEmpty(device.OutputDeviceId))
            {
                Debug.LogWarning(
                    "UnityAudioSourceOutput: per-source output device selection isn't "
                        + "supported; falling back to the system default device."
                );
            }
        }

        // True the first time we observe a supplied source as destroyed mid-session.
        // Logs a single warning, then suppresses further audio-related side effects
        // for the rest of the session. Uses Unity's overloaded == null, which
        // returns true for destroyed UnityEngine.Object references.
        private bool SuppliedSourceLost()
        {
            if (!_suppliedSource || _audioSource != null)
                return false;
            if (_disposed != 0)
                return true; // Post-Close: silently no-op (expected).
            if (!_destructionWarned)
            {
                _destructionWarned = true;
                Debug.LogWarning(
                    "[ElevenLabs] OutputAudioSource was destroyed mid-session; "
                        + "audio output disabled for the remainder of the session."
                );
            }
            return true;
        }

        // Wall-clock timestamp (Stopwatch ticks) of the very first PushAudio
        // call this session. Drives the threshold-gate timeout fallback:
        // when chunks arrive but the ring never reaches the pre-fill
        // threshold (e.g., agent ships a single short utterance), the
        // playback start fires anyway after a bounded wall-clock delay so
        // the user isn't waiting indefinitely for more audio.
        private long _firstPushAudioStampTicks;

        // True once the first real chunk has triggered AudioSource.Play().
        // Deferring Play to the moment we have enough audio queued avoids
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
            // one-time warning; subsequent pushes silently drain into the ring
            // (where they'll cycle through overrun without ever reaching the
            // audio thread, since there's no AudioSource left to drive it).
            if (SuppliedSourceLost())
                return;
            int samples = pcm.Length / 2;
            if (_firstPushAudioStampTicks == 0)
                _firstPushAudioStampTicks = TimestampProvider();
            // New agent audio arrives → cancel any in-flight interrupt fade
            // and restore the user volume so playback resumes at the right
            // level. Mirrors MediaDeviceOutput.playAudio's
            // cancelScheduledValues + gain reset before queueing the chunk.
            CancelFade();
            if (_audioSource != null)
                _audioSource.volume = _userVolume;
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
                    }
                }
            }
            // First real chunk: trigger AudioClip.Create + Play(). Both are
            // deferred from StartAudioSource because AudioClip.Create
            // SYNCHRONOUSLY fires PCMReaderCallback enough times to fill
            // Unity's streaming buffer (~12,800 clip-rate samples ≈ 800 ms
            // on the Unity 6 default audio config), and whatever the
            // callback returns gets baked into the internal clip buffer
            // ahead of the speaker — if the ring is empty or under-supplied
            // when those sync fires happen, that silence plays before the
            // real audio. Wait for the ring to hold enough samples to
            // cover the pre-fill demand (with margin), or fall back to a
            // wall-clock timeout for short single-chunk responses where
            // the threshold will never be reached. See
            // Docs~/plans/audio-output-testability.md for the design
            // history and follow-ups (timer-driven fallback + bob alignment).
            int prefillThresholdSamples = _format.SampleRate * PrefillThresholdMs / 1000;
            long timeoutTicks = (long)(PrefillTimeoutMs / 1000.0 * StopwatchTicksPerSecond);
            bool ringThresholdMet = _available >= prefillThresholdSamples;
            bool timeoutElapsed =
                _firstPushAudioStampTicks > 0
                && (TimestampProvider() - _firstPushAudioStampTicks) >= timeoutTicks;
            if (!_playbackStarted && _audioSource != null && (ringThresholdMet || timeoutElapsed))
            {
                _playbackStarted = true;
                // Keep the clip length small. Unity's pre-fill total is
                // roughly fixed at the streaming-buffer depth, but a
                // smaller clip means each PCMReaderCallback fire drains
                // less per call, which makes the underrun granularity
                // finer when the threshold gate falls back on timeout
                // with a short ring.
                int clipSamples = Math.Max(256, _format.SampleRate / 100);
                _outputClip = AudioClip.Create(
                    name: "ElevenLabsAgentOutput",
                    lengthSamples: clipSamples,
                    channels: 1,
                    frequency: _format.SampleRate,
                    stream: true,
                    pcmreadercallback: PCMReaderCallback
                );
                _audioSource.clip = _outputClip;
                _audioSource.Play();
            }
        }

        // Ring-depth gate (clip-rate ms): wait until the ring holds this
        // much audio before triggering AudioClip.Create. Sized to cover the
        // ~800 ms pre-fill Unity does synchronously inside Create, plus a
        // small safety margin so the pre-fill never silence-fills.
        internal const int PrefillThresholdMs = 900;

        // Wall-clock fallback (ms since first PushAudio): trigger anyway
        // after this elapses even if the ring never reaches the threshold,
        // so short single-chunk responses don't stall forever waiting for
        // more audio that won't arrive. NB: only fires when a subsequent
        // PushAudio runs — see audio-output-testability.md for the
        // timer-driven follow-up that handles single-chunk-then-silent.
        internal const int PrefillTimeoutMs = 500;

        // Audio-thread entry point. Unity guarantees this is called on the
        // dedicated audio thread (separate from Update / coroutines), so the
        // ring access goes through the same lock as the main-thread writes.
        private void PCMReaderCallback(float[] data) => ReadFromRing(data);

        // Internal seam so tests can drive the read path directly without
        // standing up a real AudioSource / audio thread.
        internal void ReadFromRing(float[] dest)
        {
            if (dest == null || dest.Length == 0)
                return;
            lock (_bufferLock)
            {
                int n = Math.Min(dest.Length, _available);
                for (int i = 0; i < n; i++)
                {
                    float f = _ring[_readPos];
                    dest[i] = f;
                    _readPos = (_readPos + 1) % _ringCapacity;
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
                // Underrun — fill the remainder with silence. PCMReaderCallback's
                // contract is "fill the entire buffer"; leaving the tail
                // unwritten plays whatever the buffer previously held. Feed the
                // silence into the analysis buffer too so RMS decays naturally
                // after the agent stops talking, instead of holding the last
                // non-silent value forever.
                if (n < dest.Length)
                {
                    Array.Clear(dest, n, dest.Length - n);
                    for (int i = n; i < dest.Length; i++)
                    {
                        _analysisBuffer[_analysisWritePos] = 0f;
                        _analysisWritePos = (_analysisWritePos + 1) % _analysisBuffer.Length;
                    }
                }
                // Stamp wall clock at the moment we finish this drain so
                // GetVolume can interpolate the playback position from here
                // — main-thread polling between drains gets a sweeping RMS
                // window instead of holding the audio-thread cadence value.
                _lastDrainStampTicks = TimestampProvider();
            }
        }

        public void Interrupt(int? resetDurationMs = null)
        {
            // Same warn-once gate as PushAudio so a destroyed supplied source
            // surfaces on whichever entry point fires first.
            if (SuppliedSourceLost())
                return;
            int duration = resetDurationMs ?? DefaultInterruptDurationMs;
            CancelFade();
            if (_audioSource == null || duration <= 0 || !Application.isPlaying)
            {
                // No source yet (tests), immediate-cut request, or Edit Mode
                // (Awaitable.NextFrameAsync only ticks during Play Mode) —
                // skip the fade and clear the ring synchronously.
                ClearRing();
                if (_audioSource != null)
                    _audioSource.volume = _userVolume;
                return;
            }
            _fadeCts = new CancellationTokenSource();
            _ = RunFadeAsync(duration, _fadeCts.Token);
        }

        private async Awaitable RunFadeAsync(int durationMs, CancellationToken token)
        {
            try
            {
                AudioSource source = _audioSource!;
                float startVolume = source.volume;
                float startTime = Time.unscaledTime;
                float duration = durationMs / 1000f;
                while (true)
                {
                    await Awaitable.NextFrameAsync(token);
                    if (token.IsCancellationRequested || _audioSource == null)
                        return;
                    float elapsed = Time.unscaledTime - startTime;
                    if (elapsed >= duration)
                    {
                        _audioSource.volume = 0f;
                        break;
                    }
                    _audioSource.volume = Mathf.Lerp(startVolume, 0f, elapsed / duration);
                }
                if (token.IsCancellationRequested)
                    return;
                ClearRing();
                if (_audioSource != null)
                    _audioSource.volume = _userVolume;
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
                Array.Clear(_ring, 0, _ring.Length);
                Array.Clear(_analysisBuffer, 0, _analysisBuffer.Length);
                _analysisWritePos = 0;
                // Reset the playback-position interpolation anchor so a
                // GetVolume right after an interrupt doesn't sweep into the
                // now-zeroed ring at a stale wall-clock offset.
                _lastDrainStampTicks = 0;
            }
        }

        public void SetVolume(float volume)
        {
            _userVolume = Mathf.Clamp01(volume);
            // While a fade is in flight, RunFadeAsync owns the volume value
            // and will restore _userVolume itself when the fade completes —
            // don't fight it from here.
            if (_audioSource != null && _fadeCts == null)
                _audioSource.volume = _userVolume;
        }

        public float GetVolume()
        {
            if (_audioSource == null)
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
            int sampleCount;
            lock (_bufferLock)
            {
                if (_lastDrainStampTicks == 0)
                {
                    // No drain has happened yet (pre-first-callback or post-
                    // ClearRing). The ring is either empty or carries data
                    // that hasn't yet entered playback — return 0 rather
                    // than report stale or yet-to-play audio as "current
                    // volume".
                    return 0f;
                }
                long elapsedTicks = TimestampProvider() - _lastDrainStampTicks;
                if (elapsedTicks < 0)
                    elapsedTicks = 0;
                int elapsedSamples = (int)(
                    (elapsedTicks / StopwatchTicksPerSecond) * _format.SampleRate
                );
                // Cap to queued-ahead samples — beyond that, the audio engine
                // would be in underrun and we'd be sweeping into silence-fill
                // territory. Holding the offset at _available keeps the RMS
                // window pinned at the tail of buffered audio until the next
                // chunk arrives.
                int virtualOffset = Math.Min(elapsedSamples, _available);
                // Subtract the audio engine's output latency so the RMS reflects
                // what's *audible* at the speaker right now, not what Unity has
                // *consumed* from the AudioClip — those differ by one set of
                // DSP buffers in flight (~10-30 ms on typical setups). Without
                // this, the bob leads the audio noticeably during lip-sync.
                // Clamp at 0 so early-playback callers read the most-recently-
                // drained window instead of wrapping into stale slots.
                int audibleHead = Math.Max(0, virtualOffset - OutputLatencySamples);
                // RMS window ends at the audible playback head. Walk back
                // windowSamples from there to capture the most-recently-played
                // envelope.
                for (int i = 0; i < windowSamples; i++)
                {
                    int rel = _readPos + audibleHead - windowSamples + i;
                    int pos = rel % _ringCapacity;
                    if (pos < 0)
                        pos += _ringCapacity;
                    float s = _ring[pos];
                    sumSquares += s * s;
                }
                sampleCount = windowSamples;
            }
            return Mathf.Clamp01((float)Math.Sqrt(sumSquares / sampleCount));
        }

        // Test seam: bypasses the AudioSource-null short-circuit so unit
        // tests can drive the wall-clock-interpolated RMS path directly.
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
            if (_audioSource == null)
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
            await Awaitable.MainThreadAsync();
            if (_audioSource != null)
            {
                try
                {
                    _audioSource.Stop();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
                if (_suppliedSource)
                {
                    // Restore every SDK-owned overwrite (volume, loop, clip)
                    // symmetrically so a subsequent session — or non-agent use
                    // of the same source — starts from the caller's original
                    // configuration. The AudioSource and its GameObject are
                    // left intact for the user to reuse.
                    try
                    {
                        _audioSource.clip = _savedClip;
                        _audioSource.loop = _savedLoop;
                        _audioSource.volume = _savedVolume;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }
            if (_outputClip != null)
            {
                DestroyObject(_outputClip);
                _outputClip = null;
            }
            if (_hostObject != null)
            {
                DestroyObject(_hostObject);
                _hostObject = null;
            }
            _savedClip = null;
            _audioSource = null;
        }

        private static void DestroyObject(UnityEngine.Object obj)
        {
            // Destroy is a Play-Mode operation; Edit Mode (and headless test
            // runs) need DestroyImmediate or Unity logs a "Destroy may not be
            // called from edit mode" warning and leaks the object.
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
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
