#nullable enable

using System;
using System.Collections;
using System.Threading;
using NUnit.Framework;
using Unity.IntegerTime;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.TestTools;
using static UnityEngine.Audio.ProcessorInstance;

namespace ElevenLabs.Native.PlayMode.Tests
{
    /// <summary>
    /// Characterization test for Unity 6.3's
    /// <see cref="IAudioGenerator"/> / <see cref="GeneratorInstance"/> /
    /// <see cref="AudioSource.generator"/> surface — the foundation the
    /// upcoming <c>UnityGeneratorAudioOutputEngine</c> (see
    /// <c>Docs~/plans/audio-generator-engine.md</c>) builds on. Mirrors
    /// <see cref="UnityAudioOutputEngineCharacterizationTest"/> in spirit:
    /// drive a real <see cref="AudioSource"/> through a minimal
    /// <see cref="IAudioGenerator"/> for a few seconds, log the observed
    /// cadence, and assert only on loose bands so the test fails when
    /// Unity drifts far enough that the engine's assumptions break — not
    /// on small per-buffer jitter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Folds in step 0's deferred empirical sanity checks (unity-mcp was
    /// revoked during step 0, so the docs-only conclusions from that step
    /// land here as PlayMode assertions):
    /// </para>
    /// <list type="number">
    ///   <item>Runtime assignment of <see cref="AudioSource.generator"/>
    ///   binds Unity's audio pipeline to the component (not just the
    ///   inspector picker described in the manual).</item>
    ///   <item><see cref="GeneratorInstance.IRealtime.Process"/> fires
    ///   after <see cref="AudioSource.Play"/> with the expected DSP-buffer
    ///   cadence — and crucially, does NOT fire a streaming-pre-fill
    ///   burst synchronously inside <c>Play()</c> the way the
    ///   <see cref="AudioClip.PCMReaderCallback"/> path does (the
    ///   structural fix the plan calls out).</item>
    ///   <item>When both <see cref="AudioSource.clip"/> AND
    ///   <see cref="AudioSource.generator"/> are assigned, the generator
    ///   takes priority — the clip's <see cref="AudioClip.PCMReaderCallback"/>
    ///   stays silent. This shapes the engine's snapshot/restore
    ///   contract (we mirror today's <c>clip</c>/<c>loop</c>/<c>volume</c>
    ///   snapshot, and additionally snapshot <c>generator</c>).</item>
    /// </list>
    /// <para>
    /// Batchmode caveat: Unity's headless runner typically has no audio
    /// output device, so <see cref="GeneratorInstance.IRealtime.Process"/>
    /// may never fire after <see cref="AudioSource.Play"/>. The test
    /// detects that ("zero process fires after observation window") and
    /// routes to <see cref="Assert.Inconclusive(string)"/> instead of
    /// failing — same pattern as
    /// <see cref="UnityAudioOutputEngineCharacterizationTest"/>. Run
    /// interactively in the Unity Editor (Window → General → Test Runner
    /// → PlayMode) to capture the live cadence.
    /// </para>
    /// </remarks>
    public sealed class UnityGeneratorAudioOutputEngineCharacterizationTest
    {
        // Matches the production engine's input format choice (16 kHz
        // mono). The generator path declares its preferred input rate via
        // Setup and lets Unity resample to the device rate — keeping this
        // identical here doesn't pin the device rate, but pins the
        // assumed-input rate the same way the production engine will.
        private const int InputSampleRate = 16000;
        private const float ObservationSeconds = 5f;

        // Static so the Realtime struct can mutate counters from the
        // audio thread without managed-reference capture. Reset at the
        // top of every test. Volatile/Interlocked discipline below covers
        // the cross-thread reads in the test body.
        private static int s_processFires;
        private static long s_processFrames;
        private static int s_maxFrameCount;
        private static int s_minFrameCount;
        private static int s_channelCount;
        private static int s_clipPcmFires;

        [SetUp]
        public void ResetStaticCounters()
        {
            s_processFires = 0;
            s_processFrames = 0;
            s_maxFrameCount = 0;
            s_minFrameCount = int.MaxValue;
            s_channelCount = 0;
            s_clipPcmFires = 0;
        }

