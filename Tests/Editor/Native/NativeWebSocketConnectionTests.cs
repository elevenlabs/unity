#nullable enable

using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ElevenLabs.Agents;
using ElevenLabs.Native;
using ElevenLabs.Protocol;
using NUnit.Framework;

namespace ElevenLabs.Native.Tests
{
    /// <summary>
    /// Edit Mode tests for <see cref="NativeWebSocketConnection"/>. Static
    /// helpers (URL construction, transport validation, format parsing,
    /// initiation-data shape) are exercised as pure unit tests; the handshake
    /// + read-loop + close lifecycle is driven against a localhost
    /// <see cref="LocalhostWebSocketServer"/> with the production
    /// <see cref="System.Net.WebSockets.ClientWebSocket"/> on the SDK side.
    /// </summary>
    public class NativeWebSocketConnectionTests
    {
        // Cap every async test so a stalled server / read loop surfaces as a
        // test failure instead of hanging the runner. Five seconds is generous
        // for a localhost handshake — production E2E targets are well under a
        // second.
        private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

        // BuildUrl ----------------------------------------------------------

        [Test]
        public void BuildUrl_AgentId_PointsAtProductionEndpoint()
        {
            var options = new ConversationOptions { AgentId = "agent-42" };
            Uri url = NativeWebSocketConnection.BuildUrl(options);
            Assert.AreEqual(
                "wss://api.elevenlabs.io/v1/convai/conversation?agent_id=agent-42",
                url.ToString()
            );
        }

        [Test]
        public void BuildUrl_SignedUrl_UsedVerbatim()
        {
            const string signed =
                "wss://api.elevenlabs.io/v1/convai/conversation?agent_id=foo&token=bar";
            var options = new ConversationOptions { SignedUrl = signed };
            Uri url = NativeWebSocketConnection.BuildUrl(options);
            Assert.AreEqual(signed, url.ToString());
        }

        [Test]
        public void BuildUrl_SignedUrlBeatsAgentId()
        {
            // Matches WebSocketConnection.js's ordering — signedUrl wins if both
            // are present. Keep the precedence explicit so a future refactor
            // can't accidentally invert it.
            var options = new ConversationOptions
            {
                AgentId = "agent-42",
                SignedUrl = "wss://example.com/signed",
            };
            Uri url = NativeWebSocketConnection.BuildUrl(options);
            Assert.AreEqual("wss://example.com/signed", url.ToString());
        }

        [Test]
        public void BuildUrl_NeitherAgentIdNorSignedUrl_Throws()
        {
            var options = new ConversationOptions();
            Assert.Throws<ArgumentException>(() => NativeWebSocketConnection.BuildUrl(options));
        }

