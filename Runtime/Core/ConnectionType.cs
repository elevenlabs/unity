#nullable enable

namespace ElevenLabs.Agents
{
    /// <summary>Wire transport for a <see cref="Conversation"/>.</summary>
    /// <remarks>
    /// Mirrors the <c>connectionType</c> discriminator that
    /// <c>@elevenlabs/client</c>'s <c>determineConnectionType</c> resolves to.
    /// </remarks>
    public enum ConnectionType
    {
        /// <summary>JSON-over-WebSocket transport — default, broadest device support.</summary>
        WebSocket,

        /// <summary>WebRTC transport — lower-latency audio, narrower platform support.</summary>
        WebRTC,
    }
}
