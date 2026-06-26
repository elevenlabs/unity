#nullable enable

using System;
using ElevenLabs.Agents;
using NUnit.Framework;

namespace ElevenLabs.Native.Tests
{
    /// <summary>
    /// Self-tests for <see cref="FakeAudioOutputEngine"/> — verifies the
    /// fake's own contract (sync pre-fill firing, Tick cadence, real /
    /// silence accounting) before the step-4 integration tests in
    /// <c>Docs~/plans/audio-output-testability.md</c> start asserting on
    /// <see cref="UnityAudioSourceOutput"/> + fake interactions. Keeps step
    /// 2 independently committable: if the fake regresses, these fail
    /// without ambiguity about whether the bug is in the fake or in the
    /// host.
    /// </summary>
    public class FakeAudioOutputEngineTests
    {
        private static readonly FormatConfig DefaultFormat = new("pcm", 16_000);

        [Test]
        public void Start_FiresSyncPrefillCallbacksAtConfiguredCount()
        {
            var fake = new FakeAudioOutputEngine
            {
                SyncPrefillCallbackCount = 5,
                SyncPrefillSampleCountPerCallback = 64,
            };
            int callbackFires = 0;
            fake.Start(
                DefaultFormat,
                buffer =>
                {
                    callbackFires++;
                    Assert.AreEqual(64, buffer.Length);
                    return buffer.Length; // pretend the ring had every sample.
                }
            );
            Assert.AreEqual(5, callbackFires);
            Assert.AreEqual(5 * 64, fake.RecordedRealSampleCount);
            Assert.AreEqual(0, fake.RecordedSilenceFillSamples);
        }

        [Test]
        public void Start_EmptyRing_RecordsUnderrunAsSilenceFill()
        {
            // Drain callback always returns 0 → every sample is silence.
            // Mirrors the original ~800 ms gap regression: ring empty when
            // sync pre-fill fires.
            var fake = new FakeAudioOutputEngine
            {
                SyncPrefillCallbackCount = 4,
                SyncPrefillSampleCountPerCallback = 100,
            };
            fake.Start(DefaultFormat, _ => 0);
            Assert.AreEqual(0, fake.RecordedRealSampleCount);
            Assert.AreEqual(4 * 100, fake.RecordedSilenceFillSamples);
        }

        [Test]
        public void Start_PartialUnderrun_SplitsRealAndSilenceCleanly()
        {
            // Drain returns half the buffer each time → silence-fill =
            // (buffer.Length - real) per fire.
            var fake = new FakeAudioOutputEngine
            {
                SyncPrefillCallbackCount = 3,
                SyncPrefillSampleCountPerCallback = 100,
            };
            fake.Start(DefaultFormat, buffer => buffer.Length / 2);
            Assert.AreEqual(3 * 50, fake.RecordedRealSampleCount);
            Assert.AreEqual(3 * 50, fake.RecordedSilenceFillSamples);
        }

        [Test]
        public void Start_DefaultCalibration_MatchesObservedUnity6Prefill()
        {
            // 50 × 256 = 12,800 — the empirically observed sync pre-fill on
            // a default Unity 6 audio config (DSP buffer 256 × 4 at 48 kHz,
            // streaming clip at 16 kHz). Documented in
            // Docs~/plans/audio-output-testability.md.
            var fake = new FakeAudioOutputEngine();
            fake.Start(DefaultFormat, buffer => buffer.Length);
            Assert.AreEqual(12_800, fake.RecordedRealSampleCount);
        }

        [Test]
        public void Tick_AdvancesPastPeriod_FiresOneDrain()
        {
            var fake = new FakeAudioOutputEngine
            {
                SyncPrefillCallbackCount = 0,
                OngoingCallbackBatchSize = 32,
                OngoingCallbackPeriodSeconds = 0.1,
            };
            int callbackFires = 0;
            fake.Start(
                DefaultFormat,
                buffer =>
                {
                    callbackFires++;
                    return buffer.Length;
                }
            );

            fake.Tick(0.05);
            Assert.AreEqual(0, callbackFires, "Half a period shouldn't fire a drain.");
            fake.Tick(0.06);
            Assert.AreEqual(1, callbackFires, "Crossing the period boundary fires once.");
            Assert.AreEqual(32, fake.RecordedRealSampleCount);
        }

        [Test]
        public void Tick_BigAdvance_FiresMultipleDrains()
        {
            var fake = new FakeAudioOutputEngine
            {
                SyncPrefillCallbackCount = 0,
                OngoingCallbackBatchSize = 32,
                OngoingCallbackPeriodSeconds = 0.1,
            };
            int callbackFires = 0;
            fake.Start(
                DefaultFormat,
                _ =>
                {
                    callbackFires++;
                    return 32;
                }
            );

            // 0.35 s → three full periods (0.30) consumed, 0.05 s carried over.
            fake.Tick(0.35);
            Assert.AreEqual(3, callbackFires);
        }

