#nullable enable

using System;
using System.Collections.Generic;
using ElevenLabs.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
// UnityEngine.Ping collides with the protocol wire type — alias the wire one.
using Ping = ElevenLabs.Protocol.Ping;

namespace ElevenLabs.Agents
{
    /// <summary>
    /// Cross-platform agent conversation. Owns lifecycle, status / mode
    /// tracking, message dispatch, interruption handling, client-tool
    /// invocation, and feedback eligibility.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The public surface mirrors <c>BaseConversation</c> from
    /// <c>@elevenlabs/client</c> but is idiomatic C# — standard
    /// <c>event</c> + <c>+=</c> / <c>-=</c>, <see cref="Awaitable{T}"/>
    /// for async methods, args-typed payload records generated alongside
    /// the wire DTOs.
    /// </para>
    /// <para>
    /// Event subscribers register and deregister freely at any time. The
    /// underlying connection's single <c>onMessage</c> callback is wired
    /// once at session start, and the C# router fans out into the typed
    /// events surfaced here.
    /// </para>
    /// <para>
    /// This class is the entire user-facing API. The
    /// <see cref="IConnection"/> / <see cref="IInputController"/> /
    /// <see cref="IOutputController"/> abstractions it orchestrates are
    /// <c>internal</c>; game code never sees them.
    /// </para>
    /// </remarks>
    public sealed class Conversation
    {
        private readonly IConnection _connection;
        private readonly IInputController _inputController;
        private readonly IOutputController _outputController;
        private readonly ConversationOptions _options;

        // Generated wire-event fan-out. Composition (not inheritance) keeps
        // the wire types out of Conversation's public surface — the dispatcher
        // is an implementation detail of the router, never a user-facing API.
        private readonly IncomingEventDispatcher _dispatcher = new();

        // Mirrors BaseConversation's currentEventId / lastFeedbackEventId /
        // lastInterruptTimestamp. Initial values match the JS SDK so the
        // canSendFeedback gate behaves identically before the first audio
        // event arrives.
        private int _currentEventId = 1;
        private int _lastFeedbackEventId;
        private int _lastInterruptTimestamp;

        // Client-tool dispatch table. Plan B owns this in C# (no JS Proxy,
        // no shared dispatcher key). Late registration / unregistration is
        // supported at any point in the session lifecycle.
        private readonly Dictionary<string, ClientToolDispatcher> _toolHandlers = new();

        // Lifecycle events ----------------------------------------------------

        /// <summary>Fired once the session is open and the conversation id is known.</summary>
        public event Action<string>? Connected;

        /// <summary>Fired exactly once when the session ends, regardless of reason.</summary>
        public event Action<DisconnectionDetails>? Disconnected;

        /// <summary>
        /// Fired when an error surfaces from the transport, audio pipeline, or
        /// message router. The payload is the human-readable message.
        /// </summary>
        public event Action<string>? ErrorOccurred;

        /// <summary>Fired when <see cref="Status"/> transitions.</summary>
        public event Action<Status>? StatusChanged;

        /// <summary>Fired when <see cref="Mode"/> flips between speaking and listening.</summary>
        public event Action<Mode>? ModeChanged;

        /// <summary>Fired when <see cref="CanSendFeedback"/> changes (typically after the first agent response).</summary>
        public event Action<bool>? CanSendFeedbackChanged;

        // Protocol events -----------------------------------------------------

        /// <summary>Fired once at session start with the agent's conversation initiation metadata.</summary>
        public event Action<ConversationInitiationMetadataArgs>? InitiationMetadataReceived;

        /// <summary>Fired when the user's speech has been transcribed.</summary>
        public event Action<UserTranscriptArgs>? UserTranscriptReceived;

        /// <summary>Fired when the agent emits a (possibly partial) response.</summary>
        public event Action<AgentResponseArgs>? AgentResponded;

        /// <summary>Fired when the agent rewrites a prior response (e.g. after an interruption).</summary>
        public event Action<AgentResponseCorrectionArgs>? AgentResponseCorrected;

