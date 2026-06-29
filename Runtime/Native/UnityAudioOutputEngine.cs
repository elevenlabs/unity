#nullable enable

using System;
using System.Threading;
using ElevenLabs.Agents;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ElevenLabs.Native
{
    /// <summary>
    /// Legacy <see cref="IAudioOutputEngine"/> backed by Unity's
    /// <see cref="AudioSource"/> + streaming <see cref="AudioClip"/>. Wraps
    /// the <see cref="AudioClip.Create(string, int, int, int, bool, AudioClip.PCMReaderCallback)"/>
    /// + <see cref="AudioSource.Play"/> + <see cref="AudioClip.PCMReaderCallback"/>
    /// flow so the rest of <see cref="UnityAudioSourceOutput"/> stays
    /// engine-agnostic (and therefore testable in Edit Mode).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Deprecated for production.</strong> As of the audio-generator
    /// engine swap (<c>Docs~/plans/audio-generator-engine.md</c> step 6),
    /// <see cref="UnityAudioSourceOutput.CreateAsync"/> wires
    /// <see cref="UnityGeneratorAudioOutputEngine"/> instead — Unity 6.3's
    /// <see cref="IAudioGenerator"/> surface eliminates this engine's ~800 ms
    /// structural pre-fill (the streaming-<see cref="AudioClip"/> property
    /// documented at <c>Docs~/unity-issues/streaming-audioclip-prefill-depth.md</c>).
    /// Retained in tree as the v0.2 soak fallback and as a regression baseline
    /// for the pre-fill-threshold gate; removal will follow once the generator
    /// path has soaked. New call sites should use
    /// <see cref="UnityGeneratorAudioOutputEngine"/>.
    /// </para>
    /// <para>
    /// Holds the host <see cref="GameObject"/> (when no
    /// <see cref="AudioSource"/> is supplied), the bound
    /// <see cref="AudioSource"/>, the streaming <see cref="AudioClip"/>,
    /// and the pre-session snapshot of SDK-owned overwrites on a supplied
    /// source. See <c>Docs~/plans/audio-output-testability.md</c> for the
    /// design rationale.
    /// </para>
    /// </remarks>
    internal sealed class UnityAudioOutputEngine : IAudioOutputEngine
    {
        private GameObject? _hostObject;
        private AudioSource? _audioSource;
        private AudioClip? _outputClip;
        private Func<float[], int>? _drainCallback;
        private int _disposed;

        // True when _audioSource was supplied via ConversationOptions.OutputAudioSource
        // rather than created on a hidden host. Drives Dispose() behaviour
        // (restore vs destroy) and the mid-session destruction warning.
        private readonly bool _suppliedSource;

        // Pre-session snapshot of SDK-owned overwrites on a supplied source,
        // restored on Dispose. Captured at construction so a session that's
        // torn down before any audio plays still rolls cleanly back.
        private readonly float _savedVolume;
        private readonly bool _savedLoop;
        private AudioClip? _savedClip;

        // Latches once the supplied source is observed as null (destroyed) by
        // IsAvailable so the warning fires exactly once per session. Doesn't
        // apply to the owned-host path: HideAndDontSave + DontDestroyOnLoad
        // means external code can't destroy it.
        private bool _destructionWarned;

        // Default user-level volume captured at construction so a supplied
        // source's pre-session volume can be replaced with the SDK's owned
        // 1.0 baseline (matching the owned-host path's default).
        private const float DefaultUserVolume = 1f;

        /// <summary>
        /// Construct the engine, set up an <see cref="AudioSource"/> (creating
        /// a hidden host <see cref="GameObject"/> when none is supplied), and
        /// capture the pre-session state of a supplied source for later
        /// restoration in <see cref="Dispose"/>. Does NOT create an
        /// <see cref="AudioClip"/> or begin playback yet — those happen in
        /// <see cref="Start"/> so the synchronous
        /// <see cref="AudioClip.PCMReaderCallback"/> pre-fill lands on real
        /// samples instead of silence.
        /// </summary>
        /// <param name="suppliedSource">Optional user-supplied
        /// <see cref="AudioSource"/> to play through; when <c>null</c>, the
        /// engine creates a hidden host.</param>
        /// <param name="device">Optional output device override (logged +
        /// ignored — Unity routes output via process-wide
        /// <see cref="AudioSettings"/>, not per-source).</param>
        /// <remarks>
        /// Must be invoked on the Unity main thread (GameObject creation +
        /// AudioSource component manipulation aren't thread-safe).
        /// </remarks>
        public UnityAudioOutputEngine(
            AudioSource? suppliedSource = null,
            OutputDeviceConfig? device = null
        )
        {
            if (suppliedSource != null)
            {
                _suppliedSource = true;
                _audioSource = suppliedSource;
                // Capture every SDK-owned overwrite so Dispose can put the
                // source back exactly the way the caller handed it over.
                _savedVolume = suppliedSource.volume;
                _savedLoop = suppliedSource.loop;
                _savedClip = suppliedSource.clip;
            }
            else
            {
                _hostObject = new GameObject("ElevenLabs.UnityAudioSourceOutput")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                if (Application.isPlaying)
                    UnityEngine.Object.DontDestroyOnLoad(_hostObject);
                _audioSource = _hostObject.AddComponent<AudioSource>();
            }

            // Stop any prior playback (a supplied source might have been
            // playing something else). Critically, we DO NOT assign a
            // streaming AudioClip here — Unity pre-fills the streaming
            // clip's internal buffer the moment Play() is called (8+
            // PCMReaderCallback fires at 100 ms each = ~800 ms of buffer
            // depth on the Unity 6 default audio config); if the ring is
            // empty when those fire, they all silence-fill and queue ahead
            // of the real audio. The clip + Play() are deferred to Start()
            // so the caller can withhold them until the ring is primed.
            _audioSource.Stop();
            _audioSource.loop = true;
            _audioSource.volume = DefaultUserVolume;

            // Output device switching is process-wide via AudioSettings; not
            // per-AudioSource. Mirror the SetDevice path's warning so callers
            // know the request didn't take effect.
            if (device != null && !string.IsNullOrEmpty(device.OutputDeviceId))
            {
                Debug.LogWarning(
                    "UnityAudioSourceOutput: per-source output device selection isn't "
                        + "supported; falling back to the system default device."
                );
            }
        }

        public bool IsAvailable
        {
            get
            {
                // Owned-host path: HideAndDontSave + DontDestroyOnLoad means
                // the AudioSource can't be destroyed externally — _audioSource
                // is non-null until Dispose runs.
                if (!_suppliedSource)
                    return _audioSource != null && _disposed == 0;
                // Supplied-source path: Unity's overloaded == returns true
                // for destroyed UnityEngine.Object references; treat that as
                // "no longer available" and fire the warn-once side effect
                // so the caller can stop pushing audio cleanly.
                if (_audioSource == null)
                {
                    if (_disposed != 0)
                        return false; // Post-Dispose: silent no-op (expected).
                    if (!_destructionWarned)
                    {
                        _destructionWarned = true;
                        Debug.LogWarning(
                            "[ElevenLabs] OutputAudioSource was destroyed mid-session; "
                                + "audio output disabled for the remainder of the session."
                        );
                    }
                    return false;
                }
                return _disposed == 0;
            }
        }

        public float Volume
        {
            get => _audioSource != null ? _audioSource.volume : 0f;
            set
            {
                if (_audioSource != null)
                    _audioSource.volume = value;
            }
        }

        // Empirically-measured pre-fill at 16 kHz input on a Unity 6 default
        // audio config (50 PCMReaderCallback fires × 256 samples per fire,
        // covering the streaming buffer's 256 × 4 = 1024 output-rate samples
        // at 48 kHz). Reported as input-rate samples — the controller's
        // threshold gate is sized in the same units as the SDK's ring.
        // Rate-independent in practice: Unity's pre-fill demand is fixed
        // by the streaming buffer depth, not the input rate, so this
        // constant covers typical agent input rates (16/24 kHz) with
        // margin to spare.
        public int SyncPrefillSampleCount => 12_800;

        public void Start(FormatConfig format, Func<float[], int> drainCallback)
        {
            if (format == null)
                throw new ArgumentNullException(nameof(format));
            if (drainCallback == null)
                throw new ArgumentNullException(nameof(drainCallback));
            if (_audioSource == null || _disposed != 0)
                return;
            _drainCallback = drainCallback;
            // Keep the clip length small. Unity's pre-fill total is roughly
            // fixed at the streaming-buffer depth, but a smaller clip means
            // each PCMReaderCallback fire drains less per call, which makes
            // the underrun granularity finer when the threshold gate falls
            // back on timeout with a short ring.
            int clipSamples = Math.Max(256, format.SampleRate / 100);
            _outputClip = AudioClip.Create(
                name: "ElevenLabsAgentOutput",
                lengthSamples: clipSamples,
                channels: 1,
                frequency: format.SampleRate,
                stream: true,
                pcmreadercallback: OnPcmRead
            );
            _audioSource.clip = _outputClip;
            _audioSource.Play();
        }

        // Wraps the host's drainCallback so the IAudioOutputEngine contract
        // can promise "engine silence-fills any underrun before handing the
        // buffer back to the audio system" — even though
        // UnityAudioSourceOutput.ReadFromRing already silence-fills today.
        // Keeping the Array.Clear here (over already-zeroed slots) is a
        // no-op cost-wise but lets fakes record (data.Length - n) as the
        // silence-fill count without depending on caller-side silence
        // semantics.
        private void OnPcmRead(float[] data)
        {
            Func<float[], int>? cb = _drainCallback;
            if (cb == null)
            {
                Array.Clear(data, 0, data.Length);
                return;
            }
            int n = cb(data);
            if (n < 0)
                n = 0;
            if (n < data.Length)
                Array.Clear(data, n, data.Length - n);
        }

        public void Stop()
        {
            if (_audioSource == null)
                return;
            try
            {
                _audioSource.Stop();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public void Tick(double elapsedSeconds)
        {
            // No-op: Unity's audio thread drives PCMReaderCallback
            // independently of any host-side time tick.
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;
            if (_audioSource != null)
            {
                try
                {
                    _audioSource.Stop();
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
                if (_suppliedSource)
                {
                    // Restore every SDK-owned overwrite (volume, loop, clip)
                    // symmetrically so a subsequent session — or non-agent
                    // use of the same source — starts from the caller's
                    // original configuration. The AudioSource and its
                    // GameObject are left intact for the user to reuse.
                    try
                    {
                        _audioSource.clip = _savedClip;
                        _audioSource.loop = _savedLoop;
                        _audioSource.volume = _savedVolume;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }
            if (_outputClip != null)
            {
                DestroyObject(_outputClip);
                _outputClip = null;
            }
            if (_hostObject != null)
            {
                DestroyObject(_hostObject);
                _hostObject = null;
            }
            _savedClip = null;
            _audioSource = null;
            _drainCallback = null;
        }

        private static void DestroyObject(UnityEngine.Object obj)
        {
            // Destroy is a Play-Mode operation; Edit Mode (and headless test
            // runs) need DestroyImmediate or Unity logs a "Destroy may not
            // be called from edit mode" warning and leaks the object.
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
