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
            // Why polyphase FIR over the earlier linear / cubic-Hermite
            // attempts: their narrower kernels left ~12-20 dB stopband
            // attenuation, which produced audible imaging above the
            // source Nyquist ("dust" on speech). Polyphase windowed-sinc
            // at L=16 taps, Kaiser β=8 hits ~60 dB stopband — well below
            // the perceptual floor.
            //
            // Continuity across Process calls: the kernel reads `taps`
            // samples per output frame, including `half` samples of
            // lookahead past the cursor's "consumed" position. We
            // <em>peek</em> the full window from the bridge ring
            // (PeekOrPull doesn't advance the read cursor), walk the
            // output frames, then Discard only the `consumed` samples
            // at the end. Earlier iterations of this resampler had a
            // structural sample-loss bug where every Process call
            // dropped its `half` lookahead samples — those gaps were
            // smeared by linear/Hermite's narrow kernels into "dust",
            // but the FIR's wide kernel turned each gap into a discrete
            // click at the ~187 Hz Process cadence (visible as broadband
            // vertical lines in spectrograms). The Peek+Discard split
            // eliminates that gap by source-of-truth-ing the ring
            // across call boundaries.
            //
            // Buffer layout: peek directly into MonoScratch[0..needed-1].
            // The kernel's anchor at f=0 is mono[half-1], so on a fresh
            // ring with no leading silence the first output absorbs
            // ~half-1 samples (~0.5 ms at 16 kHz) of "pre-history". The
            // threshold gate's pre-fill makes this imperceptible in
            // practice.
            float[]? kernel = bridge.ResamplerKernel;
            if (kernel == null)
            {
                SilenceFill(buffer, channels, frames);
                return frames;
            }
            const int taps = AgentAudioGeneratorBridge.KernelTaps;
            const int phases = AgentAudioGeneratorBridge.PhaseCount;

            double step = (double)inputRate / deviceRate;
            double startFrac = bridge.ResampleFracPos;

            // Compute the maximum kernel read index across all frames:
            //   max touched = floor(startFrac + (frames-1)*step) + taps - 1
            // so the peek window size is that + 1.
            int maxSampleStart = (int)Math.Floor(startFrac + (frames - 1) * step);
            if (maxSampleStart < 0)
                maxSampleStart = 0;
            int needed = maxSampleStart + taps;
            if (needed > mono.Length)
                needed = mono.Length;
            int gotR = bridge.PeekOrPull(mono.AsSpan(0, needed));
            // Pad-on-underrun with last-sample-hold (avoids transient
            // click vs zero-fill). The pad covers any kernel taps that
            // would otherwise read uninitialized slots; the convolution
            // against a flat tail is benign.
            if (gotR < needed)
            {
                float pad = gotR > 0 ? mono[gotR - 1] : 0f;
                for (int i = gotR; i < needed; i++)
                    mono[i] = pad;
            }

            double pos = startFrac;
            for (int f = 0; f < frames; f++)
            {
                // sampleStart = floor(pos); kernel reads mono[sampleStart..
                // sampleStart+taps-1]. The phase index selects the
                // appropriate row of the polyphase coefficient table —
                // nearest-phase lookup is fine at PhaseCount=256 (max
                // error well below the 60 dB stopband).
                int floorPos = (int)Math.Floor(pos);
                double frac = pos - floorPos;
                int phaseIdx = (int)(frac * phases + 0.5);
                if (phaseIdx >= phases)
                {
                    // Phase rounded up across the integer boundary;
                    // advance sampleStart by one and snap to phase 0
                    // so the kernel window doesn't double-shift.
                    phaseIdx = 0;
                    floorPos++;
                }
                int kernelBase = phaseIdx * taps;
                int sampleStart = floorPos;
                // Clamp on the right if peek underran. sampleStart
                // should always be in [0, monoEnd-taps], but defensively
                // clamp so a pathological state can't OOB-read.
                if (sampleStart < 0)
                    sampleStart = 0;
                if (sampleStart + taps > needed)
                    sampleStart = needed - taps;
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

            // After the loop, pos = startFrac + frames*step. The number
            // of input samples the cursor advanced past = floor(pos).
            // Discard exactly that many from the ring so the next
            // Process call's peek starts where this call's anchor left
            // off. The remaining samples (the kernel's lookahead) stay
            // in the ring for re-peek.
            int consumed = (int)Math.Floor(pos);
            if (consumed > 0)
                bridge.Discard(consumed);
            bridge.ResampleFracPos = pos - consumed;
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
