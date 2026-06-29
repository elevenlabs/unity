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

            // Resample path: Kaiser-windowed sinc polyphase FIR. We do
            // this on the audio thread (vs the producer side) so the
            // network-decode path stays at whatever rate the server
            // negotiated — if Unity ever lifts the realtime-generator
            // rate requirement, the resampler drops out without touching
            // the producer.
            //
            // Why polyphase FIR over the prior Hermite cubic: Hermite
            // gave ~12-20 dB stopband attenuation, which left visible
            // imaging above the source Nyquist (audible as "dust" on
            // speech; confirmed via spectrogram on the live agent).
            // Polyphase windowed-sinc at L=16 taps, Kaiser β=8 hits
            // ~60 dB stopband — well below the perceptual floor.
            //
            // Layout: the conceptual buffer is
            //   [Carry[0]..Carry[half-1], mono[0], mono[1], ...]
            // with ResampleFracPos giving the fractional offset past
            // the LAST carry sample (the most recent consumed input).
            // We prepend the half-tap carry into the first half slots
            // of MonoScratch so the loop indexes a contiguous buffer
            // (no carry-vs-fresh branch per output sample).
            float[]? kernel = bridge.ResamplerKernel;
            float[]? carry = bridge.ResamplerCarry;
            if (kernel == null || carry == null)
            {
                SilenceFill(buffer, channels, frames);
                return frames;
            }
            const int taps = AgentAudioGeneratorBridge.KernelTaps;
            const int phases = AgentAudioGeneratorBridge.PhaseCount;
            int half = taps / 2;

            double step = (double)inputRate / deviceRate;
            double startFrac = bridge.ResampleFracPos;

            // Prepend the carry buffer into the first `half` slots of
            // the scratch so the kernel's left taps are always valid
            // without a per-sample boundary branch.
            for (int i = 0; i < half; i++)
                mono[i] = carry[i];

            // Figure out how many FRESH input samples to drain into
            // mono[half..]. The last output frame's source position
            // past the most recent carry sample is
            //   startFrac + (frames - 1) * step.
            // The kernel reads `half` taps to the right of its anchor,
            // so we need ceil(that) + half samples past the carry.
            int needed = (int)Math.Ceiling(startFrac + (frames - 1) * step) + half;
            if (needed < 1)
                needed = 1;
            int drainCapacity = mono.Length - half;
            if (needed > drainCapacity)
                needed = drainCapacity;
            int gotR = bridge.Drain(mono.AsSpan(half, needed));
            // Pad-on-underrun with last-sample-hold (avoids transient
            // click vs zero-fill). Pad source is the last good fresh
            // sample, or the most recent carry sample if no fresh
            // samples arrived.
            if (gotR < needed)
            {
                float pad = gotR > 0 ? mono[half + gotR - 1] : carry[half - 1];
                for (int i = gotR; i < needed; i++)
                    mono[half + i] = pad;
            }
            int monoEnd = half + needed; // one past the last valid index

            double pos = startFrac;
            for (int f = 0; f < frames; f++)
            {
                // Anchor input index = (half - 1) + floor(pos). At f=0
                // with floor(pos)=0 the anchor is carry[half-1] (the
                // most recent consumed). The kernel's `taps` samples
                // span [anchor - half + 1, anchor + half] inclusive,
                // i.e. mono[sampleStart..sampleStart+taps-1] where
                // sampleStart = floor(pos).
                int floorPos = (int)Math.Floor(pos);
                double frac = pos - floorPos;
                int phaseIdx = (int)(frac * phases + 0.5);
                if (phaseIdx >= phases)
                {
                    // Phase rounded up across the boundary; advance
                    // anchor by one and snap to phase 0.
                    phaseIdx = 0;
                    floorPos++;
                }
                int kernelBase = phaseIdx * taps;
                int sampleStart = floorPos;
                // Clamp on the right if underrun truncated the drain.
                // sampleStart should never go negative here (floorPos
                // >= 0 + invariant startFrac in [0, 1)), but defensively
                // clamp anyway so a pathological state can't OOB-read.
                if (sampleStart < 0)
                    sampleStart = 0;
                if (sampleStart + taps > monoEnd)
                    sampleStart = monoEnd - taps;
                if (sampleStart < 0)
                    sampleStart = 0;

                double sum = 0.0;
                for (int n = 0; n < taps; n++)
                    sum += kernel[kernelBase + n] * mono[sampleStart + n];
                float sample = (float)sum;
                for (int ch = 0; ch < channels; ch++)
                    buffer[ch, f] = sample;
                pos += step;
            }

            // Update carry: keep the `half` samples ending at the new
            // anchor position. After the loop, pos = startFrac +
            // frames*step. The next call's anchor will be at conceptual
            // index (half - 1) + floor(pos); we need carry[i] = mono at
            // (floor(pos) + i) — i.e., the half samples starting from
            // mono[floor(pos)].
            int finalFloor = (int)Math.Floor(pos);
            int carryStart = finalFloor;
            // Clamp so we don't read past monoEnd in any pathological
            // case (e.g., heavy underrun truncated `needed` below
            // expectations).
            if (carryStart + half > monoEnd)
                carryStart = monoEnd - half;
            if (carryStart < 0)
                carryStart = 0;
            for (int i = 0; i < half; i++)
            {
                int idx = carryStart + i;
                carry[i] = idx < monoEnd ? mono[idx] : 0f;
            }
            bridge.ResampleFracPos = pos - finalFloor;
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
