#nullable enable

using System;
using System.Threading;
using ElevenLabs.Agents;
using UnityEngine;

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

        // Most-recent samples kept for RMS / FFT visualisers. 25 ms matches
        // UnityMicrophoneInput's analysis window so visualisers can render
        // input and output with identical band semantics.
        internal const int AnalysisWindowMs = 25;

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

        private float _userVolume = 1f;
        private CancellationTokenSource? _fadeCts;

        private GameObject? _hostObject;
        private AudioSource? _audioSource;
        private AudioClip? _outputClip;
        private int _disposed;

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
        }

        /// <summary>
        /// Production factory. Marshals onto the main thread, creates the
        /// host <see cref="GameObject"/> + <see cref="AudioSource"/> +
        /// streaming <see cref="AudioClip"/>, and starts playback. The
        /// returned instance is ready to accept <see cref="PushAudio(byte[])"/>
        /// calls immediately.
        /// </summary>
        internal static async Awaitable<UnityAudioSourceOutput> CreateAsync(
            FormatConfig format,
            OutputDeviceConfig? device = null
        )
        {
            await Awaitable.MainThreadAsync();
            var output = new UnityAudioSourceOutput(format);
            output.StartAudioSource(device);
            return output;
        }

        private void StartAudioSource(OutputDeviceConfig? device)
        {
            _hostObject = new GameObject("ElevenLabs.UnityAudioSourceOutput")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            if (Application.isPlaying)
                UnityEngine.Object.DontDestroyOnLoad(_hostObject);
            _audioSource = _hostObject.AddComponent<AudioSource>();
            // AudioClip length only governs the looping cadence (Unity drives
            // PCMReaderCallback in chunks aligned to its DSP buffer, not the
            // clip length). 100 ms is a safe lower bound that keeps the clip
            // size small while staying well above typical DSP buffer sizes.
            int clipSamples = Math.Max(1024, _format.SampleRate / 10);
            _outputClip = AudioClip.Create(
                name: "ElevenLabsAgentOutput",
                lengthSamples: clipSamples,
                channels: 1,
                frequency: _format.SampleRate,
                stream: true,
                pcmreadercallback: PCMReaderCallback
            );
            _audioSource.clip = _outputClip;
            _audioSource.loop = true;
            _audioSource.volume = _userVolume;
            _audioSource.Play();
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

        public void PushAudio(byte[] pcm)
        {
            if (pcm == null || pcm.Length < 2)
                return;
            int samples = pcm.Length / 2;
            // New agent audio arrives → cancel any in-flight interrupt fade
            // and restore the user volume so playback resumes at the right
            // level. Mirrors MediaDeviceOutput.playAudio's
            // cancelScheduledValues + gain reset before queueing the chunk.
            CancelFade();
            if (_audioSource != null)
                _audioSource.volume = _userVolume;
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
        }

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
            }
        }

        public void Interrupt(int? resetDurationMs = null)
        {
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
            // RMS over the analysis window — matches UnityMicrophoneInput's
            // GetVolume so meters render input + output identically.
            double sumSquares = 0.0;
            int count = _analysisBuffer.Length;
            lock (_bufferLock)
            {
                for (int i = 0; i < count; i++)
                {
                    float s = _analysisBuffer[i];
                    sumSquares += s * s;
                }
            }
            if (count == 0)
                return 0f;
            return Mathf.Clamp01((float)Math.Sqrt(sumSquares / count));
        }

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
