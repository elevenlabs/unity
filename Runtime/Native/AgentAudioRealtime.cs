#nullable enable

using System;
using Unity.IntegerTime;
using UnityEngine.Audio;
using static UnityEngine.Audio.ProcessorInstance;

namespace ElevenLabs.Native
{
    /// <summary>
    /// Audio-thread half of the
    /// <see cref="AgentAudioGeneratorComponent"/>'s generator pairing.
    /// Pulls mono PCM samples from the
    /// <see cref="AgentAudioGeneratorBridge"/> referenced by
    /// <see cref="Handle"/> and broadcasts them to every channel of
    /// Unity's <see cref="ChannelBuffer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Concurrency: <see cref="Process"/> runs on Unity's audio thread and
    /// MUST avoid allocations, locks, and exceptions
    /// (<see href="https://docs.unity3d.com/6000.3/Documentation/Manual/audio-scriptable-processors-generators.html"/>).
    /// All allocations are sunk into
    /// <see cref="AgentAudioGeneratorBridge"/>'s pre-sized scratch
    /// buffers.
    /// </para>
    /// <para>
    /// Handle indirection (vs a direct managed reference): Unity's
    /// <see cref="GeneratorInstance.IControl{TRealtime}"/> constraint
    /// requires <typeparamref name="TRealtime"/> to be <c>unmanaged</c>
    /// — a struct whose entire field graph is unmanaged. Storing the
    /// bridge reference inline would violate that, so the struct carries
    /// a small <see cref="int"/> handle and resolves it through
    /// <see cref="AgentAudioGeneratorBridge.LookupByHandle"/>'s lock-free
    /// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey, TValue}"/>
    /// at <see cref="Process"/> time. Keeps the struct Burst-compatible
    /// too — the plan's optional <c>[BurstCompile]</c> flip can land
    /// without a redesign.
    /// </para>
    /// </remarks>
    internal struct AgentAudioRealtime : GeneratorInstance.IRealtime
    {
        // Handle into AgentAudioGeneratorBridge's static lookup. Zero is
        // reserved as "uninitialized" so default(struct) → silence-fills
        // rather than crashing on a stale lookup.
        public int Handle;

        internal AgentAudioRealtime(int handle)
        {
            Handle = handle;
        }

        public bool isFinite => false;
        public bool isRealtime => true;
        public DiscreteTime? length => null;

        public void Update(UpdatedDataContext context, Pipe pipe)
        {
            // No control-thread data updates — the bridge is wired once
            // at engine.Start and stays put through Dispose.
        }

        public GeneratorInstance.Result Process(
            in RealtimeContext context,
            Pipe pipe,
            ChannelBuffer buffer,
            GeneratorInstance.Arguments args
        )
        {
            int frames = buffer.frameCount;
            int channels = buffer.channelCount;
            AgentAudioGeneratorBridge? bridge =
                Handle == 0 ? null : AgentAudioGeneratorBridge.LookupByHandle(Handle);
            if (bridge == null || frames <= 0 || channels <= 0)
            {
                SilenceFill(buffer, channels, frames);
                return frames;
            }

            // Drain mono samples into the bridge's pre-allocated scratch.
            // If the ring underruns, Drain synchronously pulls from the
            // host's drain callback to refill — single-shot, matching
            // UnityAudioOutputEngine.OnPcmRead's behaviour today. `request`
            // caps at MonoScratch.Length so an unusually large DSP buffer
            // (above the bridge's pre-sized capacity) silences the tail
            // rather than reading uninitialized slots.
            float[] mono = bridge.MonoScratch;
            int request = frames <= mono.Length ? frames : mono.Length;
            int got = bridge.Drain(mono.AsSpan(0, request));
            if (got < request)
                Array.Clear(mono, got, request - got);

            // Broadcast mono input to every output channel. Unity's
            // generator pipeline handles the rate conversion (declared via
            // AgentAudioControl.Configure's Setup), so this loop only
            // expands channels — no resampling math here.
            for (int ch = 0; ch < channels; ch++)
            {
                for (int f = 0; f < request; f++)
                    buffer[ch, f] = mono[f];
                // Silence-fill the overflow tail if scratch was smaller
                // than the requested frame count (defensive — see above).
                for (int f = request; f < frames; f++)
                    buffer[ch, f] = 0f;
            }

            return frames;
        }

        private static void SilenceFill(ChannelBuffer buffer, int channels, int frames)
        {
            for (int ch = 0; ch < channels; ch++)
            for (int f = 0; f < frames; f++)
                buffer[ch, f] = 0f;
        }
    }
}
