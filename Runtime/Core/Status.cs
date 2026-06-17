#nullable enable

namespace ElevenLabs.Agents
{
    /// <summary>Lifecycle state of a <see cref="Conversation"/>.</summary>
    /// <remarks>
    /// Mirrors <c>Status</c> from <c>@elevenlabs/client</c>. State transitions:
    /// <see cref="Disconnected"/> → <see cref="Connecting"/> → <see cref="Connected"/>
    /// → <see cref="Disconnecting"/> → <see cref="Disconnected"/>.
    /// </remarks>
    public enum Status
    {
        /// <summary>No active session; <see cref="Conversation.StartSessionAsync"/> has not been called or has fully torn down.</summary>
        Disconnected,

        /// <summary>Session start is in flight: transport handshake, audio device acquisition, conversation initiation handshake.</summary>
        Connecting,

        /// <summary>Session is open and exchanging events with the agent.</summary>
        Connected,

        /// <summary>Session teardown is in flight; transport and audio are being released.</summary>
        Disconnecting,
    }
}
