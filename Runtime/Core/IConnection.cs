#nullable enable

using System;
using ElevenLabs.Protocol;
using UnityEngine;

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

        /// <summary>
        /// Upload a file alongside the live conversation via the
        /// <c>POST /v1/convai/conversations/{id}/files</c> HTTP side-channel.
        /// The returned <c>file_id</c> can be passed to
        /// <see cref="Conversation.SendMultimodalMessage"/>.
        /// </summary>
        /// <param name="bytes">Raw file bytes.</param>
        /// <param name="mimeType">MIME type the server should associate with the upload (e.g. <c>image/png</c>).</param>
        /// <param name="filename">
        /// Filename to attach in the multipart form. <c>null</c> defaults to
        /// <c>upload.&lt;ext&gt;</c> derived from <paramref name="mimeType"/>,
        /// matching the JS SDK.
        /// </param>
        Awaitable<string> UploadFileAsync(byte[] bytes, string mimeType, string? filename = null);

        /// <summary>Close the underlying transport. Idempotent.</summary>
        void Close();
    }
}
