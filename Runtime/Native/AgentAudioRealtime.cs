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

            // Resample path: cubic Hermite (Catmull-Rom) interpolation
            // from the bridge's negotiated-input-rate samples up/down to
            // the device's output rate. We do this on the audio thread
            // (vs the producer side) so the network-decode path stays at
            // whatever rate the server negotiated — if Unity ever lifts
            // the realtime-generator rate requirement, the resampler
            // drops out without touching the producer.
            //
            // Hermite over linear: linear interpolation leaves harsh
            // mirror images near the source Nyquist (8 kHz when
            // upsampling 16 → 48), audible as "dust" on speech.
            // Catmull-Rom cubic is the standard "good enough" upsampler
            // for speech — it attenuates the image band by ~12-20 dB at
            // the cost of 4 multiplies + 4 adds per output sample (still
            // dirt cheap on the audio thread). Windowed-sinc would be
            // cleaner still but overkill for our bandwidth.
            //
            // Layout: the conceptual interpolation buffer is
            //   [..., Prev2, Prev1, Prev0, mono[0], mono[1], ...]
            // with ResampleFracPos giving the fractional offset past
            // Prev0. We copy the 3 prev samples into the first 3 slots
            // of MonoScratch so the loop can index a contiguous buffer
            // without conditional carry-vs-fresh branches.
            double step = (double)inputRate / deviceRate;
            double startFrac = bridge.ResampleFracPos;

            // Carry: prepend Prev2/Prev1/Prev0 into mono[0..2].
            mono[0] = bridge.Prev2;
            mono[1] = bridge.Prev1;
            mono[2] = bridge.Prev0;

            // Figure out how many FRESH input samples to drain into
            // mono[3..]. The last output frame's source position past
            // Prev0 is startFrac + (frames - 1) * step. The cubic kernel
            // reads 2 samples to the right of its left anchor (p2, p3),
            // so we need ceil(that) + 2 fresh samples past Prev0.
            int needed = (int)Math.Ceiling(startFrac + (frames - 1) * step) + 2;
            if (needed < 1)
                needed = 1;
            int drainCapacity = mono.Length - 3;
            if (needed > drainCapacity)
                needed = drainCapacity;
            int gotR = bridge.Drain(mono.AsSpan(3, needed));
            // Pad-on-underrun with last-sample-hold (avoids click on
            // underrun vs zero-fill). Pad source is the last good fresh
            // sample, or Prev0 if no fresh samples arrived.
            if (gotR < needed)
            {
                float pad = gotR > 0 ? mono[3 + gotR - 1] : bridge.Prev0;
                for (int i = gotR; i < needed; i++)
                    mono[3 + i] = pad;
            }
            int monoEnd = 3 + needed; // one past the last valid mono index

            double pos = startFrac;
            for (int f = 0; f < frames; f++)
            {
                // Interpolation window: 4 taps centered around the
                // output position. p1 is the left anchor; output is
                // between p1 and p2 at fractional `t`.
                int floorPos = (int)Math.Floor(pos);
                double t = pos - floorPos;
                int i1 = 2 + floorPos; // anchor in mono coords
                int i0 = i1 - 1;
                int i2 = i1 + 1;
                int i3 = i1 + 2;
                // Clamp the right edges in case underrun truncated the
                // drain (i1 always >= 1 since floorPos >= 0 + state
                // invariants, but the right taps can run past monoEnd).
                if (i2 >= monoEnd)
                    i2 = monoEnd - 1;
                if (i3 >= monoEnd)
                    i3 = monoEnd - 1;
                float p0 = mono[i0];
                float p1 = mono[i1];
                float p2 = mono[i2];
                float p3 = mono[i3];
                // Catmull-Rom Hermite (nested form):
                //   y(t) = ((a*t + b)*t + c)*t + p1
                // with a, b, c as below. Computed in double for
                // round-off margin; cast to float once at the end.
                double a = -0.5 * p0 + 1.5 * p1 - 1.5 * p2 + 0.5 * p3;
                double b = p0 - 2.5 * p1 + 2.0 * p2 - 0.5 * p3;
                double c = -0.5 * p0 + 0.5 * p2;
                float sample = (float)(((a * t + b) * t + c) * t + p1);
                for (int ch = 0; ch < channels; ch++)
                    buffer[ch, f] = sample;
                pos += step;
            }

            // Update carry: the new Prev0 is the last sample at the
            // "anchor" position the next call should start from — i.e.,
            // mono[2 + floor(pos)]. Prev1/Prev2 fall out one and two
            // positions earlier. Final fractional carry = pos - floor(pos).
            int finalFloor = (int)Math.Floor(pos);
            int prev0Idx = 2 + finalFloor;
            if (prev0Idx >= monoEnd)
                prev0Idx = monoEnd - 1;
            int prev1Idx = prev0Idx - 1;
            if (prev1Idx < 0)
                prev1Idx = 0;
            int prev2Idx = prev0Idx - 2;
            if (prev2Idx < 0)
                prev2Idx = 0;
            bridge.Prev0 = mono[prev0Idx];
            bridge.Prev1 = mono[prev1Idx];
            bridge.Prev2 = mono[prev2Idx];
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
