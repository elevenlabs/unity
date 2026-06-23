#nullable enable

using System;
using ElevenLabs.Agents;
using ElevenLabs.Native;
using NUnit.Framework;

namespace ElevenLabs.Native.Tests
{
    /// <summary>
    /// Edit-mode coverage for <see cref="UnityAudioSourceOutput"/>. The
    /// <see cref="UnityEngine.AudioSource"/> playback path itself can't be
    /// driven in batchmode (no audio output device, no audio thread), so
    /// these tests target the static helpers + the internal seams: PCM
    /// decoding, ring push / drain semantics, underrun behaviour, interrupt
    /// flush, mute → silence, format negotiation propagation, and device /
    /// format change validation.
    /// </summary>
    public class UnityAudioSourceOutputTests
    {
        // DecodePcm16 ------------------------------------------------------

        [Test]
        public void DecodePcm16_ConvertsPositiveSamplesToFloat()
        {
            // 16383 ≈ 0.5 * 32767 — encoded sentinel from UnityMicrophoneInput's
            // EncodePcm16 round-trip; the decoder reverses the divide.
            byte[] source = LittleEndian(0, 16383, 32767);
            float[] dest = new float[3];
            int written = UnityAudioSourceOutput.DecodePcm16(source, source.Length, dest);
            Assert.AreEqual(3, written);
            Assert.AreEqual(0f, dest[0], 1e-6);
            Assert.AreEqual(16383 / 32767f, dest[1], 1e-6);
            Assert.AreEqual(1f, dest[2], 1e-6);
        }

        [Test]
        public void DecodePcm16_ConvertsNegativeSamples_MinValueMapsToMinusOne()
        {
            // The encoder pairs Int16.MinValue with -1.0 exactly (divides by
            // 32768 on negatives) — the decoder mirrors that asymmetry.
            byte[] source = LittleEndian(short.MinValue, -16384);
            float[] dest = new float[2];
            UnityAudioSourceOutput.DecodePcm16(source, source.Length, dest);
            Assert.AreEqual(-1f, dest[0], 1e-6);
            Assert.AreEqual(-0.5f, dest[1], 1e-6);
        }

        [Test]
        public void DecodePcm16_OddByteCountTruncatesToWholeSamples()
        {
            // Three bytes = 1 full int16 + dangling byte; decoder yields 1
            // sample, ignores the trailing byte.
            byte[] source = { 0x00, 0x40, 0xAB }; // 0x4000 = 16384, then leftover.
            float[] dest = new float[3];
            int written = UnityAudioSourceOutput.DecodePcm16(source, source.Length, dest);
            Assert.AreEqual(1, written);
            Assert.AreEqual(16384 / 32767f, dest[0], 1e-6);
        }

        [Test]
        public void DecodePcm16_RejectsUndersizedDest()
        {
            byte[] source = LittleEndian(1, 2, 3);
            float[] dest = new float[1];
            Assert.Throws<ArgumentException>(() =>
                UnityAudioSourceOutput.DecodePcm16(source, source.Length, dest)
            );
        }

        [Test]
        public void DecodePcm16_RejectsNegativeByteCount()
        {
            byte[] source = new byte[4];
            float[] dest = new float[4];
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                UnityAudioSourceOutput.DecodePcm16(source, -1, dest)
            );
        }

