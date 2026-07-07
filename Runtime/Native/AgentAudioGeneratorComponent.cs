#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;
using Unity.IntegerTime;
using UnityEngine;
using UnityEngine.Audio;

namespace ElevenLabs.Native
{
    /// <summary>
    /// Heap-allocated shared state between
    /// <see cref="AgentAudioRealtime"/> (audio thread) and
    /// <see cref="AgentAudioControl"/> (control thread). Holds the SPSC
    /// PCM ring, the host-supplied drain callback, and the pre-allocated
    /// scratch buffers the audio thread reuses so <see cref="Drain"/>
    /// never allocates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity's <see cref="GeneratorInstance.IControl{TRealtime}"/> generic
    /// constraint is <c>unmanaged</c>, which forbids the
    /// <see cref="AgentAudioRealtime"/> struct from holding a managed
    /// reference inline. Instead, every bridge is assigned a monotonic
    /// <see cref="Handle"/> (non-zero, never recycled) and registered in
    /// a process-wide
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/>; the struct
    /// resolves the bridge via <see cref="LookupByHandle(int)"/> from
    /// the audio thread (lock-free read). The handle stays valid until
    /// <see cref="Dispose"/> deregisters it — re-entries after Dispose
    /// fall through to a silence-filled <see cref="Process"/>.
    /// </para>
    /// <para>
    /// Production wiring today: <see cref="UnityAudioSourceOutput"/>'s
    /// <c>ReadFromRing</c> is registered as the
    /// <see cref="Func{Single, Int32}"/> drain callback (where the array
    /// element is <see cref="float"/>), so a <see cref="Drain"/> underrun
    /// synchronously pulls samples through the host's <c>_ring</c> on the
    /// audio thread — the same pull model
    /// <see cref="UnityAudioOutputEngine"/>'s <c>OnPcmRead</c> uses today.
    /// Steps 5/6 of the plan replace that inline pull with a producer-side
    /// push from <c>PushAudio</c> into <see cref="Ring"/>, at which point
    /// the drain-callback path becomes a no-op fallback.
    /// </para>
    /// </remarks>
    internal sealed class AgentAudioGeneratorBridge : IDisposable
    {
        // Monotonic counter for issuing handles. Starts at 0; first call
        // increments to 1 so Handle=0 stays reserved as "uninitialized" /
        // "no bridge" — keeps default(struct) safe to silence-fill instead
        // of crashing on a stale lookup.
        private static int s_nextHandle;

        // Process-wide registry of live bridges keyed by handle.
        // ConcurrentDictionary's TryGetValue is documented lock-free in
        // the read path (no allocations), which is what the audio thread
        // needs. Capacity is unbounded — handle space is 31 bits, which
        // tops out at ~2B sessions before wrap; not a realistic concern.
        private static readonly ConcurrentDictionary<int, AgentAudioGeneratorBridge> s_byHandle =
            new();

        /// <summary>Handle into the static registry. Non-zero for a live
        /// bridge; resolves to <c>null</c> from
        /// <see cref="LookupByHandle"/> after <see cref="Dispose"/>.</summary>
        public int Handle { get; }

        /// <summary>Input sample rate the producer feeds, in Hz (e.g. 16000
        /// when the server negotiates <c>pcm_16000</c> via the
        /// <c>conversation_initiation_metadata</c> handshake).</summary>
        public int InputSampleRate { get; }

        // Device output sample rate, in Hz — revealed to us when Unity
        // invokes AgentAudioControl.Configure (control thread) and consumed
        // by AgentAudioRealtime.Process (audio thread) to drive the
        // input→device-rate resampler. Volatile because the cross-thread
        // publish is a single int store; no value-ordering dependency
        // beyond "Process sees the most recently published value".
        // Realtime generators must declare a Setup rate matching the
        // device's output rate (per Unity's "Realtime generators must obey
        // system sampling rate" requirement), so the bridge resamples its
        // negotiated input rate up/down to whatever Unity hands us — keeps
        // both ends of the pipeline runtime-flexible.
        private int _deviceSampleRate;

