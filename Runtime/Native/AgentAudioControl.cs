#nullable enable

using UnityEngine;
using UnityEngine.Audio;
using static UnityEngine.Audio.ProcessorInstance;

namespace ElevenLabs.Native
{
    /// <summary>
    /// Control-thread half of the
    /// <see cref="AgentAudioGeneratorComponent"/>'s generator pairing.
    /// <see cref="Configure"/> declares the generator's input
    /// <see cref="GeneratorInstance.Setup"/> (mono at the host-supplied
    /// sample rate) so Unity's audio pipeline resamples to whatever the
    /// output device wants — eliminating the hand-rolled resampler the
    /// <c>OnAudioFilterRead</c>-based design would have required.
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
            // Declare OUR input format (mono at the configured rate). The
            // `format` parameter Unity passes in describes the device's
            // requested output — by returning a Setup that differs, we ask
            // Unity to insert its rate-conversion + channel-expansion
            // stage between Process and the device. The
            // UnityGeneratorAudioOutputEngineCharacterizationTest measured
            // ChannelBuffer.channelCount=2 even though Setup declared mono
            // (Unity expanded), so the expansion path is confirmed
            // working at the API level.
            AgentAudioGeneratorBridge? bridge =
                Handle == 0 ? null : AgentAudioGeneratorBridge.LookupByHandle(Handle);
            int rate = bridge != null ? bridge.InputSampleRate : format.sampleRate;
            setup = new GeneratorInstance.Setup(AudioSpeakerMode.Mono, rate);
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
