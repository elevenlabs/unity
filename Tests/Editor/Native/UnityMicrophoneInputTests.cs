#nullable enable

using System;
using System.Collections.Generic;
using ElevenLabs.Agents;
using ElevenLabs.Native;
using NUnit.Framework;

namespace ElevenLabs.Native.Tests
{
    /// <summary>
    /// Edit-mode coverage for <see cref="UnityMicrophoneInput"/>. The
    /// microphone path itself can't be exercised in batchmode (no audio
    /// devices), so these tests target the static helpers + the internal
    /// chunk-emit seam: chunk sizing, PCM encoding, mute semantics, device
    /// switch validation, and the byte-frequency band math.
    /// </summary>
    public class UnityMicrophoneInputTests
    {
        // CalculateSamplesPerChunk -----------------------------------------

        [Test]
        public void CalculateSamplesPerChunk_16kHz_25ms_Returns400()
        {
            Assert.AreEqual(
                400,
                UnityMicrophoneInput.CalculateSamplesPerChunk(
                    sampleRate: 16000,
                    chunkDurationMs: 25
                )
            );
        }

        [Test]
        public void CalculateSamplesPerChunk_24kHz_25ms_Returns600()
        {
            Assert.AreEqual(
                600,
                UnityMicrophoneInput.CalculateSamplesPerChunk(
                    sampleRate: 24000,
                    chunkDurationMs: 25
                )
            );
        }

        [Test]
        public void CalculateSamplesPerChunk_8kHz_25ms_Returns200()
        {
            // Matches the JS worklet's bufferSize = max(1, round(rate * ms / 1000)).
            Assert.AreEqual(
                200,
                UnityMicrophoneInput.CalculateSamplesPerChunk(sampleRate: 8000, chunkDurationMs: 25)
            );
        }

        [Test]
        public void CalculateSamplesPerChunk_RoundsHalfUp()
        {
            // 44100 * 25 / 1000 = 1102.5 → 1103 with round-half-up.
            Assert.AreEqual(
                1103,
                UnityMicrophoneInput.CalculateSamplesPerChunk(
                    sampleRate: 44100,
                    chunkDurationMs: 25
                )
            );
        }

        [Test]
        public void CalculateSamplesPerChunk_NeverZero()
        {
            // 1000 Hz * 0.5 ms = 0.5 samples → would round to 1; the explicit
            // floor at 1 ensures we don't emit empty chunks even with absurd
            // configurations.
            Assert.AreEqual(
                1,
                UnityMicrophoneInput.CalculateSamplesPerChunk(sampleRate: 1, chunkDurationMs: 1)
            );
        }

