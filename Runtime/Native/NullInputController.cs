#nullable enable

using System;
using ElevenLabs.Agents;
using UnityEngine;

namespace ElevenLabs.Native
{
    /// <summary>
    /// No-op <see cref="IInputController"/> for text-only or test sessions on
    /// non-WebGL targets. Every method returns a completed <see cref="Awaitable"/>,
    /// the mute flag is honoured but never observed by any wire path, and the
    /// level/frequency queries report silence. Production native sessions use
    /// <see cref="UnityMicrophoneInput"/> instead; this remains the default
    /// for Edit Mode tests and for callers who want to disable audio capture.
    /// </summary>
    internal sealed class NullInputController : IInputController
    {
        // Mirrors the real controllers' contract: mute is a flag that survives
        // SetMuted(true) → IsMuted == true even though no audio actually flows.
        // Lets user-facing code (e.g. a Mute toggle UI) operate against the
        // null controller without behavioural surprises.
        public bool IsMuted { get; private set; }

#pragma warning disable CS0067 // Never fired — the null controller emits no audio. Required by IInputController.
        public event Action<byte[]>? AudioChunkAvailable;
#pragma warning restore CS0067

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
