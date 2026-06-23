#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace ElevenLabs.Agents
{
    /// <summary>
    /// <see cref="IFileUploader"/> implementation that POSTs a multipart
    /// <c>file</c> field to <c>{origin}/v1/convai/conversations/{id}/files</c>
    /// via <see cref="UnityWebRequest"/>. Used by both the native launcher
    /// and the bridged WebGL launcher — on WebGL, <see cref="UnityWebRequest"/>
    /// is backed by <c>XMLHttpRequest</c>, so the same code path works
    /// wherever Unity can issue an HTTP POST (assuming the server returns
    /// the necessary CORS headers, which the JS SDK's <c>fetch</c>-based
    /// helper relies on too).
    /// </summary>
    /// <remarks>
    /// Held by <see cref="Conversation"/> for the lifetime of the session.
    /// The origin + conversation id are captured at construction so each
    /// upload is a single allocation + POST without re-parsing the original
    /// <see cref="ConversationOptions"/>.
    /// </remarks>
    internal sealed class HttpFileUploader : IFileUploader
    {
        // HTTPS side-channel for file uploads, mirroring
        // @elevenlabs/client utils/uploadFile.js (`POST {origin}/v1/convai/conversations/{id}/files`).
        // The default matches HTTPS_API_ORIGIN there; SignedUrl deployments
        // get their host carried through via DeriveHttpsOrigin so a custom
        // backend stays addressable.
        internal const string DefaultHttpsOrigin = "https://api.elevenlabs.io";
        private const string FilesPathPrefix = "/v1/convai/conversations/";
        private const string FilesPathSuffix = "/files";

        private readonly string _httpsOrigin;
        private readonly string _conversationId;

        internal HttpFileUploader(string httpsOrigin, string conversationId)
        {
            if (string.IsNullOrEmpty(httpsOrigin))
                throw new ArgumentException("HTTPS origin must be non-empty.", nameof(httpsOrigin));
            if (string.IsNullOrEmpty(conversationId))
                throw new ArgumentException(
                    "Conversation id must be non-empty.",
                    nameof(conversationId)
                );
            _httpsOrigin = httpsOrigin;
            _conversationId = conversationId;
        }

        // HTTPS origin for the upload side-channel. Mirrors uploadFile.js's
        // scheme rewrite: `wss://` → `https://`, `ws://` → `http://`. When
        // SignedUrl is set we carry its host through so custom deployments
        // stay addressable; AgentId-only flows hit the public API.
        internal static string DeriveHttpsOrigin(ConversationOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.SignedUrl))
                return DefaultHttpsOrigin;
            var parsed = new Uri(options.SignedUrl);
            string scheme = parsed.Scheme switch
            {
                "wss" or "https" => "https",
                "ws" or "http" => "http",
                _ => throw new ArgumentException(
                    $"Unsupported scheme '{parsed.Scheme}' in SignedUrl '{options.SignedUrl}'.",
                    nameof(options)
                ),
            };
            // Authority includes host + port (when non-default); path/query are
            // dropped intentionally — the side-channel hits its own resource.
            return $"{scheme}://{parsed.Authority}";
        }

        // Builds `{origin}/v1/convai/conversations/{id}/files`. ConversationId
        // is escaped because the server treats it as a path segment; in
        // practice ids are URL-safe, but we don't rely on it.
        internal static string BuildUploadUrl(string httpsOrigin, string conversationId)
        {
            if (string.IsNullOrEmpty(httpsOrigin))
                throw new ArgumentException("HTTPS origin must be non-empty.", nameof(httpsOrigin));
            if (string.IsNullOrEmpty(conversationId))
                throw new ArgumentException(
                    "Conversation id must be non-empty.",
                    nameof(conversationId)
                );
            return string.Concat(
                httpsOrigin,
                FilesPathPrefix,
                Uri.EscapeDataString(conversationId),
                FilesPathSuffix
            );
        }

        // Mirrors uploadFile.js's `upload.${(mime || "image/png").split("/").pop()?.split("+")[0]}`
        // — strips the parameter subtype suffix (e.g. `svg+xml` → `svg`) so the
        // resulting filename is a plain `upload.<ext>`.
        internal static string DeriveDefaultFilename(string? mimeType)
        {
            string safe = string.IsNullOrEmpty(mimeType) ? "image/png" : mimeType!;
            int slashIdx = safe.LastIndexOf('/');
            string ext = slashIdx >= 0 ? safe.Substring(slashIdx + 1) : safe;
            int plusIdx = ext.IndexOf('+');
            if (plusIdx >= 0)
                ext = ext.Substring(0, plusIdx);
            if (string.IsNullOrEmpty(ext))
                ext = "bin";
            return $"upload.{ext}";
        }

        public async Awaitable<string> UploadFileAsync(
            byte[] bytes,
            string mimeType,
            string? filename = null
        )
        {
            if (bytes == null)
                throw new ArgumentNullException(nameof(bytes));
            if (string.IsNullOrEmpty(mimeType))
                throw new ArgumentException("MIME type must be non-empty.", nameof(mimeType));

            string resolvedFilename = string.IsNullOrEmpty(filename)
                ? DeriveDefaultFilename(mimeType)
                : filename!;
            string url = BuildUploadUrl(_httpsOrigin, _conversationId);

            // UnityWebRequest.SendWebRequest is documented to require the main
            // thread; the marshal is invisible to callers already on it and
            // covers paths where audio threads could fan in.
            await Awaitable.MainThreadAsync();

            var sections = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("file", bytes, resolvedFilename, mimeType),
            };
            using var request = UnityWebRequest.Post(url, sections);
            await request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                string errMsg = !string.IsNullOrEmpty(request.error)
                    ? request.error
                    : (request.downloadHandler?.text ?? "Unknown error.");
                throw new InvalidOperationException(
                    $"Upload failed: {request.responseCode} {errMsg}"
                );
            }

            string responseText = request.downloadHandler?.text ?? string.Empty;
            UploadFileResponse? response;
            try
            {
                response = JsonConvert.DeserializeObject<UploadFileResponse>(responseText);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Upload response was not valid JSON: {ex.Message}"
                );
            }
            if (response == null || string.IsNullOrEmpty(response.FileId))
                throw new InvalidOperationException("Upload response is missing a valid file_id.");
            return response.FileId!;
        }

        // Mirrors uploadFile.js's `{ file_id: string }` response. Kept private
        // because no user-facing code reads it — UploadFileAsync returns just
        // the id string.
        private sealed class UploadFileResponse
        {
            [JsonProperty("file_id")]
            public string? FileId { get; set; }
        }
    }
}
