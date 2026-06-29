#nullable enable

using UnityEngine;
using UnityEngine.Audio;
using static UnityEngine.Audio.ProcessorInstance;

namespace ElevenLabs.Native
{
    /// <summary>
    /// Control-thread half of the
    /// <see cref="AgentAudioGeneratorComponent"/>'s generator pairing.
    /// <see cref="Configure"/> declares the generator's
    /// <see cref="GeneratorInstance.Setup"/> at the <em>device's</em>
    /// output rate (Unity's hard requirement for realtime generators —
    /// the audio backend emits a <c>"Realtime generators must obey system
    /// sampling rate"</c> error otherwise and consumes our samples 1:1 at
    /// the device rate, producing pitch-shifted output) and publishes the
    /// device rate into the bridge so
    /// <see cref="AgentAudioRealtime.Process"/> can resample the
    /// negotiated input rate (e.g. <c>pcm_16000</c>) up/down to whatever
    /// the device wants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Concurrency: Unity invokes the IControl members on the control
    /// thread, never concurrent with
    /// <see cref="AgentAudioRealtime.Process"/>. <see cref="OnMessage"/>
    /// is the bidirectional pipe between control and realtime — we don't
    /// use it today (the bridge state is wired once at engine.Start),
    /// so returns <see cref="ProcessorInstance.Response.Unhandled"/>.
    /// </para>
    /// <para>
    /// Handle indirection: see
    /// <see cref="AgentAudioRealtime"/>'s remarks for why the bridge is
    /// referenced by <see cref="int"/> handle rather than direct managed
    /// reference (Unity's <c>unmanaged</c> generic constraint).
    /// </para>
    /// </remarks>
    internal struct AgentAudioControl : GeneratorInstance.IControl<AgentAudioRealtime>
    {
        // Handle into AgentAudioGeneratorBridge's static lookup. Set at
        // construction time, never mutated. Resolves to the same bridge
        // the paired AgentAudioRealtime sees.
        public int Handle;

        internal AgentAudioControl(int handle)
        {
            Handle = handle;
        }

        public void Configure(
            ControlContext context,
            ref AgentAudioRealtime realtime,
            in AudioFormat format,
            out GeneratorInstance.Setup setup,
            ref GeneratorInstance.Properties properties
        )
        {
            // Match the DEVICE rate Unity passed in (not our input rate).
            // Unity's realtime-generator path doesn't run a rate-conversion
            // stage between Process and the device — discovered during step
            // 7's A/B testing: declaring Setup at the input rate (e.g. 16
            // kHz) caused Unity to consume samples 1:1 at the device's 48
            // kHz, producing ~3× pitch-shifted output and logging
            // "Realtime generators must obey system sampling rate". Bridge
            // the gap on the audio thread instead — see
            // AgentAudioRealtime.Process for the resampling math. The mono
            // declaration is fine: the characterization test confirmed
            // Unity still expands mono → ChannelBuffer.channelCount on the
            // channel axis (that wasn't the broken part).
            setup = new GeneratorInstance.Setup(AudioSpeakerMode.Mono, format.sampleRate);

            // Publish the device rate into the bridge so the audio-thread
            // resampler can drive its input→output ratio. The bridge field
            // is volatile-stored (control thread writes here; audio thread
            // reads from Process).
            AgentAudioGeneratorBridge? bridge =
                Handle == 0 ? null : AgentAudioGeneratorBridge.LookupByHandle(Handle);
            if (bridge != null)
                bridge.DeviceSampleRate = format.sampleRate;
        }

        public void Dispose(ControlContext context, ref AgentAudioRealtime realtime)
        {
            // Bridge disposal is owned by
            // UnityGeneratorAudioOutputEngine.Dispose — the engine
            // outlives any single GeneratorInstance lifecycle (Stop +
            // Start re-binds), so disposing here would tear down state
            // the engine still needs.
        }

        public void Update(ControlContext context, Pipe pipe)
        {
            // No periodic control-thread work — the bridge is publish-
            // once at engine.Start.
        }

        public ProcessorInstance.Response OnMessage(
            ControlContext context,
            Pipe pipe,
            ProcessorInstance.Message message
        ) => ProcessorInstance.Response.Unhandled;
    }
}
