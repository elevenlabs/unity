#nullable enable

using System;
using ElevenLabs.Protocol;
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
    public sealed class Conversation : IncomingEventDispatcher
    {
        private readonly IConnection _connection;
        private readonly IInputController _inputController;
        private readonly IOutputController _outputController;
        private readonly ConversationOptions _options;

        // Mirrors BaseConversation's currentEventId / lastFeedbackEventId /
        // lastInterruptTimestamp. Initial values match the JS SDK so the
        // canSendFeedback gate behaves identically before the first audio
        // event arrives.
        private int _currentEventId = 1;
        private int _lastFeedbackEventId;
        private int _lastInterruptTimestamp;

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
            OnConversationInitiationMetadata += HandleConversationInitiationMetadata;
            OnAgentResponse += HandleAgentResponse;
            OnAgentResponseComplete += HandleAgentResponseComplete;
            OnUserTranscript += HandleUserTranscript;
            OnAgentResponseCorrection += HandleAgentResponseCorrection;
            OnAudioResponse += HandleAudioResponse;
            OnInterruption += HandleInterruption;
            OnVadScore += HandleVadScore;
            OnPing += HandlePing;
            OnClientToolCall += HandleClientToolCall;
            OnAgentToolResponseFullPayload += HandleAgentToolResponseFullPayload;
        }

        // Lifecycle -----------------------------------------------------------

        /// <summary>
        /// Open a session against the agent identified by <paramref name="options"/>.
        /// Returns once the underlying transport handshake completes, audio
        /// devices are acquired, and the conversation initiation event has
        /// been observed.
        /// </summary>
        /// <param name="options">Session inputs — agent id, transport, device selection.</param>
        public static Awaitable<Conversation> StartSessionAsync(ConversationOptions options)
        {
            _ = options;
            // Phase 5.3 (BridgedSession orchestration) wires the WebGL branch
            // here via #if UNITY_WEBGL; Phase 7 wires the native branch. The
            // skeleton intentionally throws so callers don't get a half-built
            // Conversation back.
            throw new NotImplementedException(
                "StartSessionAsync is wired in Phase 5.4 — see Docs~/plans/plan-b.md."
            );
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
        // generated dispatcher; per-event handlers below subscribe to the
        // dispatcher's typed events and re-raise the user-facing surface.
        private void OnConnectionMessage(IncomingSocketEvent evt) => Dispatch(evt);

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

        // Phase 4.4 will replace the body with real tool dispatch (look up
        // a registered handler, await it, send ClientToolResult). For Phase
        // 4.3 the wire DTO is also broken (self-referential ClientToolCall —
        // the codegen Data-suffix bug noted under plan-b.md task 3.4), so we
        // can't pull tool_call_id / tool_name out anyway. Surface as an error
        // so a stray tool call doesn't silently disappear.
        private void HandleClientToolCall(ClientToolCall evt)
        {
            _ = evt;
            RaiseError(
                "Received client_tool_call but client-tool dispatch is not yet wired "
                    + "(Phase 4.4) — see Docs~/plans/plan-b.md."
            );
        }

        // Mirrors BaseConversation.handleAgentToolResponseFullPayload's
        // end_call shortcut: if the agent invoked the end_call tool, drive
        // teardown with reason=agent. Same wire-codegen bug as
        // ClientToolCall — AgentToolResponseFullPayloadData self-references —
        // so tool_name extraction is deferred until the codegen fix lands.
        private void HandleAgentToolResponseFullPayload(AgentToolResponseFullPayload evt)
        {
            _ = evt;
            // TODO Phase 4 follow-up: once the wire-codegen bug noted under
            // plan-b.md task 3.4 is fixed, read tool_name and fire the
            // end_call shortcut here:
            //   if (evt.AgentToolResponseFullPayloadData.ToolName == "end_call")
            //       _ = EndSessionWithDetails(new DisconnectionDetails(
            //           DisconnectionReason.Agent,
            //           Context: new DisconnectionContext("end_call",
            //               "Agent ended the call")));
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
