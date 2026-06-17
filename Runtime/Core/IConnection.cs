#nullable enable

using System;
using ElevenLabs.Protocol;

namespace ElevenLabs.Agents
{
    /// <summary>
    /// Wire transport for an agent conversation. Mirrors
    /// <c>BaseConnection</c> from <c>@elevenlabs/client</c>; one implementation
    /// exists per (platform × transport) — WebSocket and WebRTC on WebGL via
    /// the JS SDK, native System.Net implementations on every other platform.
    /// </summary>
    /// <remarks>
    /// The C# <see cref="Conversation"/> owns subscriptions to <see cref="OnMessage"/>,
    /// <see cref="OnDisconnect"/>, and <see cref="OnModeChange"/> as standard
    /// .NET multicast events — user code wires up its own callbacks against the
    /// <see cref="Conversation"/> surface, not against the connection directly.
    /// </remarks>
    internal interface IConnection
    {
        /// <summary>Server-assigned conversation identifier.</summary>
        string ConversationId { get; }

        /// <summary>Audio format the input controller must produce for this connection.</summary>
        FormatConfig InputFormat { get; }

        /// <summary>Audio format the output controller will receive from this connection.</summary>
        FormatConfig OutputFormat { get; }

        /// <summary>Fired for every parsed incoming event (audio events, transcripts, tool calls, …).</summary>
        event Action<IncomingSocketEvent> OnMessage;

        /// <summary>Fired exactly once when the connection ends, regardless of reason.</summary>
        event Action<DisconnectionDetails> OnDisconnect;

        /// <summary>Fired when the conversation flips between speaking and listening.</summary>
        event Action<Mode> OnModeChange;

        /// <summary>Send a typed outgoing event over the wire.</summary>
        /// <param name="message">Outgoing event (e.g. <c>UserMessage</c>, <c>UserAudioChunk</c>, <c>Pong</c>).</param>
        void Send(OutgoingSocketEvent message);

        /// <summary>Close the underlying transport. Idempotent.</summary>
        void Close();
    }
}
