#nullable enable

using System;
using System.Numerics;
using System.Threading;
using ElevenLabs.Agents;
using UnityEngine;

namespace ElevenLabs.Native
{
    /// <summary>
    /// <see cref="IInputController"/> backed by Unity's <see cref="Microphone"/>
    /// API. Captures audio into a 1-second ring <see cref="AudioClip"/> and
    /// polls it on the main thread at the SDK's default 25 ms chunk cadence,
    /// converting float samples to 16-bit little-endian PCM before raising
    /// <see cref="AudioChunkAvailable"/> for the <see cref="Conversation"/>
    /// router to forward as <c>UserAudioChunk</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors <c>MediaDeviceInput</c> from <c>@elevenlabs/client</c>: the mic
    /// stays running while muted and the polling loop emits silence (zero
    /// bytes) for muted chunks rather than pausing the device. Pause/resume
    /// of OS microphones has too many device-specific surprises to be
    /// reliable for a mute toggle.
    /// </para>
    /// <para>
    /// All <see cref="Microphone"/> / <see cref="AudioClip"/> calls happen on
    /// the Unity main thread; the polling loop yields with
    /// <see cref="Awaitable.WaitForSecondsAsync"/> which keeps the
    /// continuation on the main thread. The launcher constructs this via
    /// <see cref="CreateAsync"/>, which itself marshals onto the main thread
    /// before touching any Unity API.
    /// </para>
    /// </remarks>
    internal sealed class UnityMicrophoneInput : IInputController
    {
        // Matches DEFAULT_INPUT_CHUNK_DURATION_MS in @elevenlabs/client.
        internal const int InputChunkDurationMs = 25;

        // Unity's Microphone.Start requires an AudioClip length; one second is
        // a generous ring so the polling loop has slack if a frame is missed.
        internal const int RingBufferLengthSec = 1;

        // Bands for GetByteFrequencyData are mapped to the JS-side analyser's
        // dB range (-100 dB → 0, -30 dB → 255). Matches AnalyserNode defaults
        // closely enough for a visualiser; not a signal-processing reference.
        private const float MinDecibels = -100f;
        private const float MaxDecibels = -30f;

        // Voice band the JS SDK's visualiser samples render against. The
        // upper edge is clamped to Nyquist at runtime so a low-rate mic
        // (e.g. 8 kHz µ-law) doesn't trip the band span check.
        private const float VoiceBandStartHz = 100f;
        private const float VoiceBandEndHz = 8000f;

        private readonly FormatConfig _format;
        private readonly int _samplesPerChunk;
        private readonly int _ringLengthSamples;
        private readonly float[] _floatBuffer;
        private readonly byte[] _pcmBuffer;
        private readonly CancellationTokenSource _cts = new();

        private string? _deviceName;
        private AudioClip? _micClip;
        private int _lastReadPosition;
        private int _disposed;

        public bool IsMuted { get; private set; }

        public event Action<byte[]>? AudioChunkAvailable;

