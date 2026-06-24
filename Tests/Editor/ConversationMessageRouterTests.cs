#nullable enable

using System;
using System.Collections.Generic;
using ElevenLabs.Agents;
using ElevenLabs.Protocol;
using ElevenLabs.WebGL;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
// UnityEngine.Ping collides with the protocol wire type — alias the wire one.
using Ping = ElevenLabs.Protocol.Ping;

namespace ElevenLabs.WebGL.Tests
{
    public class ConversationMessageRouterTests
    {
        // Test doubles --------------------------------------------------------

        private sealed class MockConnection : IConnection
        {
            public string ConversationId { get; set; } = "test-conversation";
            public FormatConfig InputFormat { get; set; } = new("pcm", 16000);
            public FormatConfig OutputFormat { get; set; } = new("pcm", 16000);

            public event Action<IncomingSocketEvent>? OnMessage;
            public event Action<DisconnectionDetails>? OnDisconnect;
            public event Action<Mode>? OnModeChange;

            public List<OutgoingSocketEvent> Sent { get; } = new();
            public int CloseCallCount { get; private set; }

            public void Send(OutgoingSocketEvent message) => Sent.Add(message);

            public void Close() => CloseCallCount++;

            public void FireMessage(IncomingSocketEvent evt) => OnMessage?.Invoke(evt);

            public void FireDisconnect(DisconnectionDetails details) =>
                OnDisconnect?.Invoke(details);

            public void FireModeChange(Mode mode) => OnModeChange?.Invoke(mode);
        }

        private sealed class MockInputController : IInputController
        {
            public bool IsMuted { get; private set; }
            public int CloseCallCount { get; private set; }
            public bool? LastMuteRequest { get; private set; }
            public float VolumeReturnValue { get; set; } = 0f;
            public int GetVolumeCallCount { get; private set; }
            public byte[]? LastByteFrequencyBuffer { get; private set; }
            public int SetDeviceCallCount { get; private set; }
            public InputDeviceConfig? LastSetDeviceConfig { get; private set; }
            public FormatConfig? LastSetDeviceFormat { get; private set; }
            public Exception? SetDeviceException { get; set; }

            public event System.Action<byte[]>? AudioChunkAvailable;

            // Test helper — lets tests pump a synthetic PCM chunk through the
            // Conversation router as if the microphone had emitted one.
            public void FireAudioChunkAvailable(byte[] pcm) => AudioChunkAvailable?.Invoke(pcm);

            public Awaitable Close()
            {
                CloseCallCount++;
                return CompletedAwaitable();
            }

            public Awaitable SetDevice(
                InputDeviceConfig? config = null,
                FormatConfig? format = null
            )
            {
                SetDeviceCallCount++;
                LastSetDeviceConfig = config;
                LastSetDeviceFormat = format;
                if (SetDeviceException != null)
                {
                    var source = new AwaitableCompletionSource();
                    source.SetException(SetDeviceException);
                    return source.Awaitable;
                }
                return CompletedAwaitable();
            }

            public Awaitable SetMuted(bool isMuted)
            {
                LastMuteRequest = isMuted;
                IsMuted = isMuted;
                return CompletedAwaitable();
            }

            public float GetVolume()
            {
                GetVolumeCallCount++;
                return VolumeReturnValue;
            }

            public void GetByteFrequencyData(byte[] buffer) => LastByteFrequencyBuffer = buffer;
        }

        private sealed class MockOutputController : IOutputController
        {
            public int CloseCallCount { get; private set; }
            public int InterruptCallCount { get; private set; }
            public int? LastInterruptResetDurationMs { get; private set; }
            public float LastVolume { get; private set; } = 1f;
            public int SetVolumeCallCount { get; private set; }
            public float VolumeReturnValue { get; set; } = 0f;
            public int GetVolumeCallCount { get; private set; }
            public byte[]? LastByteFrequencyBuffer { get; private set; }
            public List<byte[]> PushAudioCalls { get; } = new();
            public int SetDeviceCallCount { get; private set; }
            public OutputDeviceConfig? LastSetDeviceConfig { get; private set; }
            public FormatConfig? LastSetDeviceFormat { get; private set; }
            public Exception? SetDeviceException { get; set; }

            public Awaitable Close()
            {
                CloseCallCount++;
                return CompletedAwaitable();
            }

            public void PushAudio(byte[] pcm) => PushAudioCalls.Add(pcm);

            public Awaitable SetDevice(
                OutputDeviceConfig? config = null,
                FormatConfig? format = null
            )
            {
                SetDeviceCallCount++;
                LastSetDeviceConfig = config;
                LastSetDeviceFormat = format;
                if (SetDeviceException != null)
                {
                    var source = new AwaitableCompletionSource();
                    source.SetException(SetDeviceException);
                    return source.Awaitable;
                }
                return CompletedAwaitable();
            }

            public void SetVolume(float volume)
            {
                SetVolumeCallCount++;
                LastVolume = volume;
            }

            public void Interrupt(int? resetDurationMs = null)
            {
                InterruptCallCount++;
                LastInterruptResetDurationMs = resetDurationMs;
            }

            public float GetVolume()
            {
                GetVolumeCallCount++;
                return VolumeReturnValue;
            }

            public void GetByteFrequencyData(byte[] buffer) => LastByteFrequencyBuffer = buffer;
        }

        private sealed class MockFileUploader : IFileUploader
        {
            public int CallCount { get; private set; }
            public byte[]? LastBytes { get; private set; }
            public string? LastMimeType { get; private set; }
            public string? LastFilename { get; private set; }
            public string Result { get; set; } = "file_abc123";
            public Exception? Exception { get; set; }

            public Awaitable<string> UploadFileAsync(
                byte[] bytes,
                string mimeType,
                string? filename = null
            )
            {
                CallCount++;
                LastBytes = bytes;
                LastMimeType = mimeType;
                LastFilename = filename;
                var source = new AwaitableCompletionSource<string>();
                if (Exception != null)
                    source.SetException(Exception);
                else
                    source.SetResult(Result);
                return source.Awaitable;
            }
        }

        private static Awaitable CompletedAwaitable()
        {
            var source = new AwaitableCompletionSource();
            source.SetResult();
            return source.Awaitable;
        }

        private static Conversation NewConversation(
            out MockConnection connection,
            out MockInputController input,
            out MockOutputController output
        ) => NewConversation(out connection, out input, out output, out _);

        private static Conversation NewConversation(
            out MockConnection connection,
            out MockInputController input,
            out MockOutputController output,
            out MockFileUploader fileUploader
        )
        {
            connection = new MockConnection();
            input = new MockInputController();
            output = new MockOutputController();
            fileUploader = new MockFileUploader();
            return new Conversation(
                connection,
                input,
                output,
                fileUploader,
                new ConversationOptions()
            );
        }

        private static AudioResponse MakeAudio(int eventId, string payload = "")
        {
            return new AudioResponse
            {
                AudioEvent = new AudioEvent { AudioBase64 = payload, EventId = eventId },
            };
        }

        // Wire-to-args fan-out -----------------------------------------------

        [Test]
        public void Dispatch_ConversationInitiationMetadata_FiresInitiationReceivedWithArgs()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            ConversationInitiationMetadataArgs? received = null;
            conversation.InitiationMetadataReceived += args => received = args;

            connection.FireMessage(
                new ConversationInitiationMetadata
                {
                    ConversationInitiationMetadataEvent = new ConversationInitiationMetadataEvent
                    {
                        ConversationId = "conv-1",
                        AgentOutputAudioFormat = "pcm_24000",
                        UserInputAudioFormat = "pcm_16000",
                    },
                }
            );

            Assert.IsNotNull(received);
            Assert.AreEqual("conv-1", received!.ConversationId);
            Assert.AreEqual("pcm_24000", received.AgentOutputAudioFormat);
            Assert.AreEqual("pcm_16000", received.UserInputAudioFormat);
        }

        [Test]
        public void Dispatch_AgentResponse_FiresAgentRespondedWithArgs()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            AgentResponseArgs? received = null;
            conversation.AgentResponded += args => received = args;

            connection.FireMessage(
                new AgentResponse
                {
                    AgentResponseEvent = new AgentResponseEvent
                    {
                        AgentResponse = "Hello there",
                        EventId = 7,
                    },
                }
            );

