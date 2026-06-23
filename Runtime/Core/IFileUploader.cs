#nullable enable

using UnityEngine;

namespace ElevenLabs.Agents
{
    /// <summary>
    /// HTTP side-channel for uploading files alongside an active conversation.
    /// Returns the server-assigned <c>file_id</c> consumable by
    /// <see cref="Conversation.SendMultimodalMessage"/>. Mirrors the
    /// <c>uploadFile</c> helper in <c>@elevenlabs/client</c>.
    /// </summary>
    /// <remarks>
    /// Implemented per (platform × transport) the same way <see cref="IConnection"/>
    /// is, but kept as a separate abstraction so the WebSocket transport class
    /// doesn't have to carry HTTP plumbing. The launcher constructs the
    /// uploader after the connection handshake — once the conversation id is
    /// known — and hands both into <see cref="Conversation"/>.
    /// </remarks>
    internal interface IFileUploader
    {
        /// <summary>
        /// Upload <paramref name="bytes"/> as a multipart <c>file</c> field to
        /// <c>POST /v1/convai/conversations/{id}/files</c>. Returns the
        /// <c>file_id</c> the server assigns.
        /// </summary>
        /// <param name="bytes">Raw file bytes.</param>
        /// <param name="mimeType">MIME type the server should associate with the upload (e.g. <c>image/png</c>).</param>
        /// <param name="filename">
        /// Filename to attach in the multipart form. <c>null</c> defaults to
        /// <c>upload.&lt;ext&gt;</c> derived from <paramref name="mimeType"/>,
        /// matching the JS SDK.
        /// </param>
        Awaitable<string> UploadFileAsync(byte[] bytes, string mimeType, string? filename = null);
    }
}
