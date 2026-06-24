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
        // Edit Mode runs against the open scene, which in Unity 6 batchmode
        // already contains a default Main Camera carrying an AudioListener.
        // Tests that exercise position math need to control that — either by
        // disabling any pre-existing listeners (fallback-path tests) or by
        // owning the listener explicitly (relative-position tests). Cached
        // here so SetUp/TearDown can restore the enabled state per test.
        private readonly System.Collections.Generic.List<AudioListener> _preExistingListeners =
            new();

        [SetUp]
        public void SetUp()
        {
            _preExistingListeners.Clear();
            foreach (
                var listener in UnityEngine.Object.FindObjectsByType<AudioListener>(
                    FindObjectsSortMode.None
                )
            )
            {
                if (listener.enabled)
                {
                    listener.enabled = false;
                    _preExistingListeners.Add(listener);
                }
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var listener in _preExistingListeners)
                if (listener != null)
                    listener.enabled = true;
            _preExistingListeners.Clear();
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
                // setPosition takes three positional floats. With no
                // AudioListener in the test scene, the wrapper falls back to
                // world position with Z flipped (Unity LH → Web Audio RH).
                var pos = fake.Invocations.FirstOrDefault(i => i.Name == "setPosition");
                Assert.IsNotNull(pos);
                Assert.AreEqual(3f, (float)pos!.Args[0]);
                Assert.AreEqual(0f, (float)pos.Args[1]);
                Assert.AreEqual(-7f, (float)pos.Args[2]);
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

                // No AudioListener → fallback path: world pos with Z flipped.
                go.transform.position = new Vector3(10, 5, -2);
                output.UpdateProperties();

                Assert.AreEqual(
                    initialPositionCalls + 1,
                    fake.Invocations.Count(i => i.Name == "setPosition")
                );
                var pos = fake.Invocations.Last(i => i.Name == "setPosition");
                Assert.AreEqual(10f, (float)pos.Args[0]);
                Assert.AreEqual(5f, (float)pos.Args[1]);
                Assert.AreEqual(2f, (float)pos.Args[2]); // Unity LH → Web Audio RH.
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void UpdateProperties_WithActiveListener_PushesListenerRelativePosition()
        {
            // Listener offset + rotated 90° around Y so its local +Z (forward)
            // points to world +X. A source at world (0, 0, 5) is then to the
            // listener's left (local -X), regardless of the listener's
            // translation. The Z flip (Unity LH → Web Audio RH) leaves X
            // alone, so the expected push is (-, 0, 0) on the X axis.
            var listenerGo = new GameObject("test-listener");
            var sourceGo = new GameObject("test-source");
            try
            {
                AudioListener listener = listenerGo.AddComponent<AudioListener>();
                listenerGo.transform.position = new Vector3(2, 0, 0);
                listenerGo.transform.rotation = Quaternion.Euler(0, 90, 0);
                Assert.IsTrue(listener.isActiveAndEnabled);

                AudioSource src = sourceGo.AddComponent<AudioSource>();
                sourceGo.transform.position = new Vector3(0, 0, 5);

                var fake = new FakeJsObject();
                _ = new WebAudioBackedOutput(fake, src);

                var pos = fake.Invocations.First(i => i.Name == "setPosition");
                // Listener local: source is 5 units to the listener's left
                // (local -X), at the listener's height (local 0), and behind
                // the listener by 2 units (local -Z because rotated +90° around
                // Y means +X world = +Z local, and the source's X-world delta
                // of -2 maps to local -Z).
                // After Z flip (Unity LH → Web Audio RH): (-5, 0, 2).
                Assert.AreEqual(-5f, (float)pos.Args[0], 1e-5);
                Assert.AreEqual(0f, (float)pos.Args[1], 1e-5);
                Assert.AreEqual(2f, (float)pos.Args[2], 1e-5);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sourceGo);
                UnityEngine.Object.DestroyImmediate(listenerGo);
            }
        }

        [Test]
        public void UpdateProperties_PrefersActiveAndEnabledListener_OverDisabledOne()
        {
            // Multiple listeners — only the active+enabled one drives the math.
            // Mirrors Unity's own "active listener" semantics (multiple
            // enabled triggers Unity's warning, not ours).
            var disabledGo = new GameObject("test-listener-disabled");
            var activeGo = new GameObject("test-listener-active");
            var sourceGo = new GameObject("test-source");
            try
            {
                AudioListener disabled = disabledGo.AddComponent<AudioListener>();
                disabled.enabled = false;
                disabledGo.transform.position = new Vector3(100, 100, 100);

                _ = activeGo.AddComponent<AudioListener>();
                activeGo.transform.position = new Vector3(1, 0, 0);

                AudioSource src = sourceGo.AddComponent<AudioSource>();
                sourceGo.transform.position = new Vector3(4, 0, 0);

                var fake = new FakeJsObject();
                _ = new WebAudioBackedOutput(fake, src);

                // Listener-local: source.x - listener.x = 4 - 1 = 3. Y, Z = 0.
                // After Z flip: (3, 0, 0). Disabled listener at (100,…) ignored.
                var pos = fake.Invocations.First(i => i.Name == "setPosition");
                Assert.AreEqual(3f, (float)pos.Args[0], 1e-5);
                Assert.AreEqual(0f, (float)pos.Args[1], 1e-5);
                Assert.AreEqual(0f, (float)pos.Args[2], 1e-5);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sourceGo);
                UnityEngine.Object.DestroyImmediate(activeGo);
                UnityEngine.Object.DestroyImmediate(disabledGo);
            }
        }

        [Test]
        public void UpdateProperties_NoListenerSetsListenerPosition_UsesWorldPositionFallbackWithZFlip()
        {
            // No AudioListener anywhere in the scene → wrapper falls back to
            // raw world position with Z flipped. Spatialization is only
            // correct if the implicit Web Audio listener (at origin, default
            // orientation) happens to match the dev's intent, but at least
            // the source remains audible instead of throwing.
            var sourceGo = new GameObject("test-no-listener-source");
            try
            {
                AudioSource src = sourceGo.AddComponent<AudioSource>();
                sourceGo.transform.position = new Vector3(7, -2, 4);

                var fake = new FakeJsObject();
                _ = new WebAudioBackedOutput(fake, src);

                var pos = fake.Invocations.First(i => i.Name == "setPosition");
                Assert.AreEqual(7f, (float)pos.Args[0]);
                Assert.AreEqual(-2f, (float)pos.Args[1]);
                Assert.AreEqual(-4f, (float)pos.Args[2]);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sourceGo);
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
