#nullable enable

using ElevenLabs.Agents;
using UnityEngine;

namespace ElevenLabs.WebGL.Samples.ConversationSmokeTest
{
    /// <summary>
    /// Per-developer / per-CI agent credentials for the conversation smoke
    /// test. Place the asset at <c>Assets/Resources/ConversationSmokeConfig.asset</c>
    /// so <see cref="Resources.Load{T}(string)"/> can find it at runtime; the
    /// asset itself is gitignored so secrets never reach the repository.
    /// </summary>
    /// <remarks>
    /// When the asset is missing or <see cref="AgentId"/> is empty, the smoke
    /// test logs <c>[ConvSmoke] CONFIG MISSING</c> and exits cleanly. The
    /// Vitest harness treats that line as a skip rather than a failure, so
    /// fresh clones without an agent secret keep CI green.
    /// </remarks>
    [CreateAssetMenu(
        menuName = "ElevenLabs/Conversation Smoke Config",
        fileName = "ConversationSmokeConfig"
    )]
    public sealed class ConversationSmokeConfig : ScriptableObject
    {
        [SerializeField]
        [Tooltip("Public agent identifier from elevenlabs.io. Required.")]
        private string agentId = "";

        [SerializeField]
        [Tooltip("Optional pre-signed WebSocket URL for private agents.")]
        private string signedUrl = "";

        [SerializeField]
        [Tooltip("Transport selection. Defaults to WebSocket.")]
        private ConnectionType connectionType = ConnectionType.WebSocket;

        public string AgentId => agentId;
        public string? SignedUrl => string.IsNullOrWhiteSpace(signedUrl) ? null : signedUrl;
        public ConnectionType ConnectionType => connectionType;
    }
}