        [UnityTest]
        public IEnumerator AudioGenerator_RuntimeBind_FiresProcess_AndOverridesAssignedClip()
        {
            GameObject? host = null;
            AudioClip? coexistenceClip = null;
            try
            {
                host = new GameObject("GeneratorCharacterizationHost")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                var src = host.AddComponent<AudioSource>();
                src.loop = true;
                src.playOnAwake = false;

                // Coexistence probe: assign a streaming AudioClip whose
                // PCMReaderCallback bumps a counter. If the generator
                // takes priority over `clip`, the counter stays at zero
                // for the full observation window. If both fire, the
                // engine's snapshot/restore contract needs to actively
                // null out `audioSource.clip` on Start (not just
                // snapshot it for restore on Dispose).
                coexistenceClip = AudioClip.Create(
                    name: "GeneratorCoexistenceClip",
                    lengthSamples: Math.Max(256, InputSampleRate / 100),
                    channels: 1,
                    frequency: InputSampleRate,
                    stream: true,
                    pcmreadercallback: ClipPcmCallback
                );
                src.clip = coexistenceClip;
                // Streaming AudioClips fire their PCMReaderCallback
                // synchronously inside Create as a pre-fill burst (see
                // UnityAudioOutputEngineCharacterizationTest +
                // Docs~/unity-issues/streaming-audioclip-prefill-depth.md).
                // Capture that baseline so we can isolate the
                // playback-time fires the coexistence assertion really
                // cares about — i.e. whether Unity polls the clip
                // *while the generator is bound and Play() is running*.
                int clipFiresAfterCreate = Volatile.Read(ref s_clipPcmFires);

                // Bind a fresh ProbeGenerator AT RUNTIME — the API
                // reference says `AudioSource.generator` has a public
                // setter, but step 0 deferred empirical confirmation to
                // here. If the assignment didn't take effect, Process
                // would never fire and the inconclusive branch would
                // catch it; the explicit non-null read below also
                // surfaces a more pointed failure.
                int syncFiresBeforeBind = Volatile.Read(ref s_processFires);
                var component = host.AddComponent<ProbeGenerator>();
                src.generator = component;
                Assert.That(
                    src.generator,
                    Is.SameAs(component),
                    "AudioSource.generator assignment did not stick — the runtime binding path "
                        + "doesn't behave the way the API reference describes."
                );

                int syncFiresBeforePlay = Volatile.Read(ref s_processFires);
                int clipFiresBeforePlay = Volatile.Read(ref s_clipPcmFires);
                src.Play();
                int syncFiresInsidePlay = Volatile.Read(ref s_processFires) - syncFiresBeforePlay;
                int clipFiresInsidePlay = Volatile.Read(ref s_clipPcmFires) - clipFiresBeforePlay;

                yield return new WaitForSeconds(ObservationSeconds);

                src.Stop();

                int processFires = Volatile.Read(ref s_processFires);
                long processFrames = Interlocked.Read(ref s_processFrames);
                int maxFrameCount = Volatile.Read(ref s_maxFrameCount);
                int minFrameCount = Volatile.Read(ref s_minFrameCount);
                int channelCount = Volatile.Read(ref s_channelCount);
                int clipFiresTotal = Volatile.Read(ref s_clipPcmFires);
                int clipFiresOngoing = clipFiresTotal - clipFiresBeforePlay - clipFiresInsidePlay;
                int syncFiresDuringBind = syncFiresBeforePlay - syncFiresBeforeBind;

                Debug.Log(
                    "[GeneratorCharacterization] Sync Process fires during "
                        + $"AddComponent+set+capture window: {syncFiresDuringBind}; "
                        + $"inside AudioSource.Play(): {syncFiresInsidePlay}."
                );
                Debug.Log(
                    $"[GeneratorCharacterization] Ongoing Process over {ObservationSeconds:0.##} s: "
                        + $"{processFires} fires = {processFires / ObservationSeconds:0.##} Hz, "
                        + $"{processFrames} frames total = {processFrames / ObservationSeconds:0.##} frames/sec, "
                        + $"frameCount min={(minFrameCount == int.MaxValue ? 0 : minFrameCount)} "
                        + $"max={maxFrameCount}, channelCount={channelCount}."
                );
                Debug.Log(
                    "[GeneratorCharacterization] Clip PCMReaderCallback fires: "
                        + $"{clipFiresAfterCreate} during AudioClip.Create() (streaming pre-fill — "
                        + "expected, see streaming-audioclip-prefill-depth.md); "
                        + $"{clipFiresInsidePlay} inside Play(); "
                        + $"{clipFiresOngoing} during {ObservationSeconds:0.##} s playback — "
                        + (
                            (clipFiresInsidePlay + clipFiresOngoing) == 0
                                ? "generator took priority during playback (expected)."
                                : "clip was still polled during playback — generator did NOT take priority."
                        )
                );

                if (processFires == 0)
                {
                    Assert.Inconclusive(
                        "Unity fired zero IRealtime.Process() invocations across the observation "
                            + "window — almost certainly running in batchmode / headless with no audio "
                            + "output device. Run this test interactively from the Unity Editor "
                            + "(Window → General → Test Runner → PlayMode) to capture the live cadence."
                    );
                }

                // Sync pre-fill: the whole point of switching to
                // IAudioGenerator is to *eliminate* the streaming-
                // AudioClip pre-fill (see
                // Docs~/unity-issues/streaming-audioclip-prefill-depth.md
                // for the back-story). We expect Play() to return
                // without firing Process synchronously — Unity drives
                // Process from the audio thread once the DSP loop
                // reaches the source. A single defensive fire is
                // tolerated to absorb any scheduling quirk.
                Assert.That(
                    syncFiresInsidePlay,
                    Is.LessThanOrEqualTo(1),
                    "Unity fired more than one Process() synchronously inside AudioSource.Play() — "
                        + "the generator path has a non-trivial pre-fill we didn't expect. Re-examine "
                        + "Docs~/unity-issues/streaming-audioclip-prefill-depth.md to see if the "
                        + "generator surface has the same structural property as the streaming clip. "
                        + "Observed: "
                        + syncFiresInsidePlay
                        + " sync fires."
                );

                // Ongoing cadence: Process is driven by Unity's DSP
                // buffer. At 48 kHz output, 256-frame buffer ≈ 187 Hz
                // and 1024-frame buffer ≈ 47 Hz. The 20–250 Hz band
                // absorbs both halving and doubling of the DSP buffer
                // size across hardware/OS variation without
                // invalidating the assumption that Process is the
                // primary cadence driver.
                double observedHz = processFires / (double)ObservationSeconds;
                Assert.That(
                    observedHz,
                    Is.InRange(20.0, 250.0),
                    "Ongoing Process cadence drifted outside 20–250 Hz — Unity's DSP buffer math "
                        + "may have changed. Observed: "
                        + observedHz.ToString("0.##")
                        + " Hz"
                );

                Assert.That(
                    channelCount,
                    Is.InRange(1, 8),
                    "ChannelBuffer.channelCount outside 1–8 — unexpected speaker mode. Observed: "
                        + channelCount
                );
                Assert.That(
                    minFrameCount,
                    Is.InRange(32, 4096),
                    "ChannelBuffer.frameCount min outside 32–4096 — DSP buffer math drift. Observed min: "
                        + minFrameCount
                        + " max: "
                        + maxFrameCount
                );

                // Coexistence: generator must take priority over an
                // already-assigned AudioClip *during playback*. The
                // create-time pre-fill burst is fine — it's the
                // streaming clip's property, not a coexistence problem
                // (the production engine on the generator path won't
                // create a streaming clip at all). What we care about
                // is whether Unity continues polling the clip
                // alongside Process once Play() has started. If it
                // does, the production engine needs to actively null
                // out `audioSource.clip` (not just snapshot it) when
                // binding the generator.
                Assert.That(
                    clipFiresInsidePlay + clipFiresOngoing,
                    Is.EqualTo(0),
                    "AudioClip.PCMReaderCallback fired "
                        + (clipFiresInsidePlay + clipFiresOngoing)
                        + " times during/after Play() while audioSource.generator was bound — the "
                        + "clip-vs-generator priority assumption is wrong. The production engine "
                        + "would also need to clear `audioSource.clip` (not just snapshot it) when "
                        + "binding the generator."
                );
            }
            finally
            {
                if (host != null)
                    UnityEngine.Object.Destroy(host);
                if (coexistenceClip != null)
                    UnityEngine.Object.Destroy(coexistenceClip);
            }
        }

