#nullable enable

using ElevenLabs.Agents;
using UnityEngine;

namespace ElevenLabs.Native
{
    /// <summary>
    /// No-op <see cref="IOutputController"/> used by <see cref="NativeSessionLauncher"/>
    /// before <c>UnityAudioSourceOutput</c> (#9d) lands. The launcher hands this
    /// to <see cref="Conversation"/> so text-only sessions on non-WebGL targets
    /// can route incoming agent audio events through the C# pipeline without
    /// crashing — playback is simply dropped.
    /// </summary>
    internal sealed class NullOutputController : IOutputController
    {
        public Awaitable Close() => CompletedAwaitable();

        public void PushAudio(byte[] pcm) { }

        public Awaitable SetDevice(
            OutputDeviceConfig? config = null,
            FormatConfig? format = null
        ) => CompletedAwaitable();

        public void SetVolume(float volume) { }

        public void Interrupt(int? resetDurationMs = null) { }

        public float GetVolume() => 0f;

        public void GetByteFrequencyData(byte[] buffer)
        {
            if (buffer == null)
                return;
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