        [Test]
        public void Tick_BeforeStart_DoesNothing()
        {
            var fake = new FakeAudioOutputEngine();
            Assert.DoesNotThrow(() => fake.Tick(10.0));
            Assert.AreEqual(0, fake.RecordedRealSampleCount);
            Assert.AreEqual(0, fake.RecordedSilenceFillSamples);
        }

        [Test]
        public void Tick_AfterStop_DoesNotFireDrain()
        {
            var fake = new FakeAudioOutputEngine
            {
                SyncPrefillCallbackCount = 0,
                OngoingCallbackBatchSize = 8,
                OngoingCallbackPeriodSeconds = 0.05,
            };
            int callbackFires = 0;
            fake.Start(
                DefaultFormat,
                _ =>
                {
                    callbackFires++;
                    return 8;
                }
            );
            fake.Stop();
            fake.Tick(1.0);
            Assert.AreEqual(0, callbackFires);
            Assert.AreEqual(1, fake.StopCallCount);
        }

        [Test]
        public void Start_AfterStop_ResetsTickBudget()
        {
            // The accumulated Tick budget from a prior session shouldn't
            // bleed into a fresh Start and fire an immediate post-Start
            // drain — Start represents a clean playback boundary.
            var fake = new FakeAudioOutputEngine
            {
                SyncPrefillCallbackCount = 0,
                OngoingCallbackBatchSize = 16,
                OngoingCallbackPeriodSeconds = 0.1,
            };
            int callbackFires = 0;
            Func<float[], int> cb = _ =>
            {
                callbackFires++;
                return 16;
            };
            fake.Start(DefaultFormat, cb);
            fake.Tick(0.09); // not quite a period; budget carries 0.09.
            Assert.AreEqual(0, callbackFires);
            fake.Stop();
            fake.Start(DefaultFormat, cb);
            // No Tick yet on the new session → no drain even though the
            // pre-Stop budget was nearly a full period.
            Assert.AreEqual(0, callbackFires);
            fake.Tick(0.1);
            Assert.AreEqual(1, callbackFires);
        }

        [Test]
        public void Start_TracksCallCountAndLastFormat()
        {
            var fake = new FakeAudioOutputEngine { SyncPrefillCallbackCount = 0 };
            fake.Start(new FormatConfig("pcm", 24_000), _ => 0);
            fake.Stop();
            fake.Start(new FormatConfig("pcm", 16_000), _ => 0);
            Assert.AreEqual(2, fake.StartCallCount);
            Assert.AreEqual(16_000, fake.LastStartFormat!.SampleRate);
        }

        [Test]
        public void Start_RejectsNullArguments()
        {
            var fake = new FakeAudioOutputEngine { SyncPrefillCallbackCount = 0 };
            Assert.Throws<ArgumentNullException>(() => fake.Start(null!, _ => 0));
            Assert.Throws<ArgumentNullException>(() => fake.Start(DefaultFormat, null!));
        }

        [Test]
        public void Dispose_FlipsIsAvailable_AndShortCircuitsStart()
        {
            var fake = new FakeAudioOutputEngine { SyncPrefillCallbackCount = 1 };
            Assert.IsTrue(fake.IsAvailable);
            fake.Dispose();
            Assert.IsFalse(fake.IsAvailable);
            int callbackFires = 0;
            fake.Start(
                DefaultFormat,
                _ =>
                {
                    callbackFires++;
                    return 0;
                }
            );
            Assert.AreEqual(
                0,
                callbackFires,
                "Disposed fake should ignore Start instead of firing the sync pre-fill."
            );
        }

        [Test]
        public void ResetRecordedCounts_ZeroesRealAndSilence()
        {
            var fake = new FakeAudioOutputEngine
            {
                SyncPrefillCallbackCount = 2,
                SyncPrefillSampleCountPerCallback = 32,
            };
            fake.Start(DefaultFormat, _ => 16);
            Assert.AreEqual(32, fake.RecordedRealSampleCount);
            Assert.AreEqual(32, fake.RecordedSilenceFillSamples);
            fake.ResetRecordedCounts();
            Assert.AreEqual(0, fake.RecordedRealSampleCount);
            Assert.AreEqual(0, fake.RecordedSilenceFillSamples);
        }

        [Test]
        public void Volume_GetterReturnsLastSetValue()
        {
            var fake = new FakeAudioOutputEngine();
            Assert.AreEqual(1f, fake.Volume);
            fake.Volume = 0.25f;
            Assert.AreEqual(0.25f, fake.Volume);
        }
    }
}