            Assert.IsNotNull(received);
            Assert.AreEqual("Hello there", received!.AgentResponse);
            Assert.AreEqual(7, received.EventId);
        }

        [Test]
        public void Dispatch_UserTranscript_FiresUserTranscriptReceived()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            UserTranscriptArgs? received = null;
            conversation.UserTranscriptReceived += args => received = args;

            connection.FireMessage(
                new UserTranscript
                {
                    UserTranscriptionEvent = new UserTranscriptionEvent
                    {
                        UserTranscript = "what's the weather",
                        EventId = 3,
                    },
                }
            );

            Assert.AreEqual("what's the weather", received!.UserTranscript);
            Assert.AreEqual(3, received.EventId);
        }

        [Test]
        public void Dispatch_AgentResponseComplete_FiresAgentResponseCompleted()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            AgentResponseCompleteArgs? received = null;
            conversation.AgentResponseCompleted += args => received = args;

            connection.FireMessage(
                new AgentResponseComplete
                {
                    AgentResponseCompleteEvent = new AgentResponseCompleteEvent { EventId = 11 },
                }
            );

            Assert.AreEqual(11, received!.EventId);
        }

        [Test]
        public void Dispatch_AgentResponseCorrection_FiresAgentResponseCorrected()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            AgentResponseCorrectionArgs? received = null;
            conversation.AgentResponseCorrected += args => received = args;

            connection.FireMessage(
                new AgentResponseCorrection
                {
                    AgentResponseCorrectionEvent = new AgentResponseCorrectionEvent
                    {
                        OriginalAgentResponse = "wrong",
                        CorrectedAgentResponse = "right",
                        EventId = 5,
                    },
                }
            );

            Assert.AreEqual("wrong", received!.OriginalAgentResponse);
            Assert.AreEqual("right", received.CorrectedAgentResponse);
            Assert.AreEqual(5, received.EventId);
        }

        [Test]
        public void Dispatch_VadScore_FiresVadScoreUpdated()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            VadScoreArgs? received = null;
            conversation.VadScoreUpdated += args => received = args;

            connection.FireMessage(
                new VadScore { VadScoreEvent = new VadScoreEvent { VadScore = 0.42 } }
            );

            Assert.AreEqual(0.42, received!.VadScore, 1e-6);
        }

        [Test]
        public void Dispatch_McpToolCall_FiresMCPToolCallReceivedWithRawPayload()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            McpToolCallArgs? received = null;
            conversation.MCPToolCallReceived += args => received = args;

            // Payload shape varies by `state`; the args record exposes it as the
            // raw JToken the server emitted, so this pins the forwarding (not
            // the schema).
            var payload = JObject.Parse(
                "{\"service_id\":\"svc-1\",\"tool_call_id\":\"call-9\",\"tool_name\":\"search\","
                    + "\"parameters\":{\"q\":\"hi\"},\"state\":\"awaiting_approval\","
                    + "\"approval_timeout_secs\":30}"
            );
            connection.FireMessage(new McpToolCall { McpToolCallData = payload });

            Assert.IsNotNull(received);
            Assert.AreSame(payload, received!.McpToolCallData);
        }

        [Test]
        public void Dispatch_McpConnectionStatus_FiresMCPConnectionStatusChanged()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            McpConnectionStatusArgs? received = null;
            conversation.MCPConnectionStatusChanged += args => received = args;

            var integration = new McpConnectionStatusIntegrationsItem
            {
                IntegrationId = "int-1",
                IntegrationType = "linear",
                IsConnected = true,
                ToolCount = 4,
            };
            connection.FireMessage(
                new McpConnectionStatus
                {
                    McpConnectionStatusData = new McpConnectionStatusEvent
                    {
                        Integrations = new[] { integration },
                    },
                }
            );

            Assert.IsNotNull(received);
            CollectionAssert.AreEqual(new[] { integration }, received!.Integrations);
        }

        [Test]
        public void Dispatch_AgentToolRequest_FiresAgentToolRequestedWithArgs()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            AgentToolRequestArgs? received = null;
            conversation.AgentToolRequested += args => received = args;

            connection.FireMessage(
                new AgentToolRequest
                {
                    AgentToolRequestData = new AgentToolRequestEvent
                    {
                        ToolName = "lookup_user",
                        ToolCallId = "tc-1",
                        ToolType = "function",
                        EventId = 7,
                        ExpectsResponse = true,
                        DisableInterruptions = false,
                        ResponseTimeoutSecs = 30,
                        ExecutionMode = "sync",
                    },
                }
            );

            Assert.IsNotNull(received);
            Assert.AreEqual("lookup_user", received!.ToolName);
            Assert.AreEqual("tc-1", received.ToolCallId);
            Assert.AreEqual("function", received.ToolType);
            Assert.AreEqual(7, received.EventId);
            Assert.IsTrue(received.ExpectsResponse);
            Assert.IsFalse(received.DisableInterruptions);
            Assert.AreEqual(30, received.ResponseTimeoutSecs);
            Assert.AreEqual("sync", received.ExecutionMode);
        }

        [Test]
        public void Dispatch_AgentToolResponse_FiresAgentToolRespondedWithSlimArgs()
        {
            // Slim wire variant — FullToolResult / Truncated come back null in
            // the unified args record, mirroring the JS SDK union shape.
            var conversation = NewConversation(out var connection, out _, out _);
            AgentToolRespondedArgs? received = null;
            conversation.AgentToolResponded += args => received = args;

            connection.FireMessage(
                new AgentToolResponse
                {
                    AgentToolResponseData = new AgentToolResponseEvent
                    {
                        ToolName = "lookup_user",
                        ToolCallId = "tc-1",
                        ToolType = "function",
                        IsError = false,
                        IsBlocked = false,
                        EventId = 7,
                        IsCalled = true,
                    },
                }
            );

            Assert.IsNotNull(received);
            Assert.AreEqual("lookup_user", received!.ToolName);
            Assert.AreEqual("tc-1", received.ToolCallId);
            Assert.AreEqual("function", received.ToolType);
            Assert.IsFalse(received.IsError);
            Assert.AreEqual(false, received.IsBlocked);
            Assert.AreEqual(7, received.EventId);
            Assert.IsTrue(received.IsCalled);
            Assert.IsNull(received.FullToolResult);
            Assert.IsNull(received.Truncated);
        }

        [Test]
        public void Dispatch_AgentToolResponseFullPayload_FiresAgentToolRespondedWithFullArgs()
        {
            // Full-payload variant feeds the same public event but populates
            // FullToolResult / Truncated.
            var conversation = NewConversation(out var connection, out _, out _);
            AgentToolRespondedArgs? received = null;
            conversation.AgentToolResponded += args => received = args;

            connection.FireMessage(
                new AgentToolResponseFullPayload
                {
                    AgentToolResponseFullPayloadData = new AgentToolResponseFullPayloadEvent
                    {
                        ToolName = "lookup_user",
                        ToolCallId = "tc-2",
                        ToolType = "function",
                        IsError = false,
                        IsBlocked = false,
                        EventId = 9,
                        IsCalled = true,
                        FullToolResult = "{\"name\":\"Alice\"}",
                        Truncated = false,
                    },
                }
            );

            Assert.IsNotNull(received);
            Assert.AreEqual("lookup_user", received!.ToolName);
            Assert.AreEqual("tc-2", received.ToolCallId);
            Assert.AreEqual(9, received.EventId);
            Assert.AreEqual("{\"name\":\"Alice\"}", received.FullToolResult);
            Assert.AreEqual(false, received.Truncated);
        }

        [Test]
        public void Dispatch_AgentChatResponsePart_FiresAgentChatResponsePartReceivedWithArgs()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            AgentChatResponsePartArgs? received = null;
            conversation.AgentChatResponsePartReceived += args => received = args;

            connection.FireMessage(
                new AgentChatResponsePart
                {
                    TextResponsePart = new TextResponsePart
                    {
                        Text = "Hello, ",
                        Type = "delta",
                        EventId = 4,
                    },
                }
            );

            Assert.IsNotNull(received);
            Assert.AreEqual("Hello, ", received!.Text);
            Assert.AreEqual("delta", received.Type);
            Assert.AreEqual(4, received.EventId);
        }

        [Test]
        public void Dispatch_GuardrailTriggered_FiresGuardrailTriggeredWithArgs()
        {
            // JS SDK's onGuardrailTriggered is parameterless; we surface the
            // typed args record so subscribers can branch on which guardrail
            // fired without rebuilding the routing themselves.
            var conversation = NewConversation(out var connection, out _, out _);
            GuardrailTriggeredArgs? received = null;
            conversation.GuardrailTriggered += args => received = args;

            connection.FireMessage(
                new GuardrailTriggered
                {
                    GuardrailTriggeredEvent = new GuardrailTriggeredEvent
                    {
                        GuardrailName = "profanity_filter",
                    },
                }
            );

            Assert.IsNotNull(received);
            Assert.AreEqual("profanity_filter", received!.GuardrailName);
        }

        // Audio: state tracking + interrupt gating ----------------------------

        [Test]
        public void Dispatch_Audio_FiresAudioReceivedAndAdvancesState()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            AudioResponseArgs? received = null;
            conversation.AudioReceived += args => received = args;
            var modeChanges = new List<Mode>();
            conversation.ModeChanged += mode => modeChanges.Add(mode);

            // "YWJj" is base64 for "abc"; needs to be valid base64 now that
            // HandleAudioResponse decodes the payload to feed the output.
            connection.FireMessage(MakeAudio(eventId: 12, payload: "YWJj"));

            Assert.AreEqual("YWJj", received!.AudioBase64);
            Assert.AreEqual(12, received.EventId);
            Assert.AreEqual(Mode.Speaking, conversation.Mode);
            CollectionAssert.AreEqual(new[] { Mode.Speaking }, modeChanges);
        }

        [Test]
        public void Dispatch_Audio_PropagatesAlignmentOnArgs()
        {
            // Lip-sync subscribers read AudioResponseArgs.Alignment without
            // needing a parallel onAudioAlignment event. Pin that the
            // generated alignment payload reaches the public args record
            // unchanged.
            var conversation = NewConversation(out var connection, out _, out _);
            AudioResponseArgs? received = null;
            conversation.AudioReceived += args => received = args;

            var alignment = new AudioEventAlignment
            {
                Chars = new[] { "H", "i" },
                CharStartTimesMs = new[] { 0, 80 },
                CharDurationsMs = new[] { 80, 90 },
            };
            connection.FireMessage(
                new AudioResponse
                {
                    AudioEvent = new AudioEvent
                    {
                        AudioBase64 = "YWJj",
                        EventId = 14,
                        Alignment = alignment,
                    },
                }
            );

            Assert.IsNotNull(received);
            Assert.AreSame(alignment, received!.Alignment);
        }

        [Test]
        public void Dispatch_Audio_DecodesBase64AndPushesToOutputController()
        {
            var conversation = NewConversation(out var connection, out _, out var output);

            // "YWJj" decodes to [0x61, 0x62, 0x63] = "abc".
            connection.FireMessage(MakeAudio(eventId: 3, payload: "YWJj"));

            Assert.AreEqual(1, output.PushAudioCalls.Count);
            CollectionAssert.AreEqual(new byte[] { 0x61, 0x62, 0x63 }, output.PushAudioCalls[0]);
        }

        [Test]
        public void Dispatch_Audio_EmptyPayloadSkipsPushAudio()
        {
            // Bridged WebGL strips audio_base_64 JS-side, so the wire event
            // arrives at C# with an empty payload. The output controller
            // shouldn't see a PushAudio call in that case.
            var conversation = NewConversation(out var connection, out _, out var output);

            connection.FireMessage(MakeAudio(eventId: 4));

            Assert.AreEqual(0, output.PushAudioCalls.Count);
        }

        [Test]
        public void Dispatch_Audio_MalformedBase64_RaisesErrorWithoutCrashing()
        {
            var conversation = NewConversation(out var connection, out _, out var output);
            string? raised = null;
            conversation.ErrorOccurred += msg => raised = msg;

            // "abc" is 3 chars — not a multiple of 4, so FromBase64String
            // throws FormatException. The router should surface that as an
            // ErrorOccurred event and keep going.
            connection.FireMessage(MakeAudio(eventId: 6, payload: "abc"));

            Assert.IsNotNull(raised);
            StringAssert.Contains("audio", raised!.ToLowerInvariant());
            Assert.AreEqual(0, output.PushAudioCalls.Count);
            // Mode still advances even when the decode failed — AudioReceived
            // fired (user got the event), and currentEventId tracking should
            // not be derailed by a malformed payload.
            Assert.AreEqual(Mode.Speaking, conversation.Mode);
        }

        [Test]
        public void Dispatch_Audio_GatedByLastInterruptTimestamp_DropsStaleChunk()
        {
            var conversation = NewConversation(out var connection, out _, out _);

            // Interrupt at event_id=10 sets the bookmark, switches to listening.
            connection.FireMessage(
                new Interruption { InterruptionEvent = new InterruptionEvent { EventId = 10 } }
            );

            int audioCount = 0;
            conversation.AudioReceived += _ => audioCount++;

            // Stale chunk (event_id < bookmark) — dropped.
            connection.FireMessage(MakeAudio(eventId: 5));
            // Equal bookmark — passes (mirrors JS `<=` check).
            connection.FireMessage(MakeAudio(eventId: 10));
            // Fresh chunk — passes.
            connection.FireMessage(MakeAudio(eventId: 11));

            Assert.AreEqual(2, audioCount);
        }

        [Test]
        public void Dispatch_Audio_FlipsCanSendFeedbackOnFirstChunk()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            var transitions = new List<bool>();
            conversation.CanSendFeedbackChanged += v => transitions.Add(v);

            Assert.IsFalse(conversation.CanSendFeedback);
            connection.FireMessage(MakeAudio(eventId: 4));

            Assert.IsTrue(conversation.CanSendFeedback);
            CollectionAssert.AreEqual(new[] { true }, transitions);
        }

        // Interruption: bookmark + output flush + listening mode -------------

        [Test]
        public void Dispatch_Interruption_FiresInterruptedSwitchesToListeningAndFlushesOutput()
        {
            var conversation = NewConversation(out var connection, out _, out var output);
            // Prime mode to Speaking via an audio chunk first.
            connection.FireMessage(MakeAudio(eventId: 1));
            Assert.AreEqual(Mode.Speaking, conversation.Mode);

            InterruptionArgs? received = null;
            conversation.Interrupted += args => received = args;

            connection.FireMessage(
                new Interruption { InterruptionEvent = new InterruptionEvent { EventId = 9 } }
            );

            Assert.AreEqual(9, received!.EventId);
            Assert.AreEqual(Mode.Listening, conversation.Mode);
            Assert.AreEqual(1, output.InterruptCallCount);
        }

        // Ping auto-pong: no user event, pong written to connection ----------

        [Test]
        public void Dispatch_Ping_SendsPongAndSuppressesUserEvent()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            // No public Ping event surfaced on Conversation — the dispatcher
            // suppresses it. We assert the only side-effect is the pong.

            connection.FireMessage(
                new Ping
                {
                    PingEvent = new PingEvent { EventId = 42, PingMs = 100 },
                }
            );

            Assert.AreEqual(1, connection.Sent.Count);
            var pong = connection.Sent[0] as Pong;
            Assert.IsNotNull(pong);
            Assert.AreEqual(42, pong!.EventId);
            Assert.AreEqual("pong", pong.Type);
        }

        // Client tool dispatch (Phase 4.4) ---------------------------------

        private static ClientToolCall MakeToolCall(
            string toolName,
            string toolCallId = "tc-1",
            Dictionary<string, dynamic>? parameters = null,
            int eventId = 1,
            bool expectsResponse = true
        )
        {
            return new ClientToolCall
            {
                ClientToolCallData = new ClientToolCallEvent
                {
                    ToolName = toolName,
                    ToolCallId = toolCallId,
                    Parameters = parameters ?? new Dictionary<string, dynamic>(),
                    EventId = eventId,
                    ExpectsResponse = expectsResponse,
                },
            };
        }

        private sealed record GreetParams(string Name);

        private sealed record WeatherResult(string City, double TempC);

        [Test]
        public void RegisterTool_Sync_StringResult_SendsLiteralResult()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            conversation.RegisterTool<GreetParams, string>("greet", p => $"Hello, {p.Name}!");

            connection.FireMessage(
                MakeToolCall(
                    toolName: "greet",
                    parameters: new Dictionary<string, dynamic> { ["name"] = "Alice" }
                )
            );

            Assert.AreEqual(1, connection.Sent.Count);
            var result = connection.Sent[0] as ClientToolResult;
            Assert.IsNotNull(result);
            Assert.AreEqual("tc-1", result!.ToolCallId);
            Assert.AreEqual("Hello, Alice!", result.Result);
            Assert.IsFalse(result.IsError);
            Assert.IsNull(result.ErrorType);
        }

        [Test]
        public void RegisterTool_Sync_ObjectResult_SerialisesViaNewtonsoft()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            conversation.RegisterTool<GreetParams, WeatherResult>(
                "weather",
                _ => new WeatherResult("Copenhagen", 17.5)
            );

            connection.FireMessage(
                MakeToolCall(
                    toolName: "weather",
                    parameters: new Dictionary<string, dynamic> { ["name"] = "anywhere" }
                )
            );

            var result = (ClientToolResult)connection.Sent[0];
            // Newtonsoft default: PascalCase property names, no special config.
            Assert.AreEqual("{\"City\":\"Copenhagen\",\"TempC\":17.5}", result.Result);
        }

        [Test]
        public void RegisterTool_Async_AwaitsHandlerBeforeSendingResult()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            var source = new AwaitableCompletionSource<string>();
            conversation.RegisterTool<GreetParams, string>("slow", _ => source.Awaitable);

            connection.FireMessage(
                MakeToolCall(
                    toolName: "slow",
                    parameters: new Dictionary<string, dynamic> { ["name"] = "x" }
                )
            );

            // Handler hasn't completed yet — nothing sent.
            Assert.AreEqual(0, connection.Sent.Count);

            source.SetResult("ready");

            var result = (ClientToolResult)connection.Sent[0];
            Assert.AreEqual("ready", result.Result);
            Assert.IsFalse(result.IsError);
        }

        [Test]
        public void Dispatch_ClientToolCall_UnknownTool_SendsIsErrorAndRaisesError()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            string? errorMessage = null;
            conversation.ErrorOccurred += msg => errorMessage = msg;

            connection.FireMessage(MakeToolCall(toolName: "missing"));

            var result = (ClientToolResult)connection.Sent[0];
            Assert.IsTrue(result.IsError);
            Assert.AreEqual("tool_not_found", result.ErrorType);
            StringAssert.Contains("missing", result.Result);
            StringAssert.Contains("missing", errorMessage!);
        }

        [Test]
        public void Dispatch_ClientToolCall_HandlerThrows_SendsIsErrorAndRaisesError()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            conversation.RegisterTool<GreetParams, string>(
                "boom",
                (Func<GreetParams, string>)(_ => throw new InvalidOperationException("kaboom"))
            );
            string? errorMessage = null;
            conversation.ErrorOccurred += msg => errorMessage = msg;

            connection.FireMessage(
                MakeToolCall(
                    toolName: "boom",
                    parameters: new Dictionary<string, dynamic> { ["name"] = "x" }
                )
            );

            var result = (ClientToolResult)connection.Sent[0];
            Assert.IsTrue(result.IsError);
            Assert.IsNull(result.ErrorType);
            Assert.AreEqual("kaboom", result.Result);
            StringAssert.Contains("kaboom", errorMessage!);
        }

        [Test]
        public void Dispatch_ClientToolCall_ClientToolException_PreservesErrorType()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            conversation.RegisterTool<GreetParams, string>(
                "auth",
                (Func<GreetParams, string>)(
                    _ => throw new ClientToolException("not allowed", errorType: "unauthorized")
                )
            );

            connection.FireMessage(
                MakeToolCall(
                    toolName: "auth",
                    parameters: new Dictionary<string, dynamic> { ["name"] = "x" }
                )
            );

            var result = (ClientToolResult)connection.Sent[0];
            Assert.IsTrue(result.IsError);
            Assert.AreEqual("unauthorized", result.ErrorType);
            Assert.AreEqual("not allowed", result.Result);
        }

        [Test]
        public void Dispatch_ClientToolCall_ExpectsResponseFalse_SuppressesResultSend()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            var invoked = false;
            conversation.RegisterTool<GreetParams, string>(
                "fire_and_forget",
                _ =>
                {
                    invoked = true;
                    return "result";
                }
            );

            connection.FireMessage(
                MakeToolCall(
                    toolName: "fire_and_forget",
                    parameters: new Dictionary<string, dynamic> { ["name"] = "x" },
                    expectsResponse: false
                )
            );

            Assert.IsTrue(invoked);
            Assert.AreEqual(0, connection.Sent.Count);
        }

        [Test]
        public void Dispatch_ClientToolCall_ParamsDeserialisationFailure_RaisesErrorWithIsError()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            // Handler expects { name: string } but the wire delivers no
            // properties at all — Newtonsoft will throw on the strict path.
            conversation.RegisterTool<GreetParams, string>("needs_name", p => $"Hi {p.Name}");
            string? errorMessage = null;
            conversation.ErrorOccurred += msg => errorMessage = msg;

            // Send a payload with the WRONG shape: "name" missing AND a
            // numeric value that won't bind to the string property. The
            // missing property alone is too lenient (Newtonsoft tolerates
            // missing props and leaves the record's required member null);
            // a type mismatch reliably trips the converter.
            connection.FireMessage(
                MakeToolCall(
                    toolName: "needs_name",
                    parameters: new Dictionary<string, dynamic>
                    {
                        ["Name"] = new Dictionary<string, dynamic> { ["nested"] = "x" },
                    }
                )
            );

            var result = (ClientToolResult)connection.Sent[0];
            Assert.IsTrue(result.IsError);
            Assert.IsNotNull(errorMessage);
            StringAssert.Contains("needs_name", errorMessage!);
        }

        [Test]
        public void RegisterTool_Overwrite_LogsWarningAndReplacesHandler()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            conversation.RegisterTool<GreetParams, string>("dup", _ => "first");

            LogAssert.Expect(LogType.Warning, "Client tool 'dup' is being overwritten.");
            conversation.RegisterTool<GreetParams, string>("dup", _ => "second");

            connection.FireMessage(
                MakeToolCall(
                    toolName: "dup",
                    parameters: new Dictionary<string, dynamic> { ["name"] = "x" }
                )
            );

            var result = (ClientToolResult)connection.Sent[0];
            Assert.AreEqual("second", result.Result);
        }

        [Test]
        public void UnregisterTool_KnownName_ReturnsTrueAndPreventsDispatch()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            conversation.RegisterTool<GreetParams, string>("temp", _ => "x");

            Assert.IsTrue(conversation.UnregisterTool("temp"));
            Assert.IsFalse(conversation.UnregisterTool("temp"));

            connection.FireMessage(
                MakeToolCall(
                    toolName: "temp",
                    parameters: new Dictionary<string, dynamic> { ["name"] = "x" }
                )
            );

            // Tool no longer registered → tool_not_found error path.
            var result = (ClientToolResult)connection.Sent[0];
            Assert.IsTrue(result.IsError);
            Assert.AreEqual("tool_not_found", result.ErrorType);
        }

        [Test]
        public void RegisterTool_NullHandler_Throws()
        {
            var conversation = NewConversation(out _, out _, out _);
            Assert.Throws<ArgumentNullException>(() =>
                conversation.RegisterTool<GreetParams, string>(
                    "x",
                    (Func<GreetParams, string>)null!
                )
            );
            Assert.Throws<ArgumentNullException>(() =>
                conversation.RegisterTool<GreetParams, string>(
                    "x",
                    (Func<GreetParams, Awaitable<string>>)null!
                )
            );
        }

        [Test]
        public void RegisterTool_EmptyName_Throws()
        {
            var conversation = NewConversation(out _, out _, out _);
            Assert.Throws<ArgumentException>(() =>
                conversation.RegisterTool<GreetParams, string>("", _ => "x")
            );
        }

        // UnhandledClientToolCall (parity row 10) --------------------------

        [Test]
        public void UnhandledClientToolCall_WithSubscriber_SuppressesErrorAndToolResult()
        {
            // Mirrors BaseConversation.handleClientToolCall's else branch:
            // when onUnhandledClientToolCall is registered the SDK hands the
            // raw call to the subscriber and returns, without firing onError
            // or sending a client_tool_result is_error=true response.
            var conversation = NewConversation(out var connection, out _, out _);
            ClientToolCallArgs? received = null;
            conversation.UnhandledClientToolCall += args => received = args;
            string? errorMessage = null;
            conversation.ErrorOccurred += msg => errorMessage = msg;

            connection.FireMessage(
                MakeToolCall(
                    toolName: "missing",
                    toolCallId: "tc-99",
                    parameters: new Dictionary<string, dynamic> { ["q"] = "weather" },
                    eventId: 4
                )
            );

            Assert.IsNotNull(received);
            Assert.AreEqual("missing", received!.ToolName);
            Assert.AreEqual("tc-99", received.ToolCallId);
            Assert.AreEqual(4, received.EventId);
            Assert.AreEqual("weather", (string)received.Parameters["q"]);
            Assert.AreEqual(0, connection.Sent.Count);
            Assert.IsNull(errorMessage);
        }

        [Test]
        public void UnhandledClientToolCall_AfterAllSubscribersRemoved_RestoresDefault()
        {
            // Detaching every subscriber restores the legacy tool_not_found
            // path — the suppression check is `event != null`, not a one-shot
            // opt-in.
            var conversation = NewConversation(out var connection, out _, out _);
            Action<ClientToolCallArgs> handler = _ => { };
            conversation.UnhandledClientToolCall += handler;
            conversation.UnhandledClientToolCall -= handler;

            connection.FireMessage(MakeToolCall(toolName: "missing"));

            Assert.AreEqual(1, connection.Sent.Count);
            var result = (ClientToolResult)connection.Sent[0];
            Assert.IsTrue(result.IsError);
            Assert.AreEqual("tool_not_found", result.ErrorType);
        }

        [Test]
        public void UnhandledClientToolCall_KnownTool_DoesNotFire()
        {
            // A registered tool shouldn't trip the unhandled fallback path.
            var conversation = NewConversation(out var connection, out _, out _);
            var unhandledCount = 0;
            conversation.UnhandledClientToolCall += _ => unhandledCount++;
            conversation.RegisterTool<GreetParams, string>("greet", p => $"Hi {p.Name}");

            connection.FireMessage(
                MakeToolCall(
                    toolName: "greet",
                    parameters: new Dictionary<string, dynamic> { ["name"] = "Bob" }
                )
            );

            Assert.AreEqual(0, unhandledCount);
            var result = (ClientToolResult)connection.Sent[0];
            Assert.IsFalse(result.IsError);
            Assert.AreEqual("Hi Bob", result.Result);
        }

        // end_call shortcut (agent_tool_response_full_payload) -------------

        [Test]
        public void Dispatch_AgentToolResponseFullPayload_EndCall_TriggersDisconnect()
        {
            var conversation = NewConversation(out var connection, out var input, out var output);
            // Prime status to Connected so EndSessionWithDetails actually fires.
            conversation.UpdateStatus(Status.Connected);
            DisconnectionDetails? details = null;
            conversation.Disconnected += d => details = d;
            // The public event fires *before* the end_call shortcut runs the
            // teardown, so a subscriber sees the call that caused the
            // disconnect.
            AgentToolRespondedArgs? respondedArgs = null;
            conversation.AgentToolResponded += args => respondedArgs = args;

            connection.FireMessage(
                new AgentToolResponseFullPayload
                {
                    AgentToolResponseFullPayloadData = new AgentToolResponseFullPayloadEvent
                    {
                        ToolName = "end_call",
                        ToolCallId = "tc-end",
                        ToolType = "system",
                        EventId = 1,
                        IsError = false,
                        IsCalled = true,
                        FullToolResult = "",
                    },
                }
            );

            Assert.IsNotNull(details);
            Assert.AreEqual(DisconnectionReason.Agent, details!.Reason);
            Assert.AreEqual("end_call", details.Context?.Type);
            Assert.AreEqual(1, connection.CloseCallCount);
            Assert.AreEqual(1, input.CloseCallCount);
            Assert.AreEqual(1, output.CloseCallCount);
            Assert.IsNotNull(respondedArgs);
            Assert.AreEqual("end_call", respondedArgs!.ToolName);
        }

        [Test]
        public void Dispatch_AgentToolResponse_EndCall_TriggersDisconnect()
        {
            // JS SDK's handleAgentToolResponse runs the same end_call shortcut
            // the full-payload arm does. Mirror that here so a slim wire frame
            // with tool_name=end_call also drives teardown.
            var conversation = NewConversation(out var connection, out var input, out var output);
            conversation.UpdateStatus(Status.Connected);
            DisconnectionDetails? details = null;
            conversation.Disconnected += d => details = d;
            AgentToolRespondedArgs? respondedArgs = null;
            conversation.AgentToolResponded += args => respondedArgs = args;

            connection.FireMessage(
                new AgentToolResponse
                {
                    AgentToolResponseData = new AgentToolResponseEvent
                    {
                        ToolName = "end_call",
                        ToolCallId = "tc-end",
                        ToolType = "system",
                        EventId = 1,
                        IsError = false,
                        IsCalled = true,
                    },
                }
            );

            Assert.IsNotNull(details);
            Assert.AreEqual(DisconnectionReason.Agent, details!.Reason);
            Assert.AreEqual("end_call", details.Context?.Type);
            Assert.AreEqual(1, connection.CloseCallCount);
            Assert.AreEqual(1, input.CloseCallCount);
            Assert.AreEqual(1, output.CloseCallCount);
            Assert.IsNotNull(respondedArgs);
            Assert.AreEqual("end_call", respondedArgs!.ToolName);
        }

        [Test]
        public void Dispatch_AgentToolResponseFullPayload_NonEndCall_DoesNothing()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            conversation.UpdateStatus(Status.Connected);
            DisconnectionDetails? details = null;
            conversation.Disconnected += d => details = d;

            connection.FireMessage(
                new AgentToolResponseFullPayload
                {
                    AgentToolResponseFullPayloadData = new AgentToolResponseFullPayloadEvent
                    {
                        ToolName = "lookup_user",
                        ToolCallId = "tc-1",
                        ToolType = "function",
                        EventId = 1,
                        IsError = false,
                        IsCalled = true,
                        FullToolResult = "{}",
                    },
                }
            );

            Assert.IsNull(details);
            Assert.AreEqual(0, connection.CloseCallCount);
        }

        // SendFeedback --------------------------------------------------------

        [Test]
        public void SendFeedback_BeforeAnyAudio_LogsWarningAndDoesNotSend()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            LogAssert.Expect(
                LogType.Warning,
                "Cannot send feedback: the conversation has not started yet."
            );

            conversation.SendFeedback(like: true);

            Assert.AreEqual(0, connection.Sent.Count);
        }

        [Test]
        public void SendFeedback_AfterAudio_SendsFeedbackAndFlipsCanSendFeedbackOff()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            // Prime: an audio chunk at event_id=8 unblocks feedback.
            connection.FireMessage(MakeAudio(eventId: 8));
            Assert.IsTrue(conversation.CanSendFeedback);

            conversation.SendFeedback(like: false);

            Assert.AreEqual(1, connection.Sent.Count);
            var feedback = connection.Sent[0] as Feedback;
            Assert.IsNotNull(feedback);
            Assert.AreEqual("dislike", feedback!.Score);
            Assert.AreEqual(8, feedback.EventId);
            Assert.AreEqual("feedback", feedback.Type);
            Assert.IsFalse(conversation.CanSendFeedback);
        }

        [Test]
        public void SendFeedback_Twice_WarnsOnSecondCall()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            connection.FireMessage(MakeAudio(eventId: 8));
            conversation.SendFeedback(like: true);
            // Second call hits the post-submit warning path.
            LogAssert.Expect(
                LogType.Warning,
                "Cannot send feedback: feedback has already been sent for the current response."
            );

            conversation.SendFeedback(like: true);

            Assert.AreEqual(1, connection.Sent.Count);
        }

        // Connection-level passthroughs --------------------------------------

        [Test]
        public void Connection_OnModeChange_UpdatesModeAndFiresEvent()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            var changes = new List<Mode>();
            conversation.ModeChanged += changes.Add;

            connection.FireModeChange(Mode.Speaking);
            connection.FireModeChange(Mode.Speaking); // dedupe
            connection.FireModeChange(Mode.Listening);

            Assert.AreEqual(Mode.Listening, conversation.Mode);
            CollectionAssert.AreEqual(new[] { Mode.Speaking, Mode.Listening }, changes);
        }

        // Outgoing messages (public Conversation API) ------------------------

        [Test]
        public void SendUserMessage_SendsUserMessageWithText()
        {
            var conversation = NewConversation(out var connection, out _, out _);

            conversation.SendUserMessage("hello world");

            Assert.AreEqual(1, connection.Sent.Count);
            var msg = connection.Sent[0] as UserMessage;
            Assert.IsNotNull(msg);
            Assert.AreEqual("hello world", msg!.Text);
            Assert.AreEqual("user_message", msg.Type);
        }

        [Test]
        public void SendContextualUpdate_SendsContextualUpdateWithText()
        {
            var conversation = NewConversation(out var connection, out _, out _);

            conversation.SendContextualUpdate("player just opened the inventory");

            Assert.AreEqual(1, connection.Sent.Count);
            var msg = connection.Sent[0] as ContextualUpdate;
            Assert.IsNotNull(msg);
            Assert.AreEqual("player just opened the inventory", msg!.Text);
            Assert.AreEqual("contextual_update", msg.Type);
        }

        [Test]
        public void SendUserActivity_SendsUserActivityEvent()
        {
            var conversation = NewConversation(out var connection, out _, out _);

            conversation.SendUserActivity();

            Assert.AreEqual(1, connection.Sent.Count);
            var msg = connection.Sent[0] as UserActivity;
            Assert.IsNotNull(msg);
            Assert.AreEqual("user_activity", msg!.Type);
        }

        [Test]
        public void SendMultimodalMessage_TextOnly_OmitsFileField()
        {
            var conversation = NewConversation(out var connection, out _, out _);

            conversation.SendMultimodalMessage(text: "hi there");

            Assert.AreEqual(1, connection.Sent.Count);
            var msg = connection.Sent[0] as MultimodalMessage;
            Assert.IsNotNull(msg);
            Assert.AreEqual("multimodal_message", msg!.Type);
            Assert.IsNotNull(msg.Text);
            Assert.AreEqual("user_message", msg.Text!.Type);
            Assert.AreEqual("hi there", msg.Text.TextData);
            Assert.IsNull(msg.File);
        }

        [Test]
        public void SendMultimodalMessage_FileOnly_OmitsTextField()
        {
            var conversation = NewConversation(out var connection, out _, out _);

            conversation.SendMultimodalMessage(fileId: "file_abc123");

            Assert.AreEqual(1, connection.Sent.Count);
            var msg = connection.Sent[0] as MultimodalMessage;
            Assert.IsNotNull(msg);
            Assert.IsNull(msg!.Text);
            Assert.IsNotNull(msg.File);
            Assert.AreEqual("file_input", msg.File!.Type);
            Assert.AreEqual("file_abc123", msg.File.FileId);
        }

        [Test]
        public void SendMultimodalMessage_BothFields_PopulatesBoth()
        {
            var conversation = NewConversation(out var connection, out _, out _);

            conversation.SendMultimodalMessage(text: "describe this", fileId: "file_xyz");

            Assert.AreEqual(1, connection.Sent.Count);
            var msg = connection.Sent[0] as MultimodalMessage;
            Assert.IsNotNull(msg);
            Assert.AreEqual("describe this", msg!.Text!.TextData);
            Assert.AreEqual("file_xyz", msg.File!.FileId);
        }

        [Test]
        public void SendMultimodalMessage_EmptyStrings_OmitsBothFields()
        {
            // Matches the JS SDK's truthy-check semantics: empty strings are
            // treated the same as null and the corresponding wire field is
            // omitted. The server is then free to reject the resulting
            // payload — same contract as JS.
            var conversation = NewConversation(out var connection, out _, out _);

            conversation.SendMultimodalMessage(text: "", fileId: "");

            Assert.AreEqual(1, connection.Sent.Count);
            var msg = connection.Sent[0] as MultimodalMessage;
            Assert.IsNotNull(msg);
            Assert.IsNull(msg!.Text);
            Assert.IsNull(msg.File);
        }

        [Test]
        public void SendMultimodalMessage_NoArgs_OmitsBothFields()
        {
            var conversation = NewConversation(out var connection, out _, out _);

            conversation.SendMultimodalMessage();

            Assert.AreEqual(1, connection.Sent.Count);
            var msg = connection.Sent[0] as MultimodalMessage;
            Assert.IsNotNull(msg);
            Assert.IsNull(msg!.Text);
            Assert.IsNull(msg.File);
        }

        [Test]
        public void SendMCPToolApprovalResult_Approved_SendsWireEvent()
        {
            var conversation = NewConversation(out var connection, out _, out _);

            conversation.SendMCPToolApprovalResult("call_abc123", isApproved: true);

            Assert.AreEqual(1, connection.Sent.Count);
            var msg = connection.Sent[0] as McpToolApprovalResult;
            Assert.IsNotNull(msg);
            Assert.AreEqual("mcp_tool_approval_result", msg!.Type);
            Assert.AreEqual("call_abc123", msg.ToolCallId);
            Assert.IsTrue(msg.IsApproved);
        }

        [Test]
        public void SendMCPToolApprovalResult_Rejected_SendsWireEvent()
        {
            var conversation = NewConversation(out var connection, out _, out _);

            conversation.SendMCPToolApprovalResult("call_xyz", isApproved: false);

            Assert.AreEqual(1, connection.Sent.Count);
            var msg = connection.Sent[0] as McpToolApprovalResult;
            Assert.IsNotNull(msg);
            Assert.AreEqual("call_xyz", msg!.ToolCallId);
            Assert.IsFalse(msg.IsApproved);
        }

        // Audio device control (public Conversation API) ---------------------

        [Test]
        public void ChangeInputDevice_ForwardsConfigAndFormatToInputController()
        {
            var conversation = NewConversation(out _, out var input, out _);
            var config = new InputDeviceConfig(
                InputDeviceId: "mic-2",
                PreferHeadphonesForIosDevices: true
            );
            var format = new FormatConfig("pcm", 22050);

            conversation.ChangeInputDevice(config, format).GetAwaiter().GetResult();

            Assert.AreEqual(1, input.SetDeviceCallCount);
            Assert.AreSame(config, input.LastSetDeviceConfig);
            Assert.AreEqual(format, input.LastSetDeviceFormat);
        }

        [Test]
        public void ChangeInputDevice_DefaultArgs_ForwardsNulls()
        {
            var conversation = NewConversation(out _, out var input, out _);

            conversation.ChangeInputDevice().GetAwaiter().GetResult();

            Assert.AreEqual(1, input.SetDeviceCallCount);
            Assert.IsNull(input.LastSetDeviceConfig);
            Assert.IsNull(input.LastSetDeviceFormat);
        }

        [Test]
        public void ChangeInputDevice_PropagatesControllerException()
        {
            var conversation = NewConversation(out _, out var input, out _);
            input.SetDeviceException = new InvalidOperationException("mic in use");

            var task = conversation.ChangeInputDevice(new InputDeviceConfig("mic-x"));
            var ex = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
            Assert.AreEqual("mic in use", ex!.Message);
        }

        [Test]
        public void ChangeOutputDevice_ForwardsConfigAndFormatToOutputController()
        {
            var conversation = NewConversation(out _, out _, out var output);
            var config = new OutputDeviceConfig(OutputDeviceId: "speaker-2");
            var format = new FormatConfig("pcm", 48000);

            conversation.ChangeOutputDevice(config, format).GetAwaiter().GetResult();

            Assert.AreEqual(1, output.SetDeviceCallCount);
            Assert.AreSame(config, output.LastSetDeviceConfig);
            Assert.AreEqual(format, output.LastSetDeviceFormat);
        }

        [Test]
        public void ChangeOutputDevice_DefaultArgs_ForwardsNulls()
        {
            var conversation = NewConversation(out _, out _, out var output);

            conversation.ChangeOutputDevice().GetAwaiter().GetResult();

            Assert.AreEqual(1, output.SetDeviceCallCount);
            Assert.IsNull(output.LastSetDeviceConfig);
            Assert.IsNull(output.LastSetDeviceFormat);
        }

        [Test]
        public void ChangeOutputDevice_PropagatesControllerException()
        {
            var conversation = NewConversation(out _, out _, out var output);
            output.SetDeviceException = new InvalidOperationException("speaker missing");

            var task = conversation.ChangeOutputDevice(new OutputDeviceConfig("speaker-x"));
            var ex = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
            Assert.AreEqual("speaker missing", ex!.Message);
        }

        // File upload (public Conversation API) ------------------------------

        [Test]
        public void UploadFileAsync_ForwardsArgsToUploaderAndReturnsFileId()
        {
            var conversation = NewConversation(out _, out _, out _, out var uploader);
            uploader.Result = "file_xyz789";
            byte[] payload = new byte[] { 0xde, 0xad, 0xbe, 0xef };

            string fileId = conversation
                .UploadFileAsync(payload, "image/png", "screenshot.png")
                .GetAwaiter()
                .GetResult();

            Assert.AreEqual("file_xyz789", fileId);
            Assert.AreEqual(1, uploader.CallCount);
            Assert.AreSame(payload, uploader.LastBytes);
            Assert.AreEqual("image/png", uploader.LastMimeType);
            Assert.AreEqual("screenshot.png", uploader.LastFilename);
        }

        [Test]
        public void UploadFileAsync_DefaultFilename_ForwardsNullThrough()
        {
            var conversation = NewConversation(out _, out _, out _, out var uploader);

            conversation
                .UploadFileAsync(new byte[] { 1, 2 }, "image/jpeg")
                .GetAwaiter()
                .GetResult();

            Assert.AreEqual(1, uploader.CallCount);
            Assert.IsNull(uploader.LastFilename);
        }

        [Test]
        public void UploadFileAsync_PropagatesUploaderException()
        {
            var conversation = NewConversation(out _, out _, out _, out var uploader);
            uploader.Exception = new InvalidOperationException("Upload failed: 413 file too large");

            var task = conversation.UploadFileAsync(new byte[] { 0 }, "image/png");
            var ex = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
            StringAssert.Contains("file too large", ex!.Message);
        }

        // Input controller → connection routing ------------------------------

        [Test]
        public void InputAudioChunk_WrappedAsUserAudioChunk_AndForwardedToConnection()
        {
            // Mirrors attachInputToConnection.js — every PCM chunk the input
            // controller emits is base64-encoded and forwarded as a
            // user_audio_chunk wire event.
            var conversation = NewConversation(out var connection, out var input, out _);
            byte[] pcm = new byte[] { 0x01, 0x00, 0xff, 0x7f, 0x00, 0x80 };

            input.FireAudioChunkAvailable(pcm);

            Assert.AreEqual(1, connection.Sent.Count);
            var msg = connection.Sent[0] as UserAudioChunk;
            Assert.IsNotNull(msg);
            Assert.AreEqual(System.Convert.ToBase64String(pcm), msg!.UserAudioChunkData);
        }

        [Test]
        public void InputAudioChunk_EmptyPayload_NotForwarded()
        {
            // A zero-length chunk is meaningless on the wire; the JS SDK never
            // sees one because the worklet only posts when the buffer fills.
            var conversation = NewConversation(out var connection, out var input, out _);

            input.FireAudioChunkAvailable(System.Array.Empty<byte>());

            Assert.AreEqual(0, connection.Sent.Count);
        }

        // Audio control passthroughs (public Conversation API) --------------

        [Test]
        public void SetVolume_ForwardsValueToOutputController()
        {
            var conversation = NewConversation(out _, out _, out var output);

            conversation.SetVolume(0.42f);

            Assert.AreEqual(1, output.SetVolumeCallCount);
            Assert.AreEqual(0.42f, output.LastVolume);
        }

        [Test]
        public void SetMicMuted_True_ForwardsToInputControllerAndReflectsInState()
        {
            var conversation = NewConversation(out _, out var input, out _);

            _ = conversation.SetMicMuted(true);

            Assert.AreEqual(true, input.LastMuteRequest);
            Assert.IsTrue(input.IsMuted);
        }

        [Test]
        public void SetMicMuted_False_ForwardsToInputController()
        {
            var conversation = NewConversation(out _, out var input, out _);
            _ = conversation.SetMicMuted(true);

            _ = conversation.SetMicMuted(false);

            Assert.AreEqual(false, input.LastMuteRequest);
            Assert.IsFalse(input.IsMuted);
        }

        [Test]
        public void GetInputVolume_DelegatesToInputController()
        {
            var conversation = NewConversation(out _, out var input, out _);
            input.VolumeReturnValue = 0.73f;

            var v = conversation.GetInputVolume();

            Assert.AreEqual(0.73f, v);
            Assert.AreEqual(1, input.GetVolumeCallCount);
        }

        [Test]
        public void GetOutputVolume_DelegatesToOutputController()
        {
            var conversation = NewConversation(out _, out _, out var output);
            output.VolumeReturnValue = 0.55f;

            var v = conversation.GetOutputVolume();

            Assert.AreEqual(0.55f, v);
            Assert.AreEqual(1, output.GetVolumeCallCount);
        }

        [Test]
        public void GetInputByteFrequencyData_DelegatesToInputControllerWithSameBuffer()
        {
            var conversation = NewConversation(out _, out var input, out _);
            var buffer = new byte[32];

            conversation.GetInputByteFrequencyData(buffer);

            Assert.AreSame(buffer, input.LastByteFrequencyBuffer);
        }

        [Test]
        public void GetOutputByteFrequencyData_DelegatesToOutputControllerWithSameBuffer()
        {
            var conversation = NewConversation(out _, out _, out var output);
            var buffer = new byte[64];

            conversation.GetOutputByteFrequencyData(buffer);

            Assert.AreSame(buffer, output.LastByteFrequencyBuffer);
        }

        // Identity passthrough ----------------------------------------------

        [Test]
        public void ConversationId_DelegatesToConnection()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            connection.ConversationId = "conv-xyz";

            Assert.AreEqual("conv-xyz", conversation.ConversationId);
        }

        // Lifecycle ---------------------------------------------------------

        [Test]
        public void StartSessionAsync_NullOptions_ThrowsArgumentNull()
        {
            Assert.Throws<ArgumentNullException>(() => Conversation.StartSessionAsync(null!));
        }

        [Test]
        public void StartSessionAsync_OnNonWebGL_RoutesThroughBridge_PropagatesBridgeException()
        {
            // BridgedSessionLauncher's [InitializeOnLoadMethod] registers
            // BridgedSession.StartAsync as Conversation.SessionFactory at
            // Editor load, so a static StartSessionAsync call dispatches into
            // the bridge — which, off-WebGL, settles the first factory call
            // with a BridgeException carrying the DllImport's
            // PlatformNotSupportedException. If this assertion changes shape,
            // the WebGL launcher has stopped registering its factory.
            //
            // NativeSessionLauncher (#9b) also registers in the Editor when
            // the active build target is non-WebGL — last-write-wins, so pin
            // the bridged factory for the scope of this test and restore on
            // teardown so we exercise *this* launcher's path explicitly.
            var previousFactory = Conversation.SessionFactory;
            Conversation.SessionFactory = ElevenLabs
                .WebGL
                .Bridged
                .BridgedSessionLauncher
                .StartAsync;
            try
            {
                var task = Conversation.StartSessionAsync(
                    new ConversationOptions { AgentId = "agent-test" }
                );
                Assert.Throws<BridgeException>(() => task.GetAwaiter().GetResult());
                // Stale registry state can bleed into sibling tests; clean up
                // both the promise that surfaced the failure and any callback
                // BridgedWebSocketConnection registered before the throw.
                ElevenLabs.WebGL.Internal.PromiseRegistry.ResetForTests();
                ElevenLabs.WebGL.Internal.CallbackRegistry.ResetForTests();
            }
            finally
            {
                Conversation.SessionFactory = previousFactory;
            }
        }

        [Test]
        public void EndSession_WhenConnected_ClosesAllAndTransitionsStatusAndFiresDisconnected()
        {
            var conversation = NewConversation(out var connection, out var input, out var output);
            conversation.UpdateStatus(Status.Connected);
            var statusTransitions = new List<Status>();
            conversation.StatusChanged += statusTransitions.Add;
            DisconnectionDetails? received = null;
            conversation.Disconnected += d => received = d;

            _ = conversation.EndSession();

            Assert.AreEqual(1, connection.CloseCallCount);
            Assert.AreEqual(1, input.CloseCallCount);
            Assert.AreEqual(1, output.CloseCallCount);
            CollectionAssert.AreEqual(
                new[] { Status.Disconnecting, Status.Disconnected },
                statusTransitions
            );
            Assert.AreEqual(Status.Disconnected, conversation.Status);
            Assert.IsNotNull(received);
            Assert.AreEqual(DisconnectionReason.User, received!.Reason);
        }

        [Test]
        public void EndSession_WhenAlreadyDisconnected_IsNoOp()
        {
            var conversation = NewConversation(out var connection, out var input, out var output);
            // Status starts at Disconnected.
            DisconnectionDetails? received = null;
            conversation.Disconnected += d => received = d;

            _ = conversation.EndSession();

            Assert.AreEqual(0, connection.CloseCallCount);
            Assert.AreEqual(0, input.CloseCallCount);
            Assert.AreEqual(0, output.CloseCallCount);
            Assert.IsNull(received);
        }

        [Test]
        public void EndSession_WhenConnecting_StillRunsTeardown()
        {
            var conversation = NewConversation(out var connection, out var input, out var output);
            conversation.UpdateStatus(Status.Connecting);

            _ = conversation.EndSession();

            Assert.AreEqual(1, connection.CloseCallCount);
            Assert.AreEqual(1, input.CloseCallCount);
            Assert.AreEqual(1, output.CloseCallCount);
            Assert.AreEqual(Status.Disconnected, conversation.Status);
        }

        [Test]
        public void Connection_OnDisconnect_TriggersTeardownWithTransportDetails()
        {
            var conversation = NewConversation(out var connection, out var input, out var output);
            conversation.UpdateStatus(Status.Connected);
            DisconnectionDetails? received = null;
            conversation.Disconnected += d => received = d;

            connection.FireDisconnect(
                new DisconnectionDetails(
                    DisconnectionReason.Error,
                    Message: "socket closed",
                    Context: new DisconnectionContext("close", "abnormal", Code: 1006)
                )
            );

            Assert.AreEqual(1, connection.CloseCallCount);
            Assert.AreEqual(1, input.CloseCallCount);
            Assert.AreEqual(1, output.CloseCallCount);
            Assert.IsNotNull(received);
            Assert.AreEqual(DisconnectionReason.Error, received!.Reason);
            Assert.AreEqual("socket closed", received.Message);
            Assert.AreEqual(1006, received.Context?.Code);
        }

        [Test]
        public void Connection_OnDisconnect_AfterEndSession_IsNoOp()
        {
            var conversation = NewConversation(out var connection, out var input, out var output);
            conversation.UpdateStatus(Status.Connected);
            var disconnectCount = 0;
            conversation.Disconnected += _ => disconnectCount++;

            _ = conversation.EndSession();
            // Transport observes the close and fires its own OnDisconnect; the
            // status guard inside EndSessionWithDetails should swallow it.
            connection.FireDisconnect(new DisconnectionDetails(DisconnectionReason.Error));

            Assert.AreEqual(1, connection.CloseCallCount);
            Assert.AreEqual(1, input.CloseCallCount);
            Assert.AreEqual(1, output.CloseCallCount);
            Assert.AreEqual(1, disconnectCount);
        }

        // Status dedupe -----------------------------------------------------

        [Test]
        public void UpdateStatus_DedupesRepeatedTransitions()
        {
            var conversation = NewConversation(out _, out _, out _);
            var transitions = new List<Status>();
            conversation.StatusChanged += transitions.Add;

            conversation.UpdateStatus(Status.Connecting);
            conversation.UpdateStatus(Status.Connecting); // dedupe
            conversation.UpdateStatus(Status.Connected);

            CollectionAssert.AreEqual(new[] { Status.Connecting, Status.Connected }, transitions);
        }

        // Async client-tool dispatch (Phase 4.4 extra coverage) -------------

        [Test]
        public void RegisterTool_Async_HandlerThrows_SendsIsErrorAndRaisesError()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            conversation.RegisterTool<GreetParams, string>(
                "boom_async",
                async _ =>
                {
                    await CompletedAwaitable();
                    throw new InvalidOperationException("kaboom-async");
                }
            );
            string? errorMessage = null;
            conversation.ErrorOccurred += msg => errorMessage = msg;

            connection.FireMessage(
                MakeToolCall(
                    toolName: "boom_async",
                    parameters: new Dictionary<string, dynamic> { ["name"] = "x" }
                )
            );

            var result = (ClientToolResult)connection.Sent[0];
            Assert.IsTrue(result.IsError);
            Assert.IsNull(result.ErrorType);
            Assert.AreEqual("kaboom-async", result.Result);
            StringAssert.Contains("kaboom-async", errorMessage!);
        }

        [Test]
        public void RegisterTool_Async_ClientToolException_PreservesErrorType()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            conversation.RegisterTool<GreetParams, string>(
                "auth_async",
                async _ =>
                {
                    await CompletedAwaitable();
                    throw new ClientToolException("nope", errorType: "rate_limited");
                }
            );

            connection.FireMessage(
                MakeToolCall(
                    toolName: "auth_async",
                    parameters: new Dictionary<string, dynamic> { ["name"] = "x" }
                )
            );

            var result = (ClientToolResult)connection.Sent[0];
            Assert.IsTrue(result.IsError);
            Assert.AreEqual("rate_limited", result.ErrorType);
            Assert.AreEqual("nope", result.Result);
        }

        [Test]
        public void RegisterTool_Async_ExpectsResponseFalse_SuppressesResultSend()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            var invoked = false;
            var source = new AwaitableCompletionSource<string>();
            conversation.RegisterTool<GreetParams, string>(
                "fire_and_forget_async",
                _ =>
                {
                    invoked = true;
                    return source.Awaitable;
                }
            );

            connection.FireMessage(
                MakeToolCall(
                    toolName: "fire_and_forget_async",
                    parameters: new Dictionary<string, dynamic> { ["name"] = "x" },
                    expectsResponse: false
                )
            );
            source.SetResult("ignored");

            Assert.IsTrue(invoked);
            Assert.AreEqual(0, connection.Sent.Count);
        }

        // EnableDebugLogging — unknown-wire-event arm of JS onDebug (parity row 25)

        private static Conversation NewConversationWithOptions(
            ConversationOptions options,
            out MockConnection connection
        )
        {
            connection = new MockConnection();
            return new Conversation(
                connection,
                new MockInputController(),
                new MockOutputController(),
                new MockFileUploader(),
                options
            );
        }

        [Test]
        public void EnableDebugLogging_True_LogsUnknownIncomingEventWithTypeAndPayload()
        {
            var conversation = NewConversationWithOptions(
                new ConversationOptions { EnableDebugLogging = true },
                out var connection
            );
            LogAssert.Expect(
                LogType.Log,
                "[ElevenLabs debug] Unknown wire event type='future_event': {\"type\":\"future_event\"}"
            );

            connection.FireMessage(
                new UnknownIncomingEvent("future_event", "{\"type\":\"future_event\"}")
            );
        }

        [Test]
        public void EnableDebugLogging_True_NullTypeRendersAsPlaceholder()
        {
            var conversation = NewConversationWithOptions(
                new ConversationOptions { EnableDebugLogging = true },
                out var connection
            );
            LogAssert.Expect(
                LogType.Log,
                "[ElevenLabs debug] Unknown wire event type='(null)': {}"
            );

            connection.FireMessage(new UnknownIncomingEvent(null, "{}"));
        }

        [Test]
        public void EnableDebugLogging_False_Default_DoesNotLog()
        {
            // No LogAssert.Expect — if any debug log appears, LogAssert.NoUnexpectedReceived
            // (Unity's default at test end) will fail the test.
            var conversation = NewConversation(out var connection, out _, out _);

            connection.FireMessage(new UnknownIncomingEvent("future_event", "{}"));
        }
    }
}
