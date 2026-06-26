#nullable enable

using System;
using System.Threading.Tasks;
using ElevenLabs.Agents;
using ElevenLabs.Native;
using NUnit.Framework;
using UnityEngine;

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
        public void PushAudio_ResetsEngineVolumeToUserLevel()
        {
            // Every PushAudio cancels any in-flight fade and snaps the
            // engine volume back to _userVolume so a chunk arriving
            // mid-fade plays at the right gain. Observable via
            // FakeAudioOutputEngine.Volume — NullAudioOutputEngine has the
            // same setter signature but no test could read the value back
            // meaningfully. Step 3 of audio-output-testability.md.
            var fake = new FakeAudioOutputEngine();
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000), fake);
            output.SetVolume(0.7f);
            fake.Volume = 0.1f; // simulate mid-fade

            // 3 samples is well below the pre-fill threshold (~14,400 at
            // 16 kHz × 900 ms), so engine.Start does not fire — the
            // unconditional volume reset is the only side effect here.
            output.PushAudio(LittleEndian(1, 2, 3));
            Assert.AreEqual(0.7f, fake.Volume, 1e-6);
            Assert.AreEqual(
                0,
                fake.StartCallCount,
                "below-threshold PushAudio should not trigger engine.Start."
            );
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
        public void Interrupt_ImmediateCut_RestoresUserVolumeOnEngine()
        {
            // The immediate-cut path (resetDurationMs=0, or Edit-Mode
            // default where Application.isPlaying is false) flushes the
            // ring and writes _userVolume back to the engine so a follow-up
            // PushAudio resumes at the right gain instead of inheriting a
            // stale mid-fade level. Pre-port (NullAudioOutputEngine) the
            // ring-clear was observable but the engine-volume restore was
            // not. Step 3 of audio-output-testability.md.
            var fake = new FakeAudioOutputEngine();
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000), fake);
            output.SetVolume(0.4f);
            Assert.AreEqual(0.4f, fake.Volume, 1e-6);

            // Simulate the engine landing at a mid-fade level so the
            // post-Interrupt restore is observable as a delta, not as a
            // no-op write back to the same value.
            fake.Volume = 0f;
            output.Interrupt(resetDurationMs: 0);
            Assert.AreEqual(0.4f, fake.Volume, 1e-6);
        }

        // Analysis buffer is fed at drain time (audio thread), not push time
        // (network thread). Regression test for visualisers freezing on the
        // last received chunk while the ring drains for the remainder of
        // playback — GetVolume / GetByteFrequencyData must reflect what's
        // playing right now, not what was last received.
        [Test]
        public void PushAudio_DoesNotPopulateAnalysisBuffer_UntilDrained()
        {
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            output.PushAudio(LittleEndian(short.MaxValue, short.MaxValue, short.MaxValue));

            // Push alone leaves the analysis buffer untouched.
            Assert.AreEqual(0f, output.Test_AnalysisBufferRms, 1e-6);

            // Draining the ring feeds the analysis buffer with the played samples.
            output.ReadFromRing(new float[3]);
            Assert.Greater(output.Test_AnalysisBufferRms, 0f);
        }

        [Test]
        public void ReadFromRing_Underrun_FeedsSilenceIntoAnalysisBuffer()
        {
            // After the ring drains completely, continued PCMReaderCallback
            // invocations write silence into the analysis buffer, letting
            // RMS-driven visualisers decay back to rest.
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            output.PushAudio(LittleEndian(short.MaxValue, short.MaxValue));
            output.ReadFromRing(new float[2]);
            Assert.Greater(output.Test_AnalysisBufferRms, 0f);

            // Drain a window-sized chunk of pure underrun → analysis buffer
            // fills with zeros → RMS returns to 0.
            int windowSamples = 16_000 * UnityAudioSourceOutput.AnalysisWindowMs / 1000;
            output.ReadFromRing(new float[windowSamples]);
            Assert.AreEqual(0f, output.Test_AnalysisBufferRms, 1e-6);
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

        // Wall-clock-driven GetVolume ------------------------------------
        //
        // The native analysis buffer used to be the sole RMS source, which
        // tied GetVolume's refresh rate to PCMReaderCallback cadence
        // (empirically ~3 Hz on streaming clips, regardless of DSP buffer
        // size). The new path interpolates the playback position from a
        // wall-clock stamp captured at the last drain, sweeping the RMS
        // window forward through the ring at the caller's poll rate — same
        // semantics as WebGL's AnalyserNode.getByteTimeDomainData. These
        // tests drive the TimestampProvider seam so we can advance the
        // virtual clock between RMS reads without spinning an audio thread.

        [Test]
        public void GetVolume_BeforeFirstDrain_ReturnsZero()
        {
            // Pre-drain: stamp is 0, ring may or may not have data, but no
            // playback has happened yet — visualiser should rest at zero.
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000));
            output.PushAudio(LittleEndian(short.MaxValue, short.MaxValue));
            Assert.AreEqual(0f, output.Test_ComputeWallClockRms(), 1e-6);
        }

        [Test]
        public void GetVolume_SweepsForwardBetweenDrains_AsWallClockAdvances()
        {
            // Push two chunks with distinct amplitude profiles, drain once
            // (which stamps the clock), then advance the fake clock in steps
            // and confirm the RMS reflects a forward sweep into the queued
            // (not yet drained) chunk, even though no further drain happens.
            int sampleRate = 16_000;
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate));
            // Pin the output-latency comp to 0 so this test measures pure
            // wall-clock sweep; latency compensation has its own coverage.
            output.OutputLatencySamples = 0;
            long fakeNow = 1_000_000L; // arbitrary non-zero baseline
            output.TimestampProvider = () => fakeNow;

            // First chunk: 160 samples of silence (10 ms). Second chunk: 160
            // samples at near-full amplitude. Drain just the silent half so
            // the loud half is queued ahead and the stamp is anchored at the
            // boundary.
            int chunkSamples = sampleRate * 10 / 1000; // 160
            output.PushAudio(LittleEndianSilence(chunkSamples));
            output.PushAudio(LittleEndianConstant(chunkSamples, short.MaxValue));
            output.ReadFromRing(new float[chunkSamples]); // drains the silent half

            // Right at the drain stamp: virtual head is at _readPos (start of
            // loud chunk minus windowSamples), so RMS reads back into the
            // just-played silent samples → near zero.
            float rmsAtBoundary = output.Test_ComputeWallClockRms();

            // Advance the fake clock by 10 ms → virtual head sweeps fully
            // into the loud chunk → RMS rises to near 1.
            fakeNow += (long)(0.010 * (double)StopwatchFrequency);
            float rmsInsideLoudChunk = output.Test_ComputeWallClockRms();

            Assert.Less(
                rmsAtBoundary,
                0.1f,
                $"At drain boundary, RMS should reflect just-played silence ({rmsAtBoundary})."
            );
            Assert.Greater(
                rmsInsideLoudChunk,
                0.9f,
                $"After advancing 10 ms, RMS should reflect the loud chunk ({rmsInsideLoudChunk})."
            );
        }

        [Test]
        public void GetVolume_OutputLatencyOffset_ShiftsTheRmsWindowBackward()
        {
            // Push silence followed by a long loud chunk; drain just the
            // silent half so the stamp anchors at the boundary. With a
            // non-zero latency offset, advancing the wall clock by exactly
            // the latency duration should keep the *audible* head pinned at
            // the silence boundary (consumed head − latency = 0) and RMS
            // should still read silence — that's the comp doing its job.
            // Advancing further sweeps the audible head into the loud chunk.
            int sampleRate = 16_000;
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate));
            int latencyMs = 10;
            output.OutputLatencySamples = sampleRate * latencyMs / 1000; // 160 samples
            long fakeNow = 1_500_000L;
            output.TimestampProvider = () => fakeNow;

            int silentMs = 10;
            int loudMs = 60; // long enough that the audible head can sweep into the loud region
            int silentSamples = sampleRate * silentMs / 1000;
            int loudSamples = sampleRate * loudMs / 1000;
            output.PushAudio(LittleEndianSilence(silentSamples));
            output.PushAudio(LittleEndianConstant(loudSamples, short.MaxValue));
            output.ReadFromRing(new float[silentSamples]); // drains silent half

            // Wall clock advances by exactly the latency offset → consumed
            // head has crossed into the loud chunk, but the *audible* head
            // (consumed − latency) is still at the boundary → RMS still reads
            // just-played silence.
            fakeNow += (long)((latencyMs / 1000.0) * (double)StopwatchFrequency);
            float rmsAtAudibleBoundary = output.Test_ComputeWallClockRms();
            Assert.Less(
                rmsAtAudibleBoundary,
                0.1f,
                $"With {latencyMs} ms latency comp, the audible head should still be at the boundary ({rmsAtAudibleBoundary})."
            );

            // Advance another 20 ms (well past the latency offset) → audible
            // head sweeps fully into the loud region → RMS jumps to ~1.
            fakeNow += (long)(0.020 * (double)StopwatchFrequency);
            float rmsInsideLoudChunk = output.Test_ComputeWallClockRms();
            Assert.Greater(
                rmsInsideLoudChunk,
                0.9f,
                $"After audible head crosses past the latency offset, RMS should reflect the loud chunk ({rmsInsideLoudChunk})."
            );
        }

        [Test]
        public void GetVolume_CapsVirtualOffsetAtAvailable_HoldsRmsAtTailDuringUnderrun()
        {
            // Once the wall clock has elapsed more samples than are queued,
            // virtualOffset should clamp at _available — the RMS window
            // pins to the tail of buffered audio (the last samples that
            // would have played) until a new chunk arrives. Without the
            // clamp we'd sweep into stale/freed slots and report garbage.
            int sampleRate = 16_000;
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate));
            output.OutputLatencySamples = 0; // isolate from latency comp
            long fakeNow = 2_000_000L;
            output.TimestampProvider = () => fakeNow;

            int chunkSamples = sampleRate * 20 / 1000; // 20 ms = 320
            output.PushAudio(LittleEndianConstant(chunkSamples, short.MaxValue));
            output.ReadFromRing(new float[1]); // tiny drain to stamp the clock

            // Advance the fake clock by 10 seconds — far past whatever the
            // ring has queued. Without clamping, virtualOffset would walk
            // off the end of the queued region.
            fakeNow += (long)(10.0 * StopwatchFrequency);
            float rms = output.Test_ComputeWallClockRms();

            // Should land at the tail of the loud chunk → near 1, not 0 or
            // garbage.
            Assert.Greater(
                rms,
                0.9f,
                $"Clamped RMS at queue tail should reflect the loud chunk ({rms})."
            );
        }

        [Test]
        public void GetVolume_AfterClearRing_ReturnsZero_EvenIfWallClockAdvanced()
        {
            // Interrupt → ClearRing resets the stamp to 0 so a subsequent
            // GetVolume short-circuits to 0 instead of sweeping into the
            // freshly-zeroed ring with a stale wall-clock offset (which
            // would still read 0 here, but the short-circuit is the cheaper
            // and more honest path).
            int sampleRate = 16_000;
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate));
            long fakeNow = 3_000_000L;
            output.TimestampProvider = () => fakeNow;

            int chunkSamples = sampleRate * 20 / 1000;
            output.PushAudio(LittleEndianConstant(chunkSamples, short.MaxValue));
            output.ReadFromRing(new float[1]);
            output.ClearRing();
            fakeNow += (long)(0.5 * StopwatchFrequency);

            Assert.AreEqual(0f, output.Test_ComputeWallClockRms(), 1e-6);
        }

        // Stopwatch.Frequency shorthand for the fake-clock arithmetic
        // above; cached as a double to match the production conversion.
        private static readonly double StopwatchFrequency = (double)
            System.Diagnostics.Stopwatch.Frequency;

        // Helpers: build raw little-endian PCM byte arrays of the requested
        // shape for the wall-clock tests above. (LittleEndian(...) in the
        // existing tests takes varargs, which gets noisy for hundreds of
        // identical samples.)
        private static byte[] LittleEndianSilence(int sampleCount)
        {
            return new byte[sampleCount * 2];
        }

        private static byte[] LittleEndianConstant(int sampleCount, short value)
        {
            var bytes = new byte[sampleCount * 2];
            for (int i = 0; i < sampleCount; i++)
            {
                bytes[i * 2] = (byte)(value & 0xFF);
                bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
            }
            return bytes;
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
        public void SetVolume_ClampedToZeroOne_PropagatedToEngine()
        {
            // SetVolume clamps to [0, 1] and forwards to the engine.
            // Driving the controller through FakeAudioOutputEngine lets us
            // observe the forwarded value directly — the pre-port version
            // of this test ran on NullAudioOutputEngine and could only
            // DoesNotThrow, which proves neither the clamp nor the
            // propagation. Step 3 of audio-output-testability.md.
            var fake = new FakeAudioOutputEngine();
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000), fake);

            output.SetVolume(0.5f);
            Assert.AreEqual(0.5f, fake.Volume, 1e-6);

            output.SetVolume(-1f);
            Assert.AreEqual(0f, fake.Volume, 1e-6, "negative input must clamp to 0");

            output.SetVolume(2f);
            Assert.AreEqual(1f, fake.Volume, 1e-6, "input > 1 must clamp to 1");
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

        [Test]
        public void GetByteFrequencyData_EngineUnavailable_FillsZeros()
        {
            // Disposing the engine flips IsAvailable to false → controller
            // short-circuits to Array.Clear instead of computing the FFT
            // over the (potentially non-zero) analysis buffer. Distinct
            // from the empty-analysis-buffer path covered above, where the
            // engine is available and the FFT runs but yields zeros
            // because the input is all zeros. Step 3 of
            // audio-output-testability.md.
            var fake = new FakeAudioOutputEngine();
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000), fake);
            // Prime the analysis buffer with real samples so the
            // available-engine path would yield a non-zero FFT result —
            // proves the buffer was cleared by the IsAvailable short-
            // circuit, not because the FFT happened to read zeros.
            output.PushAudio(LittleEndian(short.MaxValue, short.MaxValue, short.MaxValue));
            output.ReadFromRing(new float[3]);
            Assert.Greater(output.Test_AnalysisBufferRms, 0f);

            fake.Dispose();
            Assert.IsFalse(fake.IsAvailable);

            byte[] buffer = new byte[8];
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = 0xAB;
            output.GetByteFrequencyData(buffer);
            CollectionAssert.AreEqual(new byte[buffer.Length], buffer);
        }

        // Lifecycle --------------------------------------------------------

        [Test]
        public async Task Close_DisposesEngine()
        {
            // Close flows through to engine.Dispose so test-side fakes
            // flip IsAvailable to false (mirroring the production
            // teardown that stops AudioSource and destroys the host
            // GameObject). Step 3 of audio-output-testability.md — the
            // pre-port lifecycle assertions all went through CreateAsync
            // + real UnityAudioOutputEngine, which couldn't be exercised
            // without standing up Unity's audio subsystem.
            var fake = new FakeAudioOutputEngine();
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000), fake);
            Assert.IsTrue(fake.IsAvailable);

            await output.Close();
            Assert.IsFalse(fake.IsAvailable);
        }

        // Supplied OutputAudioSource --------------------------------------

        [Test]
        public async Task CreateAsync_CreatesOwnHost_WhenNoAudioSourceSupplied()
        {
            // Regression-lock today's owned-host path: no AudioSource supplied
            // → controller spins up a hidden host GameObject + AudioSource.
            // Clip assignment is deferred until the first PushAudio (to avoid
            // Unity's synchronous AudioClip.Create pre-fill silence-filling
            // an empty ring — see Docs~/plans/audio-output-testability.md),
            // so we push a ring-threshold's worth of audio to trigger it.
            int sampleRate = 16_000;
            var output = await UnityAudioSourceOutput.CreateAsync(
                new FormatConfig("pcm", sampleRate)
            );
            try
            {
                AudioSource? created = FindHiddenHostAudioSource();
                Assert.IsNotNull(created, "Owned-host path should create an AudioSource.");
                Assert.IsTrue(created!.loop, "Owned host streaming clip needs loop=true.");
                Assert.IsNull(
                    created.clip,
                    "Streaming clip is deferred to first PushAudio; should be null at CreateAsync."
                );

                // Push enough audio to clear the prefill threshold.
                int triggerSamples =
                    sampleRate * UnityAudioSourceOutput.PrefillThresholdMs / 1000 + 256;
                output.PushAudio(LittleEndianConstant(triggerSamples, short.MaxValue));

                Assert.IsNotNull(
                    created.clip,
                    "After first PushAudio crosses the prefill threshold, the clip should be bound."
                );
            }
            finally
            {
                await output.Close();
            }
        }

        [Test]
        public async Task CreateAsync_BindsToSuppliedAudioSource_WhenProvided()
        {
            var go = new GameObject("test-supplied-source");
            try
            {
                AudioSource supplied = go.AddComponent<AudioSource>();
                // Caller-owned settings the SDK must preserve.
                supplied.spatialBlend = 1f;
                supplied.minDistance = 2f;
                supplied.maxDistance = 20f;
                supplied.rolloffMode = AudioRolloffMode.Linear;
                supplied.panStereo = -0.5f;
                Transform originalParent = go.transform.parent;

                int sampleRate = 16_000;
                var output = await UnityAudioSourceOutput.CreateAsync(
                    new FormatConfig("pcm", sampleRate),
                    audioSource: supplied
                );
                try
                {
                    // SDK-owned overwrites that fire at CreateAsync time
                    // (clip assignment itself is deferred to first PushAudio
                    // — see Docs~/plans/audio-output-testability.md).
                    Assert.IsTrue(
                        supplied.loop,
                        "Supplied source must be looping for PCMReaderCallback."
                    );
                    Assert.AreEqual(
                        1f,
                        supplied.volume,
                        1e-6,
                        "Default user volume should be applied."
                    );

                    // Drive a chunk past the prefill threshold so the clip
                    // gets bound; this is the point where Unity's pre-fill
                    // would otherwise silence-fill an empty ring.
                    int triggerSamples =
                        sampleRate * UnityAudioSourceOutput.PrefillThresholdMs / 1000 + 256;
                    output.PushAudio(LittleEndianConstant(triggerSamples, short.MaxValue));
                    Assert.IsNotNull(
                        supplied.clip,
                        "Supplied source should be bound to the streaming clip after first PushAudio."
                    );

                    // User-owned settings preserved verbatim.
                    Assert.AreEqual(1f, supplied.spatialBlend, 1e-6);
                    Assert.AreEqual(2f, supplied.minDistance, 1e-6);
                    Assert.AreEqual(20f, supplied.maxDistance, 1e-6);
                    Assert.AreEqual(AudioRolloffMode.Linear, supplied.rolloffMode);
                    Assert.AreEqual(-0.5f, supplied.panStereo, 1e-6);
                    Assert.AreSame(
                        originalParent,
                        go.transform.parent,
                        "SDK must not reparent the supplied source."
                    );
                }
                finally
                {
                    await output.Close();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task CreateAsync_RestoresVolumeAndLoop_OnClose_WhenSourceSupplied()
        {
            var go = new GameObject("test-restore-source");
            try
            {
                AudioSource supplied = go.AddComponent<AudioSource>();
                // Non-default snapshot the SDK must capture + restore.
                supplied.volume = 0.42f;
                supplied.loop = false;
                AudioClip? originalClip = supplied.clip; // null is fine — captured + restored.

                int sampleRate = 16_000;
                var output = await UnityAudioSourceOutput.CreateAsync(
                    new FormatConfig("pcm", sampleRate),
                    audioSource: supplied
                );
                // Mid-session: SDK overwrote volume + loop per the binding
                // contract. Clip is deferred to first PushAudio.
                Assert.AreEqual(1f, supplied.volume, 1e-6);
                Assert.IsTrue(supplied.loop);

                // Trigger the deferred clip bind so we can verify Close
                // unbinds it again below.
                int triggerSamples =
                    sampleRate * UnityAudioSourceOutput.PrefillThresholdMs / 1000 + 256;
                output.PushAudio(LittleEndianConstant(triggerSamples, short.MaxValue));
                Assert.IsNotNull(supplied.clip);

                await output.Close();

                // Post-close: original caller-visible state restored symmetrically.
                Assert.AreEqual(0.42f, supplied.volume, 1e-6, "volume must be restored on Close.");
                Assert.IsFalse(supplied.loop, "loop must be restored on Close.");
                Assert.AreSame(originalClip, supplied.clip, "clip must be restored on Close.");
                Assert.IsNotNull(supplied, "AudioSource itself must not be destroyed.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task PushAudio_LogsAndNoOps_WhenSuppliedSourceDestroyedMidSession()
        {
            var go = new GameObject("test-destroy-source");
            AudioSource supplied = go.AddComponent<AudioSource>();
            var output = await UnityAudioSourceOutput.CreateAsync(
                new FormatConfig("pcm", 16_000),
                audioSource: supplied
            );
            try
            {
                // First push: source alive → samples queued normally.
                output.PushAudio(LittleEndian(1, 2, 3));
                Assert.AreEqual(3, output.Test_AvailableSamples);

                // Destroy the supplied source out from under us.
                UnityEngine.Object.DestroyImmediate(go);

                UnityEngine.TestTools.LogAssert.Expect(
                    UnityEngine.LogType.Warning,
                    new System.Text.RegularExpressions.Regex(
                        ".*OutputAudioSource was destroyed mid-session.*"
                    )
                );

                // Second push: gate fires, no-op, no exception.
                Assert.DoesNotThrow(() => output.PushAudio(LittleEndian(4, 5, 6)));

                // Third push: warning must not fire again (warn-once semantics).
                Assert.DoesNotThrow(() => output.PushAudio(LittleEndian(7, 8, 9)));
            }
            finally
            {
                await output.Close();
            }
        }

        // Reach into the scene to find the hidden owned-host AudioSource. The
        // owned-host path stamps a deterministic GameObject name so tests
        // don't need a back-door inspector for production-private state.
        // FindObjectsByType (even with FindObjectsInactive.Include) skips
        // HideAndDontSave objects; Resources.FindObjectsOfTypeAll returns
        // every loaded object regardless of hide flags.
        private static AudioSource? FindHiddenHostAudioSource()
        {
            foreach (AudioSource src in Resources.FindObjectsOfTypeAll<AudioSource>())
            {
                if (src.gameObject.name == "ElevenLabs.UnityAudioSourceOutput")
                    return src;
            }
            return null;
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