        // Exposed for tests — the launcher takes the factory route which
        // starts the mic + polling loop, but unit tests bypass both by
        // constructing the controller directly to exercise the encoding
        // path against synthetic samples.
        internal UnityMicrophoneInput(FormatConfig format, string? deviceName = null)
        {
            _format = format ?? throw new ArgumentNullException(nameof(format));
            if (format.SampleRate <= 0)
                throw new ArgumentException(
                    "FormatConfig.SampleRate must be positive.",
                    nameof(format)
                );
            // ulaw is documented in the AsyncAPI surface but the JS SDK's
            // worklet does the codec encoding in JS. v0.1 native is PCM-only;
            // ulaw lands alongside a hand-rolled encoder in a follow-up.
            if (!string.Equals(format.Format, "pcm", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException(
                    $"UnityMicrophoneInput supports PCM only at v0.1; got '{format.Format}'."
                );
            _deviceName = string.IsNullOrEmpty(deviceName) ? null : deviceName;
            _samplesPerChunk = CalculateSamplesPerChunk(format.SampleRate, InputChunkDurationMs);
            _ringLengthSamples = format.SampleRate * RingBufferLengthSec;
            _floatBuffer = new float[_samplesPerChunk];
            _pcmBuffer = new byte[_samplesPerChunk * sizeof(short)];
        }

        /// <summary>
        /// Production factory. Marshals onto the main thread, opens the
        /// microphone, and kicks off the polling loop. The returned instance
        /// is already emitting <see cref="AudioChunkAvailable"/> by the time
        /// the awaiter resumes — subscribe (or wrap in <see cref="Conversation"/>,
        /// which subscribes in its constructor) before calling.
        /// </summary>
        internal static async Awaitable<UnityMicrophoneInput> CreateAsync(
            FormatConfig format,
            InputDeviceConfig? device = null
        )
        {
            await Awaitable.MainThreadAsync();
            var input = new UnityMicrophoneInput(format, device?.InputDeviceId);
            input.StartMicrophone();
            _ = input.PollingLoopAsync();
            return input;
        }

        // Production polling loop. Reads from the microphone ring on the main
        // thread, encodes, and raises AudioChunkAvailable. Yields between
        // chunks via WaitForSecondsAsync (game time — good enough for a
        // 25 ms cadence) which keeps the continuation on the main thread.
        private async Awaitable PollingLoopAsync()
        {
            CancellationToken token = _cts.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Awaitable.WaitForSecondsAsync(InputChunkDurationMs / 1000f);
                    if (token.IsCancellationRequested)
                        return;
                    PumpAvailableChunks();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on Close().
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        // Drain every full chunk that has accumulated in the mic ring since
        // the last read. The ring keeps writing while we encode, so we may
        // pull more than one chunk per tick if the main thread stalled.
        // Internal so tests can drive the pump directly without spinning the
        // real Awaitable polling loop.
        internal void PumpAvailableChunks()
        {
            if (_micClip == null)
                return;
            int writePos = Microphone.GetPosition(_deviceName);
            if (writePos < 0)
                return;
            int available = AvailableSamples(_lastReadPosition, writePos, _ringLengthSamples);
            while (available >= _samplesPerChunk)
            {
                if (!_micClip.GetData(_floatBuffer, _lastReadPosition))
                    return;
                EmitChunkFromFloatBuffer();
                _lastReadPosition = (_lastReadPosition + _samplesPerChunk) % _ringLengthSamples;
                available -= _samplesPerChunk;
            }
        }

        // Encode + raise from whatever is currently in _floatBuffer. Split out
        // so tests can stage synthetic samples directly without touching the
        // microphone path.
        internal void EmitChunkFromFloatBuffer()
        {
            EncodePcm16(_floatBuffer, _samplesPerChunk, IsMuted, _pcmBuffer);
            // Copy out of the reusable buffer so subscribers can keep the
            // array (the next pump call overwrites _pcmBuffer in place).
            byte[] chunk = new byte[_pcmBuffer.Length];
            Buffer.BlockCopy(_pcmBuffer, 0, chunk, 0, _pcmBuffer.Length);
            try
            {
                AudioChunkAvailable?.Invoke(chunk);
            }
            catch (Exception ex)
            {
                // A subscriber throwing must not stall the mic pump.
                Debug.LogException(ex);
            }
        }

        // Stage a synthetic sample buffer into the controller. Test seam —
        // production callers go through PumpAvailableChunks (which fills the
        // buffer from the real microphone).
        internal void TestSeam_StageFloatBuffer(float[] samples)
        {
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));
            int n = Math.Min(samples.Length, _floatBuffer.Length);
            Array.Copy(samples, _floatBuffer, n);
            if (n < _floatBuffer.Length)
                Array.Clear(_floatBuffer, n, _floatBuffer.Length - n);
        }

        private void StartMicrophone()
        {
            _micClip = Microphone.Start(
                deviceName: _deviceName,
                loop: true,
                lengthSec: RingBufferLengthSec,
                frequency: _format.SampleRate
            );
            if (_micClip == null)
                throw new InvalidOperationException(
                    $"Microphone.Start returned null for device '{_deviceName ?? "<default>"}'. "
                        + "Verify that a microphone is available and that permissions are granted."
                );
            _lastReadPosition = 0;
        }

        public Awaitable SetMuted(bool isMuted)
        {
            IsMuted = isMuted;
            return CompletedAwaitable();
        }

        public Awaitable SetDevice(InputDeviceConfig? config = null, FormatConfig? format = null)
        {
            // Format change isn't supported at v0.1 — the AudioClip's sample
            // rate is fixed at Microphone.Start time. Mirrors the JS SDK,
            // which docs the same constraint inside MediaDeviceInput.setDevice.
            if (format != null && format.SampleRate != _format.SampleRate)
                throw new NotSupportedException(
                    "Changing the input sample rate after session start is not supported; "
                        + "restart the session with the new format instead."
                );
            return SetDeviceAsync(config?.InputDeviceId);
        }

        private async Awaitable SetDeviceAsync(string? deviceId)
        {
            await Awaitable.MainThreadAsync();
            string? newDevice = string.IsNullOrEmpty(deviceId) ? null : deviceId;
            if (string.Equals(newDevice, _deviceName, StringComparison.Ordinal))
                return;
            StopMicrophone();
            _deviceName = newDevice;
            StartMicrophone();
        }

        public float GetVolume()
        {
            if (IsMuted || _micClip == null)
                return 0f;
            // Cheap RMS over the last polled chunk. The frequency-data path
            // does an FFT for visualisers; volume itself is a scalar and the
            // RMS estimate over a 25 ms window is good enough for level
            // meters.
            double sumSquares = 0.0;
            for (int i = 0; i < _samplesPerChunk; i++)
            {
                float s = _floatBuffer[i];
                sumSquares += s * s;
            }
            double mean = sumSquares / _samplesPerChunk;
            return Mathf.Clamp01((float)Math.Sqrt(mean));
        }

        public void GetByteFrequencyData(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0)
                return;
            if (IsMuted || _micClip == null)
            {
                Array.Clear(buffer, 0, buffer.Length);
                return;
            }
            ComputeByteFrequencyData(_floatBuffer, _samplesPerChunk, _format.SampleRate, buffer);
        }

