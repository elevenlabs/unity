#nullable enable

using System;
using ElevenLabs.Agents;
using NUnit.Framework;

namespace ElevenLabs.Agents.Tests
{
    /// <summary>
    /// Edit Mode tests for <see cref="HttpFileUploader"/>'s pure helpers
    /// (origin derivation, URL construction, filename defaulting). The actual
    /// <see cref="UnityEngine.Networking.UnityWebRequest"/> POST is exercised
    /// by the standalone smoke instead of an Edit-Mode mock — the HTTP layer
    /// requires real I/O and a live agent backend to be meaningful.
    /// </summary>
    public class HttpFileUploaderTests
    {
        // DeriveHttpsOrigin -------------------------------------------------

        [Test]
        public void DeriveHttpsOrigin_NoSignedUrl_ReturnsDefaultProductionOrigin()
        {
            var options = new ConversationOptions { AgentId = "agent-42" };
            Assert.AreEqual(
                "https://api.elevenlabs.io",
                HttpFileUploader.DeriveHttpsOrigin(options)
            );
        }

        [Test]
        public void DeriveHttpsOrigin_SignedUrl_RewritesWssToHttpsAndStripsPath()
        {
            var options = new ConversationOptions
            {
                SignedUrl =
                    "wss://custom.example.com/v1/convai/conversation?agent_id=foo&token=bar",
            };
            Assert.AreEqual(
                "https://custom.example.com",
                HttpFileUploader.DeriveHttpsOrigin(options)
            );
        }

        [Test]
        public void DeriveHttpsOrigin_SignedUrl_RewritesWsToHttp()
        {
            // Local-dev signed URLs over plain WebSocket should map to HTTP —
            // mirrors uploadFile.js's `ws://` → `http://` rewrite.
            var options = new ConversationOptions { SignedUrl = "ws://localhost:8000/x" };
            Assert.AreEqual("http://localhost:8000", HttpFileUploader.DeriveHttpsOrigin(options));
        }

        [Test]
        public void DeriveHttpsOrigin_SignedUrl_PassesHttpsThrough()
        {
            // Already-HTTPS signed URLs (unusual, but legal per the JS SDK's
            // schema) shouldn't be re-rewritten.
            var options = new ConversationOptions { SignedUrl = "https://custom.example.com/x" };
            Assert.AreEqual(
                "https://custom.example.com",
                HttpFileUploader.DeriveHttpsOrigin(options)
            );
        }

        [Test]
        public void DeriveHttpsOrigin_NullOptions_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => HttpFileUploader.DeriveHttpsOrigin(null!));
        }

        // BuildUploadUrl ----------------------------------------------------

        [Test]
        public void BuildUploadUrl_ComposesUnderConversationsFilesPath()
        {
            Assert.AreEqual(
                "https://api.elevenlabs.io/v1/convai/conversations/conv-123/files",
                HttpFileUploader.BuildUploadUrl("https://api.elevenlabs.io", "conv-123")
            );
        }

        [Test]
        public void BuildUploadUrl_EscapesConversationIdAsPathSegment()
        {
            // Conversation ids are URL-safe in practice, but the implementation
            // must not assume so — a slash in the id would otherwise change the
            // resource being addressed.
            Assert.AreEqual(
                "https://api.elevenlabs.io/v1/convai/conversations/odd%2Fid/files",
                HttpFileUploader.BuildUploadUrl("https://api.elevenlabs.io", "odd/id")
            );
        }

        [Test]
        public void BuildUploadUrl_EmptyArgs_Throws()
        {
            Assert.Throws<ArgumentException>(() => HttpFileUploader.BuildUploadUrl("", "conv-1"));
            Assert.Throws<ArgumentException>(() =>
                HttpFileUploader.BuildUploadUrl("https://api.elevenlabs.io", "")
            );
        }

        // DeriveDefaultFilename ---------------------------------------------

        [Test]
        public void DeriveDefaultFilename_SimpleMime_UsesSubtypeAsExtension()
        {
            Assert.AreEqual("upload.png", HttpFileUploader.DeriveDefaultFilename("image/png"));
            Assert.AreEqual(
                "upload.pdf",
                HttpFileUploader.DeriveDefaultFilename("application/pdf")
            );
        }

        [Test]
        public void DeriveDefaultFilename_SubtypeWithPlus_StripsSuffix()
        {
            // `image/svg+xml` → `upload.svg` per uploadFile.js's
            // `.split("+")[0]` rule.
            Assert.AreEqual("upload.svg", HttpFileUploader.DeriveDefaultFilename("image/svg+xml"));
        }

        [Test]
        public void DeriveDefaultFilename_NullOrEmpty_FallsBackToPng()
        {
            // Matches JS's `(file.type || "image/png")` default.
            Assert.AreEqual("upload.png", HttpFileUploader.DeriveDefaultFilename(null));
            Assert.AreEqual("upload.png", HttpFileUploader.DeriveDefaultFilename(""));
        }

        [Test]
        public void DeriveDefaultFilename_NoSlash_UsesWholeString()
        {
            // Malformed MIME without a slash shouldn't crash — fall back to the
            // string itself as the extension. Matches `.split("/").pop()`.
            Assert.AreEqual("upload.txt", HttpFileUploader.DeriveDefaultFilename("txt"));
        }

        // Constructor validation -------------------------------------------

        [Test]
        public void Construct_EmptyOriginOrConversationId_Throws()
        {
            Assert.Throws<ArgumentException>(() => new HttpFileUploader("", "conv-1"));
            Assert.Throws<ArgumentException>(() =>
                new HttpFileUploader("https://api.elevenlabs.io", "")
            );
        }
    }
}