        [Test]
        public void CalculateSamplesPerChunk_InvalidSampleRate_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                UnityMicrophoneInput.CalculateSamplesPerChunk(0, 25)
            );
        }

        [Test]
        public void CalculateSamplesPerChunk_InvalidDuration_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                UnityMicrophoneInput.CalculateSamplesPerChunk(16000, 0)
            );
        }

        // AvailableSamples -------------------------------------------------

        [Test]
        public void AvailableSamples_LinearProgress()
        {
            Assert.AreEqual(
                500,
                UnityMicrophoneInput.AvailableSamples(
                    lastRead: 100,
                    writePos: 600,
                    ringLength: 16000
                )
            );
        }

        [Test]
        public void AvailableSamples_HandlesWraparound()
        {
            // Writer at 100, reader at 15500 → writer has lapped, so 100 + (16000 - 15500) = 600.
            Assert.AreEqual(
                600,
                UnityMicrophoneInput.AvailableSamples(
                    lastRead: 15500,
                    writePos: 100,
                    ringLength: 16000
                )
            );
        }

        [Test]
        public void AvailableSamples_ZeroWhenAligned()
        {
            Assert.AreEqual(
                0,
                UnityMicrophoneInput.AvailableSamples(
                    lastRead: 12345,
                    writePos: 12345,
                    ringLength: 16000
                )
            );
        }

        // EncodePcm16 ------------------------------------------------------

        [Test]
        public void EncodePcm16_ConvertsPositiveSamplesToInt16LittleEndian()
        {
            float[] source = { 0f, 0.5f, 1f };
            byte[] dest = new byte[source.Length * 2];
            UnityMicrophoneInput.EncodePcm16(source, source.Length, muted: false, dest);
            short s0 = BitConverter.ToInt16(dest, 0);
            short s1 = BitConverter.ToInt16(dest, 2);
            short s2 = BitConverter.ToInt16(dest, 4);
            Assert.AreEqual(0, s0);
            // Positive samples scale by 32767 and truncate to int16 — matches
            // rawAudioProcessor.js, which assigns the float into an Int16Array
            // (also a truncation). 0.5 * 32767 = 16383.5 → 16383.
            Assert.AreEqual((short)(0.5f * 32767f), s1);
            Assert.AreEqual(32767, s2);
        }

        [Test]
        public void EncodePcm16_ConvertsNegativeSamples_WithoutOverflowAtMinusOne()
        {
            // Negative samples scale by 32768 → -1.0 maps to exactly Int16.MinValue.
            float[] source = { -1f, -0.5f };
            byte[] dest = new byte[source.Length * 2];
            UnityMicrophoneInput.EncodePcm16(source, source.Length, muted: false, dest);
            short s0 = BitConverter.ToInt16(dest, 0);
            short s1 = BitConverter.ToInt16(dest, 2);
            Assert.AreEqual(short.MinValue, s0);
            // Same truncation as the positive path: -0.5 * 32768 = -16384.0 exactly.
            Assert.AreEqual((short)(-0.5f * 32768f), s1);
        }

        [Test]
        public void EncodePcm16_ClampsOutOfRangeInput()
        {
            float[] source = { 2f, -2f };
            byte[] dest = new byte[source.Length * 2];
            UnityMicrophoneInput.EncodePcm16(source, source.Length, muted: false, dest);
            Assert.AreEqual(32767, BitConverter.ToInt16(dest, 0));
            Assert.AreEqual(short.MinValue, BitConverter.ToInt16(dest, 2));
        }

        [Test]
        public void EncodePcm16_MutedFillsZeros_RegardlessOfInput()
        {
            float[] source = { 0.9f, -0.9f, 0.5f, -0.5f };
            byte[] dest = new byte[source.Length * 2];
            // Pre-fill with non-zero sentinel to prove the encoder clears.
            for (int i = 0; i < dest.Length; i++)
                dest[i] = 0xAB;
            UnityMicrophoneInput.EncodePcm16(source, source.Length, muted: true, dest);
            CollectionAssert.AreEqual(new byte[dest.Length], dest);
        }

        [Test]
        public void EncodePcm16_RejectsUndersizedDest()
        {
            float[] source = { 0f, 0f, 0f };
            byte[] dest = new byte[2]; // expects 6
            Assert.Throws<ArgumentException>(() =>
                UnityMicrophoneInput.EncodePcm16(source, source.Length, false, dest)
            );
        }

        // Constructor validation -------------------------------------------

        [Test]
        public void Ctor_RejectsNullFormat()
        {
            Assert.Throws<ArgumentNullException>(() => new UnityMicrophoneInput(null!));
        }

        [Test]
        public void Ctor_RejectsNonPcmFormat()
        {
            Assert.Throws<NotSupportedException>(() =>
                new UnityMicrophoneInput(new FormatConfig("ulaw", 8000))
            );
        }

        [Test]
        public void Ctor_RejectsZeroSampleRate()
        {
            Assert.Throws<ArgumentException>(() =>
                new UnityMicrophoneInput(new FormatConfig("pcm", 0))
            );
        }

        // SetMuted ---------------------------------------------------------

        [Test]
        public void SetMuted_UpdatesIsMuted()
        {
            var input = new UnityMicrophoneInput(new FormatConfig("pcm", 16000));
            Assert.IsFalse(input.IsMuted);
            input.SetMuted(true).GetAwaiter().GetResult();
            Assert.IsTrue(input.IsMuted);
            input.SetMuted(false).GetAwaiter().GetResult();
            Assert.IsFalse(input.IsMuted);
        }

        // SetDevice format-change rejection --------------------------------

        [Test]
        public void SetDevice_RejectsSampleRateChange()
        {
            var input = new UnityMicrophoneInput(new FormatConfig("pcm", 16000));
            Assert.ThrowsAsync<NotSupportedException>(async () =>
                await input.SetDevice(format: new FormatConfig("pcm", 24000))
            );
        }

        // EmitChunkFromFloatBuffer + AudioChunkAvailable -------------------

        [Test]
        public void EmitChunkFromFloatBuffer_RaisesAudioChunk_WithEncodedPayload()
        {
            // Construct without starting the mic. Stage synthetic samples,
            // emit, assert on the captured PCM.
            var input = new UnityMicrophoneInput(new FormatConfig("pcm", 16000));
            int expectedSamples = UnityMicrophoneInput.CalculateSamplesPerChunk(16000, 25);
            float[] staged = new float[expectedSamples];
            for (int i = 0; i < staged.Length; i++)
                staged[i] = 0.25f;
            input.TestSeam_StageFloatBuffer(staged);

            byte[]? captured = null;
            input.AudioChunkAvailable += chunk => captured = chunk;
            input.EmitChunkFromFloatBuffer();

            Assert.IsNotNull(captured);
            Assert.AreEqual(expectedSamples * 2, captured!.Length);
            // Spot-check the first sample. The encoder truncates, mirroring the
            // JS worklet's Int16Array assignment: 0.25 * 32767 = 8191.75 → 8191.
            short s0 = BitConverter.ToInt16(captured, 0);
            Assert.AreEqual((short)(0.25f * 32767f), s0);
        }

        [Test]
        public void EmitChunkFromFloatBuffer_WhenMuted_EmitsZeros()
        {
            var input = new UnityMicrophoneInput(new FormatConfig("pcm", 16000));
            int expectedSamples = UnityMicrophoneInput.CalculateSamplesPerChunk(16000, 25);
            float[] staged = new float[expectedSamples];
            for (int i = 0; i < staged.Length; i++)
                staged[i] = 0.9f;
            input.TestSeam_StageFloatBuffer(staged);
            input.SetMuted(true).GetAwaiter().GetResult();

            byte[]? captured = null;
            input.AudioChunkAvailable += chunk => captured = chunk;
            input.EmitChunkFromFloatBuffer();

            Assert.IsNotNull(captured);
            CollectionAssert.AreEqual(new byte[captured!.Length], captured);
        }

        [Test]
        public void EmitChunkFromFloatBuffer_KeepsCadence_AfterUnmute()
        {
            // Mute → unmute round trip must not skip subsequent chunks; the
            // wire cadence relies on every tick producing exactly one chunk.
            var input = new UnityMicrophoneInput(new FormatConfig("pcm", 16000));
            int expectedSamples = UnityMicrophoneInput.CalculateSamplesPerChunk(16000, 25);
            float[] staged = new float[expectedSamples];
            for (int i = 0; i < staged.Length; i++)
                staged[i] = 0.5f;
            input.TestSeam_StageFloatBuffer(staged);

            var captured = new List<byte[]>();
            input.AudioChunkAvailable += chunk => captured.Add(chunk);

            input.SetMuted(true).GetAwaiter().GetResult();
            input.EmitChunkFromFloatBuffer();
            input.SetMuted(false).GetAwaiter().GetResult();
            input.EmitChunkFromFloatBuffer();

            Assert.AreEqual(2, captured.Count);
            // First chunk muted → all zeros.
            CollectionAssert.AreEqual(new byte[captured[0].Length], captured[0]);
            // Second chunk live → first sample is 0.5 * 32767 (truncated).
            Assert.AreEqual((short)(0.5f * 32767f), BitConverter.ToInt16(captured[1], 0));
        }

        [Test]
        public void EmitChunkFromFloatBuffer_SurvivesThrowingSubscriber()
        {
            // The contract: a throwing subscriber must not bubble the
            // exception out of the pump or stall future emit calls. (C#
            // multicast halts on the first throw, so subscribers ordered
            // after the thrower don't see that chunk — same shape as the
            // connection-level handler in NativeWebSocketConnection. We're
            // not pinning multicast ordering here, just confirming the pump
            // doesn't die.)
            var input = new UnityMicrophoneInput(new FormatConfig("pcm", 16000));
            int expectedSamples = UnityMicrophoneInput.CalculateSamplesPerChunk(16000, 25);
            input.TestSeam_StageFloatBuffer(new float[expectedSamples]);

            input.AudioChunkAvailable += _ => throw new InvalidOperationException("boom");

            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Exception,
                new System.Text.RegularExpressions.Regex(".*boom.*")
            );
            Assert.DoesNotThrow(() => input.EmitChunkFromFloatBuffer());

            // A second emit still goes through cleanly: the pump didn't get
            // poisoned by the prior throw.
            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Exception,
                new System.Text.RegularExpressions.Regex(".*boom.*")
            );
            Assert.DoesNotThrow(() => input.EmitChunkFromFloatBuffer());
        }

        // ComputeByteFrequencyData ----------------------------------------

        [Test]
        public void ComputeByteFrequencyData_AllZeros_ProducesZeroBands()
        {
            float[] source = new float[400];
            byte[] dest = new byte[16];
            UnityMicrophoneInput.ComputeByteFrequencyData(source, source.Length, 16000, dest);
            CollectionAssert.AreEqual(new byte[dest.Length], dest);
        }

        [Test]
        public void ComputeByteFrequencyData_ZeroLengthDest_NoOp()
        {
            float[] source = new float[400];
            byte[] dest = Array.Empty<byte>();
            Assert.DoesNotThrow(() =>
                UnityMicrophoneInput.ComputeByteFrequencyData(source, source.Length, 16000, dest)
            );
        }

        [Test]
        public void ComputeByteFrequencyData_ProducesNonZero_ForToneInsideBand()
        {
            // 1 kHz sine inside the 100–8000 Hz voice band at 16 kHz should
            // light up at least one bin above zero. Doesn't pin exact bin —
            // the visualiser doesn't need that — just confirms the FFT path
            // produces signal, not silence.
            int sampleRate = 16000;
            int count = UnityMicrophoneInput.CalculateSamplesPerChunk(
                sampleRate,
                UnityMicrophoneInput.InputChunkDurationMs
            );
            float[] source = new float[count];
            double freq = 1000.0;
            for (int i = 0; i < count; i++)
                source[i] = (float)Math.Sin(2.0 * Math.PI * freq * i / sampleRate);
            byte[] dest = new byte[32];
            UnityMicrophoneInput.ComputeByteFrequencyData(source, count, sampleRate, dest);

            bool anyNonZero = false;
            foreach (byte b in dest)
            {
                if (b > 0)
                {
                    anyNonZero = true;
                    break;
                }
            }
            Assert.IsTrue(
                anyNonZero,
                "FFT over a 1 kHz tone should produce at least one nonzero band."
            );
        }
    }
}
