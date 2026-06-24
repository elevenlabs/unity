#nullable enable

using System.Linq;
using ElevenLabs.Agents;
using ElevenLabs.WebGL.Internal;
using NUnit.Framework;
using UnityEngine;

namespace ElevenLabs.WebGL.Bridged.Tests
{
    /// <summary>
    /// Router-level tests for <see cref="WebAudioBackedOutput"/>. Drives the
    /// wrapper against a <see cref="FakeJsObject"/> so the JS sink's
    /// per-property setters can be asserted without a real WebGL runtime.
    /// </summary>
    /// <remarks>
    /// The polling loop only ticks during Play Mode (it awaits
    /// <see cref="Awaitable.NextFrameAsync"/>); these Edit Mode tests drive
    /// <see cref="WebAudioBackedOutput.UpdateProperties"/> directly to
    /// exercise the property-mirroring logic.
    /// </remarks>
    public class WebAudioBackedOutputTests
    {
        [TearDown]
        public void TearDown()
        {
            CallbackRegistry.ResetForTests();
            PromiseRegistry.ResetForTests();
        }

        // --- Construction / defaults -------------------------------------

        [Test]
        public void Ctor_NoAudioSource_PushesUnitVolumeAndZeroSpatialBlendOnce()
        {
            var fake = new FakeJsObject();

            _ = new WebAudioBackedOutput(fake, audioSource: null);

            // Initial UpdateProperties fires from the ctor — the "no source"
            // branch only pushes setVolume + setSpatialBlend.
            Assert.IsTrue(
                fake.Invocations.Any(i => i.Name == "setVolume" && (float)i.Args[0] == 1f),
                "setVolume(1) should be pushed at construction."
            );
            Assert.IsTrue(
                fake.Invocations.Any(i => i.Name == "setSpatialBlend" && (float)i.Args[0] == 0f),
                "setSpatialBlend(0) should be pushed at construction."
            );
            // Position / rolloff / pan are not pushed in the no-source path.
            Assert.IsFalse(fake.Invocations.Any(i => i.Name == "setPosition"));
            Assert.IsFalse(fake.Invocations.Any(i => i.Name == "setRolloffMode"));
        }

