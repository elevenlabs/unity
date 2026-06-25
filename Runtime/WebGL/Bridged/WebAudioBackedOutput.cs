#nullable enable

using System;
using System.Linq;
using System.Threading;
using ElevenLabs.Agents;
using UnityEngine;
using UnityEngine.Audio;

namespace ElevenLabs.WebGL.Bridged
{
    /// <summary>
    /// <see cref="IOutputController"/> implementation backed by the JS-side
    /// <c>createWebAudioSink</c> factory from
    /// <c>Bridge~/src/connection/web-audio-sink.ts</c>. Holds an optional
    /// user-supplied <see cref="AudioSource"/> and mirrors a curated subset of
    /// its properties onto the Web Audio graph once per Unity frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Audio bytes never cross the bridge — the JS sink is wired directly into
    /// the connection by <c>audio-glue.ts</c> via <c>attachConnectionToOutput</c>,
    /// so this wrapper only carries control traffic (property polling, volume,
    /// interrupt, level / frequency-data readback) over the bridge.
    /// </para>
    /// <para>
    /// FMOD-only concepts (<see cref="AudioMixerGroup"/>, custom rolloff
    /// curves, reverb-zone behaviour) emit a one-time warning the first time
    /// they're observed as non-default and degrade to the nearest Web Audio
    /// approximation. See <c>COMPATIBILITY.md</c>'s "WebGL audio output
    /// limitations" section for the full fidelity matrix.
    /// </para>
    /// </remarks>
    internal sealed class WebAudioBackedOutput : IOutputController
    {
        private readonly IJsObject _sink;
        private readonly AudioSource? _audioSource;
        private readonly CancellationTokenSource _pollCts = new();
        private bool _disposed;

        // Cached active AudioListener so the polling loop doesn't pay a
        // scene-wide walk every frame. Re-resolved lazily when null (destroyed
        // or never set) OR when the cached one is no longer active+enabled
        // (e.g., cinematic camera takes over and the original is disabled).
        // Matches Unity's own "active listener" semantics — see
        // FindActiveListener below.
        private AudioListener? _cachedListener;

        // Fallback "user volume" used only when no AudioSource was supplied.
        // With a supplied source, SetVolume writes to `_audioSource.volume`
        // and the polling loop reads from there — single source of truth.
        private float _ownVolume = 1f;

        // One-shot warning latches — fire on first observation, no spam.
        private bool _destructionWarned;
        private bool _mixerGroupWarned;
        private bool _customCurveWarned;

        // Last values pushed to the sink. Used to skip no-op updates so the
        // bridge isn't flooded with redundant setVolume / setPosition calls.
        // float.NaN sentinel ensures the first push always fires.
        private float _lastVolume = float.NaN;
        private Vector3 _lastPosition = new(float.NaN, float.NaN, float.NaN);
        private float _lastSpatialBlend = float.NaN;
        private float _lastMinDistance = float.NaN;
        private float _lastMaxDistance = float.NaN;
        private float _lastPanStereo = float.NaN;
        private float _lastDopplerLevel = float.NaN;

        // AudioRolloffMode is an enum starting at 0; use a separate flag so we
        // can detect "never pushed" vs "pushed as Logarithmic (= 0 in JS)".
        private bool _rolloffPushed;
        private AudioRolloffMode _lastRolloffMode;

        /// <summary>
        /// Wraps an already-created JS sink handle (from
        /// <c>createWebAudioSink</c>) and an optional user-supplied
        /// <see cref="AudioSource"/>. When <paramref name="audioSource"/> is
        /// <c>null</c>, the polling loop pushes silent defaults (volume 1,
        /// spatialBlend 0, mono) — the sink stays audible but loses its
        /// spatial wiring.
        /// </summary>
        internal WebAudioBackedOutput(IJsObject sink, AudioSource? audioSource)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
            _audioSource = audioSource;
            // Push one round of properties at construction so the sink reflects
            // the supplied source's initial state immediately — without it,
            // playback would start at the sink's defaults until the first
            // poll tick fires (one frame later in Play Mode, never in Edit Mode).
            UpdateProperties();
            // Awaitable.NextFrameAsync only ticks during Play Mode; in Edit
            // Mode tests the loop would block forever. Tests call
            // UpdateProperties() directly.
            if (Application.isPlaying)
                _ = RunPollLoopAsync(_pollCts.Token);
        }