        /// <summary>
        /// Fired for each chunk of agent audio. On WebGL default-mode the
        /// <c>AudioBase64</c> payload is stripped on the JS side and the
        /// audio is played natively; on native (and Unity-routed WebGL) the
        /// payload carries PCM bytes for <see cref="IOutputController"/> playback.
        /// </summary>
        public event Action<AudioResponseArgs>? AudioReceived;

        /// <summary>Fired when the agent's response is complete for the current turn.</summary>
        public event Action<AgentResponseCompleteArgs>? AgentResponseCompleted;

        /// <summary>Fired when the agent's audio is interrupted by the user speaking over it.</summary>
        public event Action<InterruptionArgs>? Interrupted;

        /// <summary>Fired on each voice-activity-detection score update.</summary>
        public event Action<VadScoreArgs>? VadScoreUpdated;

        // State ---------------------------------------------------------------

        /// <summary>Server-assigned conversation identifier. Empty before <see cref="Connected"/> fires.</summary>
        public string ConversationId => _connection.ConversationId;

        /// <summary>Current lifecycle state. Subscribe to <see cref="StatusChanged"/> for transitions.</summary>
        public Status Status { get; private set; } = Status.Disconnected;

        /// <summary>Current conversational mode. Subscribe to <see cref="ModeChanged"/> for transitions.</summary>
        public Mode Mode { get; private set; } = Mode.Listening;

        /// <summary>Whether <see cref="SendFeedback"/> is currently allowed. Subscribe to <see cref="CanSendFeedbackChanged"/> for transitions.</summary>
        public bool CanSendFeedback { get; private set; }

        // Construction --------------------------------------------------------

        /// <summary>
        /// Internal constructor. Phase 5.3's <c>BridgedSession</c> calls this
        /// from <see cref="StartSessionAsync"/> on WebGL; the native session
        /// orchestrator (Phase 7) does the same on every other platform. Edit
        /// Mode tests inject mock implementations directly.
        /// </summary>
        internal Conversation(
            IConnection connection,
            IInputController inputController,
            IOutputController outputController,
            ConversationOptions options
        )
        {
            _connection = connection;
            _inputController = inputController;
            _outputController = outputController;
            _options = options;

            // Connection-level subscriptions — mirror BaseConversation's
            // constructor (onMessage / onDisconnect / onModeChange).
            _connection.OnMessage += OnConnectionMessage;
            _connection.OnDisconnect += OnConnectionDisconnect;
            _connection.OnModeChange += UpdateMode;

            // Dispatcher fan-out — translate each wire-typed event into the
            // args-typed user-facing event, applying suppression and
            // side-effects per BaseConversation.onMessage.
            _dispatcher.OnConversationInitiationMetadata += HandleConversationInitiationMetadata;
            _dispatcher.OnAgentResponse += HandleAgentResponse;
            _dispatcher.OnAgentResponseComplete += HandleAgentResponseComplete;
            _dispatcher.OnUserTranscript += HandleUserTranscript;
            _dispatcher.OnAgentResponseCorrection += HandleAgentResponseCorrection;
            _dispatcher.OnAudioResponse += HandleAudioResponse;
            _dispatcher.OnInterruption += HandleInterruption;
            _dispatcher.OnVadScore += HandleVadScore;
            _dispatcher.OnPing += HandlePing;
            _dispatcher.OnClientToolCall += HandleClientToolCall;
            _dispatcher.OnAgentToolResponseFullPayload += HandleAgentToolResponseFullPayload;
        }

        // Lifecycle -----------------------------------------------------------

        // Set by the active platform's session launcher (WebGL:
        // BridgedSessionLauncher; native: Phase 7) via RuntimeInitializeOnLoad
        // + InitializeOnLoad. Last-write-wins; in practice only one launcher
        // ships per build because each platform asmdef's includePlatforms
        // gates which assembly is included.
        internal static Func<ConversationOptions, Awaitable<Conversation>>? SessionFactory;

        /// <summary>
        /// Open a session against the agent identified by <paramref name="options"/>.
        /// Returns once the underlying transport handshake completes, audio
        /// devices are acquired, and the conversation initiation event has
        /// been observed.
        /// </summary>
        /// <param name="options">Session inputs — agent id, transport, device selection.</param>
        public static Awaitable<Conversation> StartSessionAsync(ConversationOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            var factory = SessionFactory;
            if (factory == null)
            {
                throw new InvalidOperationException(
                    "No session factory is registered for the current platform. "
                        + "Ensure ElevenLabs.Agents.WebGL (or the native impl in v0.2) is "
                        + "included in your build target."
                );
            }
            return factory(options);
        }

