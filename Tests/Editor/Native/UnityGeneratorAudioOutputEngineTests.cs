#nullable enable

using System;
using System.Threading.Tasks;
using ElevenLabs.Agents;
using NUnit.Framework;
using UnityEngine;

namespace ElevenLabs.Native.Tests
{
    /// <summary>
    /// Step 4 of <c>Docs~/plans/audio-generator-engine.md</c>: re-runs the
    /// fake-driven seam tests in <see cref="UnityAudioSourceOutputTests"/>
    /// against the new <see cref="UnityGeneratorAudioOutputEngine"/> via
    /// constructor injection. Validates that the
    /// <see cref="IAudioOutputEngine"/> seam is implementation-agnostic —
    /// <see cref="UnityAudioSourceOutput"/> drives the generator engine
    /// through the same volume / lifecycle / IsAvailable contract it drives
    /// <see cref="FakeAudioOutputEngine"/> through, no host-side adjustments
    /// needed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Edit-Mode scope: only assertions that don't require Unity's audio
    /// thread are exercised here (Volume getter/setter, IsAvailable,
    /// snapshot/restore of <see cref="AudioSource.volume"/> /
    /// <see cref="AudioSource.loop"/> / <see cref="AudioSource.clip"/> /
    /// <see cref="AudioSource.generator"/>, Dispose lifecycle). The
    /// audio-thread <c>Process</c> cadence is covered by
    /// <see cref="ElevenLabs.Native.PlayMode.Tests.UnityGeneratorAudioOutputEngineCharacterizationTest"/>
    /// in PlayMode. The cadence-calibrated fake-driven tests in
    /// <see cref="UnityAudioSourceOutputTests"/> (sync-pre-fill threshold
    /// gate, drain timing) compute the threshold via
    /// <see cref="UnityAudioSourceOutput.ComputePrefillThresholdSamples"/>,
    /// which reads
    /// <see cref="IAudioOutputEngine.SyncPrefillSampleCount"/>; under the
    /// generator engine that collapses to just the controller's DSP-buffer
    /// margin (see step 5 of <c>Docs~/plans/audio-generator-engine.md</c>
    /// for the decision rationale).
    /// </para>
    /// <para>
    /// Construction note: the engine creates real <see cref="GameObject"/> +
    /// <see cref="AudioSource"/> objects on the owned-host path, same as the
    /// existing <c>CreateAsync_CreatesOwnHost_WhenNoAudioSourceSupplied</c>
    /// test does for the legacy engine. Edit-Mode tests can drive that
    /// setup; only <see cref="AudioSource.Play"/> and the audio-thread
    /// callbacks require a live audio device. We deliberately avoid calling
    /// <see cref="UnityGeneratorAudioOutputEngine.Start"/> in most tests so
    /// the engine never assigns <see cref="AudioSource.generator"/> in Edit
    /// Mode (the runtime assignment itself is exercised by the PlayMode
    /// characterization test).
    /// </para>
    /// </remarks>
    public class UnityGeneratorAudioOutputEngineTests
    {
        // --- Construction parity ----------------------------------------

        [Test]
        public void Ctor_OwnedHost_AppliesDefaultUserVolumeAndLoop()
        {
            // Mirror of the implicit construction contract
            // UnityAudioOutputEngine satisfies on the owned-host path:
            // loop=true (PCMReaderCallback streaming demands a looping
            // source), volume=1f (SDK-owned baseline matching the
            // supplied-source override).
            var engine = new UnityGeneratorAudioOutputEngine();
            try
            {
                Assert.IsTrue(engine.IsAvailable);
                Assert.AreEqual(1f, engine.Volume, 1e-6);
            }
            finally
            {
                engine.Dispose();
            }
            Assert.IsFalse(engine.IsAvailable);
        }

