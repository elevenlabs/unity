#nullable enable

using System;
using System.Threading;
using ElevenLabs.Agents;
using UnityEngine;
using UnityEngine.Audio;
using Debug = UnityEngine.Debug;

namespace ElevenLabs.Native
{
    /// <summary>
    /// Production <see cref="IAudioOutputEngine"/> backed by Unity 6.3's
    /// <see cref="IAudioGenerator"/> + <see cref="GeneratorInstance"/>
    /// surface (bound to an <see cref="AudioSource"/> via
    /// <see cref="AudioSource.generator"/>). Eliminates the structural
    /// ~800 ms streaming-<see cref="AudioClip"/> pre-fill the legacy
    /// <see cref="UnityAudioOutputEngine"/> compensates for; see
    /// <c>Docs~/plans/audio-generator-engine.md</c> for the full design.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors <see cref="UnityAudioOutputEngine"/>'s constructor signature
    /// and lifecycle so the seam at <see cref="IAudioOutputEngine"/> stays
    /// implementation-agnostic. The supplied-source ergonomic change vs the
    /// abandoned <c>OnAudioFilterRead</c> design is the headline: the SDK
    /// adds a <strong>sibling</strong>
    /// <see cref="AgentAudioGeneratorComponent"/> to the user's literal
    /// <see cref="AudioSource"/>'s <see cref="GameObject"/> rather than
    /// reparenting a child source, so the user's
    /// AudioSource is the one that plays the agent's voice (preserving
    /// spatialization, mixer routing, transform parenting).
    /// </para>
    /// <para>
    /// Snapshot/restore: on a supplied source we capture
    /// <see cref="AudioSource.volume"/>, <see cref="AudioSource.loop"/>,
    /// <see cref="AudioSource.clip"/>, AND <see cref="AudioSource.generator"/>
    /// at construction so <see cref="Dispose"/> can restore the source to
    /// exactly the configuration the caller handed over. The added
    /// <c>generator</c> field is the only delta vs
    /// <see cref="UnityAudioOutputEngine"/>'s snapshot set —
    /// <see cref="UnityGeneratorAudioOutputEngineCharacterizationTest"/>
    /// (PlayMode) confirmed that a coexisting
    /// <see cref="AudioSource.clip"/> is silently overridden by an active
    /// generator, so the existing clip snapshot is sufficient with no
    /// active null-out at <see cref="Start"/>.
    /// </para>
    /// </remarks>
    internal sealed class UnityGeneratorAudioOutputEngine : IAudioOutputEngine
    {
        // Pre-allocated scratch frame count handed to the bridge so
        // AgentAudioRealtime.Process never grows storage on the audio
        // thread. Sized generously above typical DSP buffer fires (256 on
        // Unity 6 by default — measured by the characterization test) so
        // even a buffer-size doubling at runtime stays within the
        // pre-allocated capacity.
        private const int ScratchFrames = 4096;

        // Ring depth in milliseconds at the input sample rate. Sized to
        // absorb several DSP-buffer fires worth of producer slack while
        // keeping per-session memory modest (~32 KB at 16 kHz × 1 s).
        // Step 5/6 will tune this once the producer-side push pattern is
        // wired through UnityAudioSourceOutput.
        private const int RingMilliseconds = 1000;

        // Default user-level volume captured at construction so a supplied
        // source's pre-session volume can be replaced with the SDK's owned
        // 1.0 baseline (matching UnityAudioOutputEngine's behaviour).
        private const float DefaultUserVolume = 1f;

        private GameObject? _hostObject;
        private AudioSource? _audioSource;
        private AgentAudioGeneratorComponent? _component;
        private AgentAudioGeneratorBridge? _bridge;
        private int _disposed;

        // True when _audioSource was supplied via
        // ConversationOptions.OutputAudioSource rather than created on a
        // hidden host. Drives Dispose() behaviour (restore vs destroy) and
        // the mid-session destruction warning.
        private readonly bool _suppliedSource;

        // Pre-session snapshot of SDK-owned overwrites on a supplied
        // source, restored on Dispose. Captured at construction so a
        // session torn down before any audio plays still rolls cleanly
        // back. The `generator` snapshot is the IAudioGenerator-path
        // addition — UnityAudioOutputEngine only snapshots clip/loop/volume.
        private readonly float _savedVolume;
        private readonly bool _savedLoop;
        private readonly IAudioGenerator? _savedGenerator;
        private AudioClip? _savedClip;

