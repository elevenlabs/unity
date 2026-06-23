#nullable enable

using System.Collections.Generic;
using ElevenLabs.Protocol;

namespace ElevenLabs.Agents
{
    /// <summary>Inputs to <see cref="Conversation.StartSessionAsync"/>.</summary>
    /// <remarks>
    /// Mirrors a curated subset of <c>Options</c> from
    /// <c>@elevenlabs/client</c>. Additional fields (client tools,
    /// callbacks-as-init, <c>SourceInfo</c> / <c>ToolMockConfig</c>) land in
    /// follow-up sub-tasks; see
    /// <c>Docs~/plans/v0.1-parity.md#10</c> for the deferral rationale.
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

        /// <summary>
        /// Per-session overrides for the agent's configuration — first message,
        /// language, TTS voice / stability / speed, prompt, etc. The
        /// <see cref="ConversationConfigOverride"/> type is generated from the
        /// AsyncAPI wire contract; future spec re-vendors widen this surface
        /// without a hand-rolled mirror.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each leaf field on the override tree is omitted from the handshake
        /// when <c>null</c> / empty, so partial overrides (e.g., a TTS voice
        /// swap without touching the prompt) cleanly survive the bridge.
        /// </para>
        /// <para>
        /// WebGL caveat — the upstream JS SDK only exposes the <c>agent</c>,
        /// <c>tts</c>, and <c>conversation</c> subtrees on its
        /// <c>SessionConfig.overrides</c>; <see cref="ConversationConfigOverride.Turn"/>
        /// is dropped on the bridged path and only takes effect once the
        /// native WebSocket transport lands (#9). Native + WebGL semantics
        /// otherwise match.
        /// </para>
        /// </remarks>
        public ConversationConfigOverride? Overrides { get; init; }

        /// <summary>
        /// Stable identifier for the end user on whose behalf the conversation
        /// is being held. Forwarded verbatim as <c>user_id</c> on the
        /// initiation event; the agent owner can join it against their own
        /// user records for analytics / per-user state.
        /// </summary>
        public string? UserId { get; init; }

        /// <summary>
        /// Free-form JSON merged into the <c>custom_llm_extra_body</c> field
        /// of the initiation event, forwarded to the configured LLM provider
        /// as request-body extras (e.g., provider-specific decoding controls).
        /// </summary>
        /// <remarks>
        /// Values are serialized via Newtonsoft.Json; any JSON-representable
        /// runtime type is accepted client-side. A <c>null</c> or empty
        /// dictionary is omitted from the handshake.
        /// </remarks>
        public IReadOnlyDictionary<string, object>? CustomLlmExtraBody { get; init; }
    }
}
