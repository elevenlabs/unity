#nullable enable

using UnityEngine;

namespace ElevenLabs.Agents
{
    /// <summary>
    /// Audio sink fed by the active <see cref="IConnection"/>. Mirrors
    /// <c>OutputController</c> from <c>@elevenlabs/client</c>.
    /// </summary>
    /// <remarks>
    /// On WebGL (default mode) the output is wired directly to the connection
    /// inside JavaScript and audio plays through the browser; on native, the
    /// C# <see cref="Conversation"/> routes incoming audio chunks into a Unity
    /// <c>AudioSource</c>.
    /// </remarks>
    internal interface IOutputController
    {
        /// <summary>Tear down the audio playback pipeline. Idempotent.</summary>
        Awaitable Close();

        /// <summary>
        /// Push a chunk of 16-bit little-endian PCM samples to the output
        /// pipeline. Called by <see cref="Conversation"/> after decoding the
        /// wire payload from each <c>audio</c> event.
        /// </summary>
        /// <remarks>
        /// The Bridged (WebGL default-mode) implementation no-ops — audio
        /// flows JS-internal between connection and output via
        /// <c>attachConnectionToOutput</c>, and the wire payload arrives at C#
        /// with the base64 field stripped, so <paramref name="pcm"/> is empty.
        /// Native implementations decode <c>int16-LE → float</c> and write
        /// into the playback ring buffer for the audio thread to consume.
        /// </remarks>
        void PushAudio(byte[] pcm);

        /// <summary>Switch output device or override the output format.</summary>
        /// <param name="config">
        /// Device selection. Pass <c>null</c> to keep the current device.
        /// </param>
        /// <param name="format">
        /// Audio format override. Pass <c>null</c> to keep the format negotiated
        /// at session start.
        /// </param>
        Awaitable SetDevice(OutputDeviceConfig? config = null, FormatConfig? format = null);

        /// <summary>Set playback gain. <paramref name="volume"/> is clamped to <c>[0, 1]</c> by the implementation.</summary>
        void SetVolume(float volume);

        /// <summary>
        /// Interrupt agent playback immediately. The output controller stops
        /// the in-flight chunk and flushes any buffered audio.
        /// </summary>
        /// <param name="resetDurationMs">
        /// Optional duration (ms) over which to fade out before resetting. Pass
        /// <c>null</c> for an immediate cut.
        /// </param>
        void Interrupt(int? resetDurationMs = null);

        /// <summary>Current output audio level as a scalar in <c>[0, 1]</c>.</summary>
        float GetVolume();

        /// <summary>
        /// Write byte-frequency data (0-255) into <paramref name="buffer"/>, focused
        /// on the human voice range (100-8000 Hz). The buffer length determines the
        /// number of frequency bands returned.
        /// </summary>
        void GetByteFrequencyData(byte[] buffer);
    }
}