        /// <summary>Tear down the session: close the transport, release the audio devices, idempotent.</summary>
        public Awaitable EndSession()
        {
            return EndSessionWithDetails(new DisconnectionDetails(DisconnectionReason.User));
        }

        // Mirrors BaseConversation.endSessionWithDetails — driven by the user
        // (public EndSession) and by the transport (OnConnectionDisconnect).
        // Idempotent: if status is already Disconnecting / Disconnected, no-op.
        private async Awaitable EndSessionWithDetails(DisconnectionDetails details)
        {
            if (Status != Status.Connected && Status != Status.Connecting)
            {
                return;
            }
            UpdateStatus(Status.Disconnecting);
            _connection.Close();
            await _inputController.Close();
            await _outputController.Close();
            UpdateStatus(Status.Disconnected);
            Disconnected?.Invoke(details);
        }

        // Outgoing messages ---------------------------------------------------

        /// <summary>Send a text message as if the user had spoken it.</summary>
        public void SendUserMessage(string text)
        {
            _connection.Send(new UserMessage { Text = text });
        }

        /// <summary>
        /// Send out-of-band context for the agent — useful for sharing UI
        /// state, game state, or other side-channel information that should
        /// influence the agent's next response without appearing as user
        /// speech.
        /// </summary>
        public void SendContextualUpdate(string text)
        {
            _connection.Send(new ContextualUpdate { Text = text });
        }

        /// <summary>
        /// Signal that the user is engaged (e.g. typing) so the agent doesn't
        /// take its turn yet.
        /// </summary>
        public void SendUserActivity()
        {
            _connection.Send(new UserActivity());
        }

        /// <summary>
        /// Send feedback on the most recent agent turn. Only honoured when
        /// <see cref="CanSendFeedback"/> is <c>true</c>.
        /// </summary>
        /// <param name="like"><c>true</c> for thumbs-up, <c>false</c> for thumbs-down.</param>
        public void SendFeedback(bool like)
        {
            if (!CanSendFeedback)
            {
                Debug.LogWarning(
                    _lastFeedbackEventId == 0
                        ? "Cannot send feedback: the conversation has not started yet."
                        : "Cannot send feedback: feedback has already been sent for the current response."
                );
                return;
            }
            _connection.Send(
                new Feedback { Score = like ? "like" : "dislike", EventId = _currentEventId }
            );
            _lastFeedbackEventId = _currentEventId;
            RefreshCanSendFeedback();
        }

        // Audio control -------------------------------------------------------

        /// <summary>Set agent-audio playback gain. <paramref name="volume"/> is clamped to <c>[0, 1]</c>.</summary>
        public void SetVolume(float volume)
        {
            _outputController.SetVolume(volume);
        }

        /// <summary>Mute or unmute the microphone. While muted, the input controller emits silence.</summary>
        public Awaitable SetMicMuted(bool isMuted)
        {
            return _inputController.SetMuted(isMuted);
        }

        /// <summary>
        /// Write byte-frequency data (0-255) for the microphone input into
        /// <paramref name="buffer"/>, focused on the human voice range
        /// (100-8000 Hz). Use for input visualisers.
        /// </summary>
        public void GetInputByteFrequencyData(byte[] buffer)
        {
            _inputController.GetByteFrequencyData(buffer);
        }

        /// <summary>
        /// Write byte-frequency data (0-255) for the agent output into
        /// <paramref name="buffer"/>, focused on the human voice range
        /// (100-8000 Hz). Use for output visualisers.
        /// </summary>
        public void GetOutputByteFrequencyData(byte[] buffer)
        {
            _outputController.GetByteFrequencyData(buffer);
        }

        /// <summary>Current input audio level as a scalar in <c>[0, 1]</c>.</summary>
        public float GetInputVolume() => _inputController.GetVolume();

