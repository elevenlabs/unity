#nullable enable

using System;
using ElevenLabs.Protocol;
using UnityEngine;

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
            // Phase 4.3 wires _connection.OnMessage into base.Dispatch and
            // fans out the dispatcher's wire-typed events into the args-typed
            // events declared above. Phase 4.4 layers client-tool dispatch
            // on top.
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
        public async Awaitable EndSession()
        {
            _connection.Close();
            await _inputController.Close();
            await _outputController.Close();
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
            _ = like;
            // Phase 4.3 ties this to the canSendFeedback + lastFeedbackEventId
            // state tracked by the message router.
            throw new NotImplementedException(
                "SendFeedback is wired in Phase 4.3 (message router) — see Docs~/plans/plan-b.md."
            );
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

        // State transition helpers (called by the Phase 4.3 router) -----------

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

        // Re-raise helpers — Phase 4.3 will subscribe to the IncomingEventDispatcher
        // base events and call these to fan out the args-typed surface. They
        // live on this class (rather than inline lambdas in Phase 4.3) so the
        // event-raising code stays in one place and is easy to mock in tests.

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
