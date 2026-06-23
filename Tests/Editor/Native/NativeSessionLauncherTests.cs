#nullable enable

using System;
using System.Threading.Tasks;
using ElevenLabs.Agents;
using ElevenLabs.Native;
using NUnit.Framework;

namespace ElevenLabs.Native.Tests
{
    /// <summary>
    /// Edit Mode coverage for <see cref="NativeSessionLauncher"/>. Validation
    /// is exercised through <see cref="NativeSessionLauncher.StartAsync"/>
    /// (synchronous: the throw happens before <c>CreateAsync</c> is reached);
    /// the post-handshake wiring is driven through
    /// <see cref="NativeSessionLauncher.BuildConversation"/> against a
    /// <see cref="PairedWebSocket"/>-backed <see cref="NativeWebSocketConnection"/>
    /// so we don't need a live TCP server to confirm the launcher hands the
    /// connection to <see cref="Conversation"/> correctly.
    /// </summary>
    public class NativeSessionLauncherTests
    {
        // Cap every async test so a stalled server / read loop surfaces as a
        // test failure instead of hanging the runner. Five seconds matches
        // NativeWebSocketConnectionTests.
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        [Test]
        public void SessionFactory_IsRegisteredAtEditorLoad()
        {
            // Both NativeSessionLauncher.EditorInit and BridgedSessionLauncher.EditorInit
            // fire when the Editor loads with a non-WebGL active build target —
            // last-write-wins, so this assertion only confirms *a* launcher is
            // registered. The launcher-specific wiring is covered by
            // BuildConversation_* tests below.
            Assert.IsNotNull(Conversation.SessionFactory);
        }

        // Validation ---------------------------------------------------------

        [Test]
        public void StartAsync_NullOptions_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                NativeSessionLauncher.StartAsync(null!).GetAwaiter().GetResult()
            );
        }

        [Test]
        public void StartAsync_RejectsZeroCredentials()
        {
            var options = new ConversationOptions();
            var ex = Assert.Throws<ArgumentException>(() =>
                NativeSessionLauncher.StartAsync(options).GetAwaiter().GetResult()
            );
            StringAssert.Contains("Exactly one of", ex!.Message);
        }

        [Test]
        public void StartAsync_RejectsConflictingCredentials()
        {
            var options = new ConversationOptions
            {
                AgentId = "agent-test",
                SignedUrl = "wss://example/signed",
            };
            var ex = Assert.Throws<ArgumentException>(() =>
                NativeSessionLauncher.StartAsync(options).GetAwaiter().GetResult()
            );
            StringAssert.Contains("Exactly one of", ex!.Message);
        }

        [Test]
        public void StartAsync_RejectsConversationToken_OnWebSocketTransport()
        {
            // Native is WebSocket-only at v0.1; ConversationToken on the native
            // path is unreachable. Surface the mismatch with an explicit message
            // rather than letting the URL builder fail later with a less helpful
            // error.
            var options = new ConversationOptions
            {
                ConversationToken = "token-test",
                ConnectionType = ConnectionType.WebSocket,
            };
            var ex = Assert.Throws<ArgumentException>(() =>
                NativeSessionLauncher.StartAsync(options).GetAwaiter().GetResult()
            );
            StringAssert.Contains("WebRTC", ex!.Message);
        }

        [Test]
        public void StartAsync_RejectsWebRtc_AsUnsupported()
        {
            // ValidateTransport on the connection rejects WebRTC outright (no
            // native LiveKit at v0.1); the launcher surfaces that
            // NotSupportedException without rewrapping.
            var options = new ConversationOptions
            {
                ConversationToken = "token-test",
                ConnectionType = ConnectionType.WebRTC,
            };
            Assert.Throws<NotSupportedException>(() =>
                NativeSessionLauncher.StartAsync(options).GetAwaiter().GetResult()
            );
        }

        // Post-handshake wiring (via PairedWebSocket + BuildConversation seam) -

        [Test]
        public async Task BuildConversation_WiresConnectionIntoConversation_AndMarksConnected()
        {
            using var pair = await PairedWebSocket.CreateAsync().WaitAsync(TestTimeout);

            Task serverHandshake = DriveServerHandshakeAsync(
                pair.Server,
                conversationId: "conv-launcher-1",
                userInputFormat: "pcm_16000",
                agentOutputFormat: "pcm_24000"
            );
            NativeWebSocketConnection connection = await NativeWebSocketConnection
                .CreateInternalAsync(
                    pair.Client,
                    NativeWebSocketConnection.BuildInitiationData(
                        new ConversationOptions { AgentId = "agent-test" }
                    )
                )
                .WaitAsync(TestTimeout);
            await serverHandshake.WaitAsync(TestTimeout);

            Conversation conversation = NativeSessionLauncher.BuildConversation(
                connection,
                new NullInputController(),
                new NullOutputController(),
                new ConversationOptions { AgentId = "agent-test" }
            );
            try
            {
                Assert.AreEqual(Status.Connected, conversation.Status);
                Assert.AreEqual("conv-launcher-1", conversation.ConversationId);
                // Sanity: the launcher must use FormatConfig from the connection.
                // No public accessor on Conversation, so we re-read it off the
                // connection — confirms the launcher didn't accidentally swap
                // the connection out.
                Assert.AreEqual(16000, connection.InputFormat.SampleRate);
                Assert.AreEqual(24000, connection.OutputFormat.SampleRate);
            }
            finally
            {
                connection.Close();
                if (connection.CleanupTask != null)
                    await connection.CleanupTask.WaitAsync(TestTimeout);
            }
        }

        [Test]
        public void BuildConversation_NullConnection_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                NativeSessionLauncher.BuildConversation(
                    null!,
                    new NullInputController(),
                    new NullOutputController(),
                    new ConversationOptions()
                )
            );
        }

        // Server-side handshake helper -------------------------------------

        // Copied from NativeWebSocketConnectionTests — the launcher fixture
        // needs the same canned metadata reply to satisfy the handshake.
        private static async Task DriveServerHandshakeAsync(
            System.Net.WebSockets.WebSocket serverSocket,
            string conversationId,
            string userInputFormat,
            string agentOutputFormat
        )
        {
            byte[] buffer = new byte[8 * 1024];
            using var ms = new System.IO.MemoryStream();
            while (true)
            {
                var result = await serverSocket
                    .ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        System.Threading.CancellationToken.None
                    )
                    .ConfigureAwait(false);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    throw new InvalidOperationException(
                        "Socket closed while awaiting initiation frame."
                    );
                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    break;
            }
            string metadataJson =
                "{\"type\":\"conversation_initiation_metadata\","
                + "\"conversation_initiation_metadata_event\":{"
                + $"\"conversation_id\":\"{conversationId}\","
                + $"\"agent_output_audio_format\":\"{agentOutputFormat}\","
                + $"\"user_input_audio_format\":\"{userInputFormat}\""
                + "}}";
            byte[] outBytes = System.Text.Encoding.UTF8.GetBytes(metadataJson);
            await serverSocket
                .SendAsync(
                    new ArraySegment<byte>(outBytes),
                    System.Net.WebSockets.WebSocketMessageType.Text,
                    endOfMessage: true,
                    System.Threading.CancellationToken.None
                )
                .ConfigureAwait(false);
        }
    }
}