        /// <summary>Current output audio level as a scalar in <c>[0, 1]</c>.</summary>
        public float GetOutputVolume() => _outputController.GetVolume();

        // Connection-level handlers -------------------------------------------

        // Connection's OnMessage delivers wire events straight into the
        // owned dispatcher; per-event handlers below subscribe to the
        // dispatcher's typed events and re-raise the user-facing surface.
        private void OnConnectionMessage(IncomingSocketEvent evt) => _dispatcher.Dispatch(evt);

        // The transport closed on its own (network error, agent end_call, …).
        // Drive the same teardown sequence as the user-initiated path; the
        // status guard inside EndSessionWithDetails makes the re-entry from
        // our own _connection.Close() above a no-op.
        private void OnConnectionDisconnect(DisconnectionDetails details)
        {
            // Fire-and-forget — event handlers can't await. The teardown is
            // idempotent so a missed observation won't leave the conversation
            // half-open.
            _ = EndSessionWithDetails(details);
        }

        // Dispatcher → args translation ---------------------------------------

        private void HandleConversationInitiationMetadata(ConversationInitiationMetadata evt)
        {
            var args = evt.ToArgs();
            InitiationMetadataReceived?.Invoke(args);
            // BaseConversation.markConnected is called by the platform-specific
            // session setup; we keep the same separation so the message router
            // can land before Phase 5.3 wires StartSessionAsync. The Connected
            // event therefore fires from Phase 5.3, not here.
        }

        private void HandleAgentResponse(AgentResponse evt) => AgentResponded?.Invoke(evt.ToArgs());

        private void HandleAgentResponseComplete(AgentResponseComplete evt) =>
            AgentResponseCompleted?.Invoke(evt.ToArgs());

        private void HandleUserTranscript(UserTranscript evt) =>
            UserTranscriptReceived?.Invoke(evt.ToArgs());

        private void HandleAgentResponseCorrection(AgentResponseCorrection evt) =>
            AgentResponseCorrected?.Invoke(evt.ToArgs());

        // Mirrors VoiceConversation.handleAudio's gating: drop chunks that
        // belong to an event the user already interrupted, otherwise advance
        // currentEventId, refresh the feedback gate, and switch mode to
        // speaking.
        private void HandleAudioResponse(AudioResponse evt)
        {
            var eventId = evt.AudioEvent.EventId;
            if (_lastInterruptTimestamp > eventId)
            {
                return;
            }
            AudioReceived?.Invoke(evt.ToArgs());
            _currentEventId = eventId;
            RefreshCanSendFeedback();
            UpdateMode(Mode.Speaking);
        }

        // Mirrors VoiceConversation.handleInterruption: bookmark the
        // interrupted event_id, switch back to listening, flush the output
        // controller. The output's Interrupt is a no-op on default-mode WebGL
        // (audio plays through JS) and a real flush on native / Unity-routed.
        private void HandleInterruption(Interruption evt)
        {
            var eventId = evt.InterruptionEvent.EventId;
            _lastInterruptTimestamp = eventId;
            Interrupted?.Invoke(evt.ToArgs());
            UpdateMode(Mode.Listening);
            _outputController.Interrupt();
        }

        private void HandleVadScore(VadScore evt) => VadScoreUpdated?.Invoke(evt.ToArgs());

        // Auto-pong: BaseConversation echoes ping_event.event_id back as a
        // pong frame to keep the connection alive. Suppressed from the
        // user-facing surface — no Conversation-level event fires.
        private void HandlePing(Ping evt)
        {
            _connection.Send(new Pong { EventId = evt.PingEvent.EventId });
        }

