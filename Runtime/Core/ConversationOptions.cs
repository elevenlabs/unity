#nullable enable

using System.Collections.Generic;
using ElevenLabs.Protocol;
using UnityEngine;

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
        /// Optional <see cref="AudioSource"/> to play agent audio through.
        /// When set, the platform output controller binds to this source instead
        /// of creating its own hidden host GameObject — letting the caller
        /// control spatialisation, mixer routing, rolloff curves, and lifetime.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Honored on native (WebSocket transport) and on WebGL (WebSocket
        /// transport only — WebRTC's audio path goes through LiveKit and ignores
        /// the field until the v0.3 <c>WebRTCAudioAdapter</c> lands).
        /// </para>
        /// <para>
        /// On native, audio plays through the supplied <see cref="AudioSource"/>
        /// with full FMOD fidelity. SDK-owned overwrites: <c>clip</c>,
        /// <c>loop</c>, and <c>volume</c> are captured at session start and
        /// restored on <see cref="Conversation.EndSessionAsync"/>. Everything
        /// else (<c>spatialBlend</c>, <c>outputAudioMixerGroup</c>, rolloff
        /// curves, transform position) is preserved untouched.
        /// </para>
        /// <para>
        /// On WebGL the binding goes through a parallel Web Audio graph
        /// (<c>WebAudioBackedOutput</c>) — Unity has no scriptable audio
        /// pipeline on WebGL, so the supplied <see cref="AudioSource"/> is
        /// decorative: the SDK mirrors a curated subset of its properties
        /// (volume, listener-local position, spatial blend, min/max distance,
        /// rolloff mode, panStereo, dopplerLevel) onto the Web Audio graph once
        /// per Unity frame, but never streams samples through the source itself.
        /// FMOD-only concepts (<see cref="AudioMixerGroup"/>, reverb zones,
        /// custom rolloff <see cref="AnimationCurve"/>s, effect bypass) emit a
        /// one-time warning and degrade to the nearest Web Audio approximation.
        /// See <c>COMPATIBILITY.md</c>'s "WebGL audio output limitations"
        /// section for the full fidelity matrix and rationale.
        /// </para>
        /// <para>
        /// If the supplied source is destroyed mid-session (scene unload,
        /// prefab swap), the SDK logs a single warning and suppresses further
        /// playback for the rest of the session; the connection stays open.
        /// </para>
        /// </remarks>
        public AudioSource? OutputAudioSource { get; init; }

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

        /// <summary>
        /// When <c>true</c>, the SDK forwards unknown wire events that arrive
        /// ahead of an SDK refresh to <see cref="UnityEngine.Debug.Log"/> with
        /// an <c>[ElevenLabs debug]</c> prefix. Mirrors the unknown-wire-event
        /// arm of the JS SDK's <c>onDebug</c> callback; defaults to <c>false</c>
        /// so production builds stay quiet.
        /// </summary>
        /// <remarks>
        /// The JS SDK exposes <c>onDebug</c> as a callback because the wire
        /// shape of these payloads is opaque diagnostic JSON; the Unity SDK
        /// routes them through Unity's existing log pipeline instead so game
        /// code doesn't bind to an unstable surface. Read by
        /// <see cref="Conversation"/> at construction time — toggling after
        /// <see cref="Conversation.StartSessionAsync"/> has no effect.
        /// </remarks>
        public bool EnableDebugLogging { get; init; }
    }
}
