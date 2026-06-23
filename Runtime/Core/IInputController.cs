#nullable enable

using System;
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
    /// never cross the bridge); on native, the input emits PCM frames via
    /// <see cref="AudioChunkAvailable"/> and the C# <see cref="Conversation"/>
    /// wraps each chunk as <c>UserAudioChunk</c>.
    /// </remarks>
    internal interface IInputController
    {
        /// <summary>Current mute state. Updated synchronously by <see cref="SetMuted"/>.</summary>
        bool IsMuted { get; }

        /// <summary>
        /// Raised when the controller has produced a chunk of 16-bit little-endian
        /// PCM samples to forward to the agent. The payload is the raw PCM bytes
        /// at the negotiated <see cref="FormatConfig.SampleRate"/>; the
        /// <see cref="Conversation"/> base64-encodes them into a
        /// <c>UserAudioChunk</c> wire message.
        /// </summary>
        /// <remarks>
        /// The Bridged (WebGL) implementation never fires this — audio flows
        /// JS-internal between input and connection via
        /// <c>attachInputToConnection</c>. Native implementations fire it on
        /// the Unity main thread at the SDK's expected chunk cadence
        /// (~25 ms). While muted, the chunk payload is silence (all zeros)
        /// so the wire cadence stays uniform — matches the JS worklet's
        /// behaviour.
        /// </remarks>
        event Action<byte[]>? AudioChunkAvailable;

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