        // Dispatch flow mirrors BaseConversation.onMessage's client_tool_call
        // arm: look up the handler, invoke it with the parameters dict,
        // catch + surface failures, and send ClientToolResult only when the
        // server asked for one (expects_response). The async-void fire-and-
        // forget shape matches the rest of the dispatcher — events can't be
        // async, and the message router shouldn't block the transport.
        private async void HandleClientToolCall(ClientToolCall evt)
        {
            var args = evt.ToArgs();
            if (!_toolHandlers.TryGetValue(args.ToolName, out var dispatcher))
            {
                var message = $"No client tool registered with name '{args.ToolName}'.";
                RaiseError(message);
                SendToolErrorIfExpected(args, message, errorType: "tool_not_found");
                return;
            }

            string resultJson;
            try
            {
                resultJson = await dispatcher(args.Parameters);
            }
            catch (ClientToolException ex)
            {
                // Handler chose to surface a specific error_type — preserve it
                // so the agent's server-side prompt can branch on it.
                RaiseError($"Client tool '{args.ToolName}' failed: {ex.Message}");
                SendToolErrorIfExpected(args, ex.Message, errorType: ex.ErrorType);
                return;
            }
            catch (Exception ex)
            {
                RaiseError($"Client tool '{args.ToolName}' threw: {ex.Message}");
                SendToolErrorIfExpected(args, ex.Message, errorType: null);
                return;
            }

            if (!args.ExpectsResponse)
            {
                return;
            }
            _connection.Send(
                new ClientToolResult
                {
                    ToolCallId = args.ToolCallId,
                    Result = resultJson,
                    IsError = false,
                }
            );
        }

        private void SendToolErrorIfExpected(
            ClientToolCallArgs args,
            string message,
            string? errorType
        )
        {
            if (!args.ExpectsResponse)
            {
                return;
            }
            _connection.Send(
                new ClientToolResult
                {
                    ToolCallId = args.ToolCallId,
                    Result = message,
                    IsError = true,
                    ErrorType = errorType,
                }
            );
        }

        // Mirrors BaseConversation.handleAgentToolResponseFullPayload's
        // end_call shortcut: if the agent invoked the end_call tool, drive
        // teardown with reason=Agent.
        private void HandleAgentToolResponseFullPayload(AgentToolResponseFullPayload evt)
        {
            if (evt.AgentToolResponseFullPayloadData.ToolName == "end_call")
            {
                _ = EndSessionWithDetails(
                    new DisconnectionDetails(
                        DisconnectionReason.Agent,
                        Context: new DisconnectionContext("end_call", "Agent ended the call")
                    )
                );
            }
        }

        // Client tool registration -------------------------------------------

        /// <summary>
        /// Register a synchronous client tool. The agent can invoke
        /// <paramref name="name"/> over the wire; <paramref name="handler"/>
        /// receives the deserialised parameter record and returns the result.
        /// The result is serialised with Newtonsoft.Json before being sent as
        /// <c>client_tool_result.result</c>; if it's already a <c>string</c>
        /// it's forwarded verbatim. Re-registering a name overwrites the
        /// previous handler with a warning. Registration is permitted before,
        /// during, or after a session.
        /// </summary>
        /// <typeparam name="TParams">
        /// Parameter record / class. Deserialised from the agent's parameters
        /// JSON object via Newtonsoft.Json — use <c>[JsonProperty]</c> if
        /// property names need to differ from the wire schema.
        /// </typeparam>
        /// <typeparam name="TResult">
        /// Result type. Anything Newtonsoft can serialise; <c>string</c> is
        /// passed through. Throw <see cref="ClientToolException"/> for a
        /// specific <c>error_type</c>, or any other exception for a generic
        /// failure response.
        /// </typeparam>
        public void RegisterTool<TParams, TResult>(string name, Func<TParams, TResult> handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            RegisterToolInternal(
                name,
                p => CompletedAwaitable(handler(DeserialiseParams<TParams>(p)))
            );
        }

