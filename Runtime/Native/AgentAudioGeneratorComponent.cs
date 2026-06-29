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
        /// pass-through (no resampling) for that window.</summary>
        public int DeviceSampleRate
        {
            get => Volatile.Read(ref _deviceSampleRate);
            set => Volatile.Write(ref _deviceSampleRate, value);
        }

        /// <summary>Last mono input sample emitted by the resampler — the
        /// "left endpoint" of the next interpolation interval. Touched
        /// only on the audio thread (no concurrency).</summary>
        public float LastInputSample;

        /// <summary>Fractional position in [0, 1) carried across
        /// <see cref="AgentAudioRealtime.Process"/> calls — how far we've
        /// stepped past <see cref="LastInputSample"/> toward the next
        /// mono sample. Touched only on the audio thread (no
        /// concurrency).</summary>
        public double ResampleFracPos;

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
        /// the ring; on underrun, synchronously pulls more from the
        /// installed drain callback into the ring and re-reads. Returns the
        /// count actually written into <paramref name="dest"/> — callers
        /// must silence-fill any remainder.
        /// </summary>
        public int Drain(Span<float> dest)
        {
            if (_disposed != 0 || dest.IsEmpty)
                return 0;
            int read = Ring.Read(dest);
            if (read >= dest.Length)
                return read;
            // Snapshot the callback once — a control-thread SetProducer
            // race may swap it out between the null-check and the
            // invocation otherwise.
            Func<float[], int>? cb = _drainCallback;
            if (cb == null)
                return read;
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
                return read;
            }
            if (got > 0)
            {
                int writeLen = Math.Min(got, _refillScratch.Length);
                Ring.Write(_refillScratch.AsSpan(0, writeLen));
            }
            int more = Ring.Read(dest.Slice(read));
            return read + more;
        }

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