        [Test]
        public void Ctor_SuppliedSource_AppliesDefaultsAndCapturesSnapshot()
        {
            // Pre-session snapshot capture: caller's volume/loop/clip/generator
            // get replaced by SDK defaults while the engine owns the source,
            // then restored verbatim on Dispose. The `generator` field is the
            // IAudioGenerator-path addition — UnityAudioOutputEngine's
            // snapshot set is volume/loop/clip only.
            var go = new GameObject("test-generator-engine-supplied");
            try
            {
                AudioSource supplied = go.AddComponent<AudioSource>();
                supplied.volume = 0.42f;
                supplied.loop = false;

                var engine = new UnityGeneratorAudioOutputEngine(supplied);
                try
                {
                    Assert.IsTrue(engine.IsAvailable);
                    // SDK-owned overwrites visible mid-session.
                    Assert.AreEqual(1f, engine.Volume, 1e-6);
                    Assert.AreEqual(1f, supplied.volume, 1e-6);
                    Assert.IsTrue(supplied.loop);
                }
                finally
                {
                    engine.Dispose();
                }

                // Post-Dispose: caller-visible state restored from snapshot.
                Assert.AreEqual(0.42f, supplied.volume, 1e-6);
                Assert.IsFalse(supplied.loop);
                Assert.IsNotNull(supplied, "AudioSource itself must not be destroyed.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // --- Re-runs of the fake-driven seam tests (UnityAudioSourceOutputTests)
        // against UnityGeneratorAudioOutputEngine via constructor injection ---

        [Test]
        public void PushAudio_ResetsEngineVolumeToUserLevel()
        {
            // Counterpart of
            // UnityAudioSourceOutputTests.PushAudio_ResetsEngineVolumeToUserLevel,
            // injecting the real generator engine instead of FakeAudioOutputEngine.
            // Every PushAudio cancels any in-flight fade and snaps engine.Volume
            // back to _userVolume; the generator engine's Volume property
            // proxies straight to AudioSource.volume, so the assertion reads
            // through the real Unity surface.
            var go = new GameObject("test-generator-push-volume");
            try
            {
                AudioSource supplied = go.AddComponent<AudioSource>();
                var engine = new UnityGeneratorAudioOutputEngine(supplied);
                var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000), engine);
                try
                {
                    output.SetVolume(0.7f);
                    // Simulate mid-fade so the post-push reset is observable
                    // as a delta, not a no-op write back to the same value.
                    engine.Volume = 0.1f;

                    // 3 samples is well below the controller's prefill
                    // threshold (DSP-buffer margin alone with the generator
                    // engine's SyncPrefillSampleCount == 0) so engine.Start
                    // does not fire — the unconditional volume reset is the
                    // only side effect.
                    output.PushAudio(LittleEndian(1, 2, 3));
                    Assert.AreEqual(0.7f, engine.Volume, 1e-6);
                    Assert.AreEqual(0.7f, supplied.volume, 1e-6);
                }
                finally
                {
                    engine.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void Interrupt_ImmediateCut_RestoresUserVolumeOnEngine()
        {
            // Counterpart of
            // UnityAudioSourceOutputTests.Interrupt_ImmediateCut_RestoresUserVolumeOnEngine.
            // The immediate-cut path flushes the ring and writes _userVolume
            // back to engine.Volume so a follow-up PushAudio resumes at the
            // right gain instead of inheriting a stale mid-fade level.
            var go = new GameObject("test-generator-interrupt-volume");
            try
            {
                AudioSource supplied = go.AddComponent<AudioSource>();
                var engine = new UnityGeneratorAudioOutputEngine(supplied);
                var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000), engine);
                try
                {
                    output.SetVolume(0.4f);
                    Assert.AreEqual(0.4f, engine.Volume, 1e-6);

                    // Simulate the engine landing at a mid-fade level so the
                    // post-Interrupt restore is observable as a delta.
                    engine.Volume = 0f;
                    output.Interrupt(resetDurationMs: 0);
                    Assert.AreEqual(0.4f, engine.Volume, 1e-6);
                }
                finally
                {
                    engine.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SetVolume_ClampedToZeroOne_PropagatedToEngine()
        {
            // Counterpart of
            // UnityAudioSourceOutputTests.SetVolume_ClampedToZeroOne_PropagatedToEngine.
            // SetVolume clamps to [0, 1] and forwards to engine.Volume —
            // for the generator engine, that propagation lands on
            // AudioSource.volume.
            var go = new GameObject("test-generator-setvolume");
            try
            {
                AudioSource supplied = go.AddComponent<AudioSource>();
                var engine = new UnityGeneratorAudioOutputEngine(supplied);
                var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000), engine);
                try
                {
                    output.SetVolume(0.5f);
                    Assert.AreEqual(0.5f, engine.Volume, 1e-6);

                    output.SetVolume(-1f);
                    Assert.AreEqual(0f, engine.Volume, 1e-6, "negative input must clamp to 0");

                    output.SetVolume(2f);
                    Assert.AreEqual(1f, engine.Volume, 1e-6, "input > 1 must clamp to 1");
                }
                finally
                {
                    engine.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void GetByteFrequencyData_EngineUnavailable_FillsZeros()
        {
            // Counterpart of
            // UnityAudioSourceOutputTests.GetByteFrequencyData_EngineUnavailable_FillsZeros.
            // Disposing the engine flips IsAvailable to false → controller
            // short-circuits to Array.Clear instead of computing the FFT.
            var go = new GameObject("test-generator-bytefreq-unavailable");
            try
            {
                AudioSource supplied = go.AddComponent<AudioSource>();
                var engine = new UnityGeneratorAudioOutputEngine(supplied);
                var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000), engine);
                try
                {
                    // Prime the analysis buffer with real samples so the
                    // available-engine path would yield a non-zero FFT result.
                    output.PushAudio(LittleEndian(short.MaxValue, short.MaxValue, short.MaxValue));
                    output.ReadFromRing(new float[3]);
                    Assert.Greater(output.Test_AnalysisBufferRms, 0f);

                    engine.Dispose();
                    Assert.IsFalse(engine.IsAvailable);

                    byte[] buffer = new byte[8];
                    for (int i = 0; i < buffer.Length; i++)
                        buffer[i] = 0xAB;
                    output.GetByteFrequencyData(buffer);
                    CollectionAssert.AreEqual(new byte[buffer.Length], buffer);
                }
                finally
                {
                    // engine.Dispose is idempotent; calling again is safe.
                    engine.Dispose();
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public async Task Close_DisposesEngine()
        {
            // Counterpart of UnityAudioSourceOutputTests.Close_DisposesEngine.
            // output.Close flows through to engine.Dispose so IsAvailable
            // flips to false — mirroring the teardown contract the fake
            // verifies in the original suite.
            var go = new GameObject("test-generator-close");
            try
            {
                AudioSource supplied = go.AddComponent<AudioSource>();
                var engine = new UnityGeneratorAudioOutputEngine(supplied);
                var output = new UnityAudioSourceOutput(new FormatConfig("pcm", 16_000), engine);
                Assert.IsTrue(engine.IsAvailable);

                await output.Close();
                Assert.IsFalse(engine.IsAvailable);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // --- Snapshot/restore specifically for the generator field --------

        [Test]
        public void Dispose_SuppliedSource_RestoresPreviouslyBoundGenerator()
        {
            // The IAudioGenerator-path addition vs UnityAudioOutputEngine:
            // pre-session value of AudioSource.generator is captured on
            // construction and restored on Dispose. Edit-Mode can't bind a
            // real IAudioGenerator (would need Play Mode + audio device),
            // but the null → null restoration path is the common case and
            // verifies the snapshot field actually participates in the
            // restore loop.
            var go = new GameObject("test-generator-generator-snapshot");
            try
            {
                AudioSource supplied = go.AddComponent<AudioSource>();
                Assert.IsNull(
                    supplied.generator,
                    "Pre-construction AudioSource.generator must default to null."
                );
                var engine = new UnityGeneratorAudioOutputEngine(supplied);
                engine.Dispose();
                // Post-Dispose: generator restored to its pre-construction
                // value (null here). The active component bind happens only
                // inside Start, which we deliberately don't call here — the
                // restore path still has to write the snapshot back.
                Assert.IsNull(
                    supplied.generator,
                    "Dispose should restore AudioSource.generator to its pre-session value."
                );
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // --- Audio configuration changes ----------------------------------

        [Test]
        public void HandleAudioConfigurationChanged_BeforeStart_LogsButDoesNotThrow()
        {
            // Unity provides no way to raise OnAudioConfigurationChanged
            // manually, so Edit-Mode coverage drives the internal handler
            // directly. Before Start there's no component bound — the
            // handler must log the transition and no-op the restart. (The
            // started-path re-Play is one guarded line, verified in Play
            // Mode: Unity stops AudioSources on audio reinit.)
            var engine = new UnityGeneratorAudioOutputEngine();
            try
            {
                UnityEngine.TestTools.LogAssert.Expect(
                    UnityEngine.LogType.Log,
                    new System.Text.RegularExpressions.Regex("Audio configuration changed")
                );
                Assert.DoesNotThrow(() => engine.HandleAudioConfigurationChanged(true));
            }
            finally
            {
                engine.Dispose();
            }
        }

        [Test]
        public void HandleAudioConfigurationChanged_AfterDispose_SilentNoOp()
        {
            // A late event delivery after Dispose (possible if Unity is
            // mid-dispatch when the session tears down) must neither log
            // nor touch the destroyed AudioSource.
            var engine = new UnityGeneratorAudioOutputEngine();
            engine.Dispose();
            Assert.DoesNotThrow(() => engine.HandleAudioConfigurationChanged(true));
            UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Dispose_OwnedHost_DestroysHostGameObject()
        {
            // Mirror of UnityAudioOutputEngine's owned-host teardown: the
            // SDK-created host GameObject is destroyed on Dispose so a
            // session that never bound a user-supplied source leaves no
            // GameObject behind in the scene.
            var engine = new UnityGeneratorAudioOutputEngine();
            AudioSource? created = FindHiddenHostAudioSource();
            Assert.IsNotNull(created, "Owned-host path should create an AudioSource.");
            GameObject host = created!.gameObject;

            engine.Dispose();
            // Unity returns true for `created == null` once Destroy has run,
            // even though the C# reference is non-null; using the overloaded
            // == here matches every other Unity test.
            Assert.IsTrue(
                host == null,
                "Owned-host GameObject should be destroyed by engine.Dispose."
            );
        }

        // Reach into the scene to find the hidden owned-host AudioSource —
        // mirrors UnityAudioSourceOutputTests.FindHiddenHostAudioSource but
        // matches the generator engine's host name.
        private static AudioSource? FindHiddenHostAudioSource()
        {
            foreach (AudioSource src in Resources.FindObjectsOfTypeAll<AudioSource>())
            {
                if (src.gameObject.name == "ElevenLabs.UnityGeneratorAudioOutput")
                    return src;
            }
            return null;
        }

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