        /// <summary>Device output sample rate, in Hz, as revealed by
        /// <see cref="AgentAudioControl.Configure"/>. Zero until the first
        /// <c>Configure</c> fire; the realtime struct falls back to a 1:1
        /// pass-through (no resampling) for that window. The setter
        /// rebuilds the polyphase resampler kernel (control thread; not
        /// concurrent with Process per Unity's IAudioGenerator threading
        /// contract) and the <see cref="Volatile.Write"/> below acts as
        /// the release fence so the audio thread sees the freshly built
        /// kernel + carry buffer once it observes a positive
        /// device rate.</summary>
        public int DeviceSampleRate
        {
            get => Volatile.Read(ref _deviceSampleRate);
            set
            {
                if (value == Volatile.Read(ref _deviceSampleRate))
                    return;
                bool resampling = value > 0 && value != InputSampleRate;
                if (resampling)
                {
                    ResamplerKernel = BuildResamplerKernel(InputSampleRate, value);
                    ResampleFracPos = 0.0;
                }
                else
                {
                    ResamplerKernel = null;
                    ResampleFracPos = 0.0;
                }
                // Rate observability: a stale or surprising device rate is
                // otherwise invisible (see Docs~/unity-issues/
                // stale-dsp-rate-bluetooth-profile-change.md — diagnosing a
                // 3× pitch shift took a live-editor session because nothing
                // logged the negotiated rates). Configure runs on Unity's
                // control thread; Debug.Log is thread-safe.
                UnityEngine.Debug.Log(
                    $"[ElevenLabs] Agent audio output: input {InputSampleRate} Hz → "
                        + $"device {value} Hz "
                        + $"({(resampling ? "resampling" : "1:1 pass-through")})."
                );
                Volatile.Write(ref _deviceSampleRate, value);
            }
        }

        /// <summary>
        /// Build a Kaiser-windowed sinc polyphase FIR kernel for the
        /// given <paramref name="inputRate"/> → <paramref name="outputRate"/>
        /// pair. Returns a flat <see cref="PhaseCount"/> × <see cref="KernelTaps"/>
        /// coefficient table laid out row-major (each row sums to ~1.0
        /// for DC gain). Cutoff is set just below the minimum Nyquist
        /// with a small safety margin so the transition band falls
        /// inside our budget.
        /// </summary>
        internal static float[] BuildResamplerKernel(int inputRate, int outputRate)
        {
            // Cutoff in INPUT-rate units (1.0 = input Nyquist). For
            // upsampling we're bandwidth-limited by the input; for
            // downsampling we need to anti-alias to the lower output
            // Nyquist. Apply a 0.95 margin to avoid putting energy
            // right at the cutoff (sinc lobes are uglier near 1.0).
            double cutoff = outputRate >= inputRate ? 1.0 : (double)outputRate / inputRate;
            cutoff *= 0.95;

            const double beta = 12.0; // Kaiser β=12 → ~85-90 dB stopband (combined with L=32 taps)
            double i0Beta = BesselI0(beta);
            int half = KernelTaps / 2;
            float[] kernel = new float[PhaseCount * KernelTaps];

            for (int p = 0; p < PhaseCount; p++)
            {
                double phase = (double)p / PhaseCount;
                double rowSum = 0.0;
                int rowBase = p * KernelTaps;
                for (int n = 0; n < KernelTaps; n++)
                {
                    // Tap position t (in input-sample units) is the
                    // signed offset between the output position
                    // (`phase` past the anchor input sample) and the
                    // input sample at tap n. With anchor at conceptual
                    // index `half - 1` and tap n covering input indices
                    // 0..L-1, t = (half - 1 - n) + phase. Note the sign:
                    // larger n → more negative t (looking further to
                    // the right of the anchor).
                    double t = half - 1 - n + phase;
                    double sincArg = Math.PI * t * cutoff;
                    double sincVal = Math.Abs(sincArg) < 1e-9 ? 1.0 : Math.Sin(sincArg) / sincArg;
                    // Kaiser window over support [-half, +half - 1].
                    // window_arg in [-1, 1]; zero at the kernel edges.
                    double winArg = t / half;
                    double window;
                    if (winArg >= 1.0 || winArg <= -1.0)
                    {
                        window = 0.0;
                    }
                    else
                    {
                        double argSq = 1.0 - winArg * winArg;
                        window = BesselI0(beta * Math.Sqrt(argSq)) / i0Beta;
                    }
                    double coeff = cutoff * sincVal * window;
                    kernel[rowBase + n] = (float)coeff;
                    rowSum += coeff;
                }
                // DC-normalize each row so a constant input passes
                // through with unity gain.
                if (rowSum > 1e-9)
                {
                    float invSum = (float)(1.0 / rowSum);
                    for (int n = 0; n < KernelTaps; n++)
                        kernel[rowBase + n] *= invSum;
                }
            }

            return kernel;
        }

