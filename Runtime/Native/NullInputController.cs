#nullable enable

using ElevenLabs.Agents;
using UnityEngine;

namespace ElevenLabs.Native
{
    /// <summary>
    /// No-op <see cref="IInputController"/> used by <see cref="NativeSessionLauncher"/>
    /// before <c>UnityMicrophoneInput</c> (#9c) lands. Lets the launcher build a
    /// <see cref="Conversation"/> for text-only sessions on non-WebGL targets:
    /// every method returns a completed <see cref="Awaitable"/>, the mute flag
    /// is honoured but never observed by any wire path, and the level/frequency
    /// queries report silence.
    /// </summary>
    internal sealed class NullInputController : IInputController
    {
        // Mirrors the real controllers' contract: mute is a flag that survives
        // SetMuted(true) → IsMuted == true even though no audio actually flows.
        // Lets user-facing code (e.g. a Mute toggle UI) operate against the
        // null controller without behavioural surprises.
        public bool IsMuted { get; private set; }

        public Awaitable Close() => CompletedAwaitable();

        public Awaitable SetDevice(InputDeviceConfig? config = null, FormatConfig? format = null) =>
            CompletedAwaitable();

        public Awaitable SetMuted(bool isMuted)
        {
            IsMuted = isMuted;
            return CompletedAwaitable();
        }

        public float GetVolume() => 0f;

        public void GetByteFrequencyData(byte[] buffer)
        {
            if (buffer == null)
                return;
            // No mic, no signal — leave the buffer at silence (zeros). Skipping
            // the clear would let stale values from a prior call leak through.
            System.Array.Clear(buffer, 0, buffer.Length);
        }

        private static Awaitable CompletedAwaitable()
        {
            var source = new AwaitableCompletionSource();
            source.SetResult();
            return source.Awaitable;
        }
    }
}