        public async Awaitable Close()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException) { }

            await Awaitable.MainThreadAsync();
            StopMicrophone();
            try
            {
                _cts.Dispose();
            }
            catch (ObjectDisposedException) { }
        }

        private void StopMicrophone()
        {
            try
            {
                if (Microphone.IsRecording(_deviceName))
                    Microphone.End(_deviceName);
            }
            catch (Exception ex)
            {
                // Microphone subsystem can throw on missing/disappeared
                // devices; the controller is being torn down anyway.
                Debug.LogException(ex);
            }
            if (_micClip != null)
            {
                DestroyClip(_micClip);
                _micClip = null;
            }
        }

        private static void DestroyClip(AudioClip clip)
        {
            // Destroy is a Play-Mode operation; Edit Mode (and headless test
            // runs) need DestroyImmediate so the AudioClip is actually
            // released instead of triggering Unity's "Destroy may not be
            // called from edit mode" warning.
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(clip);
            else
                UnityEngine.Object.DestroyImmediate(clip);
        }

        // Static helpers — pulled out so unit tests can exercise the chunk
        // sizing, encoding, and visualiser math without an active microphone.

        /// <summary>
        /// Compute the chunk size (in mono samples) that holds
        /// <paramref name="chunkDurationMs"/> of audio at
        /// <paramref name="sampleRate"/>. Rounds to nearest; never zero.
        /// </summary>
        internal static int CalculateSamplesPerChunk(int sampleRate, int chunkDurationMs)
        {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (chunkDurationMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(chunkDurationMs));
            // (rate * ms + 500) / 1000 = round-half-up to nearest sample.
            int rounded = (sampleRate * chunkDurationMs + 500) / 1000;
            return Math.Max(1, rounded);
        }

        /// <summary>
        /// Number of samples that have been written between
        /// <paramref name="lastRead"/> and <paramref name="writePos"/> in a
        /// ring of length <paramref name="ringLength"/>. Wraparound is
        /// handled by adding the ring length when the writer has lapped the
        /// reader.
        /// </summary>
        internal static int AvailableSamples(int lastRead, int writePos, int ringLength)
        {
            if (ringLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(ringLength));
            int diff = writePos - lastRead;
            if (diff < 0)
                diff += ringLength;
            return diff;
        }

        /// <summary>
        /// Encode <paramref name="count"/> samples from <paramref name="source"/>
        /// into 16-bit little-endian PCM in <paramref name="dest"/>. When
        /// <paramref name="muted"/> is true the destination is filled with
        /// zeros — the cadence stays uniform, matching the JS worklet.
        /// </summary>
        /// <remarks>
        /// Mirrors <c>rawAudioProcessor</c> in <c>@elevenlabs/client</c>: clamps
        /// each sample to <c>[-1, 1]</c>, then scales negatives by 32768 and
        /// positives by 32767 so the encoded value spans the full int16 range
        /// without overflow at <c>+1.0</c>.
        /// </remarks>
        internal static void EncodePcm16(float[] source, int count, bool muted, byte[] dest)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (dest == null)
                throw new ArgumentNullException(nameof(dest));
            if (count < 0 || count > source.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (dest.Length < count * 2)
                throw new ArgumentException(
                    "Destination buffer is too small for the requested sample count.",
                    nameof(dest)
                );
            if (muted)
            {
                Array.Clear(dest, 0, count * 2);
                return;
            }
            for (int i = 0; i < count; i++)
            {
                float s = source[i];
                if (s < -1f)
                    s = -1f;
                else if (s > 1f)
                    s = 1f;
                short v = s < 0 ? (short)(s * 32768f) : (short)(s * 32767f);
                dest[i * 2] = (byte)(v & 0xff);
                dest[i * 2 + 1] = (byte)((v >> 8) & 0xff);
            }
        }

        /// <summary>
        /// Fill <paramref name="dest"/> with byte-frequency magnitudes (0–255)
        /// for the human-voice band (100–8000 Hz) using a radix-2 FFT over
        /// the live sample buffer. Tracks the JS-side analyser semantics
        /// closely enough for level / spectrum visualisers.
        /// </summary>
        internal static void ComputeByteFrequencyData(
            float[] source,
            int count,
            int sampleRate,
            byte[] dest
        )
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (dest == null)
                throw new ArgumentNullException(nameof(dest));
            if (dest.Length == 0)
                return;
            if (count <= 0 || sampleRate <= 0)
            {
                Array.Clear(dest, 0, dest.Length);
                return;
            }
            float nyquist = sampleRate * 0.5f;
            float clampedEnd = Math.Min(VoiceBandEndHz, nyquist);
            if (clampedEnd <= VoiceBandStartHz)
            {
                Array.Clear(dest, 0, dest.Length);
                return;
            }
            int fftSize = NextPowerOfTwo(count);
            Complex[] buffer = new Complex[fftSize];
            for (int i = 0; i < count; i++)
                buffer[i] = new Complex(source[i], 0.0);
            // Tail is already zero from the new[] allocation — zero-pad up to
            // the FFT size.
            Fft(buffer);
            int binCount = fftSize / 2;
            double binHz = sampleRate / (double)fftSize;
            int bands = dest.Length;
            for (int band = 0; band < bands; band++)
            {
                float t = (band + 0.5f) / bands;
                float targetHz = VoiceBandStartHz + (clampedEnd - VoiceBandStartHz) * t;
                int bin = (int)Math.Round(targetHz / binHz);
                if (bin < 0)
                    bin = 0;
                else if (bin >= binCount)
                    bin = binCount - 1;
                double magnitude = buffer[bin].Magnitude / count;
                double db = magnitude <= 1e-9 ? MinDecibels : 20.0 * Math.Log10(magnitude);
                float normalized = Mathf.InverseLerp(MinDecibels, MaxDecibels, (float)db);
                dest[band] = (byte)Math.Round(Mathf.Clamp01(normalized) * 255.0);
            }
        }

        // Iterative in-place radix-2 Cooley–Tukey FFT. Caller guarantees the
        // input length is a power of two; bands are sampled out of the
        // resulting bins by ComputeByteFrequencyData above.
        private static void Fft(Complex[] buffer)
        {
            int n = buffer.Length;
            int bits = (int)Math.Log(n, 2);
            // Bit-reverse permutation.
            for (int j = 1; j < n; j++)
            {
                int rev = ReverseBits(j, bits);
                if (j < rev)
                    (buffer[j], buffer[rev]) = (buffer[rev], buffer[j]);
            }
            // Butterflies.
            for (int size = 2; size <= n; size <<= 1)
            {
                int half = size >> 1;
                double theta = -2.0 * Math.PI / size;
                Complex wStep = new(Math.Cos(theta), Math.Sin(theta));
                for (int i = 0; i < n; i += size)
                {
                    Complex w = Complex.One;
                    for (int k = 0; k < half; k++)
                    {
                        Complex t = w * buffer[i + k + half];
                        Complex u = buffer[i + k];
                        buffer[i + k] = u + t;
                        buffer[i + k + half] = u - t;
                        w *= wStep;
                    }
                }
            }
        }

        private static int ReverseBits(int value, int bits)
        {
            int result = 0;
            for (int i = 0; i < bits; i++)
            {
                result = (result << 1) | (value & 1);
                value >>= 1;
            }
            return result;
        }

        private static int NextPowerOfTwo(int value)
        {
            if (value <= 1)
                return 1;
            int power = 1;
            while (power < value)
                power <<= 1;
            return power;
        }

        private static Awaitable CompletedAwaitable()
        {
            var source = new AwaitableCompletionSource();
            source.SetResult();
            return source.Awaitable;
        }
    }
}
