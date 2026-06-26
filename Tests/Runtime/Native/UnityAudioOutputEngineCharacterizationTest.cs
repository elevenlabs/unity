#nullable enable

using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ElevenLabs.Native.PlayMode.Tests
{
    /// <summary>
    /// Characterization test for Unity's streaming <see cref="AudioClip"/>
    /// behaviour — the load-bearing reality that
    /// <c>Tests/Editor/Native/FakeAudioOutputEngine.cs</c> simulates so the
    /// rest of the audio output suite can stay in Edit Mode. Drives a real
    /// <see cref="AudioSource"/> for ~5 seconds and prints the observed
    /// pre-fill and ongoing callback cadence; assertions are deliberately
    /// loose ("within a believable range") so the test fails only when
    /// Unity's behaviour drifts far enough that the fake's defaults stop
    /// matching reality, not on small per-frame jitter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failure mode is "update the fake's defaults to match new reality."
    /// The numbers to tune live at
    /// <c>Tests/Editor/Native/FakeAudioOutputEngine.cs</c>:
    /// <c>SyncPrefillCallbackCount</c>, <c>SyncPrefillSampleCountPerCallback</c>,
    /// and <c>OngoingCallbackPeriodSeconds</c>. The captured values are
    /// always logged via <see cref="Debug.Log"/> so a human run can read them
    /// off the Console without re-running with extra instrumentation.
    /// </para>
    /// <para>
    /// Batchmode caveat: Unity's headless runner typically has no audio
    /// output device, so the <see cref="AudioClip.PCMReaderCallback"/> may
    /// never fire after <see cref="AudioSource.Play"/>. The test detects
    /// that ("zero ongoing fires after 5 s of playback") and routes to
    /// <see cref="Assert.Inconclusive(string)"/> instead of failing, so the
    /// CI suite stays green and the test stays meaningful when run
    /// interactively in the Unity Editor (Window → General → Test Runner →
    /// PlayMode).
    /// </para>
    /// </remarks>
    public sealed class UnityAudioOutputEngineCharacterizationTest
    {
        // Mirrors UnityAudioOutputEngine.Start's clip-creation parameters:
        // 16 kHz mono streaming clip with the same Max(256, SampleRate/100)
        // length math. Keeping these identical to production means the
        // observed pre-fill count reflects exactly what the production engine
        // sees on the same audio device.
        private const int SampleRate = 16000;
        private const int Channels = 1;
        private static int ClipSamples => Math.Max(256, SampleRate / 100);

        private const float ObservationSeconds = 5f;

        [UnityTest]
        public IEnumerator AudioClip_StreamingPCMReaderCallback_MatchesFakeDefaults()
        {
            int syncFires = 0;
            int syncSamples = 0;
            int ongoingFires = 0;
            int ongoingSamples = 0;
            bool createReturned = false;

            AudioClip.PCMReaderCallback callback = data =>
            {
                if (!createReturned)
                {
                    syncFires++;
                    syncSamples += data.Length;
                }
                else
                {
                    ongoingFires++;
                    ongoingSamples += data.Length;
                }
                Array.Clear(data, 0, data.Length);
            };

            GameObject? host = null;
            AudioClip? clip = null;
            try
            {
                host = new GameObject("CharacterizationHost")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                var src = host.AddComponent<AudioSource>();
                src.loop = true;
                src.playOnAwake = false;

                clip = AudioClip.Create(
                    name: "CharacterizationClip",
                    lengthSamples: ClipSamples,
                    channels: Channels,
                    frequency: SampleRate,
                    stream: true,
                    pcmreadercallback: callback
                );
                createReturned = true;

                int syncFiresInsideCreate = syncFires;
                int syncSamplesInsideCreate = syncSamples;

                src.clip = clip;
                src.Play();

                yield return new WaitForSeconds(ObservationSeconds);

                src.Stop();

                Debug.Log(
                    $"[Characterization] Sync pre-fill inside AudioClip.Create: {syncFiresInsideCreate} fires "
                        + $"× ~{(syncFiresInsideCreate > 0 ? syncSamplesInsideCreate / syncFiresInsideCreate : 0)} samples "
                        + $"= {syncSamplesInsideCreate} samples total."
                );
                Debug.Log(
                    $"[Characterization] Ongoing callbacks over {ObservationSeconds:0.##} s: {ongoingFires} fires "
                        + $"= {ongoingFires / ObservationSeconds:0.##} Hz, {ongoingSamples} samples total."
                );

                if (syncFiresInsideCreate == 0 && ongoingFires == 0)
                {
                    Assert.Inconclusive(
                        "Unity fired zero PCMReaderCallback invocations — almost certainly running in "
                            + "batchmode / headless with no audio output device. Run this test interactively "
                            + "from the Unity Editor (Window → General → Test Runner → PlayMode) to capture "
                            + "the live cadence."
                    );
                }

                // Sync pre-fill: today's fake defaults to 50 × 256 = 12,800 samples.
                // Range allows for DSP buffer / clip-rate combinations that vary
                // the per-fire batch size or fire count without invalidating
                // the fake's premise that Start drains a meaningful chunk
                // synchronously.
                Assert.That(
                    syncFiresInsideCreate,
                    Is.GreaterThan(0),
                    "AudioClip.Create did NOT fire PCMReaderCallback synchronously — the fake's whole "
                        + "pre-fill premise is invalid. See Docs~/plans/audio-output-testability.md → "
                        + "'What we learned the hard way' #1."
                );
                Assert.That(
                    syncSamplesInsideCreate,
                    Is.InRange(5_000, 30_000),
                    "Sync pre-fill total drifted outside the 5,000–30,000 sample band — update "
                        + "FakeAudioOutputEngine.SyncPrefillCallbackCount / SyncPrefillSampleCountPerCallback "
                        + "to match. Observed total: "
                        + syncSamplesInsideCreate
                );

                // Ongoing cadence: today's fake defaults to 1/60 s period
                // ≈ 60 Hz (measured 2026-06-26 on Unity 6.0.0.3.6f1 with a
                // 16 kHz clip + 48 kHz output + DSP buffer 256: each fire
                // covers 256 clip samples = 768 output samples = three DSP
                // buffers). Band allows 30–120 Hz to absorb hardware / OS
                // scheduling variance and to cover both halving and doubling
                // of the fire-per-buffer ratio if Unity adjusts the
                // streaming-clip batch size in a future release.
                if (ongoingFires > 0)
                {
                    double observedHz = ongoingFires / ObservationSeconds;
                    Assert.That(
                        observedHz,
                        Is.InRange(30.0, 120.0),
                        "Ongoing PCMReaderCallback cadence drifted outside 30–120 Hz — update "
                            + "FakeAudioOutputEngine.OngoingCallbackPeriodSeconds to match. Observed: "
                            + observedHz.ToString("0.##")
                            + " Hz"
                    );
                }
                else
                {
                    Assert.Inconclusive(
                        "Sync pre-fill fired but ongoing playback callbacks did not — likely no audio "
                            + "output device. Run interactively in the Unity Editor to characterize ongoing "
                            + "cadence."
                    );
                }
            }
            finally
            {
                if (clip != null)
                    UnityEngine.Object.Destroy(clip);
                if (host != null)
                    UnityEngine.Object.Destroy(host);
            }
        }
    }
}