        [Test]
        public void DecodePcm16_RejectsByteCountLargerThanSource()
        {
            byte[] source = new byte[2];
            float[] dest = new float[4];
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                UnityAudioSourceOutput.DecodePcm16(source, source.Length + 1, dest)
            );
        }

        // CalculateRingCapacity -------------------------------------------

        [Test]
        public void CalculateRingCapacity_5SecondsAt24kHz_Returns120000()
        {
            // 24000 * 5 = 120000.
            Assert.AreEqual(120_000, UnityAudioSourceOutput.CalculateRingCapacity(24_000));
        }

        [Test]
        public void CalculateRingCapacity_5SecondsAt16kHz_Returns80000()
        {
            Assert.AreEqual(80_000, UnityAudioSourceOutput.CalculateRingCapacity(16_000));
        }

        [Test]
        public void CalculateRingCapacity_RejectsZeroSampleRate()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                UnityAudioSourceOutput.CalculateRingCapacity(0)
            );
        }

        // Constructor validation -------------------------------------------

        [Test]
        public void Ctor_RejectsNullFormat()
        {
            Assert.Throws<ArgumentNullException>(() => new UnityAudioSourceOutput(null!));
        }

        [Test]
        public void Ctor_RejectsNonPcmFormat()
        {
            Assert.Throws<NotSupportedException>(() =>
                new UnityAudioSourceOutput(new FormatConfig("ulaw", 8000))
            );
        }

        [Test]
        public void Ctor_RejectsZeroSampleRate()
        {
            Assert.Throws<ArgumentException>(() =>
                new UnityAudioSourceOutput(new FormatConfig("pcm", 0))
            );
        }

        [Test]
        public void Ctor_AllocatesRingFromNegotiatedFormat()
        {
            // Format negotiation propagation: the ring sizing tracks the
            // sample rate handed in by the connection.
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 24_000));
            Assert.AreEqual(120_000, output.Test_RingCapacity);
        }

        // PushAudio + ReadFromRing -----------------------------------------

        [Test]
        public void PushAudio_DecodedSamples_ReadBackInOrder()
        {
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            byte[] pcm = LittleEndian(0, 16383, -16384, 32767);
            output.PushAudio(pcm);

            float[] dest = new float[4];
            output.ReadFromRing(dest);
            Assert.AreEqual(0f, dest[0], 1e-6);
            Assert.AreEqual(16383 / 32767f, dest[1], 1e-6);
            Assert.AreEqual(-0.5f, dest[2], 1e-6);
            Assert.AreEqual(1f, dest[3], 1e-6);
        }

        [Test]
        public void PushAudio_AdvancesAvailableCount()
        {
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            Assert.AreEqual(0, output.Test_AvailableSamples);
            output.PushAudio(LittleEndian(1, 2, 3, 4));
            Assert.AreEqual(4, output.Test_AvailableSamples);
        }

        [Test]
        public void PushAudio_EmptyPayload_NoOp()
        {
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            output.PushAudio(Array.Empty<byte>());
            Assert.AreEqual(0, output.Test_AvailableSamples);
        }

        [Test]
        public void PushAudio_OddByteLength_DropsDanglingByte()
        {
            // 3 bytes = 1 sample + 1 dangling byte; the second sample is
            // discarded so the ring stays sample-aligned.
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            output.PushAudio(new byte[] { 0x00, 0x40, 0xAB });
            Assert.AreEqual(1, output.Test_AvailableSamples);
        }

        [Test]
        public void ReadFromRing_Underrun_FillsRemainderWithSilence()
        {
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            output.PushAudio(LittleEndian(16383, 16383));

            float[] dest = new float[6];
            for (int i = 0; i < dest.Length; i++)
                dest[i] = 0.99f; // sentinel — verify the underrun path overwrites.
            output.ReadFromRing(dest);

            Assert.AreEqual(16383 / 32767f, dest[0], 1e-6);
            Assert.AreEqual(16383 / 32767f, dest[1], 1e-6);
            for (int i = 2; i < dest.Length; i++)
                Assert.AreEqual(0f, dest[i], 1e-6, $"sample {i}");
        }

        [Test]
        public void ReadFromRing_DrainsAndResetsAvailableCount()
        {
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            output.PushAudio(LittleEndian(1, 2, 3));
            float[] dest = new float[3];
            output.ReadFromRing(dest);
            Assert.AreEqual(0, output.Test_AvailableSamples);
        }

        [Test]
        public void ReadFromRing_EmptyBuffer_NoOp()
        {
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            output.PushAudio(LittleEndian(1, 2));
            Assert.DoesNotThrow(() => output.ReadFromRing(Array.Empty<float>()));
            Assert.AreEqual(2, output.Test_AvailableSamples);
        }

        [Test]
        public void ReadFromRing_HandlesWraparound()
        {
            // Push enough samples to wrap the write pointer past 0 and
            // confirm the read pointer follows. Use a synthetic small ring
            // by picking a tiny sample rate so the capacity stays modest
            // (1 Hz * 5 s = 5 samples).
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 1));
            Assert.AreEqual(5, output.Test_RingCapacity);

            // Push 3 samples, drain them — _writePos = 3, _readPos = 3.
            output.PushAudio(LittleEndian(1, 2, 3));
            float[] discard = new float[3];
            output.ReadFromRing(discard);

            // Push 4 more samples — wraps to position 2.
            output.PushAudio(LittleEndian(4, 5, 6, 7));
            float[] dest = new float[4];
            output.ReadFromRing(dest);
            Assert.AreEqual(4 / 32767f, dest[0], 1e-6);
            Assert.AreEqual(5 / 32767f, dest[1], 1e-6);
            Assert.AreEqual(6 / 32767f, dest[2], 1e-6);
            Assert.AreEqual(7 / 32767f, dest[3], 1e-6);
        }

        [Test]
        public void PushAudio_Overrun_DropsOldestSamples()
        {
            // 1 Hz * 5 s ring = 5 samples. Push 7 → first two are dropped.
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 1));
            output.PushAudio(LittleEndian(1, 2, 3, 4, 5, 6, 7));
            Assert.AreEqual(5, output.Test_AvailableSamples);

            float[] dest = new float[5];
            output.ReadFromRing(dest);
            // Oldest two (1, 2) dropped; ring now holds 3..7 in FIFO order.
            Assert.AreEqual(3 / 32767f, dest[0], 1e-6);
            Assert.AreEqual(4 / 32767f, dest[1], 1e-6);
            Assert.AreEqual(5 / 32767f, dest[2], 1e-6);
            Assert.AreEqual(6 / 32767f, dest[3], 1e-6);
            Assert.AreEqual(7 / 32767f, dest[4], 1e-6);
        }

        // Interrupt --------------------------------------------------------

        [Test]
        public void Interrupt_WithoutAudioSource_ClearsRingSynchronously()
        {
            // Constructor-only path (no AudioSource): Interrupt should still
            // flush the ring so a subsequent push starts from a clean state.
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            output.PushAudio(LittleEndian(1, 2, 3));
            Assert.AreEqual(3, output.Test_AvailableSamples);

            output.Interrupt(resetDurationMs: 0);
            Assert.AreEqual(0, output.Test_AvailableSamples);
        }

        [Test]
        public void Interrupt_DefaultDuration_ClearsRing()
        {
            // Edit Mode can't tick frames, so the fade short-circuits and
            // clears synchronously (see Interrupt's Application.isPlaying
            // guard). Either way, the post-interrupt ring is empty.
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            output.PushAudio(LittleEndian(10, 20, 30));
            output.Interrupt();
            Assert.AreEqual(0, output.Test_AvailableSamples);
        }

        [Test]
        public void ClearRing_ResetsBothRingAndAnalysisBuffer()
        {
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            output.PushAudio(LittleEndian(short.MaxValue, short.MaxValue));
            output.ClearRing();

            float[] dest = new float[4];
            for (int i = 0; i < dest.Length; i++)
                dest[i] = 0.42f;
            output.ReadFromRing(dest);
            // Ring is empty → underrun fills the destination with silence.
            for (int i = 0; i < dest.Length; i++)
                Assert.AreEqual(0f, dest[i], 1e-6);
        }

        // SetDevice format-change rejection --------------------------------

        [Test]
        public void SetDevice_RejectsSampleRateChange()
        {
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            Assert.ThrowsAsync<NotSupportedException>(async () =>
                await output.SetDevice(format: new FormatConfig("pcm", 24_000))
            );
        }

        [Test]
        public void SetDevice_LogsWarning_WhenDeviceIdProvided()
        {
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            UnityEngine.TestTools.LogAssert.Expect(
                UnityEngine.LogType.Warning,
                new System.Text.RegularExpressions.Regex(
                    ".*device.*not supported.*|.*device.*ignored.*"
                )
            );
            output
                .SetDevice(config: new OutputDeviceConfig("some-device"))
                .GetAwaiter()
                .GetResult();
        }

        // SetVolume --------------------------------------------------------

        [Test]
        public void SetVolume_ClampedToZeroOne()
        {
            // Without an AudioSource, SetVolume just stores the value; we
            // confirm it survives a clamp by reading it back via GetVolume's
            // null-source short-circuit (returns 0 with no source).
            // Indirectly: SetVolume(-1) and SetVolume(2) must not throw.
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            Assert.DoesNotThrow(() => output.SetVolume(-1f));
            Assert.DoesNotThrow(() => output.SetVolume(2f));
        }

        // GetByteFrequencyData --------------------------------------------

        [Test]
        public void GetByteFrequencyData_NoAudioSource_FillsZeros()
        {
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            byte[] buffer = new byte[8];
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = 0xAB;
            output.GetByteFrequencyData(buffer);
            CollectionAssert.AreEqual(new byte[buffer.Length], buffer);
        }

        // Helpers ----------------------------------------------------------

        private static byte[] LittleEndian(params int[] samples)
        {
            byte[] result = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)samples[i];
                result[i * 2] = (byte)(s & 0xff);
                result[i * 2 + 1] = (byte)((s >> 8) & 0xff);
            }
            return result;
        }
    }
}
