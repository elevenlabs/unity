#nullable enable

using System.Collections.Generic;

namespace ElevenLabs.Agents
{
    /// <summary>Inputs to <see cref="Conversation.StartSessionAsync"/>.</summary>
    /// <remarks>
    /// Mirrors a curated subset of <c>Options</c> from
    /// <c>@elevenlabs/client</c>. Additional fields (conversation config
    /// overrides, client tools, callbacks-as-init) land in follow-up Phase 4
    /// sub-tasks alongside the message router and client-tool dispatch work.
    /// </remarks>
    public sealed record ConversationOptions
    {
        /// <summary>Public agent identifier. Mutually exclusive with <see cref="SignedUrl"/> and <see cref="ConversationToken"/>.</summary>
        public string? AgentId { get; init; }

        /// <summary>Pre-signed WebSocket URL for private agents. Mutually exclusive with <see cref="AgentId"/>.</summary>
        public string? SignedUrl { get; init; }

        /// <summary>Short-lived WebRTC conversation token. Required when <see cref="ConnectionType"/> is <see cref="Agents.ConnectionType.WebRTC"/>.</summary>
        public string? ConversationToken { get; init; }

        /// <summary>Transport selection. Defaults to <see cref="Agents.ConnectionType.WebSocket"/>.</summary>
        public ConnectionType ConnectionType { get; init; } = ConnectionType.WebSocket;

        /// <summary>Initial microphone device selection. <c>null</c> picks the platform default.</summary>
        public InputDeviceConfig? Input { get; init; }

        /// <summary>Initial speaker / sink device selection. <c>null</c> picks the platform default.</summary>
        public OutputDeviceConfig? Output { get; init; }

        /// <summary>
        /// Per-session variables substituted into the agent's templated system
        /// prompt and first message. Values must be <c>string</c>, a numeric
        /// type, or <c>bool</c> — the server's allow-list mirrors upstream
        /// <c>@elevenlabs/client</c>'s <c>Record&lt;string, string | number | boolean&gt;</c>.
        /// Other runtime types serialize through Newtonsoft.Json but are
        /// rejected server-side.
        /// </summary>
        /// <remarks>
        /// Forwarded only at session start (no mid-session update path); a
        /// <c>null</c> or empty dictionary is omitted from the handshake.
        /// </remarks>
        public IReadOnlyDictionary<string, object>? DynamicVariables { get; init; }
    }
}
