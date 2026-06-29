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

            int inputRate = bridge.InputSampleRate;
            int deviceRate = bridge.DeviceSampleRate;
            float[] mono = bridge.MonoScratch;

            // Fast path: rates match (or device rate not yet published by
            // a Configure fire) — drain one mono sample per output frame
            // and broadcast across channels. Matches the original
            // pre-resampler shape exactly.
            if (deviceRate <= 0 || deviceRate == inputRate)
            {
                int request = frames <= mono.Length ? frames : mono.Length;
                int got = bridge.Drain(mono.AsSpan(0, request));
                if (got < request)
                    Array.Clear(mono, got, request - got);
                for (int ch = 0; ch < channels; ch++)
                {
                    for (int f = 0; f < request; f++)
                        buffer[ch, f] = mono[f];
                    for (int f = request; f < frames; f++)
                        buffer[ch, f] = 0f;
                }
                return frames;
            }

            // Resample path: linear-interpolate the bridge's
            // negotiated-input-rate samples up/down to the device's output
            // rate. We do this on the audio thread (vs the producer side)
            // so the network-decode path stays at whatever rate the server
            // negotiated — if Unity ever lifts the realtime-generator rate
            // requirement, the resampler drops out without touching the
            // producer.
            //
            // Linear interpolation is the same fidelity bar the
            // OnAudioFilterRead plan would have set; for typical
            // upsampling (16 kHz → 48 kHz speech) the artifact floor is
            // well below the source's bandwidth limit. Downsampling
            // without an anti-alias prefilter would be lossier, but the
            // realistic case is upsampling (server outputs ≤ device rate).
            double step = (double)inputRate / deviceRate;
            double startFrac = bridge.ResampleFracPos;
            // Figure out how many input mono samples we need to satisfy
            // `frames` output frames. The last output frame's source
            // position is startFrac + (frames - 1) * step; we need
            // ceil(that) + 1 mono samples for the right endpoint of the
            // final interpolation (+1 because mono[0] is the right
            // endpoint when fractional position is in [0, 1)).
            int needed = (int)Math.Ceiling(startFrac + (frames - 1) * step) + 1;
            if (needed < 1)
                needed = 1;
            if (needed > mono.Length)
                needed = mono.Length;
            int gotR = bridge.Drain(mono.AsSpan(0, needed));
            float left = bridge.LastInputSample;
            // Pad-on-underrun with last-sample-hold (avoids click on
            // underrun vs zero-fill). Cap to the requested window so the
            // interpolation loop reads only valid slots.
            if (gotR < needed)
            {
                float pad = gotR > 0 ? mono[gotR - 1] : left;
                for (int i = gotR; i < needed; i++)
                    mono[i] = pad;
            }

            // Walk the interpolation interval forward one output frame at
            // a time. `pos` tracks the fractional offset between `left`
            // and `right`; when it crosses 1.0, slide the interval by one
            // input sample.
            float right = mono[0];
            double pos = startFrac;
            int idx = 0;
            for (int f = 0; f < frames; f++)
            {
                while (pos >= 1.0)
                {
                    pos -= 1.0;
                    left = right;
                    idx++;
                    right = idx < needed ? mono[idx] : left;
                }
                float sample = (float)(left + (right - left) * pos);
                for (int ch = 0; ch < channels; ch++)
                    buffer[ch, f] = sample;
                pos += step;
            }
            // Normalize trailing whole-step accumulation so
            // ResampleFracPos stays in [0, 1) — keeps the needed-samples
            // calculation honest on the next Process call.
            while (pos >= 1.0)
            {
                pos -= 1.0;
                left = right;
                idx++;
                right = idx < needed ? mono[idx] : left;
            }
            bridge.LastInputSample = left;
            bridge.ResampleFracPos = pos;
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