        [Test]
        public void BuildUrl_NullOptions_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => NativeWebSocketConnection.BuildUrl(null!));
        }

        // ValidateTransport -------------------------------------------------

        [Test]
        public void ValidateTransport_WebSocket_DoesNotThrow()
        {
            var options = new ConversationOptions { ConnectionType = ConnectionType.WebSocket };
            Assert.DoesNotThrow(() => NativeWebSocketConnection.ValidateTransport(options));
        }

        [Test]
        public void ValidateTransport_WebRTC_ThrowsNotSupported()
        {
            var options = new ConversationOptions
            {
                ConnectionType = ConnectionType.WebRTC,
                ConversationToken = "tok",
            };
            Assert.Throws<NotSupportedException>(() =>
                NativeWebSocketConnection.ValidateTransport(options)
            );
        }

        // BuildInitiationData ----------------------------------------------

        [Test]
        public void BuildInitiationData_AllFieldsForwarded()
        {
            var options = new ConversationOptions
            {
                UserId = "user-1",
                DynamicVariables = new Dictionary<string, object> { ["name"] = "Ada" },
                CustomLlmExtraBody = new Dictionary<string, object> { ["temperature"] = 0.7 },
                Overrides = new ConversationConfigOverride
                {
                    Agent = new ConversationConfigOverrideAgent { Language = "en" },
                },
            };
            ConversationInitiationClientData data = NativeWebSocketConnection.BuildInitiationData(
                options
            );

            Assert.AreEqual("user-1", data.UserId);
            Assert.AreEqual("Ada", data.DynamicVariables!["name"]);
            Assert.AreEqual(0.7, data.CustomLlmExtraBody!["temperature"]);
            Assert.AreEqual("en", data.ConversationConfigOverride!.Agent!.Language);
            // `type` discriminator is fixed by the codegen-emitted init-only.
            Assert.AreEqual("conversation_initiation_client_data", data.Type);
        }

        [Test]
        public void BuildInitiationData_EmptyDictionariesOmitted()
        {
            // Empty containers shouldn't survive into the wire payload — the
            // SDK's union types prefer omission over noise, and the WebGL path
            // applies the same rule in BridgedSession.BuildSessionConfig.
            var options = new ConversationOptions
            {
                DynamicVariables = new Dictionary<string, object>(),
                CustomLlmExtraBody = new Dictionary<string, object>(),
            };
            ConversationInitiationClientData data = NativeWebSocketConnection.BuildInitiationData(
                options
            );
            Assert.IsNull(data.DynamicVariables);
            Assert.IsNull(data.CustomLlmExtraBody);
        }

        [Test]
        public void BuildInitiationData_NullUserId_LeavesFieldNull()
        {
            var options = new ConversationOptions { UserId = "" };
            ConversationInitiationClientData data = NativeWebSocketConnection.BuildInitiationData(
                options
            );
            Assert.IsNull(data.UserId);
        }

        // ParseFormatString -------------------------------------------------

        [Test]
        public void ParseFormatString_Pcm_Recognised()
        {
            FormatConfig fmt = NativeWebSocketConnection.ParseFormatString("pcm_16000");
            Assert.AreEqual("pcm", fmt.Format);
            Assert.AreEqual(16000, fmt.SampleRate);
        }

        [Test]
        public void ParseFormatString_Ulaw_Recognised()
        {
            FormatConfig fmt = NativeWebSocketConnection.ParseFormatString("ulaw_8000");
            Assert.AreEqual("ulaw", fmt.Format);
            Assert.AreEqual(8000, fmt.SampleRate);
        }

        [Test]
        public void ParseFormatString_UnknownCodec_Throws()
        {
            Assert.Throws<FormatException>(() =>
                NativeWebSocketConnection.ParseFormatString("opus_48000")
            );
        }

        [Test]
        public void ParseFormatString_MissingSampleRate_Throws()
        {
            Assert.Throws<FormatException>(() =>
                NativeWebSocketConnection.ParseFormatString("pcm_")
            );
        }

        // Handshake / lifecycle (paired WebSocket over TCP loopback) -------

        // Tests drive the production handshake through the test seam
        // NativeWebSocketConnection.CreateInternalAsync(WebSocket, ...) because
        // ClientWebSocket's HTTP-upgrade validator rejects the responses that
        // Mono's HttpListener WebSocket implementation produces in Unity Editor
        // (PairedWebSocket.cs has the full context). The seam exercises the
        // post-upgrade protocol path identically; URL construction and
        // transport validation are covered by the pure unit tests above.
        private static ConversationInitiationClientData DefaultInitiationData() =>
            new() { UserId = "user-test" };

        [Test]
        public async Task CreateInternalAsync_PerformsHandshake_PopulatesConversationAndFormats()
        {
            using var pair = await PairedWebSocket.CreateAsync().WaitAsync(TestTimeout);

            // Drive the server side concurrently — read the initiation event,
            // reply with conversation_initiation_metadata so the connection
            // factory can resolve.
            Task serverHandshake = DriveServerHandshakeAsync(
                pair.Server,
                conversationId: "conv-test-1",
                userInputFormat: "pcm_16000",
                agentOutputFormat: "pcm_24000"
            );

            NativeWebSocketConnection connection = await NativeWebSocketConnection
                .CreateInternalAsync(pair.Client, DefaultInitiationData())
                .WaitAsync(TestTimeout);
            await serverHandshake.WaitAsync(TestTimeout);

            try
            {
                Assert.AreEqual("conv-test-1", connection.ConversationId);
                Assert.AreEqual("pcm", connection.InputFormat.Format);
                Assert.AreEqual(16000, connection.InputFormat.SampleRate);
                Assert.AreEqual("pcm", connection.OutputFormat.Format);
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
        public async Task CreateInternalAsync_SendsInitiationData_OnHandshake()
        {
            using var pair = await PairedWebSocket.CreateAsync().WaitAsync(TestTimeout);

            var options = new ConversationOptions
            {
                UserId = "user-A",
                DynamicVariables = new Dictionary<string, object> { ["mood"] = "happy" },
            };
            ConversationInitiationClientData initiationData =
                NativeWebSocketConnection.BuildInitiationData(options);

            // Capture the raw initiation frame the server receives so we can
            // assert on its wire shape.
            var receivedJsonTcs = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            Task serverHandshake = DriveServerHandshakeAsync(
                pair.Server,
                conversationId: "conv-test-2",
                userInputFormat: "pcm_16000",
                agentOutputFormat: "pcm_24000",
                onInitiationReceived: receivedJsonTcs.SetResult
            );

            NativeWebSocketConnection connection = await NativeWebSocketConnection
                .CreateInternalAsync(pair.Client, initiationData)
                .WaitAsync(TestTimeout);
            await serverHandshake.WaitAsync(TestTimeout);

            try
            {
                string raw = await receivedJsonTcs.Task.WaitAsync(TestTimeout);
                StringAssert.Contains("\"type\":\"conversation_initiation_client_data\"", raw);
                StringAssert.Contains("\"user_id\":\"user-A\"", raw);
                StringAssert.Contains("\"dynamic_variables\"", raw);
                StringAssert.Contains("\"mood\":\"happy\"", raw);
            }
            finally
            {
                connection.Close();
                if (connection.CleanupTask != null)
                    await connection.CleanupTask.WaitAsync(TestTimeout);
            }
        }

        [Test]
        public async Task ReadLoop_DispatchesIncomingSocketEvent_OnMessage()
        {
            using var pair = await PairedWebSocket.CreateAsync().WaitAsync(TestTimeout);

            Task serverHandshake = DriveServerHandshakeAsync(
                pair.Server,
                conversationId: "conv-msg",
                userInputFormat: "pcm_16000",
                agentOutputFormat: "pcm_24000"
            );
            NativeWebSocketConnection connection = await NativeWebSocketConnection
                .CreateInternalAsync(pair.Client, DefaultInitiationData())
                .WaitAsync(TestTimeout);
            await serverHandshake.WaitAsync(TestTimeout);

            var messageTcs = new TaskCompletionSource<IncomingSocketEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            connection.OnMessage += evt => messageTcs.TrySetResult(evt);

            try
            {
                const string payload =
                    "{\"type\":\"agent_response\",\"agent_response_event\":{\"agent_response\":\"hi from the agent\",\"event_id\":7}}";
                await SendTextAsync(pair.Server, payload);

                IncomingSocketEvent evt = await messageTcs.Task.WaitAsync(TestTimeout);
                Assert.IsInstanceOf<AgentResponse>(evt);
                Assert.AreEqual(
                    "hi from the agent",
                    ((AgentResponse)evt).AgentResponseEvent.AgentResponse
                );
            }
            finally
            {
                connection.Close();
                if (connection.CleanupTask != null)
                    await connection.CleanupTask.WaitAsync(TestTimeout);
            }
        }

        [Test]
        public async Task ReadLoop_UnknownWireType_DispatchesUnknownIncomingEvent()
        {
            // Future-proofing parity with the bridged path: a `type` the
            // converter doesn't know surfaces as UnknownIncomingEvent so user
            // code can react instead of crashing.
            using var pair = await PairedWebSocket.CreateAsync().WaitAsync(TestTimeout);

            Task serverHandshake = DriveServerHandshakeAsync(
                pair.Server,
                conversationId: "conv-unknown",
                userInputFormat: "pcm_16000",
                agentOutputFormat: "pcm_24000"
            );
            NativeWebSocketConnection connection = await NativeWebSocketConnection
                .CreateInternalAsync(pair.Client, DefaultInitiationData())
                .WaitAsync(TestTimeout);
            await serverHandshake.WaitAsync(TestTimeout);

            var messageTcs = new TaskCompletionSource<IncomingSocketEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            connection.OnMessage += evt => messageTcs.TrySetResult(evt);

            try
            {
                await SendTextAsync(pair.Server, "{\"type\":\"future_event_v9\",\"value\":42}");

                IncomingSocketEvent evt = await messageTcs.Task.WaitAsync(TestTimeout);
                Assert.IsInstanceOf<UnknownIncomingEvent>(evt);
                Assert.AreEqual("future_event_v9", ((UnknownIncomingEvent)evt).Type);
            }
            finally
            {
                connection.Close();
                if (connection.CleanupTask != null)
                    await connection.CleanupTask.WaitAsync(TestTimeout);
            }
        }

        [Test]
        public async Task Send_RoundTripsThroughTransport()
        {
            using var pair = await PairedWebSocket.CreateAsync().WaitAsync(TestTimeout);
            Task serverHandshake = DriveServerHandshakeAsync(
                pair.Server,
                conversationId: "conv-send",
                userInputFormat: "pcm_16000",
                agentOutputFormat: "pcm_24000"
            );
            NativeWebSocketConnection connection = await NativeWebSocketConnection
                .CreateInternalAsync(pair.Client, DefaultInitiationData())
                .WaitAsync(TestTimeout);
            await serverHandshake.WaitAsync(TestTimeout);

            try
            {
                connection.Send(new UserMessage { Text = "round trip" });
                string received = await ReceiveTextAsync(pair.Server).WaitAsync(TestTimeout);
                StringAssert.Contains("\"type\":\"user_message\"", received);
                StringAssert.Contains("\"text\":\"round trip\"", received);
            }
            finally
            {
                connection.Close();
                if (connection.CleanupTask != null)
                    await connection.CleanupTask.WaitAsync(TestTimeout);
            }
        }

        [Test]
        public async Task ServerClose_RaisesOnDisconnect_WithAgentReasonForCode1000()
        {
            using var pair = await PairedWebSocket.CreateAsync().WaitAsync(TestTimeout);
            Task serverHandshake = DriveServerHandshakeAsync(
                pair.Server,
                conversationId: "conv-srvclose",
                userInputFormat: "pcm_16000",
                agentOutputFormat: "pcm_24000"
            );
            NativeWebSocketConnection connection = await NativeWebSocketConnection
                .CreateInternalAsync(pair.Client, DefaultInitiationData())
                .WaitAsync(TestTimeout);
            await serverHandshake.WaitAsync(TestTimeout);

            var disconnectTcs = new TaskCompletionSource<DisconnectionDetails>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            connection.OnDisconnect += details => disconnectTcs.TrySetResult(details);

            try
            {
                await pair.Server.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "agent done",
                    CancellationToken.None
                );
                DisconnectionDetails details = await disconnectTcs.Task.WaitAsync(TestTimeout);
                Assert.AreEqual(DisconnectionReason.Agent, details.Reason);
                Assert.AreEqual(1000, details.CloseCode);
                Assert.AreEqual("agent done", details.CloseReason);
            }
            finally
            {
                connection.Close();
                if (connection.CleanupTask != null)
                    await connection.CleanupTask.WaitAsync(TestTimeout);
            }
        }

        [Test]
        public async Task ClientClose_SuppressesOnDisconnect()
        {
            // The user-initiated path doesn't double-fire OnDisconnect:
            // Conversation.EndSession already drives Disconnected(reason=User);
            // the transport should stay quiet on its own close handshake.
            using var pair = await PairedWebSocket.CreateAsync().WaitAsync(TestTimeout);
            Task serverHandshake = DriveServerHandshakeAsync(
                pair.Server,
                conversationId: "conv-cliclose",
                userInputFormat: "pcm_16000",
                agentOutputFormat: "pcm_24000"
            );
            NativeWebSocketConnection connection = await NativeWebSocketConnection
                .CreateInternalAsync(pair.Client, DefaultInitiationData())
                .WaitAsync(TestTimeout);
            await serverHandshake.WaitAsync(TestTimeout);

            int disconnectCount = 0;
            connection.OnDisconnect += _ => Interlocked.Increment(ref disconnectCount);

            connection.Close();
            if (connection.CleanupTask != null)
                await connection.CleanupTask.WaitAsync(TestTimeout);
            Assert.AreEqual(0, disconnectCount);
        }

        [Test]
        public async Task Close_IsIdempotent()
        {
            using var pair = await PairedWebSocket.CreateAsync().WaitAsync(TestTimeout);
            Task serverHandshake = DriveServerHandshakeAsync(
                pair.Server,
                conversationId: "conv-idem",
                userInputFormat: "pcm_16000",
                agentOutputFormat: "pcm_24000"
            );
            NativeWebSocketConnection connection = await NativeWebSocketConnection
                .CreateInternalAsync(pair.Client, DefaultInitiationData())
                .WaitAsync(TestTimeout);
            await serverHandshake.WaitAsync(TestTimeout);

            connection.Close();
            Task? firstCleanup = connection.CleanupTask;
            connection.Close();
            Task? secondCleanup = connection.CleanupTask;
            Assert.AreSame(
                firstCleanup,
                secondCleanup,
                "Repeat Close should not spawn additional cleanup work."
            );
            if (firstCleanup != null)
                await firstCleanup.WaitAsync(TestTimeout);
        }

        // Server-side handshake helper -------------------------------------

        // Drives the server side of the handshake on a pre-paired WebSocket:
        // reads the conversation_initiation_client_data frame, replies with
        // the metadata payload so the client's CreateInternalAsync can resolve.
        // Optional callback captures the raw initiation JSON for wire-shape
        // assertions.
        private static async Task DriveServerHandshakeAsync(
            WebSocket serverSocket,
            string conversationId,
            string userInputFormat,
            string agentOutputFormat,
            Action<string>? onInitiationReceived = null
        )
        {
            string initiationJson = await ReceiveTextAsync(serverSocket);
            onInitiationReceived?.Invoke(initiationJson);
            string metadataJson =
                "{\"type\":\"conversation_initiation_metadata\","
                + "\"conversation_initiation_metadata_event\":{"
                + $"\"conversation_id\":\"{conversationId}\","
                + $"\"agent_output_audio_format\":\"{agentOutputFormat}\","
                + $"\"user_input_audio_format\":\"{userInputFormat}\""
                + "}}";
            await SendTextAsync(serverSocket, metadataJson);
        }

        private static async Task SendTextAsync(WebSocket socket, string payload)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None
            );
        }

        private static async Task<string> ReceiveTextAsync(WebSocket socket)
        {
            byte[] buffer = new byte[8 * 1024];
            using var ms = new System.IO.MemoryStream();
            while (true)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    CancellationToken.None
                );
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new InvalidOperationException(
                        "Socket closed while awaiting a text message."
                    );
                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                    return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
    }
}
