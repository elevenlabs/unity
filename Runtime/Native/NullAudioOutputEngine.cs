#nullable enable

using System;
using ElevenLabs.Agents;

namespace ElevenLabs.Native
{
    /// <summary>
    /// No-op <see cref="IAudioOutputEngine"/> for Edit-Mode tests that
    /// construct <see cref="UnityAudioSourceOutput"/> directly to exercise
    /// the ring / decode / volume-math paths without standing up a real
    /// <see cref="UnityEngine.AudioSource"/> + streaming
    /// <see cref="UnityEngine.AudioClip"/>. Lets the host stay
    /// engine-agnostic — <see cref="IAudioOutputEngine"/> is non-null in
    /// every code path — without sprinkling null checks through the host.
    /// </summary>
    /// <remarks>
    /// Production code routes through <see cref="UnityAudioOutputEngine"/>
    /// via <see cref="UnityAudioSourceOutput.CreateAsync"/>; this type is
    /// only wired up by the bare
    /// <see cref="UnityAudioSourceOutput(FormatConfig)"/> test constructor.
    /// </remarks>
    internal sealed class NullAudioOutputEngine : IAudioOutputEngine
    {
        public bool IsAvailable => true;

        public float Volume { get; set; } = 1f;

        // No real playback subsystem — the drain callback never fires, so
        // the engine drains nothing synchronously inside Start. Bare
        // Edit-Mode tests using the bare-constructor controller see the
        // threshold gate collapse to its DSP-buffer margin, which is fine
        // because those tests drive ReadFromRing directly anyway.
        public int SyncPrefillSampleCount => 0;

        public void Start(FormatConfig format, Func<float[], int> drainCallback)
        {
            // No real playback subsystem — drainCallback is never invoked.
            // Tests that need to exercise the drain path call
            // UnityAudioSourceOutput.ReadFromRing directly.
        }

        public void Stop() { }

        public void Tick(double elapsedSeconds) { }

        public void Dispose() { }
    }
}
