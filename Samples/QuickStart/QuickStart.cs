#nullable enable

using System;
using ElevenLabs.Agents;
using ElevenLabs.Protocol;
using UnityEngine;

namespace ElevenLabs.Agents.Samples.QuickStart
{
    /// <summary>
    /// Minimal sample showing how to open an ElevenLabs conversational AI
    /// session and render the live transcript on-screen. Drop this MonoBehaviour
    /// onto a GameObject, create a <see cref="QuickStartConfig"/> asset under
    /// <c>Resources/</c> with your agent id, and press Play.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session opens on <see cref="Start"/> and closes on
    /// <see cref="OnDestroy"/> (which also fires when leaving Play mode), so
    /// there is nothing to stop manually. Errors surface through
    /// <see cref="Conversation.ErrorOccurred"/>; failures during
    /// <see cref="Conversation.StartSessionAsync"/> propagate as exceptions
    /// from <see cref="StartConversationAsync"/> and land in the Unity console.
    /// </para>
    /// <para>
    /// On WebGL the bridged session launcher handles transport + audio via the
    /// JS SDK. On standalone / mobile the native launcher uses
    /// <see cref="System.Net.WebSockets.ClientWebSocket"/> together with
    /// Unity's <c>Microphone</c> and an <c>AudioSource</c>. Either way, the
    /// public API used here is the same.
    /// </para>
    /// <para>
    /// The on-screen transcript is rendered via <see cref="OnGUI"/> to keep the
    /// dependency surface minimal — no UGUI / TextMeshPro / Canvas required.
    /// For production UI, replace the <see cref="OnGUI"/> block with your own
    /// text components and forward <see cref="_status"/> / <see cref="_lastUser"/> /
    /// <see cref="_lastAgent"/> from the existing event handlers.
    /// </para>
    /// </remarks>
    public sealed class QuickStart : MonoBehaviour
    {
        private Conversation? _conversation;

        // Latest values rendered by OnGUI; mutated from the event handlers on
        // Unity's main thread (Bridged + native launchers both marshal back).
        private string _status = "Not started";
        private string _lastUser = "";
        private string _lastAgent = "";

        private void Start()
        {
            _ = StartConversationAsync();
        }

        private void OnDestroy()
        {
            // EndSession is idempotent — the bridged + native launchers both
            // tear down cleanly even if Start failed before the session opened.
            if (_conversation != null)
            {
                _ = _conversation.EndSession();
                _conversation = null;
            }
        }

        private async Awaitable StartConversationAsync()
        {
            QuickStartConfig? config = Resources.Load<QuickStartConfig>("QuickStartConfig");
            if (config == null || string.IsNullOrWhiteSpace(config.AgentId))
            {
                const string message =
                    "[QuickStart] No QuickStartConfig asset found under Resources/, or "
                    + "AgentId is empty. Create one via Assets → Create → ElevenLabs → "
                    + "Samples → QuickStart Config and fill in your agent id.";
                Debug.LogWarning(message);
                _status = "No agent configured";
                return;
            }

            _status = "Connecting…";

            var options = new ConversationOptions
            {
                AgentId = config.AgentId,
                SignedUrl = config.SignedUrl,
                ConnectionType = config.ConnectionType,
            };

            try
            {
                _conversation = await Conversation.StartSessionAsync(options);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[QuickStart] Failed to start session: {ex.Message}");
                _status = $"Failed: {ex.Message}";
                throw;
            }

            // StartSessionAsync returns once the handshake + audio setup are
            // complete, so by this line the session is already connected and
            // the conversation id is known. The Conversation.Connected event
            // fires synchronously inside the factory, before we get the
            // handle back — subscribing to it here would never catch it.
            Debug.Log($"[QuickStart] Connected — conversation id: {_conversation.ConversationId}");
            _status = "Connected";

            // Subscribe to the events that fire later in the session. We do
            // this after the await so the handlers don't capture a stale
            // reference if the awaited factory throws.
            _conversation.Disconnected += OnDisconnected;
            _conversation.ErrorOccurred += OnErrorOccurred;
            _conversation.UserTranscriptReceived += OnUserTranscriptReceived;
            _conversation.AgentResponded += OnAgentResponded;
        }

        private void OnDisconnected(DisconnectionDetails details)
        {
            Debug.Log($"[QuickStart] Disconnected — reason: {details.Reason}");
            _status = $"Disconnected ({details.Reason})";
        }

        private void OnErrorOccurred(string message)
        {
            Debug.LogError($"[QuickStart] Error: {message}");
            _status = $"Error: {message}";
        }

        private void OnUserTranscriptReceived(UserTranscriptArgs args)
        {
            Debug.Log($"[QuickStart] User: {args.UserTranscript}");
            _lastUser = args.UserTranscript;
        }

        private void OnAgentResponded(AgentResponseArgs args)
        {
            Debug.Log($"[QuickStart] Agent: {args.AgentResponse}");
            _lastAgent = args.AgentResponse;
        }

        // Cached at first OnGUI to avoid allocating a new GUIStyle every
        // frame. The default GUI font is ~12pt — illegible on a Game view
        // at typical 1080p, so we ship a larger default that still fits a
        // few lines of transcript without dominating the screen.
        private GUIStyle? _labelStyle;

        private void OnGUI()
        {
            _labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 42, wordWrap = true };

            const int padding = 16;
            int rowHeight = _labelStyle.fontSize + 8;
            int y = padding;

            GUI.Label(
                new Rect(padding, y, Screen.width - 2 * padding, rowHeight),
                $"Status: {_status}",
                _labelStyle
            );
            y += rowHeight + 8;
            GUI.Label(
                new Rect(padding, y, Screen.width - 2 * padding, rowHeight),
                $"You: {_lastUser}",
                _labelStyle
            );
            y += rowHeight + 4;
            GUI.Label(
                new Rect(padding, y, Screen.width - 2 * padding, rowHeight * 6),
                $"Agent: {_lastAgent}",
                _labelStyle
            );
        }
    }
}