        // Per-frame polling. Pulled into its own method so a future tick
        // strategy (e.g. a hidden MonoBehaviour LateUpdate) can replace it
        // without touching the property-mirroring code.
        private async Awaitable RunPollLoopAsync(CancellationToken token)
        {
            try
            {
                while (true)
                {
                    await Awaitable.NextFrameAsync(token);
                    if (token.IsCancellationRequested)
                        return;
                    UpdateProperties();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when Close cancels the loop.
            }
            catch (Exception ex)
            {
                // Don't let a transient JS-bridge exception kill the polling
                // loop silently — surface it and continue is harder than
                // surface + bail, so we bail. Caller's next interaction with
                // the wrapper (e.g. Close) will dispose the sink cleanly.
                Debug.LogException(ex);
            }
        }

        /// <summary>
        /// Read the current AudioSource state and push any changed properties
        /// to the JS sink. Exposed as <c>internal</c> so Edit Mode tests can
        /// drive the property mirroring without standing up the Play Mode
        /// frame loop.
        /// </summary>
        internal void UpdateProperties()
        {
            if (_disposed)
                return;

            // Three states: (a) never supplied, (b) supplied + alive,
            // (c) supplied + destroyed mid-session. Unity's overloaded ==
            // returns true for both (a) and (c), so we use the unboxed
            // reference check `(object)_audioSource != null` to disambiguate
            // "we had something here once" from "we were never given one".
            bool hadSource = (object)_audioSource != null;
            bool sourceAlive = hadSource && _audioSource != null;

            if (hadSource && !sourceAlive && !_destructionWarned)
            {
                _destructionWarned = true;
                Debug.LogWarning(
                    "[ElevenLabs] OutputAudioSource was destroyed mid-session; "
                        + "audio output properties frozen for the remainder of the session."
                );
            }

            if (!sourceAlive)
            {
                // Never-supplied or destroyed: push silent defaults so the
                // sink stays audible at unit gain without spatial wiring.
                PushFloat("setVolume", _ownVolume, ref _lastVolume);
                PushFloat("setSpatialBlend", 0f, ref _lastSpatialBlend);
                return;
            }

            // Effective volume tracks _audioSource.volume so the user can
            // drive it from either Conversation.SetVolume or by writing to
            // AudioSource.volume directly — both paths land in the same field.
            PushFloat("setVolume", _audioSource.volume, ref _lastVolume);
            PushFloat("setSpatialBlend", _audioSource.spatialBlend, ref _lastSpatialBlend);
            PushFloat("setMinDistance", _audioSource.minDistance, ref _lastMinDistance);
            PushFloat("setMaxDistance", _audioSource.maxDistance, ref _lastMaxDistance);
            PushFloat("setPanStereo", _audioSource.panStereo, ref _lastPanStereo);
            PushFloat("setDopplerLevel", _audioSource.dopplerLevel, ref _lastDopplerLevel);

            Vector3 pos = ComputeWebAudioPosition(_audioSource.transform.position);
            if (pos != _lastPosition)
            {
                _lastPosition = pos;
                _sink.Call("setPosition", pos.x, pos.y, pos.z);
            }

            PushRolloffMode(_audioSource.rolloffMode);
            CheckUnsupported(_audioSource);
        }

        // Convert a Unity world-space source position into the listener-local,
        // Web-Audio-handed coordinate the JS sink's PannerNode expects. Web
        // Audio's AudioListener stays pinned at the origin facing default
        // (-Z forward, +Y up), so all of Unity's listener translation +
        // rotation is folded into the source position here.
        //
        // Two conversions in one:
        //   1. InverseTransformPoint maps the source from world into the
        //      listener's local frame (+X right, +Y up, +Z forward, Unity LH).
        //   2. Negating Z flips Unity's left-handed +Z-forward convention to
        //      Web Audio's right-handed -Z-forward convention. Without this
        //      flip a source straight ahead of the camera would render as
        //      behind the player.
        //
        // No-listener fallback: world position with Z flipped. Spatialization
        // will only be correct when the implicit listener is at origin facing
        // default, but the source stays audible — better than throwing or
        // silencing playback.
        private Vector3 ComputeWebAudioPosition(Vector3 sourceWorldPos)
        {
            RefreshListener();
            if (_cachedListener == null)
            {
                return new Vector3(sourceWorldPos.x, sourceWorldPos.y, -sourceWorldPos.z);
            }
            Vector3 localPos = _cachedListener.transform.InverseTransformPoint(sourceWorldPos);
            return new Vector3(localPos.x, localPos.y, -localPos.z);
        }

        // Find the "active" AudioListener, matching Unity's own audio engine:
        // exactly one enabled + active-in-hierarchy listener is expected per
        // scene; multiple already trigger Unity's own warning, so we silently
        // pick the first. Cache invalidates when the listener is destroyed
        // (Unity overloaded ==) or disabled (a cinematic camera takeover
        // pattern), so the next poll picks up the swap.
        private void RefreshListener()
        {
            if (_cachedListener != null && _cachedListener.isActiveAndEnabled)
                return;
            _cachedListener = UnityEngine
                .Object.FindObjectsByType<AudioListener>(FindObjectsSortMode.None)
                .FirstOrDefault(l => l.isActiveAndEnabled);
        }

        // Linear → JS "linear"; Logarithmic / Custom → JS "exponential"
        // (Web Audio's closest analog). Custom curves carry their own
        // AnimationCurve which Web Audio can't represent — one-time warning.
        // (Unity's AudioRolloffMode enum is just Linear / Logarithmic /
        // Custom; there's no Inverse variant, so no separate Inverse branch.)
        private void PushRolloffMode(AudioRolloffMode mode)
        {
            if (_rolloffPushed && mode == _lastRolloffMode)
                return;
            _rolloffPushed = true;
            _lastRolloffMode = mode;
            int code = mode == AudioRolloffMode.Linear ? 0 : 1;
            _sink.Call("setRolloffMode", code);
            if (mode == AudioRolloffMode.Custom && !_customCurveWarned)
            {
                _customCurveWarned = true;
                Debug.LogWarning(
                    "[ElevenLabs] AudioSource.rolloffMode is Custom but custom rolloff "
                        + "curves aren't supported on WebGL; falling back to logarithmic falloff."
                );
            }
        }

        private void CheckUnsupported(AudioSource src)
        {
            if (!_mixerGroupWarned && src.outputAudioMixerGroup != null)
            {
                _mixerGroupWarned = true;
                Debug.LogWarning(
                    "[ElevenLabs] AudioSource.outputAudioMixerGroup is set on a supplied "
                        + "OutputAudioSource but isn't supported on WebGL (FMOD-only concept). "
                        + "Ignoring."
                );
            }
        }

        private void PushFloat(string method, float value, ref float lastPushed)
        {
            // Mathf.Approximately uses a relative epsilon — sufficient for the
            // property cadence here (frame-driven user edits, not synthetic
            // tiny floats). float.NaN compares unequal to everything including
            // itself, so the first push always fires.
            if (!float.IsNaN(lastPushed) && Mathf.Approximately(value, lastPushed))
                return;
            lastPushed = value;
            _sink.Call(method, value);
        }

        // -------------------------------------------------------------------
        // IOutputController members.
        // -------------------------------------------------------------------

        // Audio flows JS-internal via attachConnectionToOutput → sink.playAudio,
        // so this should never fire in production. Mirrors BridgedOutputController.PushAudio.
        public void PushAudio(byte[] pcm) { }

        public Awaitable SetDevice(OutputDeviceConfig? config = null, FormatConfig? format = null)
        {
            // WebAudioContext sample rate is fixed at construction (matches
            // BridgedSession's negotiated output format). Device switching is
            // a browser-level concern not exposed by Web Audio; defer to v0.3.
            if (config != null && !string.IsNullOrEmpty(config.OutputDeviceId))
            {
                Debug.LogWarning(
                    "[ElevenLabs] WebAudioBackedOutput: per-source output device selection "
                        + "isn't supported on WebGL; the requested device id was ignored."
                );
            }
            if (format != null)
            {
                throw new NotSupportedException(
                    "Changing the output format after session start is not supported; "
                        + "restart the session with the new format instead."
                );
            }
            return CompletedAwaitable();
        }

        public void SetVolume(float volume)
        {
            float v = Mathf.Clamp01(volume);
            if (_audioSource != null)
            {
                // Single source of truth: writing the AudioSource lets the
                // user observe + read back the value, and the next poll picks
                // it up uniformly with manual `audioSource.volume = x` edits.
                _audioSource.volume = v;
            }
            else
            {
                _ownVolume = v;
            }
            // Push immediately so a synchronous caller (e.g. a test that
            // doesn't await a frame) sees the effect, not just on next poll.
            UpdateProperties();
        }

        public void Interrupt(int? resetDurationMs = null)
        {
            if (_disposed)
                return;
            // JS sink expects a number — no overload taking null. When the
            // caller didn't supply a duration, pick the same 2 s default the
            // SDK uses (mirrors MediaDeviceOutput.interrupt's default).
            int duration = resetDurationMs ?? 2000;
            _sink.Call("interrupt", duration);
        }

        public float GetVolume() => _disposed ? 0f : _sink.Call<float>("getVolume");

        public void GetByteFrequencyData(byte[] buffer)
        {
            if (_disposed || buffer == null || buffer.Length == 0)
                return;
            // JS sink contract (Bridge~/src/connection/web-audio-sink.ts):
            // `getByteFrequencyData(length: number): number[]` — allocates a
            // Uint8Array of `length` bytes, fills it from the analyser, and
            // returns `Array.from(buffer)`. The number-array shape is the one
            // Newtonsoft decodes as `byte[]`; returning the Uint8Array
            // directly serialises as `{"0":..,"1":..}` (object keyed by
            // index) which Newtonsoft does NOT decode as a byte array.
            byte[]? remote = _sink.Call<byte[]>("getByteFrequencyData", buffer.Length);
            if (remote == null)
                return;
            int n = remote.Length < buffer.Length ? remote.Length : buffer.Length;
            Array.Copy(remote, buffer, n);
        }

        public async Awaitable Close()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _pollCts.Cancel();
            }
            catch (ObjectDisposedException) { }
            try
            {
                await _sink.CallAsync("close");
            }
            finally
            {
                _sink.Dispose();
                _pollCts.Dispose();
            }
        }

        private static Awaitable CompletedAwaitable()
        {
            var source = new AwaitableCompletionSource();
            source.SetResult();
            return source.Awaitable;
        }
    }
}