        /// <summary>
        /// Modified Bessel function of the first kind, order 0 — the
        /// normalizing kernel inside Kaiser windows. Converges fast for
        /// the modest β values we use; bounded iterations.
        /// </summary>
        internal static double BesselI0(double x)
        {
            double y = x / 2.0;
            double t = 1.0;
            double sum = 1.0;
            for (int k = 1; k < 50; k++)
            {
                t *= y / k;
                double inc = t * t;
                sum += inc;
                if (inc < 1e-15 * sum)
                    break;
            }
            return sum;
        }

        /// <summary>Polyphase windowed-sinc resampler kernel: a flat
        /// [<see cref="PhaseCount"/> × <see cref="KernelTaps"/>] table of
        /// coefficients laid out row-major (phase-major). Built by
        /// <see cref="BuildResamplerKernel"/> on the control thread when
        /// <see cref="DeviceSampleRate"/> is set; read by
        /// <see cref="AgentAudioRealtime.Process"/> on the audio thread
        /// via the volatile-published <c>DeviceSampleRate</c> as the
        /// release fence (the kernel ref is visible to any thread that
        /// subsequently reads <c>DeviceSampleRate</c> with acquire
        /// semantics).</summary>
        public float[]? ResamplerKernel;

        /// <summary>Fractional position in [0, 1) carried across
        /// <see cref="AgentAudioRealtime.Process"/> calls — how far we've
        /// stepped past the last consumed input sample toward the next
        /// freshly drained one. Touched only on the audio thread (no
        /// concurrency).</summary>
        public double ResampleFracPos;

        /// <summary>Resampler kernel length (taps per phase). At Kaiser
        /// β=12, 32 taps gives ~85-90 dB stopband — below any speech
        /// perceptual floor. The earlier L=16 / β=8 (~60 dB) left a
        /// faint sibilant-correlated "rattle" audible on the live agent
        /// per spectrogram (mirror-image leakage in the 8-18 kHz band);
        /// L=32 / β=12 drops that leakage by an additional ~25-30 dB.
        /// Per-output cost: 32 multiply-adds, still trivial on the
        /// audio thread (~1.5 M ops/sec at 48 kHz output).</summary>
        public const int KernelTaps = 32;

        /// <summary>Resampler phase resolution. Nearest-phase lookup
        /// (vs interpolating between phases) introduces a max error of
        /// half a phase × max(|h'|), which at <see cref="PhaseCount"/>
        /// = 256 is well below the kernel's own stopband — extra phase
        /// resolution would just bloat the table without audible
        /// benefit.</summary>
        public const int PhaseCount = 256;

        /// <summary>Shared SPSC PCM ring; written by the producer side
        /// (today: the drain callback on the audio thread; step 5/6: the
        /// host's main-thread push), read by
        /// <see cref="AgentAudioRealtime"/>'s
        /// <see cref="GeneratorInstance.IRealtime.Process"/>.</summary>
        public AudioPcmRing Ring { get; }

        /// <summary>Reusable mono scratch the realtime struct fills from
        /// the ring before broadcasting to multi-channel output. Sized at
        /// construction (no allocations on the audio thread).</summary>
        public float[] MonoScratch { get; }

        // Scratch the audio thread hands to the drain callback when the
        // ring underruns. Sized identically to MonoScratch so a single
        // refill round can satisfy one DSP buffer's worth of demand. Reused
        // across calls so the audio thread allocates zero per fire.
        private readonly float[] _refillScratch;

        // Drain callback set by the engine at Start. Volatile is overkill
        // for x86/x64 (a managed reference write is a single naturally
        // aligned store), but cheap on ARM and explicit about the
        // cross-thread publish.
        private Func<float[], int>? _drainCallback;

        private int _disposed;

