#nullable enable

using System;
using System.Collections.Generic;
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
        public void PushAudio_BeyondInitialCapacity_GrowsRingWithoutDroppingSamples()
        {
            // 1 Hz * 5 s ring = 5 samples initial. Push 7 → the ring
            // doubles to 10 and every sample survives in FIFO order.
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 1));
            Assert.AreEqual(5, output.Test_RingCapacity);
            output.PushAudio(LittleEndian(1, 2, 3, 4, 5, 6, 7));
            Assert.AreEqual(7, output.Test_AvailableSamples);
            Assert.AreEqual(10, output.Test_RingCapacity);
            Assert.AreEqual(0, output.Test_DroppedSampleCount);

            float[] dest = new float[7];
            Assert.AreEqual(7, output.ReadFromRing(dest));
            for (int i = 0; i < 7; i++)
                Assert.AreEqual((i + 1) / 32767f, dest[i], 1e-6);
        }

        [Test]
        public void PushAudio_AtGrowthCeiling_DropsNewestAndWarnsOnce()
        {
            // 1 Hz: initial capacity 5, ceiling 60. Push 70 → the ring
            // grows to the ceiling, holds the first 60 samples intact
            // (playback stays contiguous), and the newest 10 are dropped
            // with a single warning.
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 1));
            UnityEngine.TestTools.LogAssert.Expect(
                LogType.Warning,
                new System.Text.RegularExpressions.Regex("Output ring reached its .* ceiling")
            );
            int[] samples = new int[70];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = i + 1;
            output.PushAudio(LittleEndian(samples));

            Assert.AreEqual(60, output.Test_RingCapacity);
            Assert.AreEqual(60, output.Test_AvailableSamples);
            Assert.AreEqual(10, output.Test_DroppedSampleCount);

            float[] dest = new float[60];
            Assert.AreEqual(60, output.ReadFromRing(dest));
            for (int i = 0; i < 60; i++)
                Assert.AreEqual((i + 1) / 32767f, dest[i], 1e-6);

            // A second overflowing push drops silently (warn-once) but
            // keeps counting.
            output.PushAudio(LittleEndian(samples));
            output.PushAudio(LittleEndian(samples));
            Assert.AreEqual(60, output.Test_AvailableSamples);
            Assert.AreEqual(90, output.Test_DroppedSampleCount);
        }

        // Stream continuity ------------------------------------------------
        //
        // Regression coverage for the "agent jumps ahead" field report:
        // the server streams a reply's TTS audio much faster than
        // realtime, so any reply whose buffered depth exceeds the ring
        // capacity (5 s) overflows before playback can drain it. The
        // overflow policy advances the read head past queued-but-unplayed
        // samples, audible as the agent skipping ahead mid-utterance.
        // These tests encode the sample index into each pushed sample (a
        // sawtooth over int16) and assert the drained stream is the
        // complete, gap-free image of what was pushed.

        [Test]
        public void PushAudio_BurstBeyondRingCapacity_DoesNotDropQueuedAudio()
        {
            // 8 s of audio at 16 kHz delivered in one burst (the
            // pathological limit of faster-than-realtime streaming)
            // against the 5 s ring: no sample may be lost, in any order.
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16000));
            const int totalSamples = 8 * 16000;
            const int chunkSamples = 4000;
            for (long start = 0; start < totalSamples; start += chunkSamples)
                output.PushAudio(SawtoothChunk(start, chunkSamples));

            List<float> drained = DrainAll(output);
            AssertSawtoothStream(drained, startIndex: 0, expectedCount: totalSamples);
        }

        [Test]
        public void PushAudio_FasterThanRealtimeDelivery_PlaysBackContiguously()
        {
            // Same failure mode as the burst test but with playback
            // draining concurrently at realtime pace while delivery runs
            // at 2× realtime — the shape of a live session. Buffered depth
            // grows by one chunk per iteration until it crosses the ring
            // capacity mid-utterance, so a drop-oldest overflow skips
            // samples that were next in line for the speaker (a
            // mid-playback jump rather than a truncated head).
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16000));
            const int chunkSamples = 1600; // 100 ms at 16 kHz
            const int totalChunks = 160; // 16 s utterance
            var drained = new List<float>();
            float[] dest = new float[chunkSamples];
            long pushed = 0;
            for (int i = 0; i < totalChunks; i += 2)
            {
                output.PushAudio(SawtoothChunk(pushed, chunkSamples));
                pushed += chunkSamples;
                output.PushAudio(SawtoothChunk(pushed, chunkSamples));
                pushed += chunkSamples;
                int n = output.ReadFromRing(dest);
                for (int k = 0; k < n; k++)
                    drained.Add(dest[k]);
            }
            drained.AddRange(DrainAll(output));
            AssertSawtoothStream(drained, startIndex: 0, expectedCount: pushed);
        }

        [Test]
        public void PushAudio_SecondUtteranceAfterSilenceGap_PlaysFromItsFirstSample()
        {
            // Guards the turn boundary itself: after the first utterance
            // drains and the audio thread fires silence-only drains for a
            // while, the next utterance must play from its first sample —
            // nothing consumed, skipped, or replayed across the gap.
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16000));
            output.PushAudio(SawtoothChunk(0, 1600));
            List<float> firstTurn = DrainAll(output);
            AssertSawtoothStream(firstTurn, startIndex: 0, expectedCount: 1600);

            // Between turns the ring is empty and drains return nothing.
            float[] dest = new float[256];
            for (int i = 0; i < 5; i++)
                Assert.AreEqual(0, output.ReadFromRing(dest));

            // Second turn uses a distinct index range so replayed
            // first-turn samples can't masquerade as second-turn ones
            // (105000 % SawtoothPeriod = 5000 ≠ any first-turn index).
            const long secondTurnStart = 105_000;
            output.PushAudio(SawtoothChunk(secondTurnStart, 3200));
            List<float> secondTurn = DrainAll(output);
            AssertSawtoothStream(secondTurn, startIndex: secondTurnStart, expectedCount: 3200);
        }

        // Interrupt fade ----------------------------------------------------
        //
        // The fade path (Play Mode in production; driven here through the
        // NextFrameAsyncProvider / UnscaledTimeProvider seams) owns the
        // post-fade ClearRing. These tests pin the two contracts around
        // that ownership: audio condemned by an interrupt must never play
        // once new audio arrives (even though cancelling the fade skips
        // its ClearRing), and an undisturbed fade must flush and restore
        // volume on its own.

        [Test]
        public void PushAudio_DuringInterruptFade_FlushesInterruptedAudioBeforeNewChunk()
        {
            var engine = new FakeAudioOutputEngine { SyncPrefillCallbackCount = 0 };
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16000), engine);
            output.NextFrameAsyncProvider = MakeFramePump(new Queue<AwaitableCompletionSource>());

            // Agent utterance, partially played when the user barges in.
            output.PushAudio(SawtoothChunk(0, 1600));
            float[] dest = new float[256];
            Assert.AreEqual(256, output.ReadFromRing(dest));

            output.Interrupt(); // default 2000 ms fade via the frame seam
            Assert.Greater(
                output.Test_AvailableSamples,
                0,
                "Mid-fade, the condemned remainder is still queued (it fades out audibly)."
            );

            // The agent's next reply arrives before the fade completes.
            // It must not queue behind the interrupted utterance's
            // remainder — and that remainder must never play at the
            // restored full volume.
            const long secondTurnStart = 105_000;
            output.PushAudio(SawtoothChunk(secondTurnStart, 800));
            List<float> drained = DrainAll(output);
            AssertSawtoothStream(drained, startIndex: secondTurnStart, expectedCount: 800);
        }

        [Test]
        public void InterruptFade_RunsToCompletion_ClearsRingAndRestoresUserVolume()
        {
            var engine = new FakeAudioOutputEngine { SyncPrefillCallbackCount = 0 };
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16000), engine);
            var framePump = new Queue<AwaitableCompletionSource>();
            output.NextFrameAsyncProvider = MakeFramePump(framePump);
            float now = 0f;
            output.UnscaledTimeProvider = () => now;

            output.SetVolume(0.8f);
            output.PushAudio(SawtoothChunk(0, 1600));
            output.Interrupt(); // 2000 ms fade

            // Mid-fade: volume lerps toward zero, condemned audio still
            // queued (it's what the fade plays out).
            now = 1f;
            framePump.Dequeue().SetResult();
            Assert.AreEqual(0.4f, engine.Volume, 1e-3);
            Assert.Greater(output.Test_AvailableSamples, 0);

            // Fade end: ring flushed, user volume restored.
            now = 2.1f;
            framePump.Dequeue().SetResult();
            Assert.AreEqual(0, output.Test_AvailableSamples);
            Assert.AreEqual(0.8f, engine.Volume, 1e-3);
        }

        // Frame-ticker seam for the fade tests: each fade-loop iteration
        // enqueues a fresh manually-completed source; cancelling the fade
        // token cancels the pending frame awaitable the way the real
        // Awaitable.NextFrameAsync(token) does.
        private static Func<System.Threading.CancellationToken, Awaitable> MakeFramePump(
            Queue<AwaitableCompletionSource> pump
        ) =>
            token =>
            {
                var source = new AwaitableCompletionSource();
                token.Register(() => source.TrySetCanceled());
                pump.Enqueue(source);
                return source.Awaitable;
            };

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
        public void GetVolume_SweepsForwardWithWallClock_AfterFirstDrainStampsAnchor()
        {
            // New (bob-alignment-redesign) model: the first drain after
            // engine.Start stamps _playbackStartRingPos (= pre-drain linear
            // read pos, i.e. where the speaker started playing FROM) and
            // _playbackStartStampTicks (= now). ComputeWallClockRms then
            // sweeps the audible head forward at the sample rate, *not*
            // tied to subsequent drains. Drain at sample 0, advance the
            // fake clock past the silent prefix, confirm the bob lights up
            // when the audible head reaches the LOUD chunk.
            int sampleRate = 16_000;
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate));
            long fakeNow = 1_000_000L; // arbitrary non-zero baseline
            output.TimestampProvider = () => fakeNow;

            // Content layout: 5 ms SILENCE | 20 ms LOUD | 75 ms SILENCE.
            //   - First drain (1 sample) stamps anchor at preDrainLinearPos=0.
            //   - audibleRingPos = 0 + (elapsed * sampleRate).
            //   - LOUD occupies linear positions [80, 400).
            int silentHeadSamples = sampleRate * 5 / 1000; // 80
            int loudSamples = sampleRate * 20 / 1000; // 320
            int silentTailSamples = sampleRate * 75 / 1000; // 1200
            output.PushAudio(LittleEndianSilence(silentHeadSamples));
            output.PushAudio(LittleEndianConstant(loudSamples, short.MaxValue));
            output.PushAudio(LittleEndianSilence(silentTailSamples));
            output.ReadFromRing(new float[1]); // stamps anchor

            // Pre-advance: audible head at 0 → window before _playbackStartRingPos
            // → padded silence → RMS = 0.
            float rmsAtAnchor = output.Test_ComputeWallClockRms();
            Assert.Less(rmsAtAnchor, 0.1f, $"At anchor, no audio has played yet ({rmsAtAnchor}).");

            // Advance ~12 ms → audible head ≈ 192 → window [112, 192) → inside LOUD → ~1.
            fakeNow += (long)(0.012 * (double)StopwatchFrequency);
            float rmsInsideLoud = output.Test_ComputeWallClockRms();
            Assert.Greater(
                rmsInsideLoud,
                0.9f,
                $"After wall-clock advance into LOUD, bob should light up ({rmsInsideLoud})."
            );

            // Advance further to ~30 ms → audible head ≈ 480 → past LOUD (which
            // ended at 400) → window [400, 480) → in silent tail → RMS ≈ 0.
            fakeNow += (long)(0.018 * (double)StopwatchFrequency);
            float rmsAfterLoud = output.Test_ComputeWallClockRms();
            Assert.Less(
                rmsAfterLoud,
                0.1f,
                $"After wall-clock advance past LOUD, bob should fall back to 0 ({rmsAfterLoud})."
            );
        }

        [Test]
        public void GetVolume_AudibleHead_CatchesUpToWritePos_ReturnsZeroAndResetsAnchor()
        {
            // Once the wall clock has elapsed past every sample queued, the
            // audible head has caught up to _writePosLinear — the speaker has
            // finished real audio and is on silence. GetVolume must return 0
            // (not pin RMS at the chunk tail like a clamp would) AND reset
            // the audible-head anchor so the next turn's first real-sample
            // drain in ReadFromRing re-stamps cleanly.
            //
            // Holding at the tail was the bug behind the "second-turn bob
            // jumps to the new chunk's tail" PlayMode report: between
            // organic turns (no Interrupt → no ClearRing) the anchor stayed
            // pinned to turn 1's start, the clamp held RMS at the
            // _writePosLinear edge, and turn 2's PushAudio just moved that
            // edge forward by a whole chunk's worth — so the bob jumped
            // straight to the unplayed tail instead of tracking the audible
            // signal.
            int sampleRate = 16_000;
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate));
            long fakeNow = 2_000_000L;
            output.TimestampProvider = () => fakeNow;

            int chunkSamples = sampleRate * 20 / 1000; // 20 ms = 320
            output.PushAudio(LittleEndianConstant(chunkSamples, short.MaxValue));
            output.ReadFromRing(new float[1]); // tiny drain to stamp the anchor

            // Advance the fake clock by 10 seconds — far past whatever the
            // ring has queued. The audible head sweeps off the end and
            // GetVolume reports silence + resets the anchor.
            fakeNow += (long)(10.0 * StopwatchFrequency);
            float rms = output.Test_ComputeWallClockRms();

            Assert.AreEqual(
                0f,
                rms,
                1e-6,
                "Audible head past _writePosLinear means the speaker is on silence "
                    + $"— GetVolume must return 0, not pin RMS at the chunk tail ({rms})."
            );
            // Anchor reset is observable via the stampTicks==0 short-circuit:
            // a subsequent call must also return 0 immediately, even if more
            // wall-clock elapses, because there are no new real drains.
            fakeNow += (long)(0.5 * StopwatchFrequency);
            Assert.AreEqual(
                0f,
                output.Test_ComputeWallClockRms(),
                1e-6,
                "Anchor must have been reset on catch-up — subsequent GetVolume "
                    + "calls during the silence gap should keep short-circuiting."
            );
        }

        [Test]
        public void GetVolume_OrganicTurnTransition_RestampsOnFirstDrainOfNextTurn()
        {
            // Regression for the second-turn-misalignment bug: with no
            // Interrupt between turns (organic turn boundary — agent
            // finishes turn 1, silence, then turn 2 begins), the audible-
            // head anchor must reset so turn 2's first real-sample drain
            // in ReadFromRing re-stamps with the current wall clock and
            // ring position. Without the reset, the stale turn-1 anchor
            // leaves the wall-clock model saturated past _writePosLinear,
            // and turn 2's bob jumps to the new chunk's tail instead of
            // sweeping from its head.
            int sampleRate = 16_000;
            var fake = new FakeAudioOutputEngine
            {
                SyncPrefillCallbackCount = 0,
                OngoingCallbackBatchSize = 160, // 10 ms at 16 kHz
                OngoingCallbackPeriodSeconds = 0.333,
            };
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate), fake);
            long fakeNow = 7_000_000L;
            output.TimestampProvider = () => fakeNow;

            // Turn 1: push a LOUD chunk, fire one drain so the anchor stamps,
            // and advance just enough wall-clock to sweep the audible head
            // PAST the chunk tail. This mimics the natural end of turn 1 —
            // ring fully consumed by the engine, no Interrupt called.
            int turn1Samples = sampleRate * 40 / 1000; // 40 ms = 640
            output.PushAudio(LittleEndianConstant(turn1Samples, short.MaxValue));
            fake.Tick(0.333); // first real drain → stamps anchor at preDrainPos=0, t=fakeNow
            fakeNow += (long)(1.0 * StopwatchFrequency); // wall clock past every queued sample

            // GetVolume here returns 0 AND resets the anchor — this is the
            // step that makes turn 2's first real drain re-stamp.
            Assert.AreEqual(
                0f,
                output.Test_ComputeWallClockRms(),
                1e-6,
                "End of turn 1: audible head past _writePosLinear → bob drops to 0 "
                    + "and the anchor resets so turn 2 can re-stamp."
            );

            // Silence gap between turns — additional wall-clock + a stray
            // silence-only drain (the audio thread keeps firing). Neither
            // should re-stamp because there are no real samples in the ring.
            fakeNow += (long)(0.5 * StopwatchFrequency);
            fake.Tick(0.333);

            // Turn 2: push a LOUD chunk. The next real-sample drain must
            // re-stamp the anchor with the current wall clock + current
            // _readPosLinear (which sits at end-of-turn-1 since silence
            // drains didn't advance it).
            int turn2Samples = sampleRate * 100 / 1000; // 100 ms = 1,600
            output.PushAudio(LittleEndianConstant(turn2Samples, short.MaxValue));
            fake.Tick(0.333); // first real drain after the catch-up reset → re-stamps

            // Advance 10 ms past the re-stamp → audibleRingPos sweeps 160
            // samples into the turn-2 chunk → window lies inside the LOUD
            // region → bob lights up. Without the catch-up reset this would
            // still be saturated against the stale turn-1 anchor and pin RMS
            // at the new chunk's tail (also LOUD here, but for the WRONG
            // reason — the assertion below would pass even with the bug,
            // which is why the resetting GetVolume call above is the
            // load-bearing observation for this regression).
            fakeNow += (long)(0.010 * StopwatchFrequency);
            float rmsEarlyInTurn2 = output.Test_ComputeWallClockRms();
            Assert.Greater(
                rmsEarlyInTurn2,
                0.9f,
                $"After the organic turn boundary, turn 2's first real drain must "
                    + $"re-stamp the anchor so the bob tracks the audible head, not "
                    + $"the stale turn-1 anchor's clamp ({rmsEarlyInTurn2})."
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
            // Regression-lock the owned-host path: no AudioSource supplied
            // → controller spins up a hidden host GameObject + AudioSource.
            // Under the generator engine wired by CreateAsync (step 6 of
            // Docs~/plans/audio-generator-engine.md), engine.Start binds an
            // AgentAudioGeneratorComponent to AudioSource.generator on the
            // first PushAudio crossing the prefill threshold — there is no
            // streaming AudioClip in this path. The deferral pattern itself
            // is unchanged: the controller defers engine.Start until enough
            // audio is queued so Unity doesn't observe an empty pipeline at
            // Play() time.
            int sampleRate = 16_000;
            var output = await UnityAudioSourceOutput.CreateAsync(
                new FormatConfig("pcm", sampleRate)
            );
            try
            {
                AudioSource? created = FindHiddenHostAudioSource();
                Assert.IsNotNull(created, "Owned-host path should create an AudioSource.");
                Assert.IsTrue(created!.loop, "Owned host needs loop=true for generator playback.");
                Assert.IsNull(
                    created.generator,
                    "Generator binding is deferred to first PushAudio; should be null at CreateAsync."
                );

                // Push enough audio to clear the engine-driven prefill threshold.
                int triggerSamples = output.ComputePrefillThresholdSamples() + 256;
                output.PushAudio(LittleEndianConstant(triggerSamples, short.MaxValue));

                Assert.IsNotNull(
                    created.generator,
                    "After first PushAudio crosses the prefill threshold, AudioSource.generator should be bound."
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
                    // (generator binding itself is deferred to first PushAudio
                    // — see Docs~/plans/audio-output-testability.md and step 6
                    // of Docs~/plans/audio-generator-engine.md).
                    Assert.IsTrue(
                        supplied.loop,
                        "Supplied source must be looping for generator playback."
                    );
                    Assert.AreEqual(
                        1f,
                        supplied.volume,
                        1e-6,
                        "Default user volume should be applied."
                    );

                    // Drive a chunk past the prefill threshold so the
                    // AgentAudioGeneratorComponent gets bound to
                    // AudioSource.generator; the controller defers
                    // engine.Start until enough audio is queued.
                    int triggerSamples = output.ComputePrefillThresholdSamples() + 256;
                    output.PushAudio(LittleEndianConstant(triggerSamples, short.MaxValue));
                    Assert.IsNotNull(
                        supplied.generator,
                        "Supplied source should be bound to the generator after first PushAudio."
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
                UnityEngine.Audio.IAudioGenerator? originalGenerator = supplied.generator; // null is fine — captured + restored.

                int sampleRate = 16_000;
                var output = await UnityAudioSourceOutput.CreateAsync(
                    new FormatConfig("pcm", sampleRate),
                    audioSource: supplied
                );
                // Mid-session: SDK overwrote volume + loop per the binding
                // contract. Generator binding is deferred to first PushAudio.
                Assert.AreEqual(1f, supplied.volume, 1e-6);
                Assert.IsTrue(supplied.loop);

                // Trigger the deferred generator bind so we can verify Close
                // unbinds it again below.
                int triggerSamples = output.ComputePrefillThresholdSamples() + 256;
                output.PushAudio(LittleEndianConstant(triggerSamples, short.MaxValue));
                Assert.IsNotNull(supplied.generator);

                await output.Close();

                // Post-close: original caller-visible state restored symmetrically.
                Assert.AreEqual(0.42f, supplied.volume, 1e-6, "volume must be restored on Close.");
                Assert.IsFalse(supplied.loop, "loop must be restored on Close.");
                Assert.AreSame(originalClip, supplied.clip, "clip must be restored on Close.");
                Assert.AreSame(
                    originalGenerator,
                    supplied.generator,
                    "generator must be restored on Close."
                );
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

        // Engine integration via FakeAudioOutputEngine -------------------
        //
        // Step 4 of Docs~/plans/audio-output-testability.md: exercises the
        // threshold-gate, sync pre-fill, and wall-clock interpolation paths
        // against a calibrated fake engine so Unity's streaming-AudioClip
        // behaviours can be regression-locked from Edit Mode. Two tests are
        // [Ignore]d because they document open bugs (single-chunk timeout
        // fallback + bob alignment) that the step-6 redesign will fix.

        [Test]
        public void Output_EmptyRingAtStart_PrefillFillsSilence_AndQueuesAheadOfRealAudio()
        {
            // Regression for the original ~800 ms gap (pre-threshold-gate).
            // If engine.Start fires before the ring is filled, Unity's
            // synchronous pre-fill (~12,800 samples on the default Unity 6
            // audio config) silence-fills the streaming buffer ahead of
            // whatever real audio arrives next. The threshold gate prevents
            // this in production; here we bypass the gate by invoking
            // fake.Start directly to lock the failure mode in case someone
            // removes the gate later.
            int sampleRate = 16_000;
            var fake = new FakeAudioOutputEngine();
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate), fake);

            // 100 samples is far below the prefill threshold (~14,400 at 16 kHz)
            // so the controller will not trigger engine.Start on its own.
            output.PushAudio(LittleEndianConstant(100, short.MaxValue));
            Assert.AreEqual(
                0,
                fake.StartCallCount,
                "Below-threshold push must not trigger engine.Start."
            );

            // Manually fire Start to simulate the pre-threshold-gate world:
            // the under-supplied ring is the only drain source for the
            // 12,800-sample sync pre-fill.
            fake.Start(new FormatConfig("pcm", sampleRate), output.ReadFromRing);

            Assert.AreEqual(100, fake.RecordedRealSampleCount);
            Assert.AreEqual(
                12_800 - 100,
                fake.RecordedSilenceFillSamples,
                "Sync pre-fill should silence-fill ahead of the real samples when the ring is empty."
            );
        }

        [Test]
        public void Output_ThresholdGate_DelaysStartUntilRingDepthMet()
        {
            // Current threshold-gate behaviour: engine.Start defers until
            // the ring holds at least IAudioOutputEngine.SyncPrefillSampleCount
            // (plus the controller's DSP-buffer margin) so the synchronous
            // pre-fill lands entirely on real samples.
            int sampleRate = 16_000;
            var fake = new FakeAudioOutputEngine();
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate), fake);

            int thresholdSamples = output.ComputePrefillThresholdSamples();
            // Push half the threshold's worth — gate holds, Start deferred.
            output.PushAudio(LittleEndianConstant(thresholdSamples / 2, short.MaxValue));
            Assert.AreEqual(0, fake.StartCallCount, "Below threshold, Start stays deferred.");

            // Top up over the threshold — Start fires on the second push.
            output.PushAudio(LittleEndianConstant(thresholdSamples / 2 + 256, short.MaxValue));
            Assert.AreEqual(1, fake.StartCallCount, "Crossing threshold should fire Start.");
        }

        [Test]
        public void Output_ThresholdGate_TimeoutFiresIfNoFurtherChunks()
        {
            // Push a tiny chunk below the prefill threshold and never push
            // anything else. The threshold-gate's wall-clock timeout
            // fallback runs on its own Awaitable timer (via the
            // WaitForSecondsAsyncProvider seam) so engine.Start fires once
            // the timeout elapses, even without a follow-up PushAudio.
            int sampleRate = 16_000;
            var fake = new FakeAudioOutputEngine();
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate), fake);

            // Stub the timer awaitable so we control when it completes
            // (Awaitable.WaitForSecondsAsync needs Unity's frame loop, which
            // doesn't tick in Edit Mode batch tests).
            var timerSource = new AwaitableCompletionSource();
            output.WaitForSecondsAsyncProvider = (sec, ct) => timerSource.Awaitable;

            output.PushAudio(LittleEndianConstant(100, short.MaxValue));
            Assert.AreEqual(0, fake.StartCallCount, "Pre-timer: Start should not have fired yet.");

            // Fire the timer → continuation runs synchronously → StartPlayback.
            timerSource.SetResult();

            Assert.AreEqual(
                1,
                fake.StartCallCount,
                "Single-chunk fallback should fire Start once the wall-clock timeout elapses,"
                    + " even without a follow-up PushAudio."
            );
        }

        [Test]
        public void Output_ThresholdGate_TimerCanceled_IfThresholdTripsFirst()
        {
            // If a follow-up PushAudio trips the threshold before the timer
            // fires, the timer should be cancelled and StartPlayback should
            // run only once.
            int sampleRate = 16_000;
            var fake = new FakeAudioOutputEngine();
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate), fake);

            var timerSource = new AwaitableCompletionSource();
            output.WaitForSecondsAsyncProvider = (sec, ct) => timerSource.Awaitable;

            output.PushAudio(LittleEndianConstant(100, short.MaxValue));
            Assert.AreEqual(0, fake.StartCallCount);

            // Cross the threshold before the timer fires.
            int thresholdSamples = output.ComputePrefillThresholdSamples();
            output.PushAudio(LittleEndianConstant(thresholdSamples, short.MaxValue));
            Assert.AreEqual(1, fake.StartCallCount, "Threshold-met path should fire Start once.");

            // Fire the stubbed timer late; the redundant-start guard means
            // StartCallCount stays at 1 even if the continuation runs.
            timerSource.SetResult();
            Assert.AreEqual(
                1,
                fake.StartCallCount,
                "Late-firing timer should not double-fire Start (idempotent gate)."
            );
        }

        [Test]
        public void Output_PrefillDrainsEntirelyRealSamples_WhenRingHasEnoughDepth()
        {
            // Happy path: ring holds well over the prefill demand → the
            // synchronous pre-fill drains 12,800 real samples and silence-
            // fills nothing. Mirror of the empty-ring regression above
            // with the threshold gate doing its job.
            int sampleRate = 16_000;
            var fake = new FakeAudioOutputEngine();
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate), fake);

            // 2 s of audio = 32,000 samples — comfortably past the 12,800
            // sample sync pre-fill demand AND past the 14,400 sample threshold.
            output.PushAudio(LittleEndianConstant(sampleRate * 2, short.MaxValue));

            Assert.AreEqual(1, fake.StartCallCount);
            Assert.AreEqual(12_800, fake.RecordedRealSampleCount);
            Assert.AreEqual(
                0,
                fake.RecordedSilenceFillSamples,
                "With a well-supplied ring, the sync pre-fill should never silence-fill."
            );
        }

        [Test]
        public void GetVolume_TracksPlaybackPosition_DuringSyncPrefill_NotDrainHead()
        {
            // After the synchronous pre-fill, _readPos sits ~12,800 samples
            // ahead of where the speaker is actually playing. GetVolume in
            // the new (bob-alignment-redesign) model anchors on
            // _playbackStartRingPos (the linear ring pos BEFORE the first
            // drain) so the audible head starts at the beginning of the
            // queued audio, not at the drain head. Advance the clock 10 ms
            // → audible head into the LOUD chunk → bob lights up.
            //
            // Content layout: 100 ms LOUD | 850 ms SILENCE.
            //   - Threshold met by the end of the second push (15,200 > 14,400).
            //   - Sync pre-fill drains 12,800 samples; _playbackStartRingPos = 0
            //     was captured before the first drain. _readPosLinear lands at
            //     12,800 (deep inside the SILENCE region).
            // OLD-MODEL bug: reads samples around _readPos → silence → RMS ≈ 0.
            // NEW MODEL:    reads samples around audible head (= 0 + elapsed × sampleRate)
            //               → at 10 ms elapsed, reads LOUD start → RMS ≈ 1.
            int sampleRate = 16_000;
            var fake = new FakeAudioOutputEngine();
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate), fake);
            long fakeNow = 1_000_000L;
            output.TimestampProvider = () => fakeNow;

            int loudSamples = sampleRate * 100 / 1000; // 1,600
            int silenceSamples = sampleRate * 850 / 1000; // 13,600 → total = 15,200 > threshold.
            output.PushAudio(LittleEndianConstant(loudSamples, short.MaxValue));
            output.PushAudio(LittleEndianSilence(silenceSamples));

            Assert.AreEqual(
                1,
                fake.StartCallCount,
                "Threshold should trigger Start by the end of the second push."
            );

            // Advance 10 ms → audible head ≈ 160 → window [80, 160) — all
            // inside the LOUD chunk ([0, 1600)) → bob ≈ 1.
            fakeNow += (long)(0.010 * StopwatchFrequency);
            float rms = output.Test_ComputeWallClockRms();
            Assert.Greater(
                rms,
                0.9f,
                $"Bob should track audible playback head at the start of the clip, got {rms}"
                    + " (see Docs~/plans/bob-alignment-redesign.md)."
            );
        }

        [Test]
        public void GetVolume_TracksPlaybackPosition_BetweenOngoingDrains()
        {
            // Wall-clock interpolation: between ongoing drain fires,
            // GetVolume sweeps forward through the ring at the caller's
            // poll rate so the bob's update rate is decoupled from
            // PCMReaderCallback's ~3 Hz cadence. Drive the fake to fire
            // one ongoing drain (stamping the wall clock + advancing
            // _readPos to the SILENCE/LOUD boundary), then advance the
            // fake clock between drains and confirm RMS sweeps into the
            // LOUD chunk without waiting for the next Tick.
            int sampleRate = 16_000;
            var fake = new FakeAudioOutputEngine
            {
                // Skip the sync pre-fill so the test controls _readPos
                // positioning explicitly via Tick.
                SyncPrefillCallbackCount = 0,
                OngoingCallbackBatchSize = 160, // 10 ms at 16 kHz.
                OngoingCallbackPeriodSeconds = 0.333,
            };
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate), fake);
            long fakeNow = 1_000_000L;
            output.TimestampProvider = () => fakeNow;

            // Content layout: 10 ms SILENCE | 100 ms LOUD | ~890 ms SILENCE.
            // Total = 16,000 samples (1 s) → crosses threshold on the third push.
            int silenceHeadSamples = sampleRate * 10 / 1000; // 160
            int loudSamples = sampleRate * 100 / 1000; // 1,600
            int silenceTailSamples = sampleRate - silenceHeadSamples - loudSamples; // 14,240
            output.PushAudio(LittleEndianSilence(silenceHeadSamples));
            output.PushAudio(LittleEndianConstant(loudSamples, short.MaxValue));
            output.PushAudio(LittleEndianSilence(silenceTailSamples));

            Assert.AreEqual(1, fake.StartCallCount);
            // Pre-drain: anchor not set yet (no drain has happened) → bob
            // short-circuits to zero via the _playbackStartStampTicks == 0
            // guard.
            Assert.AreEqual(
                0f,
                output.Test_ComputeWallClockRms(),
                1e-6,
                "Pre-drain: no playback yet, bob should rest at zero."
            );

            // Tick fires one ongoing drain of 160 samples → first drain
            // stamps anchor at preDrainPos=0, ticks=fakeNow. _readPosLinear
            // advances to 160 (start of LOUD region in content coords).
            fake.Tick(0.333);

            // Right at the anchor (elapsed=0): audible head at 0, window
            // ends BEFORE _playbackStartRingPos → padded silence → 0.
            float rmsAtDrain = output.Test_ComputeWallClockRms();
            Assert.Less(
                rmsAtDrain,
                0.1f,
                $"Right after the drain, bob should reflect not-yet-played silence ({rmsAtDrain})."
            );

            // Advance the fake wall clock by 20 ms WITHOUT firing another
            // drain (20 ms < 333 ms ongoing period). Audible head sweeps
            // to 320 → window [240, 320) → fully inside LOUD region
            // ([160, 1760)) → bob ≈ 1.
            fakeNow += (long)(0.020 * StopwatchFrequency);

            float rmsBetweenDrains = output.Test_ComputeWallClockRms();
            Assert.Greater(
                rmsBetweenDrains,
                0.9f,
                $"Wall-clock sweep between drains should advance bob into LOUD ({rmsBetweenDrains})."
            );
        }

        [Test]
        public void PushAudio_DuringPrefill_ExtendsRingButDoesNotRetriggerStart()
        {
            // Once Start has fired, additional PushAudio calls extend the
            // ring but must not re-invoke engine.Start — restarting would
            // tear down the streaming AudioClip and replay the pre-fill
            // silence-fill behaviour.
            int sampleRate = 16_000;
            var fake = new FakeAudioOutputEngine();
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate), fake);

            int triggerSamples = output.ComputePrefillThresholdSamples() + 256;
            output.PushAudio(LittleEndianConstant(triggerSamples, short.MaxValue));
            Assert.AreEqual(1, fake.StartCallCount, "First past-threshold push fires Start.");

            output.PushAudio(LittleEndianConstant(1_000, short.MaxValue));
            output.PushAudio(LittleEndianConstant(1_000, short.MaxValue));
            Assert.AreEqual(1, fake.StartCallCount, "Subsequent pushes must not re-trigger Start.");
        }

        [Test]
        public void Interrupt_ClearsRing_AndResetsPlaybackStartAnchor()
        {
            // Interrupt → ClearRing zeroes the ring AND the wall-clock
            // anchor so a follow-up GetVolume doesn't sweep into the
            // now-empty ring at a stale offset. Drive the drain through
            // the fake to set the anchor before the interrupt clears it,
            // then advance the clock and confirm GetVolume short-circuits
            // to 0.
            int sampleRate = 16_000;
            var fake = new FakeAudioOutputEngine
            {
                SyncPrefillCallbackCount = 0,
                OngoingCallbackBatchSize = 160,
                OngoingCallbackPeriodSeconds = 0.333,
            };
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate), fake);
            long fakeNow = 1_000_000L;
            output.TimestampProvider = () => fakeNow;

            int triggerSamples = output.ComputePrefillThresholdSamples() + 256;
            output.PushAudio(LittleEndianConstant(triggerSamples, short.MaxValue));

            // One ongoing drain → first drain stamps anchor at preDrainPos=0.
            // Advance the clock 10 ms so the audible head sweeps into the
            // LOUD content → bob lights up.
            fake.Tick(0.333);
            fakeNow += (long)(0.010 * StopwatchFrequency);
            Assert.Greater(
                output.Test_ComputeWallClockRms(),
                0.5f,
                "Post-drain RMS should reflect LOUD samples after wall-clock advance into the content."
            );

            output.Interrupt(resetDurationMs: 0);
            Assert.AreEqual(0, output.Test_AvailableSamples, "Interrupt should clear the ring.");

            // Without the anchor reset, GetVolume would sweep into the
            // now-empty ring at the stale offset. With it, the
            // _playbackStartStampTicks == 0 short-circuit fires.
            fakeNow += (long)(0.1 * StopwatchFrequency);
            Assert.AreEqual(
                0f,
                output.Test_ComputeWallClockRms(),
                1e-6,
                "Post-Interrupt GetVolume should short-circuit via the anchor reset."
            );
        }

        [Test]
        public void GetVolume_AfterClearRingThenNewChunk_OnlyReStampsOnFirstRealDrain()
        {
            // After ClearRing resets the anchor to 0, the audio thread keeps
            // firing drain callbacks during the silence gap between turns
            // (Unity's streaming buffer demands samples even when the ring
            // is empty). Those drains return n=0 and silence-fill Unity's
            // buffer. They MUST NOT re-stamp the audible-head anchor —
            // doing so would anchor the wall-clock model at the silence-gap
            // start, and by the time the next turn's real samples land the
            // audible head has already swept forward past them, making
            // GetVolume jump to the chunk's tail instead of starting at the
            // chunk's head.
            //
            // Pinned in production code at
            // [`UnityAudioSourceOutput.ReadFromRing`](../../Runtime/Native/UnityAudioSourceOutput.cs)
            // via the `_playbackStartStampTicks == 0 && n > 0` gate. Without
            // the `n > 0` clause this test would fail: the first empty
            // drain after ClearRing stamps, and the assertion below would
            // see audibleRingPos already swept past the new chunk.
            int sampleRate = 16_000;
            var fake = new FakeAudioOutputEngine
            {
                SyncPrefillCallbackCount = 0,
                OngoingCallbackBatchSize = 160, // 10 ms at 16 kHz
                OngoingCallbackPeriodSeconds = 0.333,
            };
            var output = new UnityAudioSourceOutput(new FormatConfig("pcm", sampleRate), fake);
            long fakeNow = 5_000_000L;
            output.TimestampProvider = () => fakeNow;

            // Turn 1: push enough to trip the threshold + drain once to
            // stamp the anchor + advance into LOUD content.
            int triggerSamples = output.ComputePrefillThresholdSamples() + 256;
            output.PushAudio(LittleEndianConstant(triggerSamples, short.MaxValue));
            fake.Tick(0.333); // first real drain → stamps anchor at preDrainPos=0, t=fakeNow.

            // Interrupt synchronously to clear the ring and reset the
            // anchor (resetDurationMs=0 skips the fade path).
            output.Interrupt(resetDurationMs: 0);
            Assert.AreEqual(0, output.Test_AvailableSamples, "Interrupt should clear the ring.");

            // Simulate the silence gap between turns: 1 s of wall-clock,
            // three Tick fires that each return n=0 (ring is empty after
            // ClearRing). With the n>0 gate, none of these stamp the
            // anchor.
            fakeNow += (long)(0.333 * StopwatchFrequency);
            fake.Tick(0.333);
            fakeNow += (long)(0.333 * StopwatchFrequency);
            fake.Tick(0.333);
            fakeNow += (long)(0.333 * StopwatchFrequency);
            fake.Tick(0.333);

            // Pre-PushAudio: still no real drains since ClearRing → anchor
            // remains 0 → GetVolume short-circuits to 0.
            Assert.AreEqual(
                0f,
                output.Test_ComputeWallClockRms(),
                1e-6,
                "Silence-only drains between turns must not re-stamp the anchor; "
                    + "GetVolume should stay at 0 via the stampTicks==0 guard."
            );

            // Turn 2: push a LOUD chunk and fire the next ongoing drain. THIS
            // drain returns n>0 and re-stamps the anchor at the current
            // wall-clock — so audibleRingPos starts at 0 again and sweeps
            // forward from this moment, not from the silence-gap start.
            int turn2LoudSamples = sampleRate * 100 / 1000; // 100 ms = 1,600
            output.PushAudio(LittleEndianConstant(turn2LoudSamples, short.MaxValue));
            fake.Tick(0.333); // first REAL drain after ClearRing → re-stamps.

            // Advance 10 ms past the new stamp → audibleRingPos ≈ 160 →
            // window [80, 160) → inside the LOUD turn-2 chunk → bob lights up.
            fakeNow += (long)(0.010 * StopwatchFrequency);
            float rmsEarlyInTurn2 = output.Test_ComputeWallClockRms();
            Assert.Greater(
                rmsEarlyInTurn2,
                0.9f,
                $"After the re-stamp on turn 2's first real drain, the audible head "
                    + $"should be ~160 samples into the LOUD chunk, not jumped to its tail "
                    + $"or stuck at silence ({rmsEarlyInTurn2})."
            );
        }

        // Reach into the scene to find the hidden owned-host AudioSource. The
        // owned-host path stamps a deterministic GameObject name so tests
        // don't need a back-door inspector for production-private state.
        // FindObjectsByType (even with FindObjectsInactive.Include) skips
        // HideAndDontSave objects; Resources.FindObjectsOfTypeAll returns
        // every loaded object regardless of hide flags. The name tracks
        // CreateAsync's wired engine — step 6 of
        // Docs~/plans/audio-generator-engine.md swapped production to
        // UnityGeneratorAudioOutputEngine, whose owned host is named
        // "ElevenLabs.UnityGeneratorAudioOutput".
        private static AudioSource? FindHiddenHostAudioSource()
        {
            foreach (AudioSource src in Resources.FindObjectsOfTypeAll<AudioSource>())
            {
                if (src.gameObject.name == "ElevenLabs.UnityGeneratorAudioOutput")
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

        // Stream-continuity helpers ----------------------------------------

        // Period of the index-encoding sawtooth. Long enough that a skip
        // smaller than 1.25 s at 16 kHz can't alias back onto the expected
        // value; encoded values span [-10000, 9999] so they stay inside
        // int16 with headroom.
        private const int SawtoothPeriod = 20000;

        private static short SawtoothSample(long index) => (short)(index % SawtoothPeriod - 10000);

        /// <summary>PCM16-LE chunk whose sample <c>i</c> encodes stream
        /// index <c>startIndex + i</c>, so a drained stream can be checked
        /// for dropped or reordered samples.</summary>
        private static byte[] SawtoothChunk(long startIndex, int count)
        {
            byte[] result = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                short s = SawtoothSample(startIndex + i);
                result[i * 2] = (byte)(s & 0xff);
                result[i * 2 + 1] = (byte)((s >> 8) & 0xff);
            }
            return result;
        }

        private static float DecodedSawtooth(long index)
        {
            short s = SawtoothSample(index);
            return s < 0 ? s / 32768f : s / 32767f;
        }

        /// <summary>Drain the ring to empty through
        /// <see cref="UnityAudioSourceOutput.ReadFromRing"/> in
        /// audio-thread-sized chunks, collecting only the real (non-
        /// silence-fill) samples.</summary>
        private static List<float> DrainAll(UnityAudioSourceOutput output)
        {
            var drained = new List<float>();
            float[] dest = new float[256];
            // Safety cap well above any test's pushed depth so a buggy
            // ring can't spin the test runner forever.
            for (int guard = 0; guard < 10_000; guard++)
            {
                int n = output.ReadFromRing(dest);
                if (n == 0)
                    return drained;
                for (int i = 0; i < n; i++)
                    drained.Add(dest[i]);
            }
            Assert.Fail("DrainAll exceeded its iteration guard — ring never reported empty.");
            return drained; // unreachable
        }

        /// <summary>Assert <paramref name="drained"/> is the complete,
        /// gap-free image of the sawtooth stream pushed from
        /// <paramref name="startIndex"/>: exactly
        /// <paramref name="expectedCount"/> samples, each one the encoded
        /// successor of the previous. On a discontinuity, reports the
        /// position and the apparent jump size.</summary>
        private static void AssertSawtoothStream(
            IReadOnlyList<float> drained,
            long startIndex,
            long expectedCount
        )
        {
            for (int i = 0; i < drained.Count; i++)
            {
                float expected = DecodedSawtooth(startIndex + i);
                if (Math.Abs(drained[i] - expected) < 1e-6f)
                    continue;
                float f = drained[i];
                short observed = (short)Math.Round(f < 0 ? f * 32768f : f * 32767f);
                short expectedShort = SawtoothSample(startIndex + i);
                int jump =
                    ((observed - expectedShort) % SawtoothPeriod + SawtoothPeriod) % SawtoothPeriod;
                Assert.Fail(
                    $"Playback stream discontinuity at drained sample {i}: expected encoded "
                        + $"stream index {startIndex + i} (value {expectedShort}), observed "
                        + $"value {observed} — stream jumped ahead by ~{jump} samples "
                        + $"(mod {SawtoothPeriod})."
                );
            }
            Assert.AreEqual(
                expectedCount,
                drained.Count,
                $"Dropped {expectedCount - drained.Count} of {expectedCount} pushed samples — "
                    + "queued agent audio was lost before it could play."
            );
        }
    }
}