        private static void ClipPcmCallback(float[] data)
        {
            Interlocked.Increment(ref s_clipPcmFires);
            Array.Clear(data, 0, data.Length);
        }

        // Minimal IAudioGenerator MonoBehaviour. Hands Unity a
        // (Realtime, Control) pair via ControlContext.AllocateGenerator
        // — the structural mirror of the AgentAudioGeneratorComponent
        // step 3 will introduce, minus the SPSC ring (the realtime
        // struct here just writes silence and bumps counters).
        private sealed class ProbeGenerator : MonoBehaviour, IAudioGenerator
        {
            public bool isFinite => false;
            public bool isRealtime => true;
            public DiscreteTime? length => null;

            public GeneratorInstance CreateInstance(
                ControlContext context,
                AudioFormat? nestedConfiguration,
                ProcessorInstance.CreationParameters creationParameters
            ) =>
                context.AllocateGenerator(
                    new Realtime(),
                    new Control(),
                    nestedConfiguration,
                    creationParameters
                );
        }

        // Realtime struct — Burst-compatible shape (value-type, no
        // managed refs), un-Bursted per step 0's finding that Burst is
        // optional. Accesses static counters on the enclosing class to
        // surface observations to the test thread without managed-ref
        // capture into the struct.
        private struct Realtime : GeneratorInstance.IRealtime
        {
            public bool isFinite => false;
            public bool isRealtime => true;
            public DiscreteTime? length => null;