        /// <summary>
        /// Allocate the bridge.
        /// </summary>
        /// <param name="inputSampleRate">Sample rate the producer feeds, in
        /// Hz (e.g. 16000).</param>
        /// <param name="ringCapacity">SPSC ring depth, in samples.
        /// Sized generously over typical DSP buffer fires so a single slow
        /// main-thread push doesn't underrun the audio thread.</param>
        /// <param name="scratchFrames">Maximum frames per
        /// <see cref="Drain"/> request. Sized to absorb the largest DSP
        /// buffer Unity is likely to ask for so the audio thread never
        /// allocates.</param>
        public AgentAudioGeneratorBridge(int inputSampleRate, int ringCapacity, int scratchFrames)
        {
            if (inputSampleRate <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(inputSampleRate),
                    inputSampleRate,
                    "Input sample rate must be positive."
                );
            if (scratchFrames <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(scratchFrames),
                    scratchFrames,
                    "Scratch frames must be positive."
                );
            InputSampleRate = inputSampleRate;
            Ring = new AudioPcmRing(ringCapacity);
            MonoScratch = new float[scratchFrames];
            _refillScratch = new float[scratchFrames];
            Handle = Interlocked.Increment(ref s_nextHandle);
            s_byHandle[Handle] = this;
        }

        /// <summary>
        /// Resolve a handle to its live bridge, or <c>null</c> if the
        /// bridge has been disposed (or the handle was never registered).
        /// Safe to call from the audio thread — lock-free per
        /// <see cref="ConcurrentDictionary{TKey, TValue}.TryGetValue"/>.
        /// </summary>
        public static AgentAudioGeneratorBridge? LookupByHandle(int handle)
        {
            if (handle == 0)
                return null;
            return s_byHandle.TryGetValue(handle, out AgentAudioGeneratorBridge? bridge)
                ? bridge
                : null;
        }

        /// <summary>
        /// Install (or clear) the producer-side drain callback. Safe to call
        /// from the control thread while the audio thread is running:
        /// <see cref="Drain"/> snapshots the field once per call.
        /// </summary>
        public void SetProducer(Func<float[], int>? drainCallback)
        {
            _drainCallback = drainCallback;
        }

        /// <summary>
        /// Audio-thread drain. Reads up to <c>dest.Length</c> samples from
        /// the ring (advancing the read cursor); on underrun, synchronously
        /// pulls more from the installed drain callback into the ring and
        /// re-reads. Returns the count actually written into
        /// <paramref name="dest"/> — callers must silence-fill any
        /// remainder.
        /// </summary>
        /// <remarks>
        /// Used by the fast 1:1 path in <see cref="AgentAudioRealtime.Process"/>
        /// (when the negotiated input rate equals the device output rate)
        /// where every drained sample is consumed exactly once. The
        /// resampling path uses <see cref="Peek"/> + <see cref="Discard"/>
        /// instead, so kernel-lookahead samples that the next call still
        /// needs aren't dropped from the producer ring at each Process
        /// boundary.
        /// </remarks>
        public int Drain(Span<float> dest)
        {
            int read = PeekOrPull(dest);
            if (read > 0)
                Ring.Discard(read);
            return read;
        }

        /// <summary>
        /// Audio-thread peek. Reads up to <c>dest.Length</c> samples from
        /// the ring <em>without</em> advancing the read cursor; on
        /// underrun, synchronously pulls more from the installed drain
        /// callback into the ring and re-peeks. Returns the count
        /// actually written into <paramref name="dest"/>.
        /// </summary>
        /// <remarks>
        /// Used by the resampling path: the kernel reads <c>taps</c>
        /// samples per output frame including <c>half</c> samples of
        /// lookahead past the "consumed" cursor. Peeking lets the next
        /// Process call re-see those lookahead samples instead of
        /// silently dropping them at the call boundary (which caused
        /// audible clicks every ~5 ms in earlier iterations of the FIR
        /// resampler — see commit history). Pair with
        /// <see cref="Discard"/> to advance the cursor by the
        /// <em>consumed</em> count once the call's outputs are
        /// computed.
        /// </remarks>
        public int PeekOrPull(Span<float> dest)
        {
            if (_disposed != 0 || dest.IsEmpty)
                return 0;
            int peeked = Ring.Peek(dest);
            if (peeked >= dest.Length)
                return peeked;
            // Snapshot the callback once — a control-thread SetProducer
            // race may swap it out between the null-check and the
            // invocation otherwise.
            Func<float[], int>? cb = _drainCallback;
            if (cb == null)
                return peeked;
            // Never pull more than the ring can absorb: the callback hands
            // samples over destructively (the host ring's read cursor
            // advances as it fills the scratch), so anything Ring.Write
            // couldn't fit would vanish from the playback stream. The pull
            // only fires when the ring is nearly drained — and the engine
            // sizes the ring at least one full scratch deep — so this
            // guard never trips under production tuning; it turns a future
            // re-tune (scratch grown past the ring's headroom) into a
            // deferred refill instead of silently dropped audio.
            if (Ring.Available < _refillScratch.Length)
                return peeked;
            int got;
            try
            {
                got = cb(_refillScratch);
            }
            catch (Exception)
            {
                // Audio-thread callbacks aren't allowed to throw; swallow
                // so we don't poison the DSP graph. The host's drain
                // callback already silence-fills on its own underrun, so
                // a thrown exception is a programmer error we shouldn't
                // propagate into Unity's audio system.
                return peeked;
            }
            if (got > 0)
            {
                int writeLen = Math.Min(got, _refillScratch.Length);
                Ring.Write(_refillScratch.AsSpan(0, writeLen));
            }
            // Re-peek into the full dest (the prior peek already filled
            // dest[0..peeked-1]; refill is now visible at dest[peeked..]).
            int more = Ring.Peek(dest);
            return more > peeked ? more : peeked;
        }

