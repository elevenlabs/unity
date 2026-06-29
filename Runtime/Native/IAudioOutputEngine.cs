#nullable enable

using System;
using ElevenLabs.Agents;

namespace ElevenLabs.Native
{
    /// <summary>
    /// Isolates the Unity-API surface area for native audio output —
    /// <see cref="UnityEngine.AudioClip"/> creation, the
    /// <see cref="UnityEngine.AudioClip.PCMReaderCallback"/> hand-off, and
    /// <see cref="UnityEngine.AudioSource.Play"/> / <see cref="UnityEngine.AudioSource.Stop"/>
    /// — behind a seam <see cref="UnityAudioSourceOutput"/> can drive without
    /// caring whether the implementation is the real Unity engine or an
    /// Edit-Mode fake.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The motivating bugs are in
    /// <c>Docs~/plans/audio-output-testability.md</c>: Unity's streaming
    /// <see cref="UnityEngine.AudioClip"/> has at least three behaviours the
    /// SDK depends on that aren't expressible in Edit Mode today (synchronous
    /// pre-fill inside <see cref="UnityEngine.AudioClip.Create(string, int, int, int, bool, UnityEngine.AudioClip.PCMReaderCallback)"/>,
    /// silence baked into the streaming buffer when the ring is empty during
    /// pre-fill, and a coarse ~3 Hz <c>PCMReaderCallback</c> cadence
    /// regardless of DSP buffer size). The interface lets a fake simulate
    /// any of those scenarios deterministically.
    /// </para>
    /// <para>
    /// Lifecycle: construct the engine on the main thread (it may call Unity
    /// APIs to set up an <see cref="UnityEngine.AudioSource"/> / host
    /// <see cref="UnityEngine.GameObject"/>), then call <see cref="Start"/>
    /// once the host has enough audio queued, and <see cref="IDisposable.Dispose"/>
    /// at session teardown. <see cref="Stop"/> halts playback but leaves the
    /// engine usable for a follow-up <see cref="Start"/>; <see cref="IDisposable.Dispose"/>
    /// tears everything down (including restoring a supplied
    /// <see cref="UnityEngine.AudioSource"/>'s pre-session state).
    /// </para>
    /// </remarks>
    internal interface IAudioOutputEngine : IDisposable
    {
        /// <summary>
        /// <c>true</c> while the engine can accept <see cref="Start"/> /
        /// <see cref="Stop"/> calls and apply volume changes. Returns
        /// <c>false</c> after a supplied <see cref="UnityEngine.AudioSource"/>
        /// is observed as destroyed mid-session (in which case
        /// <see cref="UnityAudioSourceOutput"/> stops feeding samples and
        /// the destruction warning fires exactly once).
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Approximate number of input-rate samples the engine drains
        /// synchronously inside <see cref="Start"/> before returning.
        /// <see cref="UnityAudioSourceOutput"/> defers <see cref="Start"/>
        /// until the ring holds at least this many samples (plus a small
        /// DSP-buffer margin) so the pre-fill lands on real audio instead
        /// of silence-filling Unity's streaming buffer ahead of the speaker.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The streaming-<see cref="UnityEngine.AudioClip"/> path
        /// (<see cref="UnityAudioOutputEngine"/>) reports ~12,800 — the
        /// empirically-measured pre-fill at 16 kHz input on a Unity 6
        /// default audio config (DSP buffer 256 × 4 at 48 kHz output;
        /// see <c>Docs~/unity-issues/streaming-audioclip-prefill-depth.md</c>).
        /// The <see cref="IAudioGenerator"/> path
        /// (<see cref="UnityGeneratorAudioOutputEngine"/>) reports 0 —
        /// per the PlayMode characterization test, <c>Process</c> fires
        /// strictly asynchronously after <see cref="UnityEngine.AudioSource.Play"/>,
        /// so the controller can fire <see cref="Start"/> on the first
        /// chunk above DSP-buffer size without risking silence-fill.
        /// </para>
        /// <para>
        /// Engines that drain a fixed pre-fill regardless of input rate
        /// (Unity's streaming clip behaves this way at typical agent input
        /// rates) report a constant sample count; engines that scale with
        /// rate should overestimate so the controller's gate remains a
        /// safety bound. The controller adds a small margin (~one DSP
        /// buffer's worth of input samples) before triggering
        /// <see cref="Start"/>, so a slight underestimate here only
        /// shortens the safety cushion — it doesn't break correctness.
        /// </para>
        /// </remarks>
        int SyncPrefillSampleCount { get; }

        /// <summary>
        /// Pass-through to the underlying playback destination's volume. In
        /// production this writes <see cref="UnityEngine.AudioSource.volume"/>
        /// directly; fakes can record the value for test assertions.
        /// Returns <c>0</c> when no destination exists (e.g.,
        /// <see cref="IsAvailable"/> is <c>false</c>).
        /// </summary>
        float Volume { get; set; }

        /// <summary>
        /// Begin streaming playback.
        /// </summary>
        /// <param name="format">Negotiated agent-output format. The engine
        /// uses <see cref="FormatConfig.SampleRate"/> as the streaming clip's
        /// frequency.</param>
        /// <param name="drainCallback">Invoked by the engine whenever it needs
        /// more samples; returns the number of real samples written. The
        /// engine silence-fills any remaining slots before handing the buffer
        /// back to the audio system.</param>
        /// <remarks>
        /// On real Unity, calling <see cref="Start"/> synchronously fires
        /// <paramref name="drainCallback"/> enough times to fill the
        /// streaming buffer ahead (~12,800 clip-rate samples on a Unity 6
        /// default config, DSP buffer 256 × 4 at 48 kHz) before returning.
        /// Subsequent fires happen on Unity's audio thread at roughly 3 Hz
        /// — regardless of DSP buffer size. Fakes can simulate any
        /// pre-fill count and cadence via <see cref="Tick"/>.
        /// </remarks>
        void Start(FormatConfig format, Func<float[], int> drainCallback);

        /// <summary>
        /// Halt playback. The engine remains usable for a follow-up
        /// <see cref="Start"/> (which will install a fresh streaming clip
        /// and re-trigger the synchronous pre-fill).
        /// </summary>
        void Stop();

        /// <summary>
        /// Drive any pending engine work forward by <paramref name="elapsedSeconds"/>.
        /// </summary>
        /// <remarks>
        /// Production: no-op — Unity's audio thread runs the
        /// <c>PCMReaderCallback</c> on its own cadence and doesn't need a
        /// host-side time tick. Fakes use this to advance simulated wall
        /// clock and fire <c>drainCallback</c> as the simulated playback
        /// head consumes samples, so tests can step time deterministically
        /// without spinning a real audio thread.
        /// </remarks>
        void Tick(double elapsedSeconds);
    }
}