        [Test]
        public void Ctor_WithAudioSource_PushesAudioSourceProperties()
        {
            var go = new GameObject("test-audio-source");
            try
            {
                AudioSource src = go.AddComponent<AudioSource>();
                src.volume = 0.7f;
                src.spatialBlend = 1f;
                src.minDistance = 2f;
                src.maxDistance = 25f;
                src.panStereo = -0.3f;
                src.dopplerLevel = 0.5f;
                src.rolloffMode = AudioRolloffMode.Linear;
                go.transform.position = new Vector3(3, 0, 7);

                var fake = new FakeJsObject();

                _ = new WebAudioBackedOutput(fake, src);

                AssertCalled(fake, "setVolume", 0.7f);
                AssertCalled(fake, "setSpatialBlend", 1f);
                AssertCalled(fake, "setMinDistance", 2f);
                AssertCalled(fake, "setMaxDistance", 25f);
                AssertCalled(fake, "setPanStereo", -0.3f);
                AssertCalled(fake, "setDopplerLevel", 0.5f);
                // setRolloffMode pushes an int code (0 = Linear).
                Assert.IsTrue(
                    fake.Invocations.Any(i => i.Name == "setRolloffMode" && (int)i.Args[0] == 0),
                    "setRolloffMode(0) should be pushed for Linear."
                );
                // setPosition takes three positional floats.
                var pos = fake.Invocations.FirstOrDefault(i => i.Name == "setPosition");
                Assert.IsNotNull(pos);
                Assert.AreEqual(3f, (float)pos!.Args[0]);
                Assert.AreEqual(0f, (float)pos.Args[1]);
                Assert.AreEqual(7f, (float)pos.Args[2]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // --- Property polling --------------------------------------------

        [Test]
        public void UpdateProperties_SkipsUnchangedValues_BetweenTicks()
        {
            var go = new GameObject("test-skip-redundant");
            try
            {
                AudioSource src = go.AddComponent<AudioSource>();
                src.volume = 0.5f;
                var fake = new FakeJsObject();
                var output = new WebAudioBackedOutput(fake, src);

                int volumeCallsAfterCtor = fake.Invocations.Count(i => i.Name == "setVolume");
                Assert.AreEqual(1, volumeCallsAfterCtor, "First push should fire once.");

                // No change between ticks → no new push.
                output.UpdateProperties();
                Assert.AreEqual(
                    1,
                    fake.Invocations.Count(i => i.Name == "setVolume"),
                    "Unchanged volume must not re-push."
                );

                // Change → next tick pushes the new value.
                src.volume = 0.42f;
                output.UpdateProperties();
                Assert.AreEqual(2, fake.Invocations.Count(i => i.Name == "setVolume"));
                Assert.AreEqual(
                    0.42f,
                    (float)fake.Invocations.Last(i => i.Name == "setVolume").Args[0]
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void UpdateProperties_PushesPositionChange_WhenTransformMoves()
        {
            var go = new GameObject("test-position-change");
            try
            {
                AudioSource src = go.AddComponent<AudioSource>();
                var fake = new FakeJsObject();
                var output = new WebAudioBackedOutput(fake, src);

                int initialPositionCalls = fake.Invocations.Count(i => i.Name == "setPosition");

                go.transform.position = new Vector3(10, 5, -2);
                output.UpdateProperties();

                Assert.AreEqual(
                    initialPositionCalls + 1,
                    fake.Invocations.Count(i => i.Name == "setPosition")
                );
                var pos = fake.Invocations.Last(i => i.Name == "setPosition");
                Assert.AreEqual(10f, (float)pos.Args[0]);
                Assert.AreEqual(5f, (float)pos.Args[1]);
                Assert.AreEqual(-2f, (float)pos.Args[2]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // --- Rolloff mapping ----------------------------------------------

        [Test]
        public void UpdateProperties_LinearRolloff_PushesCode0()
        {
            var go = new GameObject("test-linear");
            try
            {
                AudioSource src = go.AddComponent<AudioSource>();
                src.rolloffMode = AudioRolloffMode.Linear;
                var fake = new FakeJsObject();
                _ = new WebAudioBackedOutput(fake, src);

                var inv = fake.Invocations.First(i => i.Name == "setRolloffMode");
                Assert.AreEqual(0, (int)inv.Args[0]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void UpdateProperties_LogarithmicRolloff_PushesCode1()
        {
            var go = new GameObject("test-log");
            try
            {
                AudioSource src = go.AddComponent<AudioSource>();
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                var fake = new FakeJsObject();
                _ = new WebAudioBackedOutput(fake, src);

                var inv = fake.Invocations.First(i => i.Name == "setRolloffMode");
                Assert.AreEqual(1, (int)inv.Args[0]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void UpdateProperties_CustomRolloff_FallsBackToExponential_AndWarnsOnce()
        {
            var go = new GameObject("test-custom-rolloff");
            try
            {
                AudioSource src = go.AddComponent<AudioSource>();
                src.rolloffMode = AudioRolloffMode.Custom;
                var fake = new FakeJsObject();

                UnityEngine.TestTools.LogAssert.Expect(
                    LogType.Warning,
                    new System.Text.RegularExpressions.Regex(
                        ".*Custom but custom rolloff curves aren't supported.*"
                    )
                );

                var output = new WebAudioBackedOutput(fake, src);

                var inv = fake.Invocations.First(i => i.Name == "setRolloffMode");
                Assert.AreEqual(1, (int)inv.Args[0]); // exponential fallback.

                // Second update with same mode must not re-warn.
                output.UpdateProperties();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // outputAudioMixerGroup warning is covered by the destruction-warned
        // latch (same one-shot pattern). Constructing a real AudioMixerGroup
        // in batchmode requires loading a serialized .mixer asset; the warn
        // path is exercised end-to-end by IntegrationTests~/ once that
        // harness lands.

        // --- Mid-session destruction --------------------------------------

        [Test]
        public void UpdateProperties_SourceDestroyedMidSession_WarnsOnce_AndFallsBackToDefaults()
        {
            var go = new GameObject("test-destruction");
            AudioSource src = go.AddComponent<AudioSource>();
            var fake = new FakeJsObject();
            var output = new WebAudioBackedOutput(fake, src);

            UnityEngine.Object.DestroyImmediate(go);

            UnityEngine.TestTools.LogAssert.Expect(
                LogType.Warning,
                new System.Text.RegularExpressions.Regex(
                    ".*OutputAudioSource was destroyed mid-session.*"
                )
            );

            output.UpdateProperties();

            int beforeCount = fake.Invocations.Count;
            output.UpdateProperties();
            // No new warnings after the first — second post-destruction tick
            // is a quiet no-op (defaults already cached as last-pushed).
            Assert.AreEqual(beforeCount, fake.Invocations.Count, "Idle ticks must not re-push.");
        }

        // --- Audio passthrough --------------------------------------------

        [Test]
        public void PushAudio_NeverForwardsToSink_AudioFlowsJsInternal()
        {
            var fake = new FakeJsObject();
            var output = new WebAudioBackedOutput(fake, audioSource: null);

            int beforePushAudio = fake.Invocations.Count;
            output.PushAudio(new byte[] { 1, 2, 3, 4 });

            // playAudio is wired JS-internal via attachConnectionToOutput;
            // the C# wrapper must not duplicate the work over the bridge.
            Assert.IsFalse(fake.Invocations.Any(i => i.Name == "playAudio"));
            Assert.AreEqual(beforePushAudio, fake.Invocations.Count);
        }

        // --- SetVolume + Interrupt ----------------------------------------

        [Test]
        public void SetVolume_WithAudioSource_WritesToSourceAndPushesEffective()
        {
            var go = new GameObject("test-set-volume");
            try
            {
                AudioSource src = go.AddComponent<AudioSource>();
                src.volume = 1f;
                var fake = new FakeJsObject();
                var output = new WebAudioBackedOutput(fake, src);

                output.SetVolume(0.3f);

                Assert.AreEqual(
                    0.3f,
                    src.volume,
                    1e-6,
                    "SetVolume must mutate the supplied source."
                );
                Assert.AreEqual(
                    0.3f,
                    (float)fake.Invocations.Last(i => i.Name == "setVolume").Args[0]
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SetVolume_NoSource_TracksOwnVolume_AndPushes()
        {
            var fake = new FakeJsObject();
            var output = new WebAudioBackedOutput(fake, audioSource: null);

            output.SetVolume(0.6f);

            Assert.AreEqual(0.6f, (float)fake.Invocations.Last(i => i.Name == "setVolume").Args[0]);
        }

        [Test]
        public void Interrupt_DefaultDuration_PassesTwoSecondsToSink()
        {
            var fake = new FakeJsObject();
            var output = new WebAudioBackedOutput(fake, audioSource: null);

            output.Interrupt();

            var inv = fake.Invocations.First(i => i.Name == "interrupt");
            Assert.AreEqual(2000, (int)inv.Args[0]);
        }

        [Test]
        public void Interrupt_ExplicitDuration_Forwarded()
        {
            var fake = new FakeJsObject();
            var output = new WebAudioBackedOutput(fake, audioSource: null);

            output.Interrupt(500);

            var inv = fake.Invocations.First(i => i.Name == "interrupt");
            Assert.AreEqual(500, (int)inv.Args[0]);
        }

        // --- Close lifecycle ----------------------------------------------

        [Test]
        public void Close_CallsCloseAsync_AndDisposes()
        {
            var fake = new FakeJsObject();
            var output = new WebAudioBackedOutput(fake, audioSource: null);

            output.Close().GetAwaiter().GetResult();

            Assert.IsTrue(fake.Invocations.Any(i => i.Name == "close"));
            Assert.IsTrue(fake.IsDisposed);
        }

        [Test]
        public void Close_IsIdempotent()
        {
            var fake = new FakeJsObject();
            var output = new WebAudioBackedOutput(fake, audioSource: null);

            output.Close().GetAwaiter().GetResult();
            output.Close().GetAwaiter().GetResult();

            Assert.AreEqual(1, fake.Invocations.Count(i => i.Name == "close"));
        }

        // --- Helpers ------------------------------------------------------

        private static void AssertCalled(FakeJsObject fake, string method, float expectedArg)
        {
            var inv = fake.Invocations.FirstOrDefault(i => i.Name == method);
            Assert.IsNotNull(inv, $"Expected at least one call to {method}.");
            Assert.AreEqual(expectedArg, (float)inv!.Args[0], 1e-6);
        }
    }
}