        /// <summary>
        /// Advance the read cursor by <paramref name="count"/> samples,
        /// clamped to the currently available count. Pair with
        /// <see cref="PeekOrPull"/> on the resampling path so peeked
        /// samples that became "consumed" by the kernel's anchor
        /// advancing past them are released to the ring.
        /// </summary>
        public void Discard(int count) => Ring.Discard(count);

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;
            _drainCallback = null;
            // Deregister before disposing the ring so a lingering audio-
            // thread Process can't resolve the handle and call into a
            // half-torn-down state.
            s_byHandle.TryRemove(Handle, out _);
            Ring.Dispose();
        }
    }

    /// <summary>
    /// <see cref="MonoBehaviour"/> + <see cref="IAudioGenerator"/> bound to
    /// the user's (or owned-host) <see cref="AudioSource"/> via
    /// <see cref="AudioSource.generator"/>. Pairs an
    /// <see cref="AgentAudioRealtime"/> (audio-thread sample writer) with
    /// an <see cref="AgentAudioControl"/> (control-thread format
    /// negotiator) via <see cref="ControlContext.AllocateGenerator"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Created and managed by <see cref="UnityGeneratorAudioOutputEngine"/> —
    /// callers configure the bridge with
    /// <see cref="Initialize(AgentAudioGeneratorBridge)"/> before assigning
    /// the component to <see cref="AudioSource.generator"/>. Unity invokes
    /// <see cref="CreateInstance"/> off the back of that assignment to
    /// build the realtime/control pair.
    /// </para>
    /// <para>
    /// Capabilities: continuous-output generator
    /// (<see cref="isFinite"/>=false, <see cref="isRealtime"/>=true,
    /// <see cref="length"/>=null) — Unity never asks for a total length
    /// or seeks within the stream.
    /// </para>
    /// </remarks>
    internal sealed class AgentAudioGeneratorComponent : MonoBehaviour, IAudioGenerator
    {
        private AgentAudioGeneratorBridge? _bridge;

        public bool isFinite => false;
        public bool isRealtime => true;
        public DiscreteTime? length => null;

        /// <summary>
        /// Wire the heap-allocated shared state the component hands to the
        /// realtime + control structs at <see cref="CreateInstance"/>
        /// time. Must be called before assigning the component to
        /// <see cref="AudioSource.generator"/>.
        /// </summary>
        internal void Initialize(AgentAudioGeneratorBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        public GeneratorInstance CreateInstance(
            ControlContext context,
            AudioFormat? nestedConfiguration,
            ProcessorInstance.CreationParameters creationParameters
        )
        {
            if (_bridge == null)
                throw new InvalidOperationException(
                    "AgentAudioGeneratorComponent.Initialize must be called before binding "
                        + "the component to AudioSource.generator."
                );
            return context.AllocateGenerator(
                new AgentAudioRealtime(_bridge.Handle),
                new AgentAudioControl(_bridge.Handle),
                nestedConfiguration,
                creationParameters
            );
        }
    }
}