        // Latches once the supplied source is observed as null (destroyed)
        // by IsAvailable so the warning fires exactly once per session.
        // Doesn't apply to the owned-host path: HideAndDontSave +
        // DontDestroyOnLoad means external code can't destroy it.
        private bool _destructionWarned;

        // Delegate instance kept so Dispose unsubscribes exactly what Start
        // subscribed. Non-null only between Start and Dispose.
        private AudioSettings.AudioConfigurationChangeHandler? _configChangeHandler;

        /// <summary>
        /// Construct the engine, set up an <see cref="AudioSource"/>
        /// (creating a hidden host <see cref="GameObject"/> when none is
        /// supplied), and capture the pre-session state of a supplied
        /// source for later restoration in <see cref="Dispose"/>. Does
        /// NOT attach the <see cref="AgentAudioGeneratorComponent"/> or
        /// begin playback yet — those happen in <see cref="Start"/> once
        /// the host has wired a drain callback.
        /// </summary>
        /// <param name="suppliedSource">Optional user-supplied
        /// <see cref="AudioSource"/> to bind to; when <c>null</c>, the
        /// engine creates a hidden host.</param>
        /// <param name="device">Optional output device override (logged +
        /// ignored — Unity routes output via process-wide
        /// <see cref="AudioSettings"/>, not per-source).</param>
        /// <remarks>
        /// Must be invoked on the Unity main thread (GameObject creation +
        /// AudioSource component manipulation aren't thread-safe).
        /// </remarks>
        public UnityGeneratorAudioOutputEngine(
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
                // `generator` is new vs UnityAudioOutputEngine —
                // characterization-test step confirmed runtime assignment
                // of audioSource.generator is supported, so the snapshot
                // captures whatever the caller had pre-bound (likely
                // null, but a non-null inspector-picked value is
                // preserved through the session).
                _savedVolume = suppliedSource.volume;
                _savedLoop = suppliedSource.loop;
                _savedClip = suppliedSource.clip;
                _savedGenerator = suppliedSource.generator;
            }
            else
            {
                _hostObject = new GameObject("ElevenLabs.UnityGeneratorAudioOutput")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                if (Application.isPlaying)
                    UnityEngine.Object.DontDestroyOnLoad(_hostObject);
                _audioSource = _hostObject.AddComponent<AudioSource>();
            }

            // Stop any prior playback (a supplied source might have been
            // playing something else). The generator-path equivalent of
            // UnityAudioOutputEngine's "defer the streaming clip" trick
            // is structurally unnecessary — Unity's IAudioGenerator
            // surface has zero pre-fill (confirmed by the
            // characterization test). We still defer Play() to Start() so
            // the host can withhold engine.Start until a drain callback
            // is ready.
            _audioSource.Stop();
            _audioSource.loop = true;
            _audioSource.volume = DefaultUserVolume;

            // Output device switching is process-wide via AudioSettings;
            // not per-AudioSource. Mirror UnityAudioOutputEngine's warning
            // so callers know the request didn't take effect.
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
                // Owned-host path: HideAndDontSave + DontDestroyOnLoad
                // means the AudioSource can't be destroyed externally —
                // _audioSource is non-null until Dispose runs.
                if (!_suppliedSource)
                    return _audioSource != null && _disposed == 0;
                // Supplied-source path: Unity's overloaded == returns true
                // for destroyed UnityEngine.Object references; treat that
                // as "no longer available" and fire the warn-once side
                // effect so the caller can stop pushing audio cleanly.
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

        // Zero by construction — Unity's IAudioGenerator surface fires
        // Process strictly on the audio thread AFTER Play(), with no
        // synchronous pre-fill inside Play() itself (confirmed in the
        // PlayMode characterization test at step 1: 0 Process fires inside
        // Play(), ~187.5 Hz cadence afterward). The controller's threshold
        // gate collapses to just its DSP-buffer margin under this engine —
        // engine.Start can fire on the first chunk above DSP-buffer size
        // without risking silence-fill.
        public int SyncPrefillSampleCount => 0;

