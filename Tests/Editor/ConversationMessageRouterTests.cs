#nullable enable

using System;
using System.Collections.Generic;
using ElevenLabs.Agents;
using ElevenLabs.Protocol;
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

            public Awaitable Close()
            {
                CloseCallCount++;
                return CompletedAwaitable();
            }

            public Awaitable SetDevice(
                InputDeviceConfig? config = null,
                FormatConfig? format = null
            ) => CompletedAwaitable();

            public Awaitable SetMuted(bool isMuted)
            {
                LastMuteRequest = isMuted;
                IsMuted = isMuted;
                return CompletedAwaitable();
            }

            public float GetVolume() => 0f;

            public void GetByteFrequencyData(byte[] buffer) { }
        }

        private sealed class MockOutputController : IOutputController
        {
            public int CloseCallCount { get; private set; }
            public int InterruptCallCount { get; private set; }
            public float LastVolume { get; private set; } = 1f;

            public Awaitable Close()
            {
                CloseCallCount++;
                return CompletedAwaitable();
            }

            public Awaitable SetDevice(
                OutputDeviceConfig? config = null,
                FormatConfig? format = null
            ) => CompletedAwaitable();

            public void SetVolume(float volume) => LastVolume = volume;

            public void Interrupt(int? resetDurationMs = null) => InterruptCallCount++;

            public float GetVolume() => 0f;

            public void GetByteFrequencyData(byte[] buffer) { }
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
        )
        {
            connection = new MockConnection();
            input = new MockInputController();
            output = new MockOutputController();
            return new Conversation(connection, input, output, new ConversationOptions());
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

        // Audio: state tracking + interrupt gating ----------------------------

        [Test]
        public void Dispatch_Audio_FiresAudioReceivedAndAdvancesState()
        {
            var conversation = NewConversation(out var connection, out _, out _);
            AudioResponseArgs? received = null;
            conversation.AudioReceived += args => received = args;
            var modeChanges = new List<Mode>();
            conversation.ModeChanged += mode => modeChanges.Add(mode);

            connection.FireMessage(MakeAudio(eventId: 12, payload: "abc"));

            Assert.AreEqual("abc", received!.AudioBase64);
            Assert.AreEqual(12, received.EventId);
            Assert.AreEqual(Mode.Speaking, conversation.Mode);
            CollectionAssert.AreEqual(new[] { Mode.Speaking }, modeChanges);
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
                    parameters: new Dictionary<string, dynamic> { ["name"] = "Kræn" }
                )
            );

            Assert.AreEqual(1, connection.Sent.Count);
            var result = connection.Sent[0] as ClientToolResult;
            Assert.IsNotNull(result);
            Assert.AreEqual("tc-1", result!.ToolCallId);
            Assert.AreEqual("Hello, Kræn!", result.Result);
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

        // end_call shortcut (agent_tool_response_full_payload) -------------

        [Test]
        public void Dispatch_AgentToolResponseFullPayload_EndCall_TriggersDisconnect()
        {
            var conversation = NewConversation(out var connection, out var input, out var output);
            // Prime status to Connected so EndSessionWithDetails actually fires.
            conversation.UpdateStatus(Status.Connected);
            DisconnectionDetails? details = null;
            conversation.Disconnected += d => details = d;

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
    }
}