        /// <summary>
        /// Register an asynchronous client tool. Same semantics as the sync
        /// overload, but the handler returns an <see cref="Awaitable{TResult}"/>
        /// the dispatcher awaits before sending the result.
        /// </summary>
        public void RegisterTool<TParams, TResult>(
            string name,
            Func<TParams, Awaitable<TResult>> handler
        )
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));
            RegisterToolInternal(
                name,
                async p => SerialiseResult(await handler(DeserialiseParams<TParams>(p)))
            );
        }

        /// <summary>
        /// Remove the handler for <paramref name="name"/>. Returns <c>true</c>
        /// when a handler was registered and removed, <c>false</c> when none
        /// existed.
        /// </summary>
        public bool UnregisterTool(string name) => _toolHandlers.Remove(name);

        private void RegisterToolInternal(string name, ClientToolDispatcher dispatcher)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Tool name must be non-empty.", nameof(name));
            if (_toolHandlers.ContainsKey(name))
            {
                Debug.LogWarning($"Client tool '{name}' is being overwritten.");
            }
            _toolHandlers[name] = dispatcher;
        }

        // Bridges the sync RegisterTool overload onto ClientToolDispatcher's
        // Awaitable<string> signature without forcing every sync handler to
        // allocate a completion source.
        private static Awaitable<string> CompletedAwaitable<TResult>(TResult result)
        {
            var source = new AwaitableCompletionSource<string>();
            source.SetResult(SerialiseResult(result));
            return source.Awaitable;
        }

        // Deserialise the parameters dict into the handler's TParams. The
        // wire delivers a Dictionary<string, dynamic>; round-trip via JObject
        // so Newtonsoft applies the handler type's [JsonProperty] / nullability /
        // converter rules instead of trying to cast individual values.
        private static TParams DeserialiseParams<TParams>(Dictionary<string, dynamic>? parameters)
        {
            var obj = parameters == null ? new JObject() : JObject.FromObject(parameters);
            var typed = obj.ToObject<TParams>();
            if (typed == null)
            {
                throw new InvalidOperationException(
                    $"Failed to deserialise parameters into {typeof(TParams).Name}."
                );
            }
            return typed;
        }

        // Result rule: a string handler result becomes the literal result
        // string; everything else is serialised via Newtonsoft. This matches
        // what game devs expect — "return 'hi'" sends `result: "hi"`, not
        // `result: "\"hi\""`.
        private static string SerialiseResult<TResult>(TResult result)
        {
            if (result is string s)
            {
                return s;
            }
            return JsonConvert.SerializeObject(result);
        }

        // State transition helpers --------------------------------------------

        internal void RaiseConnected(string conversationId) => Connected?.Invoke(conversationId);

        internal void RaiseDisconnected(DisconnectionDetails details) =>
            Disconnected?.Invoke(details);

        internal void RaiseError(string message) => ErrorOccurred?.Invoke(message);

        internal void UpdateStatus(Status status)
        {
            if (Status == status)
                return;
            Status = status;
            StatusChanged?.Invoke(status);
        }

        internal void UpdateMode(Mode mode)
        {
            if (Mode == mode)
                return;
            Mode = mode;
            ModeChanged?.Invoke(mode);
        }

        internal void UpdateCanSendFeedback(bool canSendFeedback)
        {
            if (CanSendFeedback == canSendFeedback)
                return;
            CanSendFeedback = canSendFeedback;
            CanSendFeedbackChanged?.Invoke(canSendFeedback);
        }

        // Re-evaluate the feedback gate from the current event-id bookkeeping;
        // called after every state change that could flip it (a new audio
        // event, a feedback submission). Mirrors
        // BaseConversation.updateCanSendFeedback.
        private void RefreshCanSendFeedback() =>
            UpdateCanSendFeedback(_currentEventId != _lastFeedbackEventId);

        // Re-raise helpers — kept so Phase 5.3 (BridgedSession) can mark
        // Connected once the transport handshake completes without
        // re-implementing the event-fire pattern.

        internal void RaiseInitiationMetadataReceived(ConversationInitiationMetadataArgs args) =>
            InitiationMetadataReceived?.Invoke(args);

        internal void RaiseUserTranscriptReceived(UserTranscriptArgs args) =>
            UserTranscriptReceived?.Invoke(args);

        internal void RaiseAgentResponded(AgentResponseArgs args) => AgentResponded?.Invoke(args);

        internal void RaiseAgentResponseCorrected(AgentResponseCorrectionArgs args) =>
            AgentResponseCorrected?.Invoke(args);

        internal void RaiseAudioReceived(AudioResponseArgs args) => AudioReceived?.Invoke(args);

        internal void RaiseAgentResponseCompleted(AgentResponseCompleteArgs args) =>
            AgentResponseCompleted?.Invoke(args);

        internal void RaiseInterrupted(InterruptionArgs args) => Interrupted?.Invoke(args);

        internal void RaiseVadScoreUpdated(VadScoreArgs args) => VadScoreUpdated?.Invoke(args);
    }
}