        public void Start(FormatConfig format, Func<float[], int> drainCallback)
        {
            if (format == null)
                throw new ArgumentNullException(nameof(format));
            if (drainCallback == null)
                throw new ArgumentNullException(nameof(drainCallback));
            if (_audioSource == null || _disposed != 0)
                return;
            // Idempotent gate — UnityAudioSourceOutput.StartPlayback
            // already guards against double-fire, but be defensive in case
            // a future caller drives Start/Stop/Start cycles. A second
            // call leaves the prior component+bridge in place (no leak,
            // no double-bind).
            if (_component != null)
                return;

            int ringCapacity = Math.Max(ScratchFrames, format.SampleRate * RingMilliseconds / 1000);
            _bridge = new AgentAudioGeneratorBridge(
                inputSampleRate: format.SampleRate,
                ringCapacity: ringCapacity,
                scratchFrames: ScratchFrames
            );
            _bridge.SetProducer(drainCallback);

            // Sibling component on the user's GameObject (or our hidden
            // host) — the non-breaking ergonomic improvement vs the
            // abandoned OnAudioFilterRead plan (no child GameObject
            // reparenting). AudioSource.generator's public setter (step 0
            // doc-confirmed, step 1 PlayMode-confirmed) binds the
            // component to Unity's audio pipeline.
            _component = _audioSource.gameObject.AddComponent<AgentAudioGeneratorComponent>();
            _component.Initialize(_bridge);
            _audioSource.generator = _component;
            _audioSource.Play();

            // Unity reinitializes the audio system when the output device
            // changes (e.g. a Bluetooth headset flipping A2DP ↔ HFP when a
            // conversation opens its microphone) — and STOPS every playing
            // AudioSource when it does, which would silently mute the agent
            // for the rest of the session. Watch for the event, log the
            // rate transition (see Docs~/unity-issues/
            // stale-dsp-rate-bluetooth-profile-change.md), and restart
            // playback. Unity raises this on the main thread.
            _configChangeHandler = HandleAudioConfigurationChanged;
            AudioSettings.OnAudioConfigurationChanged += _configChangeHandler;
        }

        // Internal (vs private) so Edit-Mode tests can drive the guards
        // directly — Unity provides no way to raise the event manually.
        internal void HandleAudioConfigurationChanged(bool deviceWasChanged)
        {
            if (_disposed != 0 || _audioSource == null)
                return;
            Debug.Log(
                "[ElevenLabs] Audio configuration changed "
                    + $"(deviceWasChanged={deviceWasChanged}); output sample rate is now "
                    + $"{AudioSettings.outputSampleRate} Hz. Restarting agent audio playback."
            );
            // The generator stays bound across the reinit; Unity rebuilds
            // the generator graph and re-fires AgentAudioControl.Configure
            // with the new device rate, so the bridge resampler re-adapts
            // on its own — only the Play state needs help.
            if (_component != null && !_audioSource.isPlaying)
                _audioSource.Play();
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
            // No-op: Unity's audio thread drives IRealtime.Process
            // independently of any host-side time tick.
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;
            if (_configChangeHandler != null)
            {
                AudioSettings.OnAudioConfigurationChanged -= _configChangeHandler;
                _configChangeHandler = null;
            }
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
                    // Restore every SDK-owned overwrite symmetrically so a
                    // subsequent session — or non-agent use of the same
                    // source — starts from the caller's original
                    // configuration. Restore `generator` BEFORE
                    // destroying the component below so Unity's
                    // assignment doesn't observe a half-destroyed
                    // reference.
                    try
                    {
                        _audioSource.generator = _savedGenerator;
                        _audioSource.clip = _savedClip;
                        _audioSource.loop = _savedLoop;
                        _audioSource.volume = _savedVolume;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
                else
                {
                    // Hidden-host path: explicitly clear the binding
                    // before the component is destroyed so Unity doesn't
                    // try to drive a stale reference during the
                    // destruction sequence.
                    try
                    {
                        _audioSource.generator = null;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }
            }
            if (_component != null)
            {
                DestroyObject(_component);
                _component = null;
            }
            if (_bridge != null)
            {
                _bridge.Dispose();
                _bridge = null;
            }
            if (_hostObject != null)
            {
                DestroyObject(_hostObject);
                _hostObject = null;
            }
            _savedClip = null;
            _audioSource = null;
        }

        private static void DestroyObject(UnityEngine.Object obj)
        {
            // Destroy is a Play-Mode operation; Edit Mode (and headless
            // test runs) need DestroyImmediate or Unity logs a "Destroy
            // may not be called from edit mode" warning and leaks the
            // object.
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
