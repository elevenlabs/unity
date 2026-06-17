#nullable enable

using UnityEngine;

namespace ElevenLabs.Agents
{
    /// <summary>
    /// Audio source feeding the active <see cref="IConnection"/>. Mirrors
    /// <c>InputController</c> from <c>@elevenlabs/client</c>.
    /// </summary>
    /// <remarks>
    /// Audio routing differs by platform: on WebGL the input controller is
    /// wired directly into the connection inside JavaScript (audio bytes
    /// never cross the bridge); on native, the input emits PCM frames that
    /// the C# <see cref="Conversation"/> wraps as <c>UserAudioChunk</c>.
    /// </remarks>
    public interface IInputController
    {
        /// <summary>Current mute state. Updated synchronously by <see cref="SetMuted"/>.</summary>
        bool IsMuted { get; }

        /// <summary>Tear down the audio capture pipeline. Idempotent.</summary>
        Awaitable Close();

        /// <summary>Switch microphone or override the input format.</summary>
        /// <param name="config">
        /// Device selection. Pass <c>null</c> to keep the current device.
        /// </param>
        /// <param name="format">
        /// Audio format override. Pass <c>null</c> to keep the format negotiated
        /// at session start.
        /// </param>
        Awaitable SetDevice(InputDeviceConfig? config = null, FormatConfig? format = null);

        /// <summary>Mute or unmute the microphone. While muted, the controller emits silence.</summary>
        Awaitable SetMuted(bool isMuted);

        /// <summary>Current input audio level as a scalar in <c>[0, 1]</c>.</summary>
        float GetVolume();

        /// <summary>
        /// Write byte-frequency data (0-255) into <paramref name="buffer"/>, focused
        /// on the human voice range (100-8000 Hz). The buffer length determines the
        /// number of frequency bands returned.
        /// </summary>
        void GetByteFrequencyData(byte[] buffer);
    }
}
