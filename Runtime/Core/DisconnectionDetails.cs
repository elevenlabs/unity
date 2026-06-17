#nullable enable

namespace ElevenLabs.Agents
{
    /// <summary>
    /// Reason a connection ended. Mirrors the <c>reason</c> discriminator on
    /// <c>DisconnectionDetails</c> from <c>@elevenlabs/client</c>.
    /// </summary>
    public enum DisconnectionReason
    {
        /// <summary>Transport-level failure (network error, abnormal close, etc.).</summary>
        Error,

        /// <summary>Agent-initiated end of conversation.</summary>
        Agent,

        /// <summary>User-initiated end of conversation (e.g. <c>Conversation.EndSession</c>).</summary>
        User,
    }

    /// <summary>
    /// Platform-agnostic context describing the underlying event that caused
    /// a disconnection. Mirrors <c>DisconnectionContext</c> from <c>@elevenlabs/client</c>.
    /// </summary>
    /// <param name="Type">Symbolic event name (e.g. <c>"close"</c>, <c>"error"</c>).</param>
    /// <param name="Reason">Free-form reason string, if the transport provided one.</param>
    /// <param name="Code">Numeric code (e.g. WebSocket close code), if applicable.</param>
    public sealed record DisconnectionContext(string Type, string? Reason = null, int? Code = null);

    /// <summary>
    /// Details delivered to <see cref="IConnection.OnDisconnect"/> when a
    /// connection ends. Flattens the JS union <c>DisconnectionDetails</c> into
    /// a single record; only fields relevant to <see cref="Reason"/> will be
    /// populated.
    /// </summary>
    /// <param name="Reason">Top-level reason discriminator.</param>
    /// <param name="Message">
    /// Human-readable failure message. Always set when
    /// <see cref="Reason"/> is <see cref="DisconnectionReason.Error"/>.
    /// </param>
    /// <param name="Context">
    /// Transport-level context for the disconnection. Set when
    /// <see cref="Reason"/> is <see cref="DisconnectionReason.Error"/> or
    /// <see cref="DisconnectionReason.Agent"/>.
    /// </param>
    /// <param name="CloseCode">WebSocket close code, if the transport provided one.</param>
    /// <param name="CloseReason">WebSocket close reason string, if any.</param>
    public sealed record DisconnectionDetails(
        DisconnectionReason Reason,
        string? Message = null,
        DisconnectionContext? Context = null,
        int? CloseCode = null,
        string? CloseReason = null
    );
}