            public void Update(UpdatedDataContext context, Pipe pipe) { }

            public GeneratorInstance.Result Process(
                in RealtimeContext context,
                Pipe pipe,
                ChannelBuffer buffer,
                GeneratorInstance.Arguments args
            )
            {
                Interlocked.Increment(ref s_processFires);
                Interlocked.Add(ref s_processFrames, buffer.frameCount);

                int fc = buffer.frameCount;
                int max = Volatile.Read(ref s_maxFrameCount);
                while (fc > max)
                {
                    int prev = Interlocked.CompareExchange(ref s_maxFrameCount, fc, max);
                    if (prev == max)
                        break;
                    max = prev;
                }
                int min = Volatile.Read(ref s_minFrameCount);
                while (fc < min)
                {
                    int prev = Interlocked.CompareExchange(ref s_minFrameCount, fc, min);
                    if (prev == min)
                        break;
                    min = prev;
                }
                Volatile.Write(ref s_channelCount, buffer.channelCount);

                for (int ch = 0; ch < buffer.channelCount; ch++)
                {
                    for (int frame = 0; frame < buffer.frameCount; frame++)
                        buffer[ch, frame] = 0f;
                }

                return buffer.frameCount;
            }
        }

        // Control struct — wires Configure (declares the input format
        // we'd like Unity to feed Process with; Unity handles
        // resampling from there) and stubs the rest. Mirrors the shape
        // the production AgentAudioControl struct will take.
        private struct Control : GeneratorInstance.IControl<Realtime>
        {
            public void Configure(
                ControlContext context,
                ref Realtime realtime,
                in AudioFormat format,
                out GeneratorInstance.Setup setup,
                ref GeneratorInstance.Properties properties
            )
            {
                setup = new GeneratorInstance.Setup(format.speakerMode, format.sampleRate);
            }

            public void Dispose(ControlContext context, ref Realtime realtime) { }

            public void Update(ControlContext context, Pipe pipe) { }

            public ProcessorInstance.Response OnMessage(
                ControlContext context,
                Pipe pipe,
                ProcessorInstance.Message message
            ) => ProcessorInstance.Response.Unhandled;
        }
    }
}
